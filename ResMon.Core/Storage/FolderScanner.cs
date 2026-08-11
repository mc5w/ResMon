using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using ResMon.Core.Diagnostics;
using ResMon.Core.Native;

namespace ResMon.Core.Storage;

/// <summary>
/// Läuft eine Partition ab und summiert die Belegung je Ordner.
/// </summary>
/// <remarks>
/// Die einzige Datenquelle der Anwendung ohne Takt: sie läuft ausschließlich auf
/// ausdrückliche Anforderung (DESIGN.md §9). Gemeldet wird die <em>logische</em>
/// Größe, nicht die Belegung auf dem Datenträger — was das bedeutet, steht in
/// README „Abweichungen".
/// </remarks>
public sealed class FolderScanner
{
    /// <summary>Knoten je Block. Blöcke wandern nie, deshalb darf parallel hineingeschrieben werden.</summary>
    internal const int ChunkShift = 16;

    internal const int ChunkSize = 1 << ChunkShift;
    internal const int ChunkMask = ChunkSize - 1;

    /// <summary>268 Millionen Ordner — jenseits dessen, was eine Partition trägt.</summary>
    private const int MaxChunks = 4096;

    /// <summary>Ab dieser Größe bekommt eine Datei einen eigenen Eintrag.</summary>
    private const long LargeFileThreshold = 16L * 1024 * 1024;

    /// <summary>
    /// Jeder <c>NtQueryDirectoryFile</c>-Aufruf füllt diesen Puffer. 64 KB statt
    /// der voreingestellten 4 KB sparen bei <c>WinSxS</c> und
    /// <c>Windows\Installer</c> ein Vielfaches an Aufrufen.
    /// </summary>
    private const int ReadBufferSize = 64 * 1024;

    /// <summary>
    /// <c>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS</c>. <see cref="FileAttributes"/>
    /// kennt das Attribut nicht; OneDrive setzt es auf Dateien, die erst beim
    /// Zugriff heruntergeladen werden.
    /// </summary>
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    private readonly DirNode[][] _chunks = new DirNode[MaxChunks][];
    private readonly Lock _grow = new();
    private readonly ConcurrentStack<PendingDirectory> _stack = new();
    private readonly SemaphoreSlim _work = new(0);

    private int _count;
    private int _outstanding;
    private int _workerCount;
    private volatile bool _finished;

    private List<BigFile>[] _bigPerWorker = [];

    private long _scannedBytes;
    private int _scannedDirectories;
    private int _scannedFiles;
    private int _deniedFolders;
    private int _reparsePoints;
    private long _cloudBytes;
    private string _currentPath = string.Empty;

    /// <summary>Ein Ordner, der noch abzulaufen ist, samt seinem Platz im Baum.</summary>
    private readonly record struct PendingDirectory(int Index, string Path);

    /// <summary>Bisher summierte Bytes. Für die Fortschrittsanzeige.</summary>
    public long ScannedBytes => Interlocked.Read(ref _scannedBytes);

    public int ScannedDirectories => Volatile.Read(ref _scannedDirectories);

    public int ScannedFiles => Volatile.Read(ref _scannedFiles);

    /// <summary>
    /// Der zuletzt begonnene Ordner. Absichtlich ungesichert: es ist eine
    /// Referenzzuweisung, die Anzeige darf einen Takt hinterherhinken, und eine
    /// Sperre je Ordner wäre teurer als die Auskunft wert ist.
    /// </summary>
    public string CurrentPath => _currentPath;

    /// <summary>
    /// Läuft den Baum ab. Blockiert bis zum Ende und gehört deshalb auf einen
    /// eigenen Thread. Wird abgebrochen, kommt der Teilbaum zurück statt einer
    /// Ausnahme — „das Größte, was bisher gefunden wurde" ist immer noch eine
    /// Antwort, und der Aufrufer bleibt frei von Abbruchbehandlung.
    /// </summary>
    public FolderScanResult Run(string root, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();

        int rootIndex = Reserve(1);
        ref DirNode rootNode = ref Node(rootIndex);
        rootNode.Name = root;
        rootNode.Parent = -1;
        rootNode.FirstChild = -1;
        rootNode.FirstFile = -1;

        _workerCount = WorkerCount(root);
        _bigPerWorker = new List<BigFile>[_workerCount];

        _outstanding = 1;
        _stack.Push(new PendingDirectory(rootIndex, root));
        _work.Release();

        // Wartende Worker müssen beim Abbruch sofort aufwachen; sie hängen im
        // Semaphor und sähen den Token sonst erst nach der nächsten Arbeit.
        using CancellationTokenRegistration registration = token.Register(() =>
        {
            _finished = true;
            _work.Release(_workerCount);
        });

        var threads = new Thread[_workerCount];
        for (int i = 0; i < _workerCount; i++)
        {
            int worker = i;
            _bigPerWorker[worker] = [];
            threads[worker] = new Thread(() => Work(worker, token))
            {
                IsBackground = true,
                Name = $"ResMon Ordner-Scan {worker + 1}",
                // Der Monitor darf nicht selbst zum Lastverursacher werden
                // (DESIGN.md §9).
                Priority = ThreadPriority.BelowNormal,
            };
            threads[worker].Start();
        }

        foreach (Thread thread in threads)
            thread.Join();

        return Build(root, token.IsCancellationRequested, watch.Elapsed);
    }

    /// <summary>
    /// Auf einer Festplatte kostet jeder zusätzliche Thread Kopfbewegungen statt
    /// Durchsatz; auf einer SSD hält ein einzelner Thread die Warteschlange des
    /// Geräts nicht gefüllt. Der Unterschied ist der zwischen zwanzig Sekunden
    /// und einer Viertelstunde.
    /// </summary>
    private static int WorkerCount(string root)
        => StorageDevice.HasSeekPenalty(root) == true
            ? 2
            : Math.Clamp(Environment.ProcessorCount, 4, 8);

    private void Work(int worker, CancellationToken token)
    {
        while (true)
        {
            _work.Wait();
            if (_finished)
                return;

            if (!_stack.TryPop(out PendingDirectory pending))
                continue;

            if (!token.IsCancellationRequested)
                ProcessDirectory(worker, pending);

            // Der letzte, der fertig wird, macht das Licht aus — und weckt alle
            // anderen, die noch auf Arbeit warten, die nie mehr kommt.
            if (Interlocked.Decrement(ref _outstanding) == 0)
            {
                _finished = true;
                _work.Release(_workerCount);
                return;
            }
        }
    }

    private void ProcessDirectory(int worker, PendingDirectory pending)
    {
        _currentPath = pending.Path;

        long smallBytes = 0;
        long cloudBytes = 0;
        int fileCount = 0;
        int reparseCount = 0;
        FolderFlags flags = FolderFlags.None;
        List<string> subdirectories;
        List<(string Name, long Bytes)> bigFiles;

        try
        {
            using var reader = new LevelReader(pending.Path, ReadOptions());
            while (reader.MoveNext())
            {
                // ShouldIncludeEntry liefert immer false; ein einziger Durchlauf
                // wertet damit das ganze Verzeichnis aus, ohne je ein Ergebnis zu
                // erzeugen.
            }

            smallBytes = reader.SmallBytes;
            cloudBytes = reader.CloudBytes;
            fileCount = reader.FileCount;
            reparseCount = reader.ReparseCount;
            flags = reader.Flags;
            subdirectories = reader.Subdirectories;
            bigFiles = reader.BigFiles;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
        {
            // Ein gesperrter Ordner darf den Lauf nicht anhalten. Gezählt wird er
            // trotzdem — eine Summe mit unbekannter Lücke ist etwas anderes als
            // eine vollständige.
            Interlocked.Increment(ref _deniedFolders);
            Node(pending.Index).Flags |= FolderFlags.Denied;
            return;
        }

        if ((flags & FolderFlags.Denied) != 0)
            Interlocked.Increment(ref _deniedFolders);

        Interlocked.Add(ref _reparsePoints, reparseCount);
        Interlocked.Add(ref _cloudBytes, cloudBytes);

        long bigBytes = 0;
        List<BigFile> sink = _bigPerWorker[worker];
        foreach ((string name, long bytes) in bigFiles)
        {
            sink.Add(new BigFile(name, bytes, pending.Index));
            bigBytes += bytes;
        }

        int first = subdirectories.Count > 0 ? Reserve(subdirectories.Count) : -1;

        ref DirNode node = ref Node(pending.Index);
        node.OwnBytes = smallBytes;
        node.TotalBytes = smallBytes + bigBytes;
        node.FileCount = fileCount;
        node.TotalFileCount = fileCount;
        node.Flags |= flags;
        node.FirstChild = first;
        node.ChildCount = first >= 0 ? subdirectories.Count : 0;

        Interlocked.Add(ref _scannedBytes, smallBytes + bigBytes);
        Interlocked.Increment(ref _scannedDirectories);
        Interlocked.Add(ref _scannedFiles, fileCount);

        if (first < 0)
            return;

        for (int i = 0; i < subdirectories.Count; i++)
        {
            ref DirNode child = ref Node(first + i);
            child.Name = subdirectories[i];
            child.Parent = pending.Index;
            child.FirstChild = -1;
            child.FirstFile = -1;
        }

        // Erst die Zähler erhöhen, dann die Arbeit sichtbar machen: sonst könnte
        // der letzte Worker auf null fallen, während hier noch Kinder anstehen.
        Interlocked.Add(ref _outstanding, subdirectories.Count);
        for (int i = 0; i < subdirectories.Count; i++)
            _stack.Push(new PendingDirectory(first + i, Path.Join(pending.Path, subdirectories[i])));

        _work.Release(subdirectories.Count);
    }

    /// <summary>
    /// Ordnet die Großdateien ihren Ordnern zu und summiert den Baum auf.
    /// </summary>
    private FolderScanResult Build(string root, bool cancelled, TimeSpan duration)
    {
        // Nach Elternindex sortiert liegen die Dateien eines Ordners beieinander;
        // damit genügen im Knoten Index und Anzahl, wie bei den Unterordnern.
        var files = new List<BigFile>();
        foreach (List<BigFile> perWorker in _bigPerWorker)
            files.AddRange(perWorker);

        files.Sort(static (left, right) => left.Parent.CompareTo(right.Parent));

        BigFile[] sorted = [.. files];
        for (int i = 0; i < sorted.Length;)
        {
            int parent = sorted[i].Parent;
            int run = 1;
            long bytes = sorted[i].Bytes;
            while (i + run < sorted.Length && sorted[i + run].Parent == parent)
            {
                bytes += sorted[i + run].Bytes;
                run++;
            }

            ref DirNode owner = ref Node(parent);
            owner.FirstFile = i;
            owner.FileNodeCount = run;
            i += run;
        }

        // Der Index eines Kindes ist immer größer als der seines Elternteils —
        // der Block wird beim Bearbeiten des Elternteils reserviert, und das war
        // vorher an der Reihe. Deshalb genügt eine einzige Rückwärtsschleife,
        // ohne Rekursion und ohne Stapel.
        for (int i = _count - 1; i > 0; i--)
        {
            ref DirNode node = ref Node(i);
            if (node.Parent < 0)
                continue;

            ref DirNode parent = ref Node(node.Parent);
            parent.TotalBytes += node.TotalBytes;
            parent.TotalFileCount += node.TotalFileCount;
        }

        (long total, long free) = VolumeSize(root);

        return new FolderScanResult(
            _chunks,
            _count,
            sorted,
            root,
            cancelled,
            Volatile.Read(ref _deniedFolders),
            Volatile.Read(ref _reparsePoints),
            Interlocked.Read(ref _cloudBytes),
            total,
            free,
            duration);
    }

    private static (long Total, long Free) VolumeSize(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            return drive.IsReady ? (drive.TotalSize, drive.TotalFreeSpace) : (0, 0);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Report("Ordner-Scan", ex, $"Die Größe von „{root}“ ließ sich nicht ermitteln");
            return (0, 0);
        }
    }

    private ref DirNode Node(int index) => ref _chunks[index >> ChunkShift][index & ChunkMask];

    /// <summary>
    /// Reserviert einen zusammenhängenden Block. Die Blöcke selbst wandern nie —
    /// ein wachsendes Feld würde beim Umkopieren die Schreibzugriffe der anderen
    /// Worker in das verwaiste alte Feld laufen lassen.
    /// </summary>
    private int Reserve(int count)
    {
        if (count <= 0)
            return -1;

        lock (_grow)
        {
            long end = (long)_count + count;
            if (end > (long)MaxChunks * ChunkSize)
                return -1;

            int start = _count;
            for (int chunk = start >> ChunkShift; chunk <= (int)((end - 1) >> ChunkShift); chunk++)
                _chunks[chunk] ??= new DirNode[ChunkSize];

            _count = (int)end;
            return start;
        }
    }

    private static EnumerationOptions ReadOptions() => new()
    {
        // Die Rekursion steuern wir selbst — sonst ließen sich Abzweigungen nicht
        // aussparen und jeder Ordner bräuchte seine Summe wieder aus Pfadstrings.
        RecurseSubdirectories = false,

        // Die Voreinstellung wäre Hidden|System. Damit fehlten ausgerechnet
        // pagefile.sys, hiberfil.sys und ProgramData — also gerade das, was eine
        // volle Partition erklärt.
        AttributesToSkip = 0,

        // Bewusst false: nur so kommt ContinueOnError, und ein übersprungener
        // Ordner soll gezählt werden, nicht stillschweigend fehlen.
        IgnoreInaccessible = false,

        ReturnSpecialDirectories = false,
        MatchType = MatchType.Simple,
        BufferSize = ReadBufferSize,
    };

    /// <summary>
    /// Zählt eine einzelne Verzeichnisebene aus.
    /// </summary>
    /// <remarks>
    /// Die Auswertung steckt in <see cref="ShouldIncludeEntry"/>, das immer
    /// <c>false</c> liefert: ein einziger <c>MoveNext()</c> läuft damit durch das
    /// ganze Verzeichnis, ohne dass je ein Ergebnis entsteht. Der Weg über
    /// <c>TransformEntry</c> legte für jeden Eintrag eine Zeichenkette an — bei
    /// einer Million Dateien der Unterschied zwischen Sekunden und Minuten.
    /// Namen entstehen nur für Unterordner und Großdateien.
    ///
    /// Die Listen sind Feldinitialisierer und keine Konstruktorparameter: die
    /// laufen vor dem Basiskonstruktor, der die Aufzählung bereits vorbereitet.
    /// </remarks>
    private sealed class LevelReader(string directory, EnumerationOptions options)
        : FileSystemEnumerator<int>(directory, options)
    {
        public readonly List<string> Subdirectories = [];
        public readonly List<(string Name, long Bytes)> BigFiles = [];

        public long SmallBytes;
        public long CloudBytes;
        public int FileCount;
        public int ReparseCount;
        public FolderFlags Flags;

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
        {
            FileAttributes attributes = entry.Attributes;

            if ((attributes & FileAttributes.Directory) != 0)
            {
                // Abzweigungen zeigen auf Daten, die anderswo schon gezählt
                // werden: C:\Users\All Users auf C:\ProgramData, und
                // AppData\Local\Application Data auf sich selbst — letzteres
                // liefe ohne diese Bedingung endlos im Kreis. Eingehängte
                // Volumes sind ebenfalls Abzweigungen; ihr Inhalt gehört zu
                // einer anderen Partition.
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    Flags |= FolderFlags.Reparse;
                    ReparseCount++;
                }
                else
                {
                    Subdirectories.Add(entry.FileName.ToString());
                }

                return false;
            }

            if ((attributes & (FileAttributes.Compressed | FileAttributes.SparseFile)) != 0)
                Flags |= FolderFlags.Compressed;

            // Cloud-Platzhalter: die gemeldete Länge ist die volle Dateigröße,
            // auf dem Datenträger liegt fast nichts davon.
            if ((attributes & (FileAttributes.Offline | RecallOnDataAccess)) != 0)
            {
                Flags |= FolderFlags.Cloud;
                CloudBytes += entry.Length;
            }

            if (entry.Length >= LargeFileThreshold)
                BigFiles.Add((entry.FileName.ToString(), entry.Length));
            else
                SmallBytes += entry.Length;

            FileCount++;
            return false;
        }

        protected override int TransformEntry(ref FileSystemEntry entry) => 0;

        protected override bool ContinueOnError(int error)
        {
            Flags |= FolderFlags.Denied;
            return true;
        }
    }
}

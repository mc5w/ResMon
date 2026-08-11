namespace ResMon.Core.Storage;

/// <summary>Was bei einem Ordner zu beachten ist, damit seine Zahl richtig gelesen wird.</summary>
[Flags]
public enum FolderFlags
{
    None = 0,

    /// <summary>Enthält Abzweigungen, die nicht verfolgt wurden.</summary>
    Reparse = 1,

    /// <summary>Windows hat den Inhalt nicht herausgegeben — die Summe ist zu klein.</summary>
    Denied = 2,

    /// <summary>Enthält komprimierte oder dünn besetzte Dateien; die Größe ist die logische.</summary>
    Compressed = 4,

    /// <summary>Enthält Cloud-Platzhalter; die gemeldete Größe liegt nicht auf diesem Datenträger.</summary>
    Cloud = 8,
}

/// <summary>
/// Ein Ordner im Ergebnisbaum.
/// </summary>
/// <remarks>
/// Struktur in einem flachen Feld statt Objektgraph mit Kinderlisten: eine
/// Viertelmillion Objekte mit je einer Liste kosten das Vierfache an Speicher und
/// landen samt Listen in Generation 2 — eine GC-Pause von mehreren hundert
/// Millisekunden in einer Anwendung, deren Zweck ein ruckelfreies Diagramm im
/// Sekundentakt ist. Die Kinder eines Ordners liegen zusammenhängend, weil ihr
/// Block in einem Zug reserviert wird; deshalb genügen Index und Anzahl.
/// </remarks>
public struct DirNode
{
    /// <summary>Nur das Segment; an der Wurzel der volle Laufwerkspfad.</summary>
    public string Name;

    /// <summary>Nach dem Rollup: samt aller Unterordner und Großdateien.</summary>
    public long TotalBytes;

    /// <summary>
    /// Dateien unmittelbar in diesem Ordner, die <em>keinen</em> eigenen Eintrag
    /// bekommen haben. Großdateien stehen in <see cref="FolderScanResult"/>
    /// gesondert und dürfen hier nicht mitzählen, sonst stünden sie doppelt in
    /// der Summe.
    /// </summary>
    public long OwnBytes;

    /// <summary>-1 an der Wurzel.</summary>
    public int Parent;

    /// <summary>-1 ohne Unterordner.</summary>
    public int FirstChild;

    public int ChildCount;

    /// <summary>Index der ersten Großdatei dieses Ordners, -1 wenn er keine hat.</summary>
    public int FirstFile;

    public int FileNodeCount;

    /// <summary>Alle Dateien unmittelbar hier, groß wie klein.</summary>
    public int FileCount;

    /// <summary>Nach dem Rollup: samt aller Unterordner.</summary>
    public int TotalFileCount;

    public FolderFlags Flags;
}

/// <summary>
/// Eine Datei, die groß genug ist, um eigens genannt zu werden. Ohne sie fehlten
/// <c>hiberfil.sys</c> und <c>pagefile.sys</c> ausgerechnet in der Ansicht, die
/// erklären soll, warum die Partition voll ist.
/// </summary>
public readonly record struct BigFile(string Name, long Bytes, int Parent);

/// <summary>
/// Ein Knoten, wie ihn die Oberfläche bekommt.
/// </summary>
/// <remarks>
/// Wie viele Kinder tatsächlich mitgeschickt wurden und wie viele Bytes dabei
/// unter den Tisch fielen, steht bewusst <em>nicht</em> darin: die Seite hat
/// beides ohnehin vorliegen, und nach einem Nachschlag stimmten die vom Host
/// mitgegebenen Werte nicht mehr. <c>ChildCount</c> gegen die Zahl der bekannten
/// Kinder gehalten ergibt „lässt sich noch aufklappen"; <c>TotalBytes</c> minus
/// <c>OwnBytes</c> minus der bekannten Kinder ergibt den Rest.
/// </remarks>
public readonly record struct FolderSlice(
    int Id,
    int Parent,
    string Name,
    long TotalBytes,
    long OwnBytes,
    int ChildCount,
    int TotalFileCount,
    bool IsFile,
    FolderFlags Flags);

/// <summary>
/// Das Ergebnis eines Durchlaufs. Hält den vollständigen Baum für die Dauer der
/// Sitzung; die Oberfläche bekommt daraus nur einen beschnittenen Auszug und holt
/// den Rest über <see cref="ChildrenOf"/> nach.
/// </summary>
public sealed class FolderScanResult
{
    /// <summary>
    /// Harte Obergrenze der ersten Nutzlast. Ein Schwellwert allein genügt nicht:
    /// bei einem Baum aus lauter gleich großen Ordnern kämen beliebig viele
    /// Knoten durch.
    /// </summary>
    private const int NodeBudget = 2000;

    /// <summary>
    /// Anteil an der Wurzelsumme, ab dem ein Knoten mitgeschickt wird. Auf einer
    /// 270-GiB-Partition sind das 55 MB; gemessen bleiben damit knapp 2000 Knoten
    /// übrig, das Budget wird also gerade ausgeschöpft. Mit dem naheliegenderen
    /// 0,1 % kamen nur 500 Knoten durch — die Hälfte des Weges nach unten hätte
    /// die Oberfläche dann einzeln nachfordern müssen.
    /// </summary>
    private const double MinShare = 0.0002;

    /// <summary>Untergrenze für kleine Partitionen, wo der Anteil nur ein paar MB wäre.</summary>
    private const long MinSize = 16L * 1024 * 1024;

    /// <summary>Höchstzahl Kinder je Knoten in der ersten Nutzlast.</summary>
    private const int MaxChildren = 32;

    private readonly DirNode[][] _chunks;
    private readonly int _dirCount;
    private readonly BigFile[] _files;

    internal FolderScanResult(
        DirNode[][] chunks,
        int dirCount,
        BigFile[] files,
        string root,
        bool cancelled,
        int deniedFolders,
        int reparsePoints,
        long cloudBytes,
        long volumeTotalBytes,
        long volumeFreeBytes,
        TimeSpan duration)
    {
        _chunks = chunks;
        _dirCount = dirCount;
        _files = files;
        Root = root;
        Cancelled = cancelled;
        DeniedFolders = deniedFolders;
        ReparsePoints = reparsePoints;
        CloudBytes = cloudBytes;
        VolumeTotalBytes = volumeTotalBytes;
        VolumeFreeBytes = volumeFreeBytes;
        Duration = duration;
    }

    /// <summary>Die durchsuchte Wurzel, etwa <c>C:\</c>.</summary>
    public string Root { get; }

    /// <summary>
    /// Ob der Lauf abgebrochen wurde. Dann sind die Summen der noch nicht
    /// fertigen Zweige zu klein — das muss bis in die Anzeige durchschlagen.
    /// </summary>
    public bool Cancelled { get; }

    public int DeniedFolders { get; }

    public int ReparsePoints { get; }

    /// <summary>Anteil der Summe, der in Cloud-Platzhaltern steckt.</summary>
    public long CloudBytes { get; }

    public long VolumeTotalBytes { get; }

    public long VolumeFreeBytes { get; }

    public TimeSpan Duration { get; }

    public long TotalBytes => _dirCount > 0 ? Node(0).TotalBytes : 0;

    public int TotalFileCount => _dirCount > 0 ? Node(0).TotalFileCount : 0;

    public int DirectoryCount => _dirCount;

    public int BigFileCount => _files.Length;

    private ref DirNode Node(int index) => ref _chunks[index >> FolderScanner.ChunkShift][index & FolderScanner.ChunkMask];

    /// <summary>Ob die Kennung eine Großdatei statt eines Ordners meint.</summary>
    public bool IsFile(int id) => id >= _dirCount;

    public bool IsKnown(int id) => id >= 0 && id < _dirCount + _files.Length;

    /// <summary>
    /// Baut den vollen Pfad über die Elternkette zusammen. Die Nutzlast trägt nur
    /// Namenssegmente; der Pfad entsteht erst, wenn ihn jemand braucht.
    /// </summary>
    public string PathOf(int id)
    {
        if (!IsKnown(id))
            return string.Empty;

        if (id >= _dirCount)
        {
            BigFile file = _files[id - _dirCount];
            return Path.Join(PathOf(file.Parent), file.Name);
        }

        var segments = new List<string>();
        for (int index = id; index >= 0; index = Node(index).Parent)
            segments.Add(Node(index).Name);

        segments.Reverse();
        return Path.Join([.. segments]);
    }

    /// <summary>
    /// Der Auszug für die erste Nutzlast: größensortierte Breitensuche, bis das
    /// Budget voll ist. „Größtes zuerst" ist genau die Reihenfolge, die eine
    /// Treemap braucht — Auswahl und Anzeige stimmen damit überein.
    /// </summary>
    public IReadOnlyList<FolderSlice> Prune()
    {
        if (_dirCount == 0)
            return [];

        long threshold = Math.Max((long)(TotalBytes * MinShare), MinSize);
        var selected = new bool[_dirCount + _files.Length];
        var queue = new PriorityQueue<int, long>();

        selected[0] = true;
        int count = 1;
        // PriorityQueue ist ein Min-Heap; das Vorzeichen dreht ihn um.
        queue.Enqueue(0, -Node(0).TotalBytes);

        var children = new List<(int Id, long Bytes)>();
        while (queue.Count > 0 && count < NodeBudget)
        {
            int id = queue.Dequeue();
            CollectChildren(id, children);
            children.Sort(static (left, right) => right.Bytes.CompareTo(left.Bytes));

            int taken = 0;
            foreach ((int childId, long bytes) in children)
            {
                if (taken >= MaxChildren || count >= NodeBudget)
                    break;

                if (bytes < threshold)
                    break;   // absteigend sortiert — alles Weitere ist kleiner

                selected[childId] = true;
                count++;
                taken++;

                if (childId < _dirCount)
                    queue.Enqueue(childId, -bytes);
            }
        }

        var slices = new List<FolderSlice>(count);
        for (int id = 0; id < selected.Length; id++)
        {
            if (selected[id])
                slices.Add(SliceOf(id));
        }

        return slices;
    }

    /// <summary>
    /// Die größten Kinder eines Knotens — die Antwort auf ein Aufklappen in der
    /// Oberfläche. Läuft aus dem Speicher, der Baum wird nie erneut durchlaufen.
    /// </summary>
    public IReadOnlyList<FolderSlice> ChildrenOf(int id, int max = 100)
    {
        if (!IsKnown(id) || id >= _dirCount)
            return [];

        var children = new List<(int Id, long Bytes)>();
        CollectChildren(id, children);
        children.Sort(static (left, right) => right.Bytes.CompareTo(left.Bytes));

        var slices = new List<FolderSlice>(Math.Min(max, children.Count));
        for (int i = 0; i < children.Count && i < max; i++)
            slices.Add(SliceOf(children[i].Id));

        return slices;
    }

    /// <summary>Unterordner und Großdateien eines Ordners in einer Liste.</summary>
    private void CollectChildren(int id, List<(int Id, long Bytes)> into)
    {
        into.Clear();
        if (id >= _dirCount)
            return;

        ref DirNode node = ref Node(id);
        for (int i = 0; i < node.ChildCount; i++)
        {
            int childId = node.FirstChild + i;
            into.Add((childId, Node(childId).TotalBytes));
        }

        for (int i = 0; i < node.FileNodeCount; i++)
        {
            int fileIndex = node.FirstFile + i;
            into.Add((_dirCount + fileIndex, _files[fileIndex].Bytes));
        }
    }

    /// <summary>Baut einen Knoten für die Leitung.</summary>
    private FolderSlice SliceOf(int id)
    {
        if (id >= _dirCount)
        {
            BigFile file = _files[id - _dirCount];
            return new FolderSlice(
                id, file.Parent, file.Name, file.Bytes,
                OwnBytes: 0, ChildCount: 0, TotalFileCount: 1, IsFile: true, FolderFlags.None);
        }

        ref DirNode node = ref Node(id);
        return new FolderSlice(
            id,
            node.Parent,
            node.Name,
            node.TotalBytes,
            node.OwnBytes,
            node.ChildCount + node.FileNodeCount,
            node.TotalFileCount,
            IsFile: false,
            node.Flags);
    }
}

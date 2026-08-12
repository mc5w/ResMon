using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using ResMon.Core.Diagnostics;

namespace ResMon.Core.Native;

/// <summary>Die Handles eines Prozesses, nach Objektart gezählt.</summary>
/// <param name="Pid">Der Prozess.</param>
/// <param name="Total">Alle Handles zusammen.</param>
/// <param name="ByType">Anzahl je Objektart; nicht zugeordnete unter „Übrige“.</param>
public sealed record ProcessHandles(int Pid, int Total, IReadOnlyDictionary<string, int> ByType);

/// <summary>Ein geöffnetes Objekt eines Prozesses.</summary>
/// <param name="Handle">Der Handle-Wert im besitzenden Prozess.</param>
/// <param name="Kind">Datei, Pipe, Zeichengerät.</param>
/// <param name="Name">Der aufgelöste Pfad; <c>null</c>, wenn er nicht ermittelbar war.</param>
public sealed record OpenFile(long Handle, string Kind, string? Name);

/// <summary>
/// Systemweite Handle-Tabelle über <c>NtQuerySystemInformation</c> — die Quelle,
/// aus der auch Process Explorer und <c>handle.exe</c> schöpfen.
/// </summary>
/// <remarks>
/// Ein einziger Aufruf liefert <b>alle</b> Handles des Systems mit besitzender
/// PID und Objektart. Das ist der Grund, warum die Zählung nichts kostet: es gibt
/// keinen Aufruf je Prozess, sondern einen für alles. Auf einem laufenden System
/// sind das je nach Last 100 000 bis 400 000 Einträge und ein Puffer von einigen
/// Megabyte.
/// <para>
/// Wofür das gut ist: ein Prozess, dessen Handle-Zahl über Stunden steigt und nie
/// fällt, hat ein Leck — das ist von außen an nichts anderem zu erkennen, und
/// irgendwann bringt es ihn zum Stehen. Für die Startanalyse ist es die
/// Nebenfrage; die eigentliche Antwort auf „hängt in einem Zeitlimit“ gibt
/// <see cref="WaitChain"/>.
/// </para>
/// </remarks>
public static class HandleTable
{
    private const int SystemExtendedHandleInformation = 64;
    private const int ObjectNameInformation = 1;

    private const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;

    private const int PROCESS_DUP_HANDLE = 0x0040;
    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int DUPLICATE_SAME_ACCESS = 0x0002;

    private const int FILE_TYPE_DISK = 0x0001;
    private const int FILE_TYPE_CHAR = 0x0002;
    private const int FILE_TYPE_PIPE = 0x0003;

    /// <summary>Kopfgröße von SYSTEM_HANDLE_INFORMATION_EX: Anzahl plus reserviertes Feld.</summary>
    private const int TableHeaderSize = 16;

    /// <summary>Größe eines SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX auf x64.</summary>
    private const int EntrySize = 40;

    private static readonly Lock Gate = new();
    private static IReadOnlyDictionary<int, string>? _typeNames;

    /// <summary>
    /// Zählt die Handles aller Prozesse. Liefert eine leere Liste, wenn die
    /// Abfrage nicht möglich war.
    /// </summary>
    public static IReadOnlyList<ProcessHandles> Snapshot()
    {
        byte[]? table = ReadTable(out int count);
        if (table is null)
            return [];

        IReadOnlyDictionary<int, string> names = TypeNames();
        var byProcess = new Dictionary<int, Dictionary<string, int>>();
        var totals = new Dictionary<int, int>();

        for (int i = 0; i < count; i++)
        {
            int offset = TableHeaderSize + (i * EntrySize);
            if (offset + EntrySize > table.Length)
                break;

            int pid = (int)BitConverter.ToInt64(table, offset + 8);
            int typeIndex = BitConverter.ToUInt16(table, offset + 28);

            totals[pid] = totals.GetValueOrDefault(pid) + 1;

            string type = names.TryGetValue(typeIndex, out string? known) ? known : "Übrige";
            if (!byProcess.TryGetValue(pid, out Dictionary<string, int>? counts))
                byProcess[pid] = counts = [];
            counts[type] = counts.GetValueOrDefault(type) + 1;
        }

        return [.. totals
            .Select(entry => new ProcessHandles(
                entry.Key,
                entry.Value,
                byProcess.TryGetValue(entry.Key, out Dictionary<string, int>? types)
                    ? types
                    : new Dictionary<string, int>()))
            .OrderByDescending(p => p.Total)];
    }

    /// <summary>
    /// Die von einem Prozess geöffneten Dateien, mit aufgelöstem Pfad.
    /// </summary>
    /// <remarks>
    /// Der Weg ist der von <c>handle.exe</c>: den fremden Handle in den eigenen
    /// Prozess duplizieren und den Objektnamen abfragen. Dabei lauert die
    /// bekannteste Falle dieser Schnittstelle — <c>NtQueryObject</c> blockiert
    /// <b>für immer</b>, wenn der Handle auf eine synchrone Named Pipe zeigt,
    /// deren Gegenstelle nicht liest. Process Explorer läuft dafür in einen
    /// eigenen Thread und bricht ihn nach einem Zeitlimit ab.
    /// <para>
    /// Hier wird der Fall stattdessen vorher ausgeschlossen: <c>GetFileType</c>
    /// verrät ohne Blockierung, ob ein Handle eine Datei, eine Pipe oder ein
    /// Zeichengerät ist, und nur bei einer echten Datei wird nach dem Namen
    /// gefragt. Das ist kein Zeitlimit, sondern der Verzicht auf die Frage, die
    /// hängen bleibt — und spart den Thread, den .NET ohnehin nicht sicher
    /// abbrechen könnte.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<OpenFile> FilesOf(int pid)
    {
        byte[]? table = ReadTable(out int count);
        if (table is null)
            return [];

        // Ohne bekannte Kennung wird jeder Handle geprüft; GetFileType sortiert
        // dann aus. Das ist langsamer, aber nie falsch.
        int wanted = TypeIndexOf("Datei") ?? -1;

        nint process = OpenProcess(PROCESS_DUP_HANDLE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == nint.Zero)
            return [];

        var files = new List<OpenFile>();
        IReadOnlyDictionary<string, string> devices = DeviceMap();

        try
        {
            nint self = GetCurrentProcess();

            for (int i = 0; i < count; i++)
            {
                int offset = TableHeaderSize + (i * EntrySize);
                if (offset + EntrySize > table.Length)
                    break;

                if ((int)BitConverter.ToInt64(table, offset + 8) != pid)
                    continue;

                if (wanted >= 0 && BitConverter.ToUInt16(table, offset + 28) != wanted)
                    continue;

                long value = BitConverter.ToInt64(table, offset + 16);
                if (!DuplicateHandle(process, value, self, out nint copy, 0, false, DUPLICATE_SAME_ACCESS))
                    continue;

                try
                {
                    int fileType = GetFileType(copy);
                    string kind = fileType switch
                    {
                        FILE_TYPE_DISK => "Datei",
                        FILE_TYPE_PIPE => "Pipe",
                        FILE_TYPE_CHAR => "Zeichengerät",
                        _ => "unbekannt",
                    };

                    // Nur bei einer echten Datei nach dem Namen fragen; alles
                    // andere ist der Fall, der blockiert.
                    string? name = fileType == FILE_TYPE_DISK ? ToDosPath(QueryName(copy), devices) : null;
                    files.Add(new OpenFile(value, kind, name));
                }
                finally
                {
                    CloseHandle(copy);
                }
            }
        }
        finally
        {
            CloseHandle(process);
        }

        return [.. files.OrderBy(f => f.Name ?? "\uFFFF", StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Ruft die Tabelle ab. Der Puffer muss wachsend probiert werden: die Zahl
    /// der Handles ändert sich zwischen Größenabfrage und Abruf laufend.
    /// </summary>
    private static byte[]? ReadTable(out int count)
    {
        count = 0;
        int size = 1 << 20;

        for (int attempt = 0; attempt < 8; attempt++)
        {
            byte[] buffer = new byte[size];
            uint status = NtQuerySystemInformation(
                SystemExtendedHandleInformation, buffer, buffer.Length, out int needed);

            if (status == STATUS_INFO_LENGTH_MISMATCH)
            {
                // Der gemeldete Bedarf ist beim nächsten Aufruf schon wieder zu
                // klein; großzügig aufschlagen statt exakt nachziehen.
                size = Math.Max(needed + (needed / 4), size * 2);
                continue;
            }

            if (status != 0)
            {
                DiagnosticLog.Report("Handle-Tabelle", $"NtQuerySystemInformation meldete 0x{status:X8}");
                return null;
            }

            long handles = BitConverter.ToInt64(buffer, 0);
            count = (int)Math.Min(handles, (buffer.Length - TableHeaderSize) / EntrySize);
            return buffer;
        }

        return null;
    }

    /// <summary>
    /// Ordnet Objektartkennungen ihren Namen zu — durch Ausprobieren statt durch
    /// Auslesen der Typtabelle.
    /// </summary>
    /// <remarks>
    /// Die Kennungen sind nicht festgelegt und verschieben sich zwischen
    /// Windows-Fassungen. Sie sauber aufzulösen hieße,
    /// <c>ObjectTypesInformation</c> zu lesen: eine Kette von Strukturen mit
    /// variabler Länge und eigener Ausrichtungsregel, die bei jeder Änderung
    /// still falsche Namen liefern kann.
    /// <para>
    /// Stattdessen wird je gesuchter Art <b>ein Objekt selbst angelegt</b> und in
    /// der Tabelle nachgeschlagen, welche Kennung der eigene Prozess dafür
    /// bekommen hat. Das ist von der Windows-Fassung unabhängig, kostet einen
    /// Tabellenabruf beim ersten Zugriff und kann nicht das Falsche behaupten:
    /// gefunden wird genau die Kennung des Objekts, das man in der Hand hält.
    /// Der Preis ist, dass nur die angelegten Arten Namen bekommen — alles andere
    /// zählt als „Übrige“.
    /// </para>
    /// </remarks>
    private static IReadOnlyDictionary<int, string> TypeNames()
    {
        lock (Gate)
        {
            if (_typeNames is not null)
                return _typeNames;

            var names = new Dictionary<int, string>();

            try
            {
                using var file = File.OpenRead(Environment.ProcessPath ?? typeof(HandleTable).Assembly.Location);
                using var signal = new EventWaitHandle(false, EventResetMode.ManualReset);
                using var mutex = new Mutex();
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT");

                byte[]? table = ReadTable(out int count);
                if (table is not null)
                {
                    int self = Environment.ProcessId;
                    Add(names, table, count, self, file.SafeFileHandle.DangerousGetHandle(), "Datei");
                    Add(names, table, count, self, signal.SafeWaitHandle.DangerousGetHandle(), "Ereignis");
                    Add(names, table, count, self, mutex.SafeWaitHandle.DangerousGetHandle(), "Mutex");
                    if (key is not null)
                        Add(names, table, count, self, key.Handle.DangerousGetHandle(), "Registry");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                DiagnosticLog.Report("Handle-Tabelle", ex, "Die Objektarten ließen sich nicht bestimmen");
            }

            return _typeNames = names;
        }
    }

    private static void Add(
        Dictionary<int, string> names, byte[] table, int count, int self, nint handle, string label)
    {
        long value = handle;

        for (int i = 0; i < count; i++)
        {
            int offset = TableHeaderSize + (i * EntrySize);
            if (offset + EntrySize > table.Length)
                return;

            if ((int)BitConverter.ToInt64(table, offset + 8) == self
                && BitConverter.ToInt64(table, offset + 16) == value)
            {
                names[BitConverter.ToUInt16(table, offset + 28)] = label;
                return;
            }
        }
    }

    private static int? TypeIndexOf(string label)
    {
        foreach ((int index, string name) in TypeNames())
        {
            if (name == label)
                return index;
        }

        return null;
    }

    private static string? QueryName(nint handle)
    {
        byte[] buffer = new byte[2048];
        uint status = NtQueryObject(handle, ObjectNameInformation, buffer, buffer.Length, out _);
        if (status != 0)
            return null;

        // UNICODE_STRING: Länge in Byte, Kapazität, dann der Zeiger auf die
        // Zeichen — die unmittelbar hinter der Struktur im selben Puffer liegen.
        int length = BitConverter.ToUInt16(buffer, 0);
        return length > 0 && 16 + length <= buffer.Length
            ? System.Text.Encoding.Unicode.GetString(buffer, 16, length)
            : null;
    }

    /// <summary>
    /// Übersetzt einen Gerätepfad in einen Laufwerksbuchstaben.
    /// </summary>
    /// <remarks>
    /// Der Objektname eines Dateihandles ist ein NT-Pfad wie
    /// <c>\Device\HarddiskVolume3\Users\…</c>. Für die Anzeige will man
    /// <c>C:\Users\…</c>; die Zuordnung liefert <c>QueryDosDevice</c> je
    /// Laufwerksbuchstabe.
    /// </remarks>
    private static string? ToDosPath(string? ntPath, IReadOnlyDictionary<string, string> devices)
    {
        if (string.IsNullOrEmpty(ntPath))
            return null;

        foreach ((string device, string letter) in devices)
        {
            if (ntPath.StartsWith(device, StringComparison.OrdinalIgnoreCase))
                return letter + ntPath[device.Length..];
        }

        return ntPath;
    }

    private static IReadOnlyDictionary<string, string> DeviceMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var target = new System.Text.StringBuilder(512);

        for (char letter = 'A'; letter <= 'Z'; letter++)
        {
            string drive = $"{letter}:";
            if (QueryDosDevice(drive, target, target.Capacity) > 0)
                map[target.ToString()] = drive;
        }

        return map;
    }

    [DllImport("ntdll.dll")]
    private static extern uint NtQuerySystemInformation(
        int infoClass, byte[] buffer, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern uint NtQueryObject(
        nint handle, int infoClass, byte[] buffer, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(int access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        nint sourceProcess, long sourceHandle, nint targetProcess, out nint target,
        int access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int options);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll")]
    private static extern int GetFileType(nint handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int QueryDosDevice(string device, System.Text.StringBuilder target, int max);
}

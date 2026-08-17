using System.Runtime.InteropServices;

namespace ResMon.Core.Startup;

/// <summary>Was eine laufende Aufzeichnungssitzung bisher geschrieben hat.</summary>
/// <param name="Running">Ob die Sitzung überhaupt existiert.</param>
/// <param name="Bytes">Bisher weggeschriebene Menge.</param>
public readonly record struct TraceVolume(bool Running, long Bytes)
{
    public static readonly TraceVolume None = new(false, 0);
}

/// <summary>
/// Liest, wie viel eine laufende Startaufzeichnung bereits auf den Datenträger
/// geschrieben hat.
/// </summary>
/// <remarks>
/// <b>Warum es das gibt.</b> Eine Aufzeichnung im Dateimodus wächst unbegrenzt
/// weiter, bis jemand sie beendet — sie hört nicht von selbst auf, wenn der
/// Start vorbei ist. Auf der Referenzmaschine schrieb sie mit rund 12 MB/s und
/// hatte nach wenigen Stunden <b>87 GB</b> belegt. Die Anwendung braucht deshalb
/// eine Zahl, an der sie das erkennen kann, bevor der Datenträger voll ist.
/// <para>
/// <b>Warum nicht die Dateigröße.</b> Die <c>.etl</c> steht im Verzeichnis mit
/// <b>0 Byte</b>, solange die Sitzung läuft: ETW schreibt die Größe erst beim
/// Beenden in den Verzeichniseintrag. Wer die Datei ansieht, sieht nichts — und
/// genau deshalb fällt der Fall im Ordnerbaum nicht auf.
/// </para>
/// <para>
/// <b>Woher die Zahl stattdessen kommt.</b> <c>ControlTrace</c> mit
/// <c>QUERY</c> liefert die Kennzahlen der Sitzung in Feldern:
/// <c>BuffersWritten</c> mal <c>BufferSize</c> ist die geschriebene Menge. Felder
/// und keine Textausgabe — dieselbe Überlegung wie bei den PDH-Zählernamen
/// (DESIGN.md §8.1): <c>logman query</c> nennt dieselben Zahlen, aber hinter
/// übersetzten Beschriftungen, und was übersetzt ist, taugt nicht als
/// Schnittstelle. Nachgemessen an den Sitzungen von Windows selbst: NtfsLog
/// 8 KB × 167 Puffer, DiagTrack-Listener 64 KB × 4445 Puffer.
/// </para>
/// </remarks>
public static class BootTraceSession
{
    /// <summary>
    /// Die beiden Sitzungen, die <c>wpr -addboot</c> einrichtet. Die Namen legt
    /// der Windows Performance Recorder fest; sie stehen so in
    /// <c>logman query -ets</c>.
    /// </summary>
    private static readonly string[] SessionNames =
    [
        "WPR_initiated_WprApp_boottr_WPR System Collector",
        "WPR_initiated_WprApp_boottr_WPR Event Collector",
    ];

    private const uint EVENT_TRACE_CONTROL_QUERY = 0;

    /// <summary>Sitzung gibt es nicht — der Regelfall, wenn nichts aufzeichnet.</summary>
    private const int ERROR_WMI_INSTANCE_NOT_FOUND = 4201;

    /// <summary>
    /// Beide Sitzungen zusammen. Läuft keine, kommt <see cref="TraceVolume.None"/>
    /// zurück.
    /// </summary>
    public static TraceVolume Read()
    {
        bool running = false;
        long total = 0;

        foreach (string name in SessionNames)
        {
            TraceVolume one = ReadOne(name);
            if (!one.Running)
                continue;

            running = true;
            total += one.Bytes;
        }

        return running ? new TraceVolume(true, total) : TraceVolume.None;
    }

    private static TraceVolume ReadOne(string session)
    {
        // Der Puffer trägt hinter der Struktur zwei Zeichenketten — Sitzungs- und
        // Dateiname. ControlTrace schreibt sie dorthin zurück und verlangt den
        // Platz dafür, auch wenn nur abgefragt wird.
        int header = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>();
        int size = header + (2 * NameBytes);

        IntPtr buffer = Marshal.AllocHGlobal(size);

        try
        {
            for (int i = 0; i < size; i++)
                Marshal.WriteByte(buffer, i, 0);

            var properties = new EVENT_TRACE_PROPERTIES
            {
                WnodeBufferSize = (uint)size,
                LoggerNameOffset = (uint)header,
                LogFileNameOffset = (uint)(header + NameBytes),
            };

            Marshal.StructureToPtr(properties, buffer, false);

            int result = ControlTraceW(0, session, buffer, EVENT_TRACE_CONTROL_QUERY);
            if (result != 0)
            {
                // Nicht vorhanden ist kein Fehler, sondern der Normalfall. Alles
                // andere ebenfalls stumm: diese Abfrage läuft im Takt, und ein
                // Protokolleintrag je Sekunde wäre schlimmer als die Lücke.
                return TraceVolume.None;
            }

            var read = Marshal.PtrToStructure<EVENT_TRACE_PROPERTIES>(buffer);
            return new TraceVolume(true, (long)read.BuffersWritten * read.BufferSize * 1024);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Platz für einen Namen im Anhang der Struktur.</summary>
    private const int NameBytes = 1024;

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_TRACE_PROPERTIES
    {
        public uint WnodeBufferSize;
        public uint WnodeProviderId;
        public ulong WnodeHistoricalContext;
        public long WnodeTimeStamp;
        public Guid WnodeGuid;
        public uint WnodeClientContext;
        public uint WnodeFlags;

        /// <summary>In Kilobyte.</summary>
        public uint BufferSize;

        public uint MinimumBuffers;
        public uint MaximumBuffers;

        /// <summary>Obergrenze in Megabyte, 0 für keine.</summary>
        public uint MaximumFileSize;

        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;

        /// <summary>Wie viele Puffer bereits weggeschrieben wurden.</summary>
        public uint BuffersWritten;

        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int ControlTraceW(ulong handle, string name, IntPtr properties, uint code);
}

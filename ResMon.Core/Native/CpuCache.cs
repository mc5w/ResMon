using System.Runtime.InteropServices;
using ResMon.Core.Diagnostics;

namespace ResMon.Core.Native;

/// <summary>Wofür ein Cache zuständig ist.</summary>
public enum CpuCacheKind
{
    /// <summary>Daten und Befehle gemeinsam — ab L2 der Normalfall.</summary>
    Unified,

    /// <summary>Nur Befehle (L1i).</summary>
    Instruction,

    /// <summary>Nur Daten (L1d).</summary>
    Data,

    /// <summary>Trace-Cache; seit Pentium 4 ausgestorben, der Vollständigkeit halber.</summary>
    Trace,
}

/// <summary>
/// Gleich große Caches derselben Ebene und Art, zusammengefasst.
/// <paramref name="Count"/> ist ihre Anzahl — bei L1 also die Zahl der Kerne.
/// </summary>
public readonly record struct CpuCacheGroup(int Level, CpuCacheKind Kind, long BytesEach, int Count)
{
    public long TotalBytes => BytesEach * Count;
}

/// <summary>
/// Die Cache-Ebenen des Prozessors, aus <c>GetLogicalProcessorInformationEx</c>.
/// </summary>
/// <remarks>
/// WMI beantwortet die Frage nur halb: <c>Win32_Processor</c> kennt L2 und L3,
/// aber kein Feld für L1, und <c>Win32_CacheMemory</c> wirft alle Caches einer
/// Ebene zu einer Zahl zusammen. Die Kernelfunktion liefert dagegen jeden Cache
/// einzeln — samt Trennung in Daten- und Befehlscache, die genau bei L1
/// existiert. Sie kostet keinen Prozess-Aufruf und keine WMI-Verbindung.
/// </remarks>
public static class CpuCache
{
    private const int RelationCache = 2;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    /// <summary>
    /// Offsets in SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX. Die Struktur ist eine
    /// Union über vier Beziehungsarten; hier interessiert nur CACHE_RELATIONSHIP,
    /// deren führende Felder seit Windows 7 unverändert sind.
    /// </summary>
    private const int OffsetSize = 4;
    private const int OffsetLevel = 8;
    private const int OffsetCacheSize = 12;
    private const int OffsetCacheType = 16;

    /// <summary>
    /// Alle Caches, nach Ebene und Art gruppiert. Leer, wenn die Abfrage
    /// fehlschlägt — der Aufrufer zeigt die Angabe dann nicht an.
    /// </summary>
    public static IReadOnlyList<CpuCacheGroup> Read()
    {
        int length = 0;
        if (GetLogicalProcessorInformationEx(RelationCache, IntPtr.Zero, ref length))
            return [];

        int error = Marshal.GetLastWin32Error();
        if (error != ERROR_INSUFFICIENT_BUFFER || length <= 0)
        {
            DiagnosticLog.Report("Prozessor-Caches",
                $"GetLogicalProcessorInformationEx meldete Fehler {error} — die Cache-Größen fehlen.");
            return [];
        }

        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationCache, buffer, ref length))
            {
                DiagnosticLog.Report("Prozessor-Caches",
                    $"GetLogicalProcessorInformationEx meldete Fehler {Marshal.GetLastWin32Error()} — " +
                    "die Cache-Größen fehlen.");
                return [];
            }

            // Gleich große Caches derselben Ebene und Art werden gezählt, nicht
            // einzeln aufgeführt: „6 × 32 KB" ist die Auskunft, „32 KB" sechsmal
            // untereinander wäre keine.
            var counts = new Dictionary<(int Level, CpuCacheKind Kind, long Bytes), int>();

            int offset = 0;
            while (offset + OffsetCacheType + 4 <= length)
            {
                int size = Marshal.ReadInt32(buffer, offset + OffsetSize);
                if (size <= 0)
                    break;

                int level = Marshal.ReadByte(buffer, offset + OffsetLevel);
                long bytes = (uint)Marshal.ReadInt32(buffer, offset + OffsetCacheSize);
                var kind = (CpuCacheKind)Marshal.ReadInt32(buffer, offset + OffsetCacheType);

                if (level > 0 && bytes > 0)
                {
                    var key = (level, kind, bytes);
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }

                offset += size;
            }

            return counts
                .Select(entry => new CpuCacheGroup(entry.Key.Level, entry.Key.Kind, entry.Key.Bytes, entry.Value))
                .OrderBy(group => group.Level)
                // Daten vor Befehlen: „32 KB Daten + 32 KB Befehle" ist die
                // Reihenfolge, in der über L1 gesprochen wird.
                .ThenBy(group => group.Kind switch
                {
                    CpuCacheKind.Unified => 0,
                    CpuCacheKind.Data => 1,
                    CpuCacheKind.Instruction => 2,
                    _ => 3,
                })
                .ToList();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(
        int relationshipType, IntPtr buffer, ref int returnedLength);
}

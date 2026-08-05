using System.Runtime.InteropServices;

namespace ResMon.Core.Native;

/// <summary>Ein Instanzname samt zugehörigem Zählerwert aus einer Wildcard-Abfrage.</summary>
public readonly record struct PdhInstanceValue(string Instance, double Value);

/// <summary>Wie <see cref="PdhInstanceValue"/>, aber für ganzzahlige Zähler (Bytes, IDs).</summary>
public readonly record struct PdhInstanceValueL(string Instance, long Value);

internal static class Pdh
{
    private const string Dll = "pdh.dll";

    internal const uint PDH_FMT_DOUBLE = 0x00000200;
    internal const uint PDH_FMT_LARGE = 0x00000400;
    internal const uint PDH_FMT_NOCAP100 = 0x00008000;

    internal const uint PDH_CSTATUS_VALID_DATA = 0x00000000;
    internal const uint PDH_CSTATUS_NEW_DATA = 0x00000001;
    internal const uint PDH_MORE_DATA = 0x800007D2;

    [DllImport(Dll, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint PdhOpenQueryW(string? szDataSource, IntPtr dwUserData, out IntPtr phQuery);

    [DllImport(Dll, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint PdhAddEnglishCounterW(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern uint PdhCollectQueryData(IntPtr hQuery);

    [DllImport(Dll, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint PdhGetFormattedCounterArrayW(IntPtr hCounter, uint dwFormat, ref uint lpdwBufferSize, out uint lpdwItemCount, IntPtr ItemBuffer);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern uint PdhGetFormattedCounterValue(IntPtr hCounter, uint dwFormat, out uint lpdwType, out PDH_FMT_COUNTERVALUE_DOUBLE pValue);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern uint PdhGetFormattedCounterValue(IntPtr hCounter, uint dwFormat, out uint lpdwType, out PDH_FMT_COUNTERVALUE_LARGE pValue);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern uint PdhCloseQuery(IntPtr hQuery);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PDH_FMT_COUNTERVALUE_DOUBLE
    {
        public uint CStatus;
        public double doubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PDH_FMT_COUNTERVALUE_LARGE
    {
        public uint CStatus;
        public long largeValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PDH_FMT_COUNTERVALUE_ITEM_DOUBLE
    {
        public IntPtr szName;
        public PDH_FMT_COUNTERVALUE_DOUBLE FmtValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PDH_FMT_COUNTERVALUE_ITEM_LARGE
    {
        public IntPtr szName;
        public PDH_FMT_COUNTERVALUE_LARGE FmtValue;
    }

    internal static bool IsValueUsable(uint cStatus)
        => cStatus is PDH_CSTATUS_VALID_DATA or PDH_CSTATUS_NEW_DATA;
}

/// <summary>
/// Ein einzelner Zähler innerhalb einer <see cref="PdhQuery"/>. Zähler mit
/// Wildcard-Instanz (<c>(*)</c>) werden über die Array-Methoden gelesen.
/// </summary>
public sealed class PdhCounter
{
    private readonly IntPtr _handle;

    internal PdhCounter(IntPtr handle, string path)
    {
        _handle = handle;
        Path = path;
    }

    public string Path { get; }

    public bool TryGetDouble(out double value, bool noCap100 = false)
    {
        uint format = Pdh.PDH_FMT_DOUBLE | (noCap100 ? Pdh.PDH_FMT_NOCAP100 : 0);
        uint status = Pdh.PdhGetFormattedCounterValue(_handle, format, out _, out Pdh.PDH_FMT_COUNTERVALUE_DOUBLE raw);
        if (status != 0 || !Pdh.IsValueUsable(raw.CStatus))
        {
            value = 0;
            return false;
        }

        value = raw.doubleValue;
        return true;
    }

    public bool TryGetInt64(out long value)
    {
        uint status = Pdh.PdhGetFormattedCounterValue(_handle, Pdh.PDH_FMT_LARGE, out _, out Pdh.PDH_FMT_COUNTERVALUE_LARGE raw);
        if (status != 0 || !Pdh.IsValueUsable(raw.CStatus))
        {
            value = 0;
            return false;
        }

        value = raw.largeValue;
        return true;
    }

    /// <summary>Liest alle Instanzen eines Wildcard-Zählers als Fließkommawerte.</summary>
    public IReadOnlyList<PdhInstanceValue> ReadArrayDouble(bool noCap100 = false)
    {
        uint format = Pdh.PDH_FMT_DOUBLE | (noCap100 ? Pdh.PDH_FMT_NOCAP100 : 0);
        if (!TryReadArray(format, out IntPtr buffer, out uint itemCount))
            return [];

        try
        {
            int stride = Marshal.SizeOf<Pdh.PDH_FMT_COUNTERVALUE_ITEM_DOUBLE>();
            var result = new List<PdhInstanceValue>((int)itemCount);
            for (uint i = 0; i < itemCount; i++)
            {
                var item = Marshal.PtrToStructure<Pdh.PDH_FMT_COUNTERVALUE_ITEM_DOUBLE>(buffer + (int)(i * stride));
                if (!Pdh.IsValueUsable(item.FmtValue.CStatus))
                    continue;
                string? name = Marshal.PtrToStringUni(item.szName);
                if (name is null)
                    continue;
                result.Add(new PdhInstanceValue(name, item.FmtValue.doubleValue));
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Liest alle Instanzen eines Wildcard-Zählers als 64-Bit-Ganzzahlen.</summary>
    public IReadOnlyList<PdhInstanceValueL> ReadArrayInt64()
    {
        if (!TryReadArray(Pdh.PDH_FMT_LARGE, out IntPtr buffer, out uint itemCount))
            return [];

        try
        {
            int stride = Marshal.SizeOf<Pdh.PDH_FMT_COUNTERVALUE_ITEM_LARGE>();
            var result = new List<PdhInstanceValueL>((int)itemCount);
            for (uint i = 0; i < itemCount; i++)
            {
                var item = Marshal.PtrToStructure<Pdh.PDH_FMT_COUNTERVALUE_ITEM_LARGE>(buffer + (int)(i * stride));
                if (!Pdh.IsValueUsable(item.FmtValue.CStatus))
                    continue;
                string? name = Marshal.PtrToStringUni(item.szName);
                if (name is null)
                    continue;
                result.Add(new PdhInstanceValueL(name, item.FmtValue.largeValue));
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Ermittelt die nötige Puffergröße, belegt sie und liest das Array. Der Aufrufer
    /// muss <paramref name="buffer"/> im Erfolgsfall freigeben.
    /// </summary>
    private bool TryReadArray(uint format, out IntPtr buffer, out uint itemCount)
    {
        buffer = IntPtr.Zero;
        uint bufferSize = 0;
        uint status = Pdh.PdhGetFormattedCounterArrayW(_handle, format, ref bufferSize, out itemCount, IntPtr.Zero);
        if (status != Pdh.PDH_MORE_DATA || bufferSize == 0 || itemCount == 0)
            return false;

        buffer = Marshal.AllocHGlobal((int)bufferSize);
        status = Pdh.PdhGetFormattedCounterArrayW(_handle, format, ref bufferSize, out itemCount, buffer);
        if (status == 0)
            return true;

        Marshal.FreeHGlobal(buffer);
        buffer = IntPtr.Zero;
        itemCount = 0;
        return false;
    }
}

/// <summary>
/// Dünner Wrapper um eine PDH-Abfrage. Zähler werden ausschließlich über
/// <c>PdhAddEnglishCounterW</c> hinzugefügt, damit die Pfade unabhängig von der
/// Systemsprache funktionieren (siehe DESIGN.md §8.1).
/// </summary>
/// <remarks>Nicht threadsicher — pro Abfrage darf immer nur ein Takt laufen.</remarks>
public sealed class PdhQuery : IDisposable
{
    private IntPtr _query;
    private bool _primed;

    public PdhQuery()
    {
        uint status = Pdh.PdhOpenQueryW(null, IntPtr.Zero, out _query);
        if (status != 0)
            throw new InvalidOperationException($"PdhOpenQueryW fehlgeschlagen (0x{status:X8}).");
    }

    /// <summary>
    /// True, sobald mindestens zwei erfolgreiche <see cref="Collect"/>-Aufrufe
    /// erfolgt sind und Deltawerte damit belastbar sind.
    /// </summary>
    public bool HasUsableData => _primed;

    /// <summary>Fügt einen Zähler hinzu; gibt <c>null</c> zurück, wenn der Pfad auf diesem System nicht existiert.</summary>
    public PdhCounter? TryAddCounter(string englishPath)
    {
        uint status = Pdh.PdhAddEnglishCounterW(_query, englishPath, IntPtr.Zero, out IntPtr handle);
        return status != 0 ? null : new PdhCounter(handle, englishPath);
    }

    public PdhCounter AddCounter(string englishPath)
        => TryAddCounter(englishPath)
           ?? throw new InvalidOperationException($"PDH-Zähler nicht verfügbar: {englishPath}");

    /// <summary>
    /// Holt ein Sample. Liefert erst ab dem zweiten erfolgreichen Aufruf <c>true</c>,
    /// weil ratenbasierte Zähler ein Vorgänger-Sample benötigen.
    /// </summary>
    public bool Collect()
    {
        uint status = Pdh.PdhCollectQueryData(_query);
        if (status != 0)
            return false;

        if (!_primed)
        {
            _primed = true;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Verwirft das Aufwärm-Sample. Nötig, wenn eine Abfrage nach längerer Pause
    /// wieder aufgenommen wird — die Deltas wären sonst über die Pause gemittelt.
    /// </summary>
    public void ResetPriming() => _primed = false;

    public void Dispose()
    {
        if (_query == IntPtr.Zero)
            return;

        Pdh.PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }
}

using ResMon.Core.Model;
using ResMon.Core.Native;

namespace ResMon.Core.Sensors;

/// <summary>
/// Datenträgerdurchsatz gesamt aus <c>\PhysicalDisk(_Total)</c>. Physische
/// Datenträger statt logischer Laufwerke, damit gespiegelte oder gespannte
/// Volumes nicht doppelt zählen.
/// </summary>
public sealed class DiskSource
{
    private const string ReadPath = @"\PhysicalDisk(_Total)\Disk Read Bytes/sec";
    private const string WritePath = @"\PhysicalDisk(_Total)\Disk Write Bytes/sec";
    private const string IdlePath = @"\PhysicalDisk(_Total)\% Idle Time";

    private readonly PdhCounter? _read;
    private readonly PdhCounter? _write;
    private readonly PdhCounter? _idle;

    public DiskSource(PdhQuery query)
    {
        _read = query.TryAddCounter(ReadPath);
        _write = query.TryAddCounter(WritePath);
        _idle = query.TryAddCounter(IdlePath);
    }

    public bool Available => _read is not null && _write is not null;

    public DiskMetrics Read()
    {
        if (!Available)
            return DiskMetrics.Empty;

        double read = 0, write = 0, idle = 100;
        _read!.TryGetDouble(out read, noCap100: true);
        _write!.TryGetDouble(out write, noCap100: true);
        _idle?.TryGetDouble(out idle);

        // Der Task-Manager zeigt "aktive Zeit" — das ist das Gegenstück zur
        // Leerlaufzeit, die PDH liefert.
        double busy = Math.Clamp(100 - idle, 0, 100);
        return new DiskMetrics(Math.Max(0, read), Math.Max(0, write), busy, Available: true);
    }
}

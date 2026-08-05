namespace ResMon.Core.Model;

/// <summary>Eine Zeile der Prozesstabelle im Detailfenster.</summary>
public sealed record ProcessSample(
    int Pid,
    int? ParentPid,
    string Name,
    string? Description,
    double CpuPercent,
    long WorkingSetBytes,
    long PrivateBytes,
    double GpuPercent,
    IReadOnlyDictionary<string, double> GpuByEngineType,
    long GpuMemBytes,
    IReadOnlyList<string> ServiceNames,
    double NetReceivedBytesPerSec,
    double NetSentBytesPerSec);

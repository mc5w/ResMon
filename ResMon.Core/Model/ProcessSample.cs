namespace ResMon.Core.Model;

/// <summary>
/// Einteilung der Prozesstabelle, wie sie auch der Task-Manager vornimmt: was der
/// Benutzer vor sich sieht, was für ihn im Hintergrund läuft, und was zu Windows
/// selbst gehört.
/// </summary>
public enum ProcessCategory
{
    /// <summary>Läuft unter einem Benutzerkonto und hat ein sichtbares Fenster.</summary>
    App,

    /// <summary>Läuft unter einem Benutzerkonto, aber ohne Oberfläche.</summary>
    Background,

    /// <summary>Läuft unter einem Systemkonto oder gibt sein Token nicht heraus.</summary>
    Windows,
}

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
    double NetSentBytesPerSec,
    double IoReadBytesPerSec,
    double IoWriteBytesPerSec,
    string? ImagePath,
    int ThreadCount)
{
    /// <summary>
    /// Konto, unter dem der Prozess läuft, in der Schreibweise des Systems
    /// (<c>DOMÄNE\Benutzer</c>). <c>null</c>, wenn der Prozess sein Token nicht
    /// herausgibt — bei geschützten Systemprozessen der Normalfall.
    /// </summary>
    public string? UserName { get; init; }

    public ProcessCategory Category { get; init; } = ProcessCategory.Windows;

    /// <summary>Titel des vordersten Fensters, sofern der Prozess eines hat.</summary>
    public string? WindowTitle { get; init; }

    /// <summary>
    /// True, wenn ein Fenster des Prozesses keine Nachrichten mehr abholt.
    /// Windows schreibt in diesem Fall „(Keine Rückmeldung)" in die Titelleiste.
    /// </summary>
    public bool NotResponding { get; init; }

    /// <summary>
    /// Beschreibung eines gemeldeten Absturzes oder Hängers aus dem
    /// Anwendungsprotokoll, sofern es für diese Datei einen gibt.
    /// </summary>
    public string? FaultNote { get; init; }

    /// <summary>TCP-Ports, auf denen der Prozess auf Verbindungen wartet.</summary>
    public IReadOnlyList<int> ListeningTcpPorts { get; init; } = [];

    /// <summary>UDP-Ports, die der Prozess gebunden hat.</summary>
    public IReadOnlyList<int> ListeningUdpPorts { get; init; } = [];

    /// <summary>Zahl der TCP-Verbindungen, die gerade nicht im Zustand „Abhören" sind.</summary>
    public int ConnectionCount { get; init; }
}

using System.IO;

namespace ResMon.Core.Inventory;

/// <summary>
/// Nur die Kapazität der logischen Laufwerke — ohne die Systemübersicht drumherum.
/// </summary>
/// <remarks>
/// <see cref="SystemInfoProvider"/> liefert dieselben Angaben, aber erst nach
/// mehreren WMI-Abfragen für Modell, Schnittstelle und Medientyp; das dauert
/// einige hundert Millisekunden und gehört deshalb hinter einen Knopf. Der freie
/// Platz allein kostet je Laufwerk einen <c>GetDiskFreeSpaceEx</c> hinter
/// <see cref="DriveInfo"/> — wenige Mikrosekunden, weil Windows die Angabe im
/// Dateisystem mitführt und nicht nachzählt.
/// <para>
/// Damit ist er billig genug für einen Takt, und genau darum geht es: wer
/// aufräumt, will den freien Platz wachsen sehen, ohne die Partition erneut zu
/// durchlaufen.
/// </para>
/// </remarks>
public static class VolumeSpace
{
    /// <summary>
    /// Liest die Laufwerke, die der Reiter „Speicher" zur Wahl stellt.
    /// </summary>
    /// <remarks>
    /// Dieselbe Auswahl wie in der Systemübersicht: fest eingebaute und
    /// Wechseldatenträger. Netzlaufwerke bleiben draußen — eine Abfrage über eine
    /// hängende Freigabe blockiert bis zum Zeitlimit des Redirectors, und im Takt
    /// wäre das ein Aussetzer je Runde.
    /// </remarks>
    public static IReadOnlyList<VolumeInfo> Read()
    {
        var volumes = new List<VolumeInfo>();

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady)
                    continue;

                volumes.Add(new VolumeInfo(
                    drive.Name.TrimEnd('\\'),
                    string.IsNullOrWhiteSpace(drive.VolumeLabel) ? null : drive.VolumeLabel,
                    drive.DriveFormat,
                    drive.TotalSize,
                    drive.AvailableFreeSpace)
                {
                    Type = drive.DriveType,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Ein Laufwerk, das sich zwischen Auflistung und Abfrage entzieht —
                // eine ausgeworfene Karte etwa. Beim nächsten Takt ist es fort.
            }
        }

        return volumes;
    }
}

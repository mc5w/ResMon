using System.Management;

namespace ResMon.Core.Processes;

/// <summary>
/// Löst <c>svchost.exe</c>-PIDs zu den tatsächlich laufenden Diensten auf
/// (DESIGN.md §8.4). WMI ist für den 2-Sekunden-Takt zu langsam, deshalb wird das
/// Ergebnis 30 Sekunden gecacht und im Hintergrund erneuert.
/// </summary>
public sealed class ServiceResolver
{
    private const string Query = "SELECT Name, DisplayName, ProcessId FROM Win32_Service WHERE State = 'Running'";

    private readonly Lock _gate = new();
    private IReadOnlyDictionary<int, IReadOnlyList<string>> _cache =
        new Dictionary<int, IReadOnlyList<string>>();

    /// <summary>Der zuletzt erfolgreich gelesene Stand. Nie <c>null</c>, anfangs leer.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<string>> Current
    {
        get
        {
            lock (_gate)
                return _cache;
        }
    }

    public IReadOnlyList<string> ForPid(int pid)
        => Current.TryGetValue(pid, out IReadOnlyList<string>? names) ? names : [];

    /// <summary>Fragt WMI ab und tauscht den Cache aus. Blockiert — gehört auf einen Hintergrund-Takt.</summary>
    public void Refresh()
    {
        try
        {
            var byPid = new Dictionary<int, List<string>>();

            using var searcher = new ManagementObjectSearcher(new ObjectQuery(Query));
            using ManagementObjectCollection results = searcher.Get();

            foreach (ManagementBaseObject item in results)
            {
                using (item)
                {
                    if (item["ProcessId"] is not uint rawPid || rawPid == 0)
                        continue;

                    string? label = item["DisplayName"] as string ?? item["Name"] as string;
                    if (string.IsNullOrWhiteSpace(label))
                        continue;

                    int pid = (int)rawPid;
                    if (!byPid.TryGetValue(pid, out List<string>? names))
                        byPid[pid] = names = [];
                    names.Add(label);
                }
            }

            var snapshot = new Dictionary<int, IReadOnlyList<string>>(byPid.Count);
            foreach ((int pid, List<string> names) in byPid)
            {
                names.Sort(StringComparer.CurrentCultureIgnoreCase);
                snapshot[pid] = names;
            }

            lock (_gate)
                _cache = snapshot;
        }
        catch (ManagementException)
        {
            // WMI kann kurzzeitig nicht verfügbar sein. Alten Cache behalten,
            // statt die Dienstspalte leerzuräumen.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

using ResMon.Core.Storage;

namespace ResMon.App;

/// <summary>
/// Ein einzelner Ordner-Scan: Abbruchmarke, Aufgabe und Ergebnis in einem Stück.
/// </summary>
/// <remarks>
/// Die Sitzung ist zugleich ihre eigene Identität — startet die Seite einen
/// zweiten Lauf, wird der erste storniert, und sein Ergebnis erkennt sich am
/// Vergleich der Sitzungsobjekte als überholt. Dieselbe Sicherung wie bei der
/// Systemübersicht in <c>App.RefreshSystemInfo</c>.
/// </remarks>
internal sealed class FolderScanSession(string root, int scanId) : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly FolderScanner _scanner = new();

    public string Root { get; } = root;

    public int ScanId { get; } = scanId;

    /// <summary>Liegt erst vor, wenn <see cref="RunAsync"/> durch ist.</summary>
    public FolderScanResult? Result { get; private set; }

    public int Directories => _scanner.ScannedDirectories;

    public int Files => _scanner.ScannedFiles;

    public long Bytes => _scanner.ScannedBytes;

    public string CurrentPath => _scanner.CurrentPath;

    public Task<FolderScanResult> RunAsync()
        => Task.Factory.StartNew(
            () =>
            {
                FolderScanResult result = _scanner.Run(Root, _cancellation.Token);
                Result = result;
                return result;
            },
            // Der Token gehört nicht an StartNew: der Scanner behandelt den
            // Abbruch selbst und liefert den Teilbaum zurück. Hier gesetzt würde
            // die Aufgabe stattdessen als abgebrochen enden und der Aufrufer
            // müsste eine Ausnahme fangen, um an ein Ergebnis zu kommen, das es
            // längst gibt.
            CancellationToken.None,
            // Eigener Thread statt Threadpool — der Aufruf blockiert bis zum
            // Ende des Laufs, und der Pool hat im Sekundentakt anderes zu tun.
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    public void Cancel()
    {
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Schon abgeräumt; das Ziel ist erreicht.
        }
    }

    public void Dispose() => _cancellation.Dispose();
}

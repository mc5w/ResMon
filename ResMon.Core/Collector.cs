using ResMon.Core.Config;
using ResMon.Core.Inventory;
using ResMon.Core.Model;
using ResMon.Core.Native;
using ResMon.Core.Processes;
using ResMon.Core.Sensors;

namespace ResMon.Core;

/// <summary>
/// Bündelt alle Datenquellen und treibt die Takte aus DESIGN.md §9. Veröffentlicht
/// nach jedem Aggregat-Takt ein <see cref="SystemSnapshot"/>.
/// </summary>
public sealed class Collector : IDisposable
{
    /// <summary>300 Einträge = 5 Minuten bei 1 Hz (DESIGN.md §10).</summary>
    public const int HistoryCapacity = 300;

    private static readonly IReadOnlyList<ProcessSample> NoProcesses = [];
    private static readonly IReadOnlyList<NetConnection> NoConnections = [];

    private readonly AppSettings _settings;
    private readonly PdhQuery _aggregateQuery = new();
    private readonly CounterSource _counters;
    private readonly GpuEngineSource _gpu;
    private readonly NetworkSource _network;
    private readonly DiskSource _disk;
    private readonly HardwareSource _hardware = new();
    private readonly ServiceResolver _services = new();
    private readonly AppErrorLog _faults = new();
    private readonly ProcessSampler _processes;
    private readonly NetworkTracer _networkTracer = new();

    private readonly Lock _aggregateGate = new();
    private readonly Lock _processGate = new();

    private Timer? _aggregateTimer;
    private Timer? _hardwareTimer;
    private Timer? _processTimer;
    private Timer? _serviceTimer;

    private volatile HardwareReading _lastHardware = HardwareReading.Empty;
    private volatile IReadOnlyList<ProcessSample> _lastProcesses = NoProcesses;
    private volatile IReadOnlyList<NetConnection> _lastConnections = NoConnections;
    private IReadOnlyDictionary<int, GpuProcessUsage> _lastGpuByPid = new Dictionary<int, GpuProcessUsage>();

    private int _aggregateBusy;
    private int _hardwareBusy;
    private int _processBusy;
    private int _serviceBusy;
    private bool _processSamplingEnabled;
    private bool _running;

    public Collector(AppSettings settings)
    {
        _settings = settings;
        _counters = new CounterSource(_aggregateQuery);
        _gpu = new GpuEngineSource(_aggregateQuery);
        _network = new NetworkSource(_aggregateQuery);
        _disk = new DiskSource(_aggregateQuery);
        _processes = new ProcessSampler(_services, _faults);
        History = new RingBuffer<AggregateSample>(HistoryCapacity);

        // WMI ist langsam; die Übersicht wird einmalig im Hintergrund erhoben.
        SystemInfoReady = Task.Run(SystemInfoProvider.Collect);
    }

    /// <summary>Wird nach jedem Aggregat-Takt im Timer-Thread ausgelöst.</summary>
    public event Action<SystemSnapshot>? SnapshotReady;

    public RingBuffer<AggregateSample> History { get; }

    /// <summary>Fehlermeldung, falls der Sensor-Treiber nicht geladen werden konnte.</summary>
    public string? HardwareError => _hardware.OpenError;

    public bool GpuCountersAvailable => _gpu.Available;

    public bool NetworkCountersAvailable => _network.Available;

    public bool DiskCountersAvailable => _disk.Available;

    public bool ProcessCountersAvailable => _processes.Available;

    /// <summary>True, wenn <c>Process V2</c> fehlt und der ältere Zählersatz greift.</summary>
    public bool UsesLegacyProcessCounters => _processes.UsesLegacyCounterSet;

    /// <summary>Läuft beim ersten Start im Hintergrund; WMI braucht dafür einen Moment.</summary>
    public Task<SystemInfo> SystemInfoReady { get; }

    /// <summary>
    /// Fehlermeldung, falls die ETW-Sitzung für den Netzverkehr pro Prozess nicht
    /// startet. Die übrigen Spalten funktionieren dann weiter.
    /// </summary>
    public string? NetworkTraceError => _networkTracer.Error;

    /// <summary>
    /// True, wenn CPU-Sensoren zwar existieren, aber konstant 0 melden — typisch
    /// für einen von der Speicherintegrität blockierten WinRing0-Treiber.
    /// </summary>
    public bool CpuSensorsBlocked => _lastHardware.CpuSensorsBlocked;

    /// <summary>
    /// True, wenn der Super-I/O-Chip des Mainboards nicht erreichbar ist —
    /// Sockeltemperatur und Gehäuselüfter fehlen dann. Erst nach dem ersten
    /// Hardware-Takt aussagekräftig.
    /// </summary>
    public bool BoardSensorsMissing => !_lastHardware.BoardSensorsAvailable;

    /// <summary>
    /// Steuert die Prozess-Enumeration. Bleibt aus, solange das Detailfenster
    /// geschlossen ist — sie ist der teuerste Teil der Erfassung (DESIGN.md §9).
    /// </summary>
    public bool ProcessSamplingEnabled
    {
        get => _processSamplingEnabled;
        set
        {
            if (_processSamplingEnabled == value)
                return;

            _processSamplingEnabled = value;
            if (!_running)
                return;

            if (value)
            {
                // Der Prozess-Takt könnte gerade laufen — Restart() greift auf
                // dieselbe PDH-Abfrage zu.
                lock (_processGate)
                    _processes.Restart();

                // Das Aufsetzen der ETW-Sitzung dauert einige hundert Millisekunden
                // und würde das Öffnen des Detailfensters spürbar verzögern.
                _ = Task.Run(_networkTracer.Start);

                _serviceTimer?.Change(0, _settings.Intervals.ServiceMs);
                _processTimer?.Change(0, _settings.Intervals.ProcessMs);
            }
            else
            {
                _processTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _serviceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _networkTracer.Stop();
                _lastProcesses = NoProcesses;
                _lastConnections = NoConnections;
            }
        }
    }

    public void Start()
    {
        if (_running)
            return;

        _running = true;
        _hardware.Open();

        IntervalSettings intervals = _settings.Intervals;
        _aggregateTimer = new Timer(OnAggregateTick, null, 0, intervals.AggregateMs);
        _hardwareTimer = new Timer(OnHardwareTick, null, 0, intervals.HardwareMs);
        _processTimer = new Timer(OnProcessTick, null, Timeout.Infinite, Timeout.Infinite);
        _serviceTimer = new Timer(OnServiceTick, null, Timeout.Infinite, Timeout.Infinite);

        if (_processSamplingEnabled)
        {
            _serviceTimer.Change(0, intervals.ServiceMs);
            _processTimer.Change(0, intervals.ProcessMs);
        }
    }

    public void Stop()
    {
        _running = false;
        _aggregateTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _hardwareTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _processTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _serviceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void OnAggregateTick(object? state)
    {
        // Überspringen statt anstauen, falls ein Takt länger dauert als sein Intervall.
        if (Interlocked.Exchange(ref _aggregateBusy, 1) == 1)
            return;

        try
        {
            CounterReading counters;
            GpuEngineReading gpu;
            NetworkMetrics network;
            DiskMetrics disk;
            lock (_aggregateGate)
            {
                // Das erste Sample nach dem Start liefert keine Deltas und wird verworfen.
                if (!_aggregateQuery.Collect())
                    return;

                counters = _counters.Read();
                gpu = _gpu.Read();
                network = _network.Read();
                disk = _disk.Read();
            }

            _lastGpuByPid = gpu.ByProcess;

            HardwareReading hardware = _lastHardware;
            var cpu = new CpuMetrics(
                counters.CpuTotalPercent,
                counters.CpuPerCorePercent,
                // Ohne Die-Temperatur ist die Sockeltemperatur die beste Auskunft,
                // die es gibt — sie kommt vom Mainboard und überlebt einen
                // gesperrten Sensortreiber.
                hardware.CpuPackageTempC ?? hardware.CpuSocketTempC,
                hardware.CpuClockMhz,
                hardware.CpuPackagePowerW)
            {
                SocketTempC = hardware.CpuSocketTempC,
            };

            // Fällt der PDH-Zählersatz aus, springt die LHM-Last als Ersatz ein.
            double gpuPercent = _gpu.Available ? gpu.TotalPercent : hardware.GpuLoadPercent ?? 0;
            var gpuMetrics = new GpuMetrics(
                gpuPercent,
                gpu.ByEngineType,
                hardware.GpuTempC,
                hardware.GpuMemUsedBytes,
                hardware.GpuMemTotalBytes,
                hardware.GpuFanRpm,
                hardware.GpuPowerW)
            {
                Available = _gpu.Available || hardware.GpuLoadPercent is not null || hardware.GpuTempC is not null,
            };

            var memory = new MemoryMetrics(
                counters.MemoryUsedBytes,
                counters.MemoryTotalBytes,
                counters.CommittedBytes,
                counters.MemoryPercent);

            var energy = new EnergyMetrics(
                hardware.CpuPackagePowerW,
                hardware.GpuPowerW,
                hardware.Rails,
                hardware.Fans,
                hardware.Battery)
            {
                Temperatures = hardware.Temperatures,
            };

            var timestamp = DateTime.Now;
            History.Add(new AggregateSample(
                timestamp,
                cpu.TotalPercent,
                gpuMetrics.TotalPercent,
                memory.Percent,
                cpu.PackageTempC,
                gpuMetrics.TempC,
                network.ReceivedBytesPerSec,
                network.SentBytesPerSec,
                disk.ReadBytesPerSec,
                disk.WriteBytesPerSec)
            {
                CpuPowerW = energy.CpuPackagePowerW ?? 0,
                GpuPowerW = energy.GpuPowerW ?? 0,
            });

            SnapshotReady?.Invoke(new SystemSnapshot(
                timestamp, cpu, gpuMetrics, memory, network, disk, _lastProcesses)
            {
                // Zwei int-Felder aus dem Prozess-Takt; für die Anzeige ist ein
                // Takt Versatz belanglos, deshalb ohne Sperre gelesen.
                ProcessCount = _processes.ProcessCount,
                ThreadCount = _processes.ThreadCount,
                Energy = energy,
                Connections = _lastConnections,
            });
        }
        finally
        {
            Interlocked.Exchange(ref _aggregateBusy, 0);
        }
    }

    private void OnHardwareTick(object? state)
    {
        if (Interlocked.Exchange(ref _hardwareBusy, 1) == 1)
            return;

        try
        {
            _lastHardware = _hardware.Update();
        }
        finally
        {
            Interlocked.Exchange(ref _hardwareBusy, 0);
        }
    }

    private void OnProcessTick(object? state)
    {
        if (Interlocked.Exchange(ref _processBusy, 1) == 1)
            return;

        try
        {
            // Die Verbindungstabelle liefert zugleich die Ports je Prozess; sie
            // steht deshalb vor der Prozesserfassung.
            IReadOnlyList<NetConnection> connections = ConnectionSource.Read();
            _lastConnections = connections;

            IReadOnlyList<ProcessSample> samples;
            lock (_processGate)
            {
                samples = _processes.Sample(
                    _lastGpuByPid,
                    _networkTracer.Read(),
                    ConnectionSource.ByProcess(connections));
                _networkTracer.Prune(_processes.LivePids);
            }

            // Leere Liste heißt "Aufwärm-Sample" — den vorherigen Stand behalten.
            if (samples.Count > 0)
                _lastProcesses = samples;
        }
        finally
        {
            Interlocked.Exchange(ref _processBusy, 0);
        }
    }

    private void OnServiceTick(object? state)
    {
        if (Interlocked.Exchange(ref _serviceBusy, 1) == 1)
            return;

        try
        {
            _services.Refresh();

            // Beides ist zu langsam für den Prozess-Takt und ändert sich selten;
            // der 30-Sekunden-Takt trägt sie gemeinsam.
            _faults.Refresh();
        }
        finally
        {
            Interlocked.Exchange(ref _serviceBusy, 0);
        }
    }

    public void Dispose()
    {
        Stop();
        _aggregateTimer?.Dispose();
        _hardwareTimer?.Dispose();
        _processTimer?.Dispose();
        _serviceTimer?.Dispose();

        _networkTracer.Dispose();

        lock (_processGate)
            _processes.Dispose();
        lock (_aggregateGate)
            _aggregateQuery.Dispose();

        _hardware.Dispose();
    }
}

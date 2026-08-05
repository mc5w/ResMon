using ResMon.Core.Config;
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

    private readonly AppSettings _settings;
    private readonly PdhQuery _aggregateQuery = new();
    private readonly CounterSource _counters;
    private readonly GpuEngineSource _gpu;
    private readonly NetworkSource _network;
    private readonly HardwareSource _hardware = new();
    private readonly ServiceResolver _services = new();
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
        _processes = new ProcessSampler(_services);
        History = new RingBuffer<AggregateSample>(HistoryCapacity);
    }

    /// <summary>Wird nach jedem Aggregat-Takt im Timer-Thread ausgelöst.</summary>
    public event Action<SystemSnapshot>? SnapshotReady;

    public RingBuffer<AggregateSample> History { get; }

    /// <summary>Fehlermeldung, falls der Sensor-Treiber nicht geladen werden konnte.</summary>
    public string? HardwareError => _hardware.OpenError;

    public bool GpuCountersAvailable => _gpu.Available;

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
            lock (_aggregateGate)
            {
                // Das erste Sample nach dem Start liefert keine Deltas und wird verworfen.
                if (!_aggregateQuery.Collect())
                    return;

                counters = _counters.Read();
                gpu = _gpu.Read();
                network = _network.Read();
            }

            _lastGpuByPid = gpu.ByProcess;

            HardwareReading hardware = _lastHardware;
            var cpu = new CpuMetrics(
                counters.CpuTotalPercent,
                counters.CpuPerCorePercent,
                hardware.CpuPackageTempC,
                hardware.CpuClockMhz,
                hardware.CpuPackagePowerW);

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

            var timestamp = DateTime.Now;
            History.Add(new AggregateSample(
                timestamp,
                cpu.TotalPercent,
                gpuMetrics.TotalPercent,
                memory.Percent,
                cpu.PackageTempC,
                gpuMetrics.TempC,
                network.ReceivedBytesPerSec,
                network.SentBytesPerSec));

            SnapshotReady?.Invoke(new SystemSnapshot(timestamp, cpu, gpuMetrics, memory, network, _lastProcesses));
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
            IReadOnlyList<ProcessSample> samples;
            lock (_processGate)
            {
                samples = _processes.Sample(_lastGpuByPid, _networkTracer.Read());
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

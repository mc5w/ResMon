using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace ResMon.Core.Processes;

/// <summary>Netzdurchsatz eines Prozesses, gemittelt über das letzte Messfenster.</summary>
public readonly record struct NetworkUsage(double ReceivedBytesPerSec, double SentBytesPerSec);

/// <summary>
/// Netzverkehr je Prozess über eine Kernel-ETW-Sitzung. PDH kennt dafür keine
/// Zähler — ETW ist unter Windows der einzige Weg (DESIGN.md §18).
/// </summary>
/// <remarks>
/// Erfordert Administratorrechte. Die Sitzung läuft nur, solange das
/// Detailfenster offen ist, damit der Monitor im Leerlauf nichts kostet.
/// </remarks>
public sealed class NetworkTracer : IDisposable
{
    private const string SessionName = "ResMon-Network";

    private readonly ConcurrentDictionary<int, Counters> _totals = new();
    private readonly Lock _gate = new();

    private TraceEventSession? _session;
    private Thread? _pump;
    private Dictionary<int, CounterTotals> _previous = [];
    private long _previousTimestamp;

    /// <summary>Fehlermeldung, falls die ETW-Sitzung nicht gestartet werden konnte.</summary>
    public string? Error { get; private set; }

    public bool IsRunning => _session is not null;

    public void Start()
    {
        lock (_gate)
        {
            if (_session is not null)
                return;

            try
            {
                // Eine Sitzung gleichen Namens kann aus einem abgestürzten Lauf
                // übrig sein; TraceEvent übernimmt und startet sie dann neu.
                _session = new TraceEventSession(SessionName) { StopOnDispose = true };
                _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

                KernelTraceEventParser kernel = _session.Source.Kernel;
                kernel.TcpIpRecv += data => Add(data.ProcessID, received: data.size);
                kernel.TcpIpRecvIPV6 += data => Add(data.ProcessID, received: data.size);
                kernel.TcpIpSend += data => Add(data.ProcessID, sent: data.size);
                kernel.TcpIpSendIPV6 += data => Add(data.ProcessID, sent: data.size);
                kernel.UdpIpRecv += data => Add(data.ProcessID, received: data.size);
                kernel.UdpIpRecvIPV6 += data => Add(data.ProcessID, received: data.size);
                kernel.UdpIpSend += data => Add(data.ProcessID, sent: data.size);
                kernel.UdpIpSendIPV6 += data => Add(data.ProcessID, sent: data.size);

                // Process() blockiert bis zum Stop und braucht deshalb einen
                // eigenen Thread.
                _pump = new Thread(PumpEvents)
                {
                    IsBackground = true,
                    Name = "ResMon ETW",
                };
                _pump.Start();

                _previous = [];
                _previousTimestamp = 0;
                Error = null;
            }
            catch (Exception ex)
            {
                // Ohne Adminrechte oder bei blockierten Kernel-Sitzungen läuft die
                // Anwendung ohne Netzspalten weiter.
                Error = ex.Message;
                _session?.Dispose();
                _session = null;
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_session is null)
                return;

            try
            {
                _session.Source.StopProcessing();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
            }

            _session.Dispose();
            _session = null;
            _totals.Clear();
            _previous = [];
        }
    }

    /// <summary>
    /// Bildet aus den aufgelaufenen Bytesummen Raten. Der erste Aufruf nach
    /// <see cref="Start"/> liefert leere Werte, weil noch kein Vorgängerstand
    /// existiert.
    /// </summary>
    public IReadOnlyDictionary<int, NetworkUsage> Read()
    {
        var result = new Dictionary<int, NetworkUsage>();
        if (_session is null)
            return result;

        long now = Stopwatch.GetTimestamp();
        var current = _totals.ToDictionary(entry => entry.Key, entry => entry.Value.Snapshot());

        if (_previousTimestamp != 0)
        {
            double seconds = (now - _previousTimestamp) / (double)Stopwatch.Frequency;
            if (seconds > 0.05)
            {
                foreach ((int pid, CounterTotals total) in current)
                {
                    CounterTotals before = _previous.GetValueOrDefault(pid);
                    double received = Math.Max(0, total.Received - before.Received) / seconds;
                    double sent = Math.Max(0, total.Sent - before.Sent) / seconds;
                    if (received > 0 || sent > 0)
                        result[pid] = new NetworkUsage(received, sent);
                }
            }
        }

        _previous = current;
        _previousTimestamp = now;
        return result;
    }

    /// <summary>Entfernt Zähler beendeter Prozesse, damit die Tabelle nicht wächst.</summary>
    public void Prune(IReadOnlySet<int> livePids)
    {
        foreach (int pid in _totals.Keys)
        {
            if (!livePids.Contains(pid))
                _totals.TryRemove(pid, out _);
        }
    }

    private void PumpEvents()
    {
        try
        {
            _session?.Source.Process();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    private void Add(int pid, int received = 0, int sent = 0)
    {
        if (pid <= 0)
            return;

        Counters counters = _totals.GetOrAdd(pid, _ => new Counters());
        if (received > 0)
            Interlocked.Add(ref counters.Received, received);
        if (sent > 0)
            Interlocked.Add(ref counters.Sent, sent);
    }

    public void Dispose() => Stop();

    /// <summary>Aufgelaufene Bytesummen eines Prozesses zu einem Zeitpunkt.</summary>
    private readonly record struct CounterTotals(long Received, long Sent);

    /// <summary>
    /// Klasse statt Struct: die ETW-Rückrufe laufen auf einem eigenen Thread und
    /// aktualisieren die Felder per <see cref="Interlocked"/>.
    /// </summary>
    private sealed class Counters
    {
        public long Received;
        public long Sent;

        public CounterTotals Snapshot()
            => new(Interlocked.Read(ref Received), Interlocked.Read(ref Sent));
    }
}

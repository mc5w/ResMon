using ResMon.Core.Model;
using ResMon.Core.Native;

namespace ResMon.Core.Sensors;

/// <summary>
/// Netzdurchsatz gesamt aus <c>\Network Interface</c>. Pseudo-Adapter werden
/// herausgefiltert, sonst zählt lokaler Verkehr doppelt.
/// </summary>
public sealed class NetworkSource
{
    private const string ReceivedPath = @"\Network Interface(*)\Bytes Received/sec";
    private const string SentPath = @"\Network Interface(*)\Bytes Sent/sec";

    /// <summary>Loopback und Tunnel-Adapter tragen keinen echten Außenverkehr.</summary>
    private static readonly string[] IgnoredInstances =
    [
        "Loopback", "isatap", "Teredo", "Pseudo-Interface", "Filter", "QoS",
    ];

    private readonly PdhCounter? _received;
    private readonly PdhCounter? _sent;

    public NetworkSource(PdhQuery query)
    {
        _received = query.TryAddCounter(ReceivedPath);
        _sent = query.TryAddCounter(SentPath);
    }

    public bool Available => _received is not null && _sent is not null;

    public NetworkMetrics Read()
    {
        if (!Available)
            return NetworkMetrics.Empty;

        return new NetworkMetrics(SumRelevant(_received!), SumRelevant(_sent!), Available: true);
    }

    private static double SumRelevant(PdhCounter counter)
    {
        double total = 0;
        foreach (PdhInstanceValue value in counter.ReadArrayDouble(noCap100: true))
        {
            if (value.Value <= 0 || IsIgnored(value.Instance))
                continue;
            total += value.Value;
        }

        return total;
    }

    private static bool IsIgnored(string instance)
    {
        foreach (string marker in IgnoredInstances)
        {
            if (instance.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

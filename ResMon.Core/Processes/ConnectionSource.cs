using System.Net;
using System.Runtime.InteropServices;
using ResMon.Core.Diagnostics;

namespace ResMon.Core.Processes;

/// <summary>Eine offene TCP- oder UDP-Verbindung samt besitzendem Prozess.</summary>
/// <remarks>
/// UDP ist verbindungslos: dort steht immer nur die lokale Seite, und
/// <see cref="State"/> ist leer.
/// </remarks>
public sealed record NetConnection(
    string Protocol,
    string LocalAddress,
    int LocalPort,
    string? RemoteAddress,
    int? RemotePort,
    TcpConnectionState State,
    int Pid);

/// <summary>
/// Verbindungszustand nach RFC 793, wie ihn <c>MIB_TCPROW</c> meldet.
/// <see cref="None"/> steht für UDP.
/// </summary>
public enum TcpConnectionState
{
    None = 0,
    Closed = 1,
    Listen = 2,
    SynSent = 3,
    SynReceived = 4,
    Established = 5,
    FinWait1 = 6,
    FinWait2 = 7,
    CloseWait = 8,
    Closing = 9,
    LastAck = 10,
    TimeWait = 11,
    DeleteTcb = 12,
}

/// <summary>Die Ports eines Prozesses, verdichtet für die Prozesstabelle.</summary>
public readonly record struct ProcessPorts(
    IReadOnlyList<int> ListeningTcp,
    IReadOnlyList<int> ListeningUdp,
    int EstablishedCount)
{
    public static readonly ProcessPorts Empty = new([], [], 0);
}

/// <summary>
/// Liest die Verbindungstabellen des Systems über <c>iphlpapi.dll</c>. Dieselbe
/// Quelle wie <c>netstat -ano</c> — anders als der ETW-Durchsatz liefert sie
/// nicht Bytes, sondern wer gerade mit wem verbunden ist.
/// </summary>
/// <remarks>
/// Der Aufruf kostet unter einer Millisekunde und läuft im Prozess-Takt mit,
/// also nur bei geöffnetem Detailfenster.
/// </remarks>
public static class ConnectionSource
{
    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const int UDP_TABLE_OWNER_PID = 1;
    private const uint NO_ERROR = 0;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    /// <summary>
    /// Alle TCP- und UDP-Einträge beider Adressfamilien. Bei einem Fehler bleibt
    /// die jeweilige Tabelle leer, statt dass der ganze Takt ausfällt.
    /// </summary>
    public static IReadOnlyList<NetConnection> Read()
    {
        var result = new List<NetConnection>(512);
        ReadTcp(AF_INET, "TCP", result);
        ReadTcp(AF_INET6, "TCPv6", result);
        ReadUdp(AF_INET, "UDP", result);
        ReadUdp(AF_INET6, "UDPv6", result);
        return result;
    }

    /// <summary>
    /// Verdichtet die Verbindungen je Prozess: die Ports, auf denen er lauscht,
    /// und die Zahl der bestehenden Verbindungen.
    /// </summary>
    public static Dictionary<int, ProcessPorts> ByProcess(IReadOnlyList<NetConnection> connections)
    {
        var tcp = new Dictionary<int, SortedSet<int>>();
        var udp = new Dictionary<int, SortedSet<int>>();
        var established = new Dictionary<int, int>();

        foreach (NetConnection connection in connections)
        {
            if (connection.Pid <= 0)
                continue;

            bool isUdp = connection.State == TcpConnectionState.None;
            if (isUdp)
            {
                Add(udp, connection.Pid, connection.LocalPort);
            }
            else if (connection.State == TcpConnectionState.Listen)
            {
                Add(tcp, connection.Pid, connection.LocalPort);
            }
            else
            {
                established[connection.Pid] = established.GetValueOrDefault(connection.Pid) + 1;
            }
        }

        var pids = new HashSet<int>(tcp.Keys);
        pids.UnionWith(udp.Keys);
        pids.UnionWith(established.Keys);

        var result = new Dictionary<int, ProcessPorts>(pids.Count);
        foreach (int pid in pids)
        {
            result[pid] = new ProcessPorts(
                tcp.TryGetValue(pid, out SortedSet<int>? listening) ? [.. listening] : [],
                udp.TryGetValue(pid, out SortedSet<int>? bound) ? [.. bound] : [],
                established.GetValueOrDefault(pid));
        }

        return result;
    }

    private static void Add(Dictionary<int, SortedSet<int>> sink, int pid, int port)
    {
        if (!sink.TryGetValue(pid, out SortedSet<int>? ports))
            sink[pid] = ports = [];
        ports.Add(port);
    }

    private static void ReadTcp(int family, string protocol, List<NetConnection> sink)
    {
        IntPtr table = Allocate(family, tcp: true, out int rows);
        if (table == IntPtr.Zero)
            return;

        try
        {
            // Die Tabelle beginnt mit dwNumEntries, danach folgen die Zeilen dicht
            // gepackt.
            IntPtr cursor = table + sizeof(int);
            if (family == AF_INET)
            {
                int size = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                for (int i = 0; i < rows; i++, cursor += size)
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(cursor);
                    sink.Add(new NetConnection(
                        protocol,
                        Address(row.LocalAddr),
                        Port(row.LocalPort),
                        Address(row.RemoteAddr),
                        Port(row.RemotePort),
                        State(row.State),
                        (int)row.OwningPid));
                }
            }
            else
            {
                int size = Marshal.SizeOf<MIB_TCP6ROW_OWNER_PID>();
                for (int i = 0; i < rows; i++, cursor += size)
                {
                    var row = Marshal.PtrToStructure<MIB_TCP6ROW_OWNER_PID>(cursor);
                    sink.Add(new NetConnection(
                        protocol,
                        Address(row.LocalAddr, row.LocalScopeId),
                        Port(row.LocalPort),
                        Address(row.RemoteAddr, row.RemoteScopeId),
                        Port(row.RemotePort),
                        State(row.State),
                        (int)row.OwningPid));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    private static void ReadUdp(int family, string protocol, List<NetConnection> sink)
    {
        IntPtr table = Allocate(family, tcp: false, out int rows);
        if (table == IntPtr.Zero)
            return;

        try
        {
            IntPtr cursor = table + sizeof(int);
            if (family == AF_INET)
            {
                int size = Marshal.SizeOf<MIB_UDPROW_OWNER_PID>();
                for (int i = 0; i < rows; i++, cursor += size)
                {
                    var row = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(cursor);
                    sink.Add(new NetConnection(
                        protocol, Address(row.LocalAddr), Port(row.LocalPort),
                        null, null, TcpConnectionState.None, (int)row.OwningPid));
                }
            }
            else
            {
                int size = Marshal.SizeOf<MIB_UDP6ROW_OWNER_PID>();
                for (int i = 0; i < rows; i++, cursor += size)
                {
                    var row = Marshal.PtrToStructure<MIB_UDP6ROW_OWNER_PID>(cursor);
                    sink.Add(new NetConnection(
                        protocol, Address(row.LocalAddr, row.LocalScopeId), Port(row.LocalPort),
                        null, null, TcpConnectionState.None, (int)row.OwningPid));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    /// <summary>
    /// Holt die Tabelle in einen passend großen Puffer. Zwischen dem Ermitteln der
    /// Größe und dem Lesen können Verbindungen dazukommen — dann meldet der
    /// zweite Aufruf erneut „Puffer zu klein" und wir versuchen es mit der neuen
    /// Größe noch einmal. Der Aufrufer gibt den Puffer frei.
    /// </summary>
    private static IntPtr Allocate(int family, bool tcp, out int rows)
    {
        rows = 0;
        int size = 0;

        // Erst die nötige Größe erfragen. Mit einem Nullzeiger meldet die
        // Funktion immer ERROR_INSUFFICIENT_BUFFER, auch bei leerer Tabelle.
        uint status = Query(IntPtr.Zero, ref size, family, tcp);
        if (status != ERROR_INSUFFICIENT_BUFFER || size <= 0)
        {
            Report(family, tcp, status);
            return IntPtr.Zero;
        }

        for (int attempt = 0; attempt < 4; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            status = Query(buffer, ref size, family, tcp);
            if (status == NO_ERROR)
            {
                rows = Marshal.ReadInt32(buffer);
                return buffer;
            }

            Marshal.FreeHGlobal(buffer);

            // Zwischen Größenabfrage und Lesen können Verbindungen dazugekommen
            // sein; die Funktion hat size dann bereits nachgezogen.
            if (status != ERROR_INSUFFICIENT_BUFFER)
            {
                Report(family, tcp, status);
                return IntPtr.Zero;
            }
        }

        Report(family, tcp, status);
        return IntPtr.Zero;
    }

    /// <summary>
    /// Meldet eine nicht gelesene Tabelle. Die Zeile im Reiter „Logs" ist die
    /// einzige Spur, die dieser Fehler sonst hinterlässt: eine leere Tabelle
    /// sieht aus wie ein Rechner ohne Verbindungen.
    /// </summary>
    private static void Report(int family, bool tcp, uint status)
        => DiagnosticLog.Report(
            "Verbindungstabelle",
            $"{(tcp ? "GetExtendedTcpTable" : "GetExtendedUdpTable")} für " +
            $"{(family == AF_INET ? "IPv4" : "IPv6")} meldete Status 0x{status:X8} — " +
            "diese Einträge fehlen in der Tabelle und in der Portübersicht.");

    private static uint Query(IntPtr buffer, ref int size, int family, bool tcp)
        => tcp
            ? GetExtendedTcpTable(buffer, ref size, false, family, TCP_TABLE_OWNER_PID_ALL, 0)
            : GetExtendedUdpTable(buffer, ref size, false, family, UDP_TABLE_OWNER_PID, 0);

    /// <summary>Die Portnummer steht in den unteren zwei Bytes in Netzwerk-Byteordnung.</summary>
    private static int Port(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));

    private static string Address(uint raw) => new IPAddress(raw).ToString();

    private static string Address(byte[] raw, uint scopeId)
    {
        try
        {
            return new IPAddress(raw, scopeId).ToString();
        }
        catch (ArgumentException)
        {
            return "::";
        }
    }

    private static TcpConnectionState State(uint raw)
        => raw is >= 1 and <= 12 ? (TcpConnectionState)raw : TcpConnectionState.None;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool sorted,
        int family, int tableClass, int reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool sorted,
        int family, int tableClass, int reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;

        public uint LocalScopeId;
        public uint LocalPort;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddr;

        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID
    {
        public uint LocalAddr;
        public uint LocalPort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;

        public uint LocalScopeId;
        public uint LocalPort;
        public uint OwningPid;
    }
}

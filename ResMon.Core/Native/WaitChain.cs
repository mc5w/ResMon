using System.Runtime.InteropServices;
using ResMon.Core.Diagnostics;

namespace ResMon.Core.Native;

/// <summary>Worauf ein Glied der Wartekette wartet.</summary>
public enum WaitObjectType
{
    CriticalSection = 1,
    SendMessage = 2,
    Mutex = 3,
    Alpc = 4,
    Com = 5,
    ThreadWait = 6,
    ProcessWait = 7,
    Thread = 8,
    ComActivation = 9,
    Unknown = 10,
}

/// <summary>In welchem Zustand ein Glied der Kette ist.</summary>
public enum WaitObjectStatus
{
    NoAccess = 1,
    Running = 2,
    Blocked = 3,
    PidOnly = 4,
    PidOnlyRpcss = 5,
    Owned = 6,
    NotOwned = 7,
    Abandoned = 8,
    Unknown = 9,
    Error = 10,
}

/// <summary>Ein Glied einer Wartekette.</summary>
/// <param name="Type">Die Art des Objekts — Thread, Sperre, COM-Aufruf.</param>
/// <param name="Status">Was das Objekt gerade tut.</param>
public sealed record WaitNode(WaitObjectType Type, WaitObjectStatus Status)
{
    /// <summary>Bei einem Thread-Glied: der besitzende Prozess.</summary>
    public int ProcessId { get; init; }

    public int ThreadId { get; init; }

    /// <summary>Wie lange der Thread schon wartet, in Millisekunden.</summary>
    public long WaitMilliseconds { get; init; }

    public int ContextSwitches { get; init; }

    /// <summary>Bei einem Sperr-Glied: der Name des Objekts, sofern es einen hat.</summary>
    public string? ObjectName { get; init; }
}

/// <summary>Die Wartekette eines Threads.</summary>
/// <param name="ThreadId">Der Thread, von dem aus gefragt wurde.</param>
/// <param name="Nodes">Die Kette, beginnend beim gefragten Thread.</param>
/// <param name="IsCycle">Ob die Kette einen Ring bildet — eine echte Verklemmung.</param>
public sealed record WaitChainResult(int ThreadId, IReadOnlyList<WaitNode> Nodes, bool IsCycle);

/// <summary>
/// Wartekettenanalyse über <c>GetThreadWaitChain</c> — dieselbe Funktion, die im
/// Task-Manager hinter „Wartekette analysieren“ steckt.
/// </summary>
/// <remarks>
/// Beantwortet die Frage, die eine Prozessliste nicht beantworten kann: ein
/// Prozess mit 0 % CPU-Last kann beschäftigt sein oder blockiert, und von außen
/// sieht beides gleich aus. Die Kette sagt, <b>worauf</b> er wartet und
/// <b>wer</b> ihn hält — über Prozessgrenzen hinweg und quer durch
/// kritische Abschnitte, Mutexe, ALPC-Anfragen und COM-Aufrufe.
/// <para>
/// Für die Startanalyse ist das die Live-Ergänzung: was beim letzten Start in ein
/// Zeitlimit lief, steht im Protokoll — was <i>jetzt gerade</i> hängt, steht
/// nirgends und ist nur hier zu sehen. Ein Ring in der Kette
/// (<see cref="WaitChainResult.IsCycle"/>) ist eine echte Verklemmung; die löst
/// sich nicht mehr von allein.
/// </para>
/// <para>
/// Threads fremder Prozesse verlangen <c>SeDebugPrivilege</c>; die Anwendung
/// läuft erhöht, das Recht muss aber trotzdem eigens eingeschaltet werden
/// (<see cref="ProcessPrivileges"/>). Ohne es liefert die Abfrage ein Glied mit
/// dem Status <see cref="WaitObjectStatus.NoAccess"/> statt einer Kette.
/// </para>
/// </remarks>
public static class WaitChain
{
    /// <summary>Die Kette wird nach so vielen Gliedern abgeschnitten (WCT_MAX_NODE_COUNT).</summary>
    private const int MaxNodes = 16;

    private const int OutOfProcess = 0x1;
    private const int OutOfProcessCom = 0x2;
    private const int OutOfProcessCriticalSection = 0x4;

    private const int ObjectNameLength = 128;

    /// <summary>
    /// Die Wartekette eines Threads, oder <c>null</c>, wenn sie sich nicht
    /// ermitteln ließ.
    /// </summary>
    public static WaitChainResult? For(int threadId)
    {
        nint session = OpenThreadWaitChainSession(
            OutOfProcess | OutOfProcessCom | OutOfProcessCriticalSection, nint.Zero);

        if (session == nint.Zero)
            return null;

        try
        {
            var nodes = new WaitChainNode[MaxNodes];
            int count = MaxNodes;

            if (!GetThreadWaitChain(session, nint.Zero, 0, (uint)threadId, ref count, nodes, out bool cycle))
                return null;

            var result = new List<WaitNode>(count);
            for (int i = 0; i < Math.Min(count, MaxNodes); i++)
                result.Add(Convert(nodes[i]));

            return new WaitChainResult(threadId, result, cycle);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            DiagnosticLog.Report("Wartekette", ex, "GetThreadWaitChain steht nicht zur Verfügung");
            return null;
        }
        finally
        {
            CloseThreadWaitChainSession(session);
        }
    }

    /// <summary>
    /// Die aussagekräftigste Wartekette eines Prozesses: die des Threads, der am
    /// längsten wartet.
    /// </summary>
    /// <remarks>
    /// Ein Prozess hat Dutzende Threads, und die meisten warten völlig
    /// regelkonform auf ihre nächste Nachricht. Interessant ist der, dessen
    /// Kette am weitesten reicht — er ist der, der irgendwo festhängt. Ketten mit
    /// nur einem Glied fallen deshalb heraus: ein Thread, der auf nichts
    /// Benennbares wartet, ist keine Antwort.
    /// </remarks>
    public static WaitChainResult? ForProcess(int pid)
    {
        WaitChainResult? best = null;

        foreach (int threadId in Toolhelp.ThreadsOf(pid))
        {
            WaitChainResult? chain = For(threadId);
            if (chain is null || chain.Nodes.Count < 2)
                continue;

            if (chain.IsCycle)
                return chain;

            if (best is null || chain.Nodes.Count > best.Nodes.Count)
                best = chain;
        }

        return best;
    }

    private static WaitNode Convert(WaitChainNode node)
    {
        var type = (WaitObjectType)node.ObjectType;
        var status = (WaitObjectStatus)node.ObjectStatus;

        // Die Struktur ist eine Union: bei einem Thread-Glied stehen dort PID,
        // TID und Wartezeit, bei allen anderen der Objektname. Dieselben Bytes
        // anders zu lesen wäre kein Fehler, den der Compiler bemerkt — deshalb
        // entscheidet die Art und nicht der Inhalt.
        if (node.Union is not { Length: >= 16 } union)
            return new WaitNode(type, status);

        if (type == WaitObjectType.Thread)
        {
            return new WaitNode(type, status)
            {
                ProcessId = BitConverter.ToInt32(union, 0),
                ThreadId = BitConverter.ToInt32(union, 4),
                WaitMilliseconds = BitConverter.ToUInt32(union, 8),
                ContextSwitches = BitConverter.ToInt32(union, 12),
            };
        }

        string name = System.Text.Encoding.Unicode
            .GetString(union, 0, ObjectNameLength * 2)
            .TrimEnd('\0');

        return new WaitNode(type, status) { ObjectName = name.Length > 0 ? name : null };
    }

    /// <summary>
    /// Die native Struktur. Der Union-Teil bleibt bewusst ein Bytefeld.
    /// </summary>
    /// <remarks>
    /// <c>WAITCHAIN_NODE_INFO</c> endet in einer Union aus einem Sperrobjekt
    /// (Name, Zeitlimit, Wecksignal) und einem Threadobjekt (vier Zahlen). Als
    /// <c>LayoutKind.Explicit</c> abgebildet bräuchte der Namensteil ein
    /// <c>fixed char</c>-Feld und damit <c>unsafe</c> für das ganze Projekt —
    /// eine Zeichenkette als überlagertes <c>ByValTStr</c> lehnt die Laufzeit ab,
    /// weil ein Verweisfeld nicht mit Wertfeldern überlappen darf. Die Bytes
    /// selbst zu deuten ist der kleinere Eingriff, und welche Deutung gilt,
    /// entscheidet ohnehin <c>ObjectType</c> und nicht der Typ des Feldes.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct WaitChainNode
    {
        public int ObjectType;
        public int ObjectStatus;

        /// <summary>256 Byte Name, 8 Byte Zeitlimit, 4 Byte Wecksignal, auf 8 aufgefüllt.</summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 272)]
        public byte[] Union;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern nint OpenThreadWaitChainSession(int flags, nint callback);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadWaitChain(
        nint session,
        nint context,
        int flags,
        uint threadId,
        ref int nodeCount,
        [Out] WaitChainNode[] nodeInfoArray,
        [MarshalAs(UnmanagedType.Bool)] out bool isCycle);

    [DllImport("advapi32.dll")]
    private static extern void CloseThreadWaitChainSession(nint session);
}

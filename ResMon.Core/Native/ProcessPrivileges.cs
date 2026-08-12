using System.Runtime.InteropServices;
using ResMon.Core.Diagnostics;

namespace ResMon.Core.Native;

/// <summary>
/// Schaltet Rechte im eigenen Prozesstoken ein, die ein erhöhter Lauf zwar
/// besitzt, aber nicht von sich aus aktiviert.
/// </summary>
/// <remarks>
/// Ein Administratortoken trägt <c>SeDebugPrivilege</c> im Zustand „vorhanden,
/// aber abgeschaltet“. Windows aktiviert es nicht von allein — das ist Absicht:
/// ein Recht, das jeden fremden Prozess öffnet, soll nicht versehentlich in
/// jedem Aufruf mitlaufen. Ohne es liefern Wartekettenanalyse und Handle-Abfrage
/// für fremde Prozesse nur „kein Zugriff“, und zwar ohne Fehlermeldung.
/// </remarks>
public static class ProcessPrivileges
{
    private const string DebugPrivilege = "SeDebugPrivilege";

    private const int TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const int TOKEN_QUERY = 0x0008;
    private const int SE_PRIVILEGE_ENABLED = 0x0002;

    private static bool? _debugEnabled;

    /// <summary>
    /// Schaltet <c>SeDebugPrivilege</c> ein. Das Ergebnis wird gemerkt — der
    /// Zustand ändert sich zu Lebzeiten des Prozesses nicht mehr.
    /// </summary>
    public static bool EnableDebug() => _debugEnabled ??= Enable(DebugPrivilege);

    private static bool Enable(string privilege)
    {
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out nint token))
                return false;

            try
            {
                if (!LookupPrivilegeValue(null, privilege, out long luid))
                    return false;

                var state = new TokenPrivileges
                {
                    PrivilegeCount = 1,
                    Luid = luid,
                    Attributes = SE_PRIVILEGE_ENABLED,
                };

                if (!AdjustTokenPrivileges(token, false, ref state, 0, nint.Zero, nint.Zero))
                    return false;

                // AdjustTokenPrivileges meldet auch dann Erfolg, wenn es das Recht
                // gar nicht zuweisen konnte; nur der letzte Fehlercode verrät das.
                return Marshal.GetLastWin32Error() == 0;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            DiagnosticLog.Report("Prozessrechte", ex, $"„{privilege}“ ließ sich nicht einschalten");
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges
    {
        public int PrivilegeCount;
        public long Luid;
        public int Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, int access, out nint token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        nint token,
        [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivileges newState,
        int bufferLength,
        nint previousState,
        nint returnLength);
}

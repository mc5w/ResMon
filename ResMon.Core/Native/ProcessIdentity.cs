using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ResMon.Core.Native;

/// <summary>
/// Art des Kontos, unter dem ein Prozess läuft. Grundlage für die Einteilung der
/// Prozesstabelle in Apps, Hintergrund- und Windows-Prozesse.
/// </summary>
public enum AccountKind
{
    /// <summary>Das Konto ließ sich nicht ermitteln — bei geschützten Prozessen der Normalfall.</summary>
    Unknown,

    /// <summary>Lokales System, Lokaler Dienst, Netzwerkdienst oder ein virtuelles Dienstkonto.</summary>
    System,

    /// <summary>Ein angemeldeter Benutzer.</summary>
    User,
}

/// <summary>Das Konto eines Prozesses, wie es die Tabelle anzeigt.</summary>
public readonly record struct ProcessOwner(string? Account, AccountKind Kind)
{
    public static readonly ProcessOwner Unknown = new(null, AccountKind.Unknown);
}

/// <summary>
/// Ermittelt den Besitzer eines Prozesses über sein Zugriffstoken. Der Aufruf ist
/// teuer (Handle öffnen, SID auflösen) und gehört deshalb hinter einen Cache — das
/// Konto eines Prozesses ändert sich zu Lebzeiten ohnehin nicht.
/// </summary>
public static class ProcessIdentity
{
    private const int TOKEN_QUERY = 0x0008;
    private const int TokenUser = 1;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    /// <summary>
    /// Aufgelöste Kontonamen je SID. Auf einem Rechner laufen hunderte Prozesse
    /// unter einer Handvoll Konten; <see cref="SecurityIdentifier.Translate"/>
    /// geht dagegen jedes Mal an die LSA.
    /// </summary>
    private static readonly Dictionary<string, string> AccountBySid = new(StringComparer.Ordinal);

    /// <summary>
    /// Liest das Konto eines Prozesses. Liefert <see cref="ProcessOwner.Unknown"/>,
    /// wenn sich der Prozess nicht öffnen lässt — geschützte Prozesse wie
    /// <c>csrss.exe</c> geben ihr Token auch Administratoren nicht heraus.
    /// </summary>
    public static ProcessOwner Read(IntPtr processHandle)
    {
        if (processHandle == IntPtr.Zero)
            return ProcessOwner.Unknown;

        if (!OpenProcessToken(processHandle, TOKEN_QUERY, out IntPtr token))
            return ProcessOwner.Unknown;

        try
        {
            GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out int size);
            if (size <= 0 && Marshal.GetLastWin32Error() != ERROR_INSUFFICIENT_BUFFER)
                return ProcessOwner.Unknown;

            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, TokenUser, buffer, size, out _))
                    return ProcessOwner.Unknown;

                IntPtr sidPointer = Marshal.PtrToStructure<TOKEN_USER>(buffer).User.Sid;
                if (sidPointer == IntPtr.Zero)
                    return ProcessOwner.Unknown;

                var sid = new SecurityIdentifier(sidPointer);
                return new ProcessOwner(Describe(sid), Classify(sid));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException)
        {
            return ProcessOwner.Unknown;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>
    /// Der lesbare Kontoname in der Sprache des Systems, etwa
    /// <c>NT-AUTORITÄT\SYSTEM</c>. Ist die SID nicht auflösbar — gelöschtes Konto,
    /// Konto aus einer anderen Domäne —, bleibt die SID selbst stehen.
    /// </summary>
    private static string Describe(SecurityIdentifier sid)
    {
        string key = sid.Value;
        lock (AccountBySid)
        {
            if (AccountBySid.TryGetValue(key, out string? cached))
                return cached;
        }

        string account;
        try
        {
            account = ((NTAccount)sid.Translate(typeof(NTAccount))).Value;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            account = key;
        }

        lock (AccountBySid)
            AccountBySid[key] = account;

        return account;
    }

    /// <summary>
    /// Dienstkonten erkennen. Neben den drei bekannten Konten zählen auch die
    /// virtuellen Konten dazu, unter denen Windows einzelne Dienste isoliert:
    /// S-1-5-80 (Dienst), S-1-5-82 (IIS-Anwendungspool), S-1-5-83
    /// (virtuelle Maschine), S-1-5-90 (Fensterverwaltung), S-1-5-94 (WinRM).
    /// </summary>
    private static AccountKind Classify(SecurityIdentifier sid)
    {
        if (sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
            || sid.IsWellKnown(WellKnownSidType.LocalServiceSid)
            || sid.IsWellKnown(WellKnownSidType.NetworkServiceSid))
        {
            return AccountKind.System;
        }

        string value = sid.Value;
        foreach (string prefix in ServiceSidPrefixes)
        {
            if (value.StartsWith(prefix, StringComparison.Ordinal))
                return AccountKind.System;
        }

        return AccountKind.User;
    }

    private static readonly string[] ServiceSidPrefixes =
        ["S-1-5-80-", "S-1-5-82-", "S-1-5-83-", "S-1-5-90-", "S-1-5-94-"];

    [StructLayout(LayoutKind.Sequential)]
    private struct SID_AND_ATTRIBUTES
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_USER
    {
        public SID_AND_ATTRIBUTES User;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, int desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

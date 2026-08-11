using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ResMon.Core.Native;

/// <summary>
/// Fragt beim Volume nach, ob sein Datenträger eine Kopfbewegung kostet.
/// </summary>
/// <remarks>
/// <c>Win32_DiskDrive.MediaType</c> taugt dafür nicht: es meldet auch für SSDs
/// „Fixed hard disk media" — das Feld beschreibt das Wechselmedien-Bit, nicht die
/// Technik. <c>InterfaceType</c> ebensowenig, NVMe meldet dort meist „SCSI".
/// <c>StorageDeviceSeekPenaltyProperty</c> beantwortet genau die Frage, auf die es
/// beim Aufzählen von Verzeichnissen ankommt: ob sich hunderttausend Zugriffe
/// überlappen lassen oder ob sie sich gegenseitig im Weg stehen.
/// </remarks>
public static class StorageDevice
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int PropertyStandardQuery = 0;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_SEEK_PENALTY_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        [MarshalAs(UnmanagedType.U1)]
        public bool IncursSeekPenalty;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref STORAGE_PROPERTY_QUERY lpInBuffer,
        int nInBufferSize,
        out DEVICE_SEEK_PENALTY_DESCRIPTOR lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        nint lpOverlapped);

    /// <summary>
    /// <c>true</c> bei einer Festplatte, <c>false</c> bei einer SSD,
    /// <c>null</c>, wenn das Gerät keine Auskunft gibt — etwa bei virtuellen
    /// Laufwerken, Speicherplätzen oder Netzfreigaben.
    /// </summary>
    /// <param name="driveRoot">Laufwerkswurzel wie <c>C:\</c>.</param>
    public static bool? HasSeekPenalty(string driveRoot)
    {
        if (string.IsNullOrWhiteSpace(driveRoot) || driveRoot.Length < 2 || driveRoot[1] != ':')
            return null;

        // Der Gerätepfad trägt keinen abschließenden Trennstrich; mit einem
        // liefert CreateFile ERROR_INVALID_NAME.
        string device = $@"\\.\{driveRoot[0]}:";

        try
        {
            // Zugriffsrecht 0: die Abfrage braucht das Gerät nicht zu öffnen,
            // sondern nur zu benennen. Damit geht sie auch ohne erhöhte Rechte.
            using SafeFileHandle handle = CreateFileW(
                device, 0, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);

            if (handle.IsInvalid)
                return null;

            var query = new STORAGE_PROPERTY_QUERY
            {
                PropertyId = StorageDeviceSeekPenaltyProperty,
                QueryType = PropertyStandardQuery,
            };

            bool ok = DeviceIoControl(
                handle,
                IoctlStorageQueryProperty,
                ref query,
                Marshal.SizeOf<STORAGE_PROPERTY_QUERY>(),
                out DEVICE_SEEK_PENALTY_DESCRIPTOR descriptor,
                Marshal.SizeOf<DEVICE_SEEK_PENALTY_DESCRIPTOR>(),
                out _,
                0);

            return ok ? descriptor.IncursSeekPenalty : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }
}

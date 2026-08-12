using System.IO;
using System.Text;

namespace ResMon.Core.Startup;

/// <summary>
/// Liest das Ziel einer Windows-Verknüpfung direkt aus der Datei.
/// </summary>
/// <remarks>
/// Der Startordner enthält Verknüpfungen, keine Programme — ohne das Ziel ließe
/// sich weder prüfen, ob es die Datei noch gibt, noch der Eintrag der gemessenen
/// Startkette zuordnen, die ja Programmnamen nennt.
/// <para>
/// Das Format ist MS-SHLLINK: ein Kopf von 76 Byte, dahinter je nach Merkmalsbits
/// eine Elementliste und ein <c>LinkInfo</c>-Block, in dem der Pfad steht.
/// Bewusst kein <c>IShellLink</c> — dessen COM-Interop wäre für einen Wert, der
/// an einer festen Stelle in der Datei liegt, ein Apartment-Modell und drei
/// Schnittstellen zu viel. Der Preis ist, dass alles Ungewöhnliche —
/// Verknüpfungen auf Ordner-Elemente ohne Dateipfad, reine Netzwerkziele —
/// hier als „kein Ziel“ endet statt aufgelöst zu werden.
/// </para>
/// </remarks>
internal static class ShellLink
{
    private const int HeaderSize = 0x4C;
    private const uint HasLinkTargetIdList = 0x01;
    private const uint HasLinkInfo = 0x02;

    /// <summary>Der Zielpfad, oder <c>null</c>, wenn er sich nicht herauslesen ließ.</summary>
    public static string? ReadTarget(string linkPath)
    {
        try
        {
            byte[] data = File.ReadAllBytes(linkPath);
            if (data.Length < HeaderSize || BitConverter.ToInt32(data, 0) != HeaderSize)
                return null;

            uint flags = BitConverter.ToUInt32(data, 20);
            int position = HeaderSize;

            if ((flags & HasLinkTargetIdList) != 0)
            {
                if (position + 2 > data.Length)
                    return null;

                // Die Elementliste ist eine Kette von PIDLs; sie interessiert
                // hier nicht, nur ihre Länge, um über sie hinwegzukommen.
                position += 2 + BitConverter.ToUInt16(data, position);
            }

            if ((flags & HasLinkInfo) == 0 || position + 24 > data.Length)
                return null;

            return ReadLinkInfo(data, position);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string? ReadLinkInfo(byte[] data, int start)
    {
        int headerSize = BitConverter.ToInt32(data, start + 4);
        uint infoFlags = BitConverter.ToUInt32(data, start + 8);

        // Bit 0: der Block trägt einen lokalen Pfad. Fehlt es, zeigt die
        // Verknüpfung auf eine Netzwerkfreigabe oder auf gar keinen Pfad.
        if ((infoFlags & 0x01) == 0)
            return null;

        int basePathOffset = BitConverter.ToInt32(data, start + 16);
        int suffixOffset = BitConverter.ToInt32(data, start + 24);

        // Ab einem Kopf von 0x24 Byte stehen zusätzlich die Unicode-Fassungen der
        // beiden Pfadteile dahinter. Sie sind die genaueren: die ANSI-Fassung
        // verliert alles, was die Codepage nicht kennt.
        if (headerSize >= 0x24 && start + 32 <= data.Length)
        {
            int unicodeBase = BitConverter.ToInt32(data, start + 28);
            int unicodeSuffix = BitConverter.ToInt32(data, start + 32);
            string? path = ReadUnicode(data, start + unicodeBase);
            if (path is { Length: > 0 })
                return path + ReadUnicode(data, start + unicodeSuffix);
        }

        string? ansi = ReadAnsi(data, start + basePathOffset);
        return ansi is { Length: > 0 } ? ansi + ReadAnsi(data, start + suffixOffset) : null;
    }

    private static string? ReadAnsi(byte[] data, int offset)
    {
        if (offset < 0 || offset >= data.Length)
            return null;

        int end = Array.IndexOf<byte>(data, 0, offset);
        if (end < 0)
            end = data.Length;

        return Encoding.Default.GetString(data, offset, end - offset);
    }

    private static string? ReadUnicode(byte[] data, int offset)
    {
        if (offset < 0 || offset + 1 >= data.Length)
            return null;

        int end = offset;
        while (end + 1 < data.Length && (data[end] != 0 || data[end + 1] != 0))
            end += 2;

        return Encoding.Unicode.GetString(data, offset, end - offset);
    }
}

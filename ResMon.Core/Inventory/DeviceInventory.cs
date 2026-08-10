using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ResMon.Core.Diagnostics;

namespace ResMon.Core.Inventory;

/// <summary>
/// Erhebt, womit der Rechner mit der Außenwelt verbunden ist: Netzwerkkarten
/// samt WLAN, Bluetooth-Funkmodule mit ihren gekoppelten Geräten und alles, was
/// per USB angeschlossen ist.
/// </summary>
/// <remarks>
/// Anders als Prozessor und Mainboard ändert sich das im Betrieb — ein USB-Stick
/// wird abgezogen, das WLAN verbindet sich neu. Die Erhebung läuft deshalb auf
/// Anforderung erneut und nicht nur einmal beim Start.
/// </remarks>
public static class DeviceInventory
{
    /// <summary>
    /// Alle drei Gruppen werden immer geliefert, auch leer: „kein Bluetooth
    /// vorhanden" ist eine Antwort auf die Frage nach der Konnektivität, ein
    /// fehlender Abschnitt wäre keine.
    /// </summary>
    public static List<DeviceGroup> Collect() =>
    [
        new("Netzwerk", "Die Adapter, die auch unter Netzwerkverbindungen stehen. Virtuelle " +
                        "Adapter von Hyper-V, WSL oder VPN-Software erscheinen hier ebenfalls, " +
                        "die Filtertreiber darüber nicht.",
            NetworkAdapters()),
        new("Bluetooth", "Das Funkmodul und die damit gekoppelten Geräte. Gekoppelt heißt nicht " +
                         "verbunden — ein ausgeschalteter Kopfhörer bleibt in der Liste stehen.",
            BluetoothDevices()),
        new("USB-Geräte", "Angeschlossene Geräte am USB-Bus. Hubs, Controller-Wurzelknoten und die " +
                          "Einzelfunktionen von Verbundgeräten sind zusammengefasst — eine Maus mit " +
                          "Tastenbelegung meldet sich sonst dreimal.",
            UsbDevices()),
    ];

    // ---------- Netzwerk ----------

    private static List<DeviceEntry> NetworkAdapters()
    {
        var result = new List<DeviceEntry>();

        // Die Liste kommt aus WMI, nicht aus NetworkInterface: letztere führt
        // jede NDIS-Filterinstanz als eigenen Adapter auf — QoS-Planer,
        // WFP-Schichten, Paketfilter von Mitschnittwerkzeugen. Aus zwei echten
        // Karten werden so vierzig Einträge. Win32_NetworkAdapter mit gesetztem
        // NetConnectionID liefert genau das, was auch unter „Netzwerkverbindungen"
        // steht.
        var byId = new Dictionary<string, NetworkInterface>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                byId[adapter.Id] = adapter;
        }
        catch (NetworkInformationException ex)
        {
            DiagnosticLog.Report("Geräteübersicht (Netzwerk)", ex,
                "Die Adapterliste des Systems war nicht lesbar — Adressen und Verbindungsdaten fehlen");
        }

        foreach (ManagementBaseObject adapter in Query(
                     "SELECT Name, NetConnectionID, GUID, MACAddress, Manufacturer, PhysicalAdapter, " +
                     "NetConnectionStatus FROM Win32_NetworkAdapter WHERE NetConnectionID IS NOT NULL"))
        {
            using (adapter)
            {
                string? model = Text(adapter, "Name");
                if (model is null)
                    continue;

                string? guid = Text(adapter, "GUID");
                NetworkInterface? live = guid is not null ? byId.GetValueOrDefault(guid) : null;
                bool physical = adapter["PhysicalAdapter"] is not bool flag || flag;

                var details = new List<InfoItem>();
                Add(details, "Verbindung", Text(adapter, "NetConnectionID"));
                Add(details, "Art", live is not null
                    ? AdapterKind(live.NetworkInterfaceType)
                    : physical ? "Netzwerkkarte" : "virtueller Adapter");

                if (!physical)
                    Add(details, "Hinweis", "virtueller Adapter");

                Add(details, "Hersteller", Text(adapter, "Manufacturer"));

                bool up = live?.OperationalStatus == OperationalStatus.Up;
                if (live is not null)
                {
                    if (up && live.Speed > 0)
                        Add(details, "Geschwindigkeit", FormatSpeed(live.Speed));

                    AddAddresses(details, live);
                }

                Add(details, "MAC-Adresse", Text(adapter, "MACAddress"));

                result.Add(new DeviceEntry(
                    model,
                    details,
                    live is not null ? StatusText(live.OperationalStatus) : ConnectionStatus(Number(adapter, "NetConnectionStatus")),
                    up ? DeviceHealth.Active : DeviceHealth.Idle));
            }
        }

        // Verbundene Adapter zuerst; darunter interessiert die Reihenfolge nicht.
        return result
            .OrderBy(entry => entry.Health == DeviceHealth.Active ? 0 : 1)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static void AddAddresses(List<InfoItem> details, NetworkInterface adapter)
    {
        try
        {
            IPInterfaceProperties properties = adapter.GetIPProperties();

            Add(details, "IPv4", string.Join(", ", properties.UnicastAddresses
                .Where(entry => entry.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(entry => entry.Address.ToString())));

            // Link-lokale Adressen (fe80::) hat jeder Adapter; sie sagen nichts
            // darüber, ob er im Netz erreichbar ist.
            Add(details, "IPv6", string.Join(", ", properties.UnicastAddresses
                .Where(entry => entry.Address.AddressFamily == AddressFamily.InterNetworkV6
                                && !entry.Address.IsIPv6LinkLocal)
                .Select(entry => entry.Address.ToString())));

            Add(details, "Gateway", string.Join(", ", properties.GatewayAddresses
                .Where(entry => entry.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(entry => entry.Address.ToString())));

            Add(details, "DNS", string.Join(", ", properties.DnsAddresses
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                .Select(address => address.ToString())));
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            // Ein Adapter, der gerade verschwindet, wirft hier; die übrigen
            // Angaben bleiben trotzdem brauchbar.
        }
    }

    /// <summary>NetConnectionStatus aus Win32_NetworkAdapter, falls kein Live-Adapter dazu passt.</summary>
    private static string ConnectionStatus(ulong? code) => code switch
    {
        0 => "getrennt",
        1 => "verbindet",
        2 => "verbunden",
        3 => "trennt",
        4 => "nicht vorhanden",
        7 => "Leitung tot",
        _ => "unbekannt",
    };

    private static string AdapterKind(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Wireless80211 => "WLAN",
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx => "Ethernet",
        NetworkInterfaceType.Ppp => "Einwahl oder VPN",
        _ => type.ToString(),
    };

    private static string StatusText(OperationalStatus status) => status switch
    {
        OperationalStatus.Up => "verbunden",
        OperationalStatus.Down => "getrennt",
        OperationalStatus.Dormant => "im Ruhezustand",
        OperationalStatus.NotPresent => "nicht vorhanden",
        OperationalStatus.LowerLayerDown => "Leitung tot",
        _ => "unbekannt",
    };

    private static string FormatSpeed(long bitsPerSecond)
    {
        if (bitsPerSecond >= 1_000_000_000)
            return $"{bitsPerSecond / 1_000_000_000.0:N1} Gbit/s";
        return $"{bitsPerSecond / 1_000_000.0:N0} Mbit/s";
    }

    private static string FormatMac(PhysicalAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return bytes.Length == 0 ? string.Empty : string.Join(':', bytes.Select(b => b.ToString("X2")));
    }

    // ---------- Bluetooth ----------

    private static List<DeviceEntry> BluetoothDevices()
    {
        var radios = new List<DeviceEntry>();
        var paired = new List<DeviceEntry>();

        foreach (ManagementBaseObject device in Query(
                     "SELECT Name, Manufacturer, PNPDeviceID, Status, Present FROM Win32_PnPEntity " +
                     "WHERE PNPClass = 'Bluetooth'"))
        {
            using (device)
            {
                string? id = Text(device, "PNPDeviceID");
                string? name = Text(device, "Name");
                if (id is null || name is null)
                    continue;

                bool present = device["Present"] is not bool flag || flag;
                bool ok = string.Equals(Text(device, "Status"), "OK", StringComparison.OrdinalIgnoreCase);

                var details = new List<InfoItem>();
                Add(details, "Hersteller", CleanManufacturer(Text(device, "Manufacturer")));

                // BTHENUM und BTHLEDevice sind die gekoppelten Gegenstellen; alles
                // andere ist das Funkmodul selbst oder ein Treiberknoten dazu.
                bool isPeer = id.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase)
                              || id.StartsWith("BTHLEDEVICE", StringComparison.OrdinalIgnoreCase)
                              || id.StartsWith("BTHLE", StringComparison.OrdinalIgnoreCase);

                var entry = new DeviceEntry(
                    name,
                    details,
                    isPeer ? (present && ok ? "gekoppelt und verbunden" : "gekoppelt") : (ok ? "aktiv" : "gestört"),
                    ok && present ? DeviceHealth.Active : isPeer ? DeviceHealth.Idle : DeviceHealth.Problem);

                (isPeer ? paired : radios).Add(entry);
            }
        }

        // Windows legt je gekoppeltem Gerät mehrere Knoten an — einen je
        // unterstütztem Profil. Für die Übersicht zählt der Gerätename.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<DeviceEntry>();
        foreach (DeviceEntry entry in radios.Concat(paired))
        {
            if (seen.Add(entry.Name))
                result.Add(entry);
        }

        return result;
    }

    // ---------- USB ----------

    /// <summary>
    /// Knoten, die zwar am USB-Bus hängen, aber kein angeschlossenes Gerät
    /// darstellen: Wurzelknoten der Controller, Hubs und die Sammelknoten von
    /// Verbundgeräten.
    /// </summary>
    private static readonly string[] UsbNoise =
    ["ROOT_HUB", "USB\\VID_0000", "\\HUB", "GENERIC_HUB"];

    /// <summary>
    /// Namen, die Windows vergibt, wenn der Treiber keinen eigenen mitbringt.
    /// Sie beschreiben nur die Rolle im Gerätebaum, nicht das Gerät.
    /// </summary>
    private static readonly string[] GenericNames =
    [
        "USB-Verbundgerät", "USB Composite Device", "USB-Eingabegerät", "USB Input Device",
        "USB-Massenspeichergerät", "USB Mass Storage Device", "HID-",
    ];

    private static List<DeviceEntry> UsbDevices()
    {
        // Ein Gerät meldet sich je Funktion einmal: die Maus als Zeigegerät und
        // als Tastenbelegung, das Headset als Audioausgabe, -eingabe und
        // Steuerkanal. Alle tragen dieselbe VID&PID — die ist deshalb der
        // Schlüssel, und je Schlüssel gewinnt der Eintrag mit dem sprechendsten
        // Namen.
        var best = new Dictionary<string, (DeviceEntry Entry, int Score)>(StringComparer.OrdinalIgnoreCase);

        foreach (ManagementBaseObject device in Query(
                     "SELECT Name, Manufacturer, PNPDeviceID, PNPClass, Status, Service, Present " +
                     "FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\\\%'"))
        {
            using (device)
            {
                string? id = Text(device, "PNPDeviceID");
                string? name = Text(device, "Name");
                if (id is null || name is null)
                    continue;

                if (device["Present"] is bool present && !present)
                    continue;

                string upper = id.ToUpperInvariant();
                if (UsbNoise.Any(marker => upper.Contains(marker, StringComparison.Ordinal)))
                    continue;

                string? service = Text(device, "Service");
                if (string.Equals(service, "USBHUB3", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(service, "usbhub", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(service, "usbccgp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string key = HardwareIds(id) ?? id;

                var details = new List<InfoItem>();
                Add(details, "Hersteller", CleanManufacturer(Text(device, "Manufacturer")));
                Add(details, "Art", ClassLabel(Text(device, "PNPClass")));
                Add(details, "Kennung", key);

                bool ok = string.Equals(Text(device, "Status"), "OK", StringComparison.OrdinalIgnoreCase);
                var entry = new DeviceEntry(
                    name,
                    details,
                    ok ? "angeschlossen" : Text(device, "Status") ?? "Zustand unbekannt",
                    ok ? DeviceHealth.Active : DeviceHealth.Problem);

                int score = GenericNames.Any(generic => name.StartsWith(generic, StringComparison.OrdinalIgnoreCase))
                    ? 0
                    : 1;

                if (!best.TryGetValue(key, out (DeviceEntry Entry, int Score) known) || score > known.Score)
                    best[key] = (entry, score);
            }
        }

        return best.Values
            .Select(item => item.Entry)
            .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Zieht <c>VID_xxxx&amp;PID_xxxx</c> aus der PnP-Kennung, ohne die Funktionsnummer.</summary>
    private static string? HardwareIds(string pnpDeviceId)
    {
        string[] parts = pnpDeviceId.Split('\\');
        if (parts.Length < 2)
            return null;

        // "VID_1B1C&PID_0A92&MI_00" — der Zusatz MI_nn benennt die Funktion des
        // Verbundgeräts und gehört nicht zur Geräteidentität.
        string id = parts[1];
        int cut = id.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase);
        return cut > 0 ? id[..cut] : id;
    }

    /// <summary>
    /// Bei Geräten ohne eigenen Treiber trägt WMI den Anbieter der
    /// Windows-Standardtreiber ein, in Klammern. Das ist kein Hersteller.
    /// </summary>
    private static string? CleanManufacturer(string? value)
        => value is null || value.StartsWith('(') ? null : value;

    private static string? ClassLabel(string? pnpClass)
    {
        if (pnpClass is null)
            return null;

        // WMI liefert die Klasse je nach Treiber in unterschiedlicher
        // Schreibweise — "Media" und "MEDIA" sind dieselbe.
        return pnpClass.ToUpperInvariant() switch
        {
            "HIDCLASS" => "Eingabegerät",
            "KEYBOARD" => "Tastatur",
            "MOUSE" => "Maus",
            "USB" or "USBDEVICE" => "USB-Gerät",
            "MEDIA" or "AUDIOENDPOINT" => "Audio",
            "CAMERA" or "IMAGE" => "Kamera",
            "NET" => "Netzwerk",
            "DISKDRIVE" or "VOLUME" => "Datenträger",
            "WPD" => "Tragbares Gerät",
            "PRINTER" => "Drucker",
            "BLUETOOTH" => "Bluetooth",
            "XNACOMPOSITE" or "XBOXCOMPOSITE" => "Spielesteuerung",
            "SMARTCARDREADER" => "Kartenleser",
            _ => pnpClass,
        };
    }

    // ---------- Hilfsmittel ----------

    private static void Add(List<InfoItem> items, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            items.Add(new InfoItem(label, value.Trim()));
    }

    private static string? Text(ManagementBaseObject item, string property)
    {
        try
        {
            return item[property]?.ToString();
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    private static ulong? Number(ManagementBaseObject item, string property)
    {
        try
        {
            return item[property] switch
            {
                ulong value => value,
                uint value => value,
                ushort value => value,
                byte value => value,
                int value and >= 0 => (ulong)value,
                _ => null,
            };
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    /// <summary>
    /// Wie in <see cref="SystemInfoProvider"/>: die Aufzählung selbst kann
    /// werfen, deshalb wird sie vollständig materialisiert. Der Aufrufer gibt die
    /// Elemente frei.
    /// </summary>
    private static List<ManagementBaseObject> Query(string query)
    {
        var items = new List<ManagementBaseObject>();
        try
        {
            using var searcher = new ManagementObjectSearcher(new ObjectQuery(query));
            using ManagementObjectCollection results = searcher.Get();

            foreach (ManagementBaseObject item in results)
                items.Add(item);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException)
        {
            DiagnosticLog.Report("Geräteübersicht (WMI)", ex, $"Abfrage »{query}«");
        }

        return items;
    }
}

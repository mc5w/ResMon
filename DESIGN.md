# ResMon — Entwurfsdokument

Desktop-Overlay für Windows 11 zur Live-Anzeige von CPU-, GPU- und RAM-Auslastung
inklusive Temperaturen, plus ein Detailfenster mit genauerer Aufschlüsselung als
der Windows-Task-Manager.

| | |
|---|---|
| Status | Entwurf, freigegeben für Implementierung |
| Zielplattform | Windows 11 (x64) |
| Runtime | .NET 9 |
| Referenzhardware | NVIDIA-GPU |

---

## 1. Motivation

Der Lüfter dreht gelegentlich ohne erkennbaren Grund hoch. Öffnet man daraufhin
den Task-Manager, ist die Lastspitze meist schon vorbei — und der Task-Manager
zeigt ohnehin nicht präzise genug, *was* die Ressourcen belegt:

- `Diensthost: lokales System (12)` sagt nicht, welcher Dienst tatsächlich läuft
- GPU-Last wird zu einem einzigen Prozentwert zusammengefasst, obwohl Windows
  intern nach Engine-Typ (3D, Copy, VideoDecode, Compute) getrennt zählt
- Browser- und Chat-Anwendungen erscheinen als Dutzende Einzelprozesse
- Temperaturen fehlen vollständig

Ziel ist ein permanent sichtbares Overlay plus ein Detailfenster, das genau diese
Lücken schließt.

## 2. Begriffsklärung

Ein **Windows-11-Widget** im offiziellen Sinn ist eine Kachel im Widget-Board
(`Win+W`). Solche Widgets erfordern MSIX-Paketierung, einen Widget-Provider und
Adaptive Cards; ihre Aktualisierungsfrequenz ist systemseitig auf Minutenintervalle
gedrosselt. Für Live-Metriken ist das ungeeignet.

Dieses Projekt baut stattdessen ein **Desktop-Overlay** (auch Gadget oder OSD):
ein randloses, transparentes Always-on-Top-Fenster, vergleichbar mit Rainmeter
oder Sidebar Diagnostics.

## 3. Ziele und Nicht-Ziele

### In Scope (v1)

- Overlay mit CPU-, GPU- und RAM-Auslastung sowie CPU- und GPU-Temperatur
- Verschiebbar, Position wird gespeichert, Autostart mit Windows
- Detailfenster mit sortierbarer Prozessliste
- Auflösung von `svchost.exe` zu konkreten Dienstnamen
- GPU-Last pro Prozess, aufgeschlüsselt nach Engine-Typ
- Optionale Aggregation von Prozessbäumen
- Ringpuffer über 5 Minuten für Aggregatwerte (Sparklines)

### Out of Scope (v1)

- Trigger-basierte Snapshots bei Schwellwertüberschreitung
- ETW-Erfassung von Disk- und Netzwerk-I/O pro Prozess
- Prozesse beenden aus der Anwendung heraus
- Aufteilung in Windows-Dienst plus unprivilegiertes UI
- Nicht-NVIDIA-GPUs (AMD/Intel werden nicht aktiv getestet)

## 4. Architektur

```
┌─────────────────────┬─────────────────────┬─────────────────────┐
│ Hardware-Sensoren   │ PDH-Counter         │ (später: ETW)       │
│ LibreHardwareMonitor│ Last pro Prozess    │ Disk und Netzwerk   │
└──────────┬──────────┴──────────┬──────────┴──────────┬──────────┘
           │                     │                     │
           └─────────────────────┼─────────────────────┘
                                 ▼
                  ┌──────────────────────────────┐
                  │ Collector (erhöhte Rechte)   │
                  │ Sampling, Ringpuffer         │
                  └──────────────┬───────────────┘
                                 │
           ┌─────────────────────┼─────────────────────┐
           ▼                     ▼                     ▼
   ┌───────────────┐   ┌───────────────────┐   ┌───────────────┐
   │ Overlay       │   │ Detailfenster     │   │ Tray-Menü     │
   │ Live-Anzeige  │   │ Prozesse, Verlauf │   │ Einstellungen │
   └───────────────┘   └───────────────────┘   └───────────────┘
```

Die Anwendung läuft als **ein einziger Prozess mit Administratorrechten**. Ein
Split in Dienst plus unprivilegiertes UI wird erst nötig, wenn das UI ohne
Elevation laufen soll — das ist bewusst auf später verschoben.

## 5. Technologieentscheidungen

| Baustein | Wahl | Begründung |
|---|---|---|
| Runtime | .NET 9 | LibreHardwareMonitorLib ist .NET-nativ |
| Fenster-Host | WPF | Transparenz, Always-on-Top, Klick-Durchlässigkeit und Tray-Icon mit dem geringsten Aufwand |
| UI-Inhalt | WebView2 (HTML/CSS/JS) | Layout und Theming deutlich angenehmer als XAML; WebView2 unterstützt transparente Hintergründe |
| Sensoren | LibreHardwareMonitorLib (MPL-2.0) | Temperaturen, Takt, Package Power, Lüfterdrehzahl |
| Auslastung | PDH direkt per P/Invoke | Gleiche Quelle wie der Task-Manager, aber ungefiltert |
| Persistenz | JSON-Datei | Nur Einstellungen; Messwerte bleiben im RAM |

**Kein NVML/NVAPI als separate Abhängigkeit.** LibreHardwareMonitorLib deckt bei
NVIDIA-Karten Temperatur, Last, VRAM, Power und Lüfter bereits ab, und die
Pro-Prozess-Aufschlüsselung kommt ohnehin aus PDH.

## 6. Projektstruktur

```
ResMon.sln
├─ ResMon.Core                     net9.0, Klassenbibliothek
│  ├─ Native/
│  │  ├─ PdhQuery.cs               P/Invoke-Wrapper um pdh.dll
│  │  ├─ CpuCache.cs               L1/L2/L3 aus der Prozessortopologie
│  │  ├─ StorageDevice.cs          SSD oder Festplatte (Seek Penalty, §8.11)
│  │  └─ Toolhelp.cs               Prozessbaum via CreateToolhelp32Snapshot
│  ├─ Diagnostics/DiagnosticLog.cs Sammelstelle für gefangene Fehler (§13.7)
│  ├─ Sensors/
│  │  ├─ HardwareSource.cs         LHM: Temperaturen, Takt, Power, Lüfter
│  │  ├─ CounterSource.cs          PDH: CPU, RAM
│  │  └─ GpuEngineSource.cs        PDH: GPU Engine, GPU Process Memory
│  ├─ Processes/
│  │  ├─ ProcessSampler.cs         Prozessliste zusammenführen
│  │  └─ ServiceResolver.cs        svchost-PID → Dienstnamen
│  ├─ Model/
│  │  ├─ SystemSnapshot.cs
│  │  ├─ ProcessSample.cs
│  │  └─ RingBuffer.cs
│  ├─ Storage/
│  │  ├─ FolderTree.cs             DirNode, BigFile, Auszug für die Oberfläche
│  │  └─ FolderScanner.cs          Ordnerbelegung auf Anforderung (§8.11)
│  ├─ Startup/                     Analyse des Systemstarts (§8.12)
│  │  ├─ StartupModel.cs           Eintrag, Kettenglied, Phase, Befund, Bericht
│  │  ├─ StartupInventory.cs       Run-Keys, Startordner, Aufgaben, Dienste, Store-Apps
│  │  ├─ ShellLink.cs              Ziel einer .lnk aus der Datei lesen
│  │  ├─ StartupEvents.cs          Ereignisprotokolle, Felder statt Meldungstext
│  │  ├─ BootChain.cs              Gemessene Startkette aus Shell-Core
│  │  ├─ BootPerformanceReader.cs  Windows' eigene Startmessung
│  │  ├─ StartupFindings.cs        Die bekannten Muster und ihre Belege
│  │  ├─ BootTrace.cs              ETW-Aufzeichnung scharfstellen und einsammeln
│  │  ├─ BootTraceAnalyzer.cs      ETL auswerten: CPU und E/A je Prozess
│  │  └─ StartupAnalyzer.cs        Klammer um alles, auf Anforderung
│  ├─ Native/
│  │  ├─ WaitChain.cs              GetThreadWaitChain: worauf ein Thread wartet
│  │  ├─ HandleTable.cs            Systemweite Handle-Tabelle, offene Dateien
│  │  └─ ProcessPrivileges.cs      SeDebugPrivilege einschalten
│  ├─ Config/AppSettings.cs
│  └─ Collector.cs                 Timer-Schleifen, Event SnapshotReady
└─ ResMon.App                      net9.0-windows, WinExe
   ├─ OverlayWindow.xaml(.cs)
   ├─ DetailWindow.xaml(.cs)
   ├─ FolderScanSession.cs         ein Scan-Lauf: Abbruch, Aufgabe, Ergebnis
   ├─ PathActions.cs               Im Explorer zeigen, Pfad kopieren
   ├─ TrayIcon.cs
   ├─ AppIcon.cs                   lädt ResMon.ico für den Infobereich
   ├─ ResMon.ico                   Anwendungssymbol, erzeugt (siehe unten)
   ├─ Bridge/WebBridge.cs          WebView2-Nachrichtenprotokoll
   ├─ app.manifest                 requireAdministrator
   └─ wwwroot/
      ├─ overlay.html / overlay.css / overlay.js
      └─ detail.html / detail.css / detail.js

tools/make-icon.ps1                erzeugt ResMon.ico
```

### Symbol

Ein durchgehender Linienzug wie eine Herzschlagkurve, in dem **R, S und M**
stecken: R als Spitze mit Bogen, S als offener Zickzack, M als Doppelspitze,
dazwischen und außen die Grundlinie. Der Farbverlauf läuft über die drei
Kachelfarben — CPU-Blau, GPU-Grün, RAM-Orange.

**Zwei Detailstufen in einer Datei.** Unter 24 Pixeln fällt der Schriftzug in
sich zusammen: ein Buchstabe ist dort drei Pixel breit, und die Innenräume laufen
mit der Strichstärke zu. Für 16 und 20 Pixel liegt deshalb eine vereinfachte
Pulslinie im Symbol — dieselbe Bildidee ohne Buchstaben. Windows sucht sich je
Einsatzort die passende Auflösung: Titelleiste und Infobereich nehmen die kleine,
Taskleiste und Alt-Tab die große.

`ResMon.ico` ist eine **erzeugte** Binärdatei; die Form steht als Punktliste in
`tools/make-icon.ps1` und wird bei Änderungen dort geändert und neu erzeugt.
Dieselbe Form steht als SVG-Pfad in `overlay.html` (Kopfzeile der kleinen
Ansicht) und `detail.html` (vor den Reitern).

Die Fenster setzen **kein** `Window.Icon`: `ApplicationIcon` legt den ganzen
Symbolverbund in die Exe, und ein WPF-Fenster ohne eigene Eigenschaft holt sich
daraus für jede Stelle die passende Auflösung. Eine gesetzte Eigenschaft wäre ein
einzelnes Bild, das WPF für alle Größen herunterrechnen müsste — genau der Fall,
für den die kleinen Auflösungen gemacht sind. Der Infobereich lädt über
`AppIcon` mit ausdrücklicher Größe, weil `System.Drawing.Icon` das
PNG-komprimierte 256er-Bild nicht auspacken kann und ohne Größenangabe genau
danach greift.

## 7. Datenmodell

```csharp
public sealed record SystemSnapshot(
    DateTime Timestamp,
    CpuMetrics Cpu,
    GpuMetrics Gpu,
    MemoryMetrics Memory,
    IReadOnlyList<ProcessSample> Processes);

public sealed record CpuMetrics(
    double TotalPercent,
    double[] PerCorePercent,
    double? PackageTempC,
    double? ClockMhz,
    double? PackagePowerW);

public sealed record GpuMetrics(
    double TotalPercent,
    IReadOnlyDictionary<string, double> ByEngineType,
    double? TempC,
    long MemUsedBytes,
    long MemTotalBytes,
    double? FanRpm,
    double? PowerW);

public sealed record MemoryMetrics(
    long UsedBytes,
    long TotalBytes,
    long CommittedBytes,
    double Percent);

public sealed record ProcessSample(
    int Pid,
    int? ParentPid,
    string Name,
    string? Description,
    double CpuPercent,
    long WorkingSetBytes,
    long PrivateBytes,
    double GpuPercent,
    IReadOnlyDictionary<string, double> GpuByEngineType,
    long GpuMemBytes,
    IReadOnlyList<string> ServiceNames)
{
    string? UserName;                    // Konto aus dem Prozesstoken
    ProcessCategory Category;            // App, Background, Windows
    string? WindowTitle;
    IReadOnlyList<int> ListeningTcpPorts;
    IReadOnlyList<int> ListeningUdpPorts;
    int ConnectionCount;
}

public sealed record EnergyMetrics(
    double? CpuPackagePowerW,
    double? GpuPowerW,
    IReadOnlyList<PowerRail> Rails,      // Hardware, Sensorname, Watt
    IReadOnlyList<FanSample> Fans,       // Hardware, Name, Drehzahl, Ansteuerung
    BatteryMetrics? Battery);            // null ohne Akku
```

## 8. Datenquellen

### 8.1 Kritischer Fallstrick: lokalisierte Counter-Namen

Auf einem deutschsprachigen Windows heißen die PDH-Kategorien lokalisiert
("Prozessorinformationen" statt "Processor Information"). Der Ansatz über
`System.Diagnostics.PerformanceCounter` mit englischen Namen schlägt dort fehl.

**Verbindliche Lösung:** P/Invoke auf `pdh.dll` mit `PdhAddEnglishCounterW`.
Diese Funktion nimmt immer den englischen Pfad entgegen, unabhängig von der
Systemsprache.

Benötigte Imports:

```
PdhOpenQueryW
PdhAddEnglishCounterW
PdhCollectQueryData
PdhGetFormattedCounterArrayW
PdhCloseQuery
```

Da `PdhGetFormattedCounterArrayW` erst nach dem zweiten `PdhCollectQueryData`
sinnvolle Deltawerte liefert, muss beim Start ein Aufwärm-Sample verworfen werden.

### 8.2 Counter-Pfade

| Metrik | Pfad | Nachbearbeitung |
|---|---|---|
| CPU gesamt | `\Processor Information(_Total)\% Processor Utility` | auf 100 begrenzen; Fallback `% Processor Time` falls nicht vorhanden |
| CPU je Kern | `\Processor Information(*)\% Processor Utility` | `_Total`-Instanz herausfiltern |
| CPU je Prozess | `\Process V2(*)\% Processor Time` | durch `Environment.ProcessorCount` teilen |
| PID-Zuordnung | `\Process V2(*)\ID Process` | |
| Arbeitsspeicher je Prozess | `\Process V2(*)\Working Set - Private` | |
| GPU je Prozess | `\GPU Engine(*)\Utilization Percentage` | Instanzname parsen |
| VRAM je Prozess | `\GPU Process Memory(*)\Local Usage` | |
| Commit Charge | `\Memory\Committed Bytes` | |
| RAM gesamt | `GlobalMemoryStatusEx` (kein PDH) | günstiger als ein Counter |

`Process V2` existiert erst ab Windows 11 und löst das Problem, dass sich mehrere
Prozesse gleichen Namens früher eine Counter-Instanz teilten.

### 8.3 GPU-Instanznamen parsen

Instanznamen haben die Form:

```
pid_1234_luid_0x00000000_0x0000D3F5_phys_0_eng_0_engtype_3D
```

Relevant sind `pid_<n>` und `engtype_<typ>`. Übliche Typen: `3D`, `Copy`,
`VideoDecode`, `VideoEncode`, `VideoProcessing`, `Compute`, `Security`.

**Aggregationsregel:** Die GPU-Gesamtlast ist das **Maximum über die
Engine-Typen**, nicht deren Summe. Andernfalls zeigt die Anzeige bei
Videowiedergabe Werte deutlich über 100 %.

### 8.4 Dienstauflösung

WMI-Abfrage `SELECT Name, DisplayName, ProcessId FROM Win32_Service WHERE
State = 'Running'`, Ergebnis nach PID gruppieren und 30 Sekunden cachen. WMI ist
zu langsam für den 2-Sekunden-Takt.

### 8.5 Prozessbaum

Elternprozess-IDs über `CreateToolhelp32Snapshot` mit `Process32FirstW` /
`Process32NextW` (Feld `th32ParentProcessID`). Schnell und ohne Sonderrechte.
`Win32_Process` über WMI wäre deutlich langsamer.

### 8.6 Hardware-Sensoren

LibreHardwareMonitorLib mit aktivierten Komponenten `IsCpuEnabled`,
`IsGpuEnabled`, `IsMemoryEnabled`, `IsMotherboardEnabled` und `IsBatteryEnabled`.
Der Aufruf `Computer.Accept(visitor)` beziehungsweise `hardware.Update()` ist
teuer und gehört in einen eigenen, langsameren Takt.

Leistungs-, Lüfter- und Temperatursensoren werden **rekursiv** eingesammelt: die
Lüfter des Mainboards hängen am Super-I/O-Chip, und der ist ein eigenes
`IHardware` unterhalb des Mainboards. Ein Lüfter kann eine Drehzahl
(`SensorType.Fan`) melden, eine Ansteuerung in Prozent (`SensorType.Control`)
oder beides; beide Sensoren tragen denselben Namen und gehören in dieselbe Zeile.

**Sockeltemperatur.** Der Prozessor misst am Die (`Core (Tctl/Tdie)` bei AMD,
`CPU Package` bei Intel), das Mainboard am Sockel. Beide Sensoren heißen oft
„CPU" und meinen Verschiedenes — die Sockeltemperatur liegt niedriger und
reagiert träger. Deshalb trägt jede Temperatur ihre Herkunft (`TemperatureSource`)
mit, und als Sockelwert gilt nur ein Sensor der Herkunft `Board`.

**Wenn der Kernel-Treiber fehlt**, taucht der Super-I/O-Chip in der
Hardwareliste gar nicht erst auf: keine Sockeltemperatur, keine Gehäuselüfter.
Die CPU-Sensoren existieren dann zwar, liefern aber nichts — je nach
Windows-Fassung konstant 0 oder gar keinen Wert. `CpuSensorsBlocked` erkennt
beide Formen und unterscheidet sie von „diese Hardware hat den Sensor nicht":
gemeldet wird nur, was angelegt ist und trotzdem schweigt. Die GPU ist davon
nicht betroffen — NVAPI braucht keinen eigenen Treiber.

**Zwei Ursachen, zwei Meldungen.** Ein fehlender Super-I/O-Chip heißt auf einem
Desktop „Treiber blockiert", auf einem Notebook dagegen „gibt es hier nicht":
dort sitzt an seiner Stelle ein Embedded Controller mit herstellereigenem
Protokoll, den auch ein geladener Treiber nicht aufschlösse. Unterschieden wird
am Akku (`HasBattery`) — beides in eine Meldung zu ziehen hieße, dem Anwender
eine Abhilfe zu versprechen, die es nicht gibt.

### 8.6.1 Treiberfreie Ersatzquellen

Alles, was ohne `WinRing0` erreichbar ist, wird auch ohne ihn geholt. Beide
Quellen hängen an der bestehenden PDH-Abfrage und kosten deshalb keinen eigenen
Takt:

| Quelle | Zähler | ersetzt |
|---|---|---|
| `ThermalZoneSource` | `\Thermal Zone Information(*)\High Precision Temperature` | CPU-Temperatur |
| `CounterSource` | `\Processor Information(_Total)\% Processor Performance` × Basistakt | CPU-Takt |

Die Zonenwerte kommen in Zehntel-Kelvin; nicht belegte Zonen melden exakt
273,2 K und fallen über eine Plausibilitätsgrenze (5 °C … 130 °C) heraus. Der
Takt entsteht so, wie ihn auch der Task-Manager bildet: `% Processor
Performance` ist ein Verhältnis zum Basistakt aus
`HARDWARE\DESCRIPTION\System\CentralProcessor\0\~MHz`, kein Frequenzwert, und
darf über 100 % liegen — sonst bliebe der Turbo unsichtbar.

Beide Werte sind **schlechter** als die des Sensortreibers: eine Thermalzone
misst die Umgebung des Prozessors statt seinen Die, der Takt ist ein Mittel über
alle Kerne im Messintervall. Deshalb greifen sie nur als Rückfall, und die
Herkunft geht als `CpuTempOrigin` beziehungsweise `ClockIsEstimated` mit an die
Oberfläche: dort steht „(ACPI-Zone)" und „≈" am Wert. Ein Ersatzwert, der sich
als Messwert ausgibt, wäre schlechter als gar keiner.

Ein Lüfter mit 0 rpm ist kein Fehler: Grafikkarten schalten unterhalb einer
Schwelltemperatur ganz ab. Die Oberfläche schreibt deshalb „steht" statt „0 rpm".

### 8.7 Prozessbesitzer

`OpenProcessToken` mit `PROCESS_QUERY_LIMITED_INFORMATION`, dann
`GetTokenInformation(TokenUser)` und `SecurityIdentifier.Translate`. Beides ist
teuer und ändert sich zu Lebzeiten eines Prozesses nicht — der Handle wird
deshalb gemeinsam mit `QueryFullProcessImageNameW` genutzt, und das Ergebnis
liegt pro PID im Cache. Kontonamen werden zusätzlich je SID gehalten: auf einem
Rechner laufen hunderte Prozesse unter einer Handvoll Konten.

Geschützte Prozesse (`csrss.exe`, Antivirensoftware) geben ihr Token auch
Administratoren nicht heraus. Das ist kein Fehlerfall, sondern das Merkmal, an
dem sie als Windows-Prozesse erkannt werden.

Daraus ergibt sich die Einteilung der Tabelle, dieselbe wie im Task-Manager:

| Art | Bedingung |
|---|---|
| Windows-Prozesse | Systemkonto (S-1-5-18/19/20, virtuelle Dienstkonten S-1-5-80 ff.) oder Token nicht lesbar |
| Apps | Benutzerkonto **und** ein sichtbares Fenster oberster Ebene |
| Hintergrundprozesse | Benutzerkonto ohne Fenster |

Die Fenster kommen aus `EnumWindows`, gefiltert auf sichtbar, ohne Besitzer,
ohne `WS_EX_TOOLWINDOW` und mit nicht leerem Titel. Anders als Konto und Pfad
ändert sich das zu Lebzeiten und wird deshalb in jedem Prozess-Takt neu erhoben.

### 8.8 TCP- und UDP-Verbindungen

`GetExtendedTcpTable` und `GetExtendedUdpTable` aus `iphlpapi.dll`, je einmal
für `AF_INET` und `AF_INET6`, mit `TCP_TABLE_OWNER_PID_ALL` beziehungsweise
`UDP_TABLE_OWNER_PID` — dieselbe Quelle wie `netstat -ano`. Die Portnummer steht
in den unteren zwei Bytes eines DWORD in Netzwerk-Byteordnung und muss gedreht
werden.

Der Aufruf kostet unter einer Millisekunde und läuft im Prozess-Takt mit. Er
liefert zugleich die Ports-Spalte der Prozesstabelle: die Ports im Zustand
`LISTEN`, die gebundenen UDP-Ports und die Zahl der übrigen TCP-Verbindungen.

### 8.9 Ereignisprotokoll

Zwei Fragen lassen sich nur dort beantworten; beide über `EventLogReader` mit
einer XPath-Abfrage, rückwärts gelesen, damit nicht das ganze Protokoll
durchlaufen wird.

**Wie lange läuft der Rechner schon?** `GetTickCount64` beantwortet das nicht.
Windows' Schnellstart schreibt beim Herunterfahren die Kernelsitzung in
`hiberfil.sys` und lädt sie beim Einschalten zurück; der Tickzähler wird um die
Schlafenszeit fortgeschrieben und läuft über das Ausschalten hinweg weiter. Der
Task-Manager zeigt deshalb Laufzeiten von Wochen für einen Rechner, der jeden
Abend ausgeschaltet wird. `QueryUnbiasedInterruptTime` hilft nicht: sie lässt die
Schlafenszeit weg, zählt aber die vorherigen Sitzungen weiter mit.

Verlässlich ist `Microsoft-Windows-Kernel-Boot`, Ereignis 27. Es wird bei jedem
Einschalten geschrieben und trägt im Feld `BootType` die Startart: 0 Kaltstart,
1 Schnellstart, 2 Fortsetzung aus dem Ruhezustand. Dazu die Ereignisse 6006 und
6008 des Protokolldienstes für das letzte — geordnete oder eben nicht geordnete —
Herunterfahren. Alle drei Zeitangaben stehen nebeneinander in der Übersicht,
weil sie drei verschiedene Fragen beantworten.

**Was ist abgestürzt?** Anwendungsprotokoll, Ereignis 1000 (`Application Error`)
und 1002 (`Application Hang`), Zeitfenster sechs Stunden, gedeckelt auf 200
Einträge. Das erste Datenfeld ist der Dateiname. Die Zuordnung läuft über den
Namen und nicht über die PID: der abgestürzte Prozess ist zum Zeitpunkt des
Eintrags längst weg. Läuft im 30-Sekunden-Takt zusammen mit der Dienstauflösung.

**Hängende Fenster** kommen dagegen nicht aus dem Protokoll, sondern aus
`IsHungAppWindow` — dieselbe Prüfung, mit der der Explorer „(Keine Rückmeldung)"
anzeigt. Sie blockiert nicht, anders als eine Testnachricht an das Fenster.

### 8.10 Geräte und Konnektivität

| Was | Quelle | Fallstrick |
|---|---|---|
| Netzwerkadapter | `Win32_NetworkAdapter WHERE NetConnectionID IS NOT NULL`, verbunden mit `NetworkInterface` über die GUID | `NetworkInterface.GetAllNetworkInterfaces` allein führt **jede NDIS-Filterinstanz** als eigenen Adapter auf — QoS-Planer, WFP-Schichten, Paketfilter. Aus zwei Karten werden vierzig Einträge. |
| Bluetooth | `Win32_PnPEntity WHERE PNPClass = 'Bluetooth'` | `BTHENUM`/`BTHLE` sind die gekoppelten Gegenstellen, alles andere ist das Funkmodul. Je Gerät legt Windows einen Knoten pro Profil an. |
| USB-Geräte | `Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\%'` | Ein Gerät meldet sich je Funktion einmal. Zusammengefasst wird über `VID_xxxx&PID_xxxx` ohne den Zusatz `&MI_nn`; je Kennung gewinnt der sprechendste Name. Hubs, Wurzelknoten und `usbccgp`-Sammelknoten fallen heraus. |
| Arbeitsspeicher | `Win32_PhysicalMemory`, `Win32_PhysicalMemoryArray` | WMI bildet `uint16` auf `ushort` ab. Wer beim Auslesen nur `uint` und `ulong` behandelt, verliert `FormFactor` und `MemoryDevices` lautlos — es sieht aus wie ein fehlendes Feld. |

Anders als Prozessor und Mainboard ändert sich das im Betrieb. Die Erhebung
läuft deshalb auf Anforderung erneut; die Systemseite hat dafür eine Schaltfläche.

### 8.11 Ordnerbelegung

`System.IO.Enumeration.FileSystemEnumerator<T>`, eine Instanz je Verzeichnisebene.
Unter Windows ist das **nicht** `FindFirstFileEx`, sondern `NtQueryDirectoryFile`
mit `FILE_FULL_DIR_INFORMATION`: ein Aufruf füllt einen Puffer mit vielen
Einträgen, und die Dateigröße steht bereits darin. Kein zweiter Zugriff je Datei,
kein Dateihandle — deshalb sind auch `pagefile.sys` und `hiberfil.sys` kein
Sonderfall, obwohl sie gesperrt sind.

| Stellschraube | Wert | Warum |
|---|---|---|
| `AttributesToSkip` | `0` | Die Voreinstellung `Hidden\|System` verschluckte ausgerechnet `pagefile.sys`, `hiberfil.sys` und `ProgramData` — also gerade das, was eine volle Partition erklärt. |
| `IgnoreInaccessible` | `false` | Nur so kommt `ContinueOnError`. Ein übersprungener Ordner wird gezählt und genannt, statt stillschweigend zu fehlen. |
| `BufferSize` | 64 KB | Statt der voreingestellten 4 KB. Spart bei `WinSxS` und `Windows\Installer` ein Vielfaches an Aufrufen. |
| `RecurseSubdirectories` | `false` | Die Rekursion steuert der Scanner selbst — sonst ließen sich Abzweigungen nicht aussparen und jeder Ordner bräuchte seine Summe wieder aus Pfadzeichenketten. |

**Abzweigungen werden nicht verfolgt.** `C:\Users\All Users` zeigt auf
`C:\ProgramData` und `AppData\Local\Application Data` auf sich selbst — ohne diese
Bedingung liefe der Durchlauf im Kreis und zählte doppelt. Eingehängte Volumes
sind ebenfalls Abzweigungen; ihr Inhalt gehört zu einer anderen Partition.

**Dateien werden im Regelfall keine Knoten.** Ein volles `C:` hat gemessen 1,04
Mio. Dateien, aber nur 291 000 Ordner. Eine Datei fließt in die Summe ihres
Ordners ein und verschwindet; nur ab 16 MB bekommt sie einen eigenen Eintrag.
Sonst fehlten `hiberfil.sys` und `pagefile.sys` ausgerechnet in der Ansicht, die
erklären soll, warum die Platte voll ist.

Der Baum liegt als **flaches Feld aus Strukturen** in Blöcken zu 65 536 Knoten.
Ein Objektgraph mit einer Kinderliste je Ordner kostete das Vierfache und legte
eine Viertelmillion Objekte in Generation 2 — eine GC-Pause von mehreren hundert
Millisekunden in einer Anwendung, deren Zweck ein ruckelfreies Diagramm im
Sekundentakt ist. Die Blöcke wandern nie, weil sonst ein wachsendes Feld beim
Umkopieren die Schreibzugriffe der übrigen Worker in das verwaiste alte Feld
laufen ließe. Der Index eines Kindes ist immer größer als der seines Elternteils;
das Aufsummieren ist deshalb eine einzige Rückwärtsschleife statt einer Rekursion.
Gemessener Bedarf für `C:`: rund 19 MB.

**Parallelität:** ein `ConcurrentStack` und N eigene Threads mit
`ThreadPriority.BelowNormal`, nicht `Parallel.ForEach` über die oberste Ebene —
`C:\Windows` und `C:\Users` sind 80 % der Arbeit, eine feste Aufteilung ließe die
übrigen Threads nach zwei Sekunden leerlaufen. Tiefensuche statt Breitensuche,
sonst hielte der Stapel über 100 000 Pfadzeichenketten gleichzeitig. **Eigene**
Threads, weil acht blockierte Threadpool-Threads die Nachschub-Heuristik des Pools
auslösen und damit Aussetzer in den 1-Hz-Takt setzen würden.

Der Grad hängt am Medium: **2 auf einer Festplatte, 4–8 auf einer SSD**.
`Win32_DiskDrive.MediaType` taugt zur Unterscheidung nicht — es meldet auch für
SSDs „Fixed hard disk media", das Feld beschreibt das Wechselmedien-Bit und nicht
die Technik. `IOCTL_STORAGE_QUERY_PROPERTY` mit
`StorageDeviceSeekPenaltyProperty` beantwortet die Frage direkt
(`ResMon.Core/Native/StorageDevice.cs`).

Gemeldet wird die **logische** Größe, nicht die Belegung auf dem Datenträger. Was
das bedeutet, steht in README „Abweichungen".

### 8.12 Systemstart

Der Task-Manager zeigt zum Autostart eine Liste und eine Einstufung in drei
Stufen „Startauswirkung". Beides beantwortet die eigentliche Frage nicht: *warum*
dauert der Start so lange. Windows misst dafür genug — nur an fünf verschiedenen
Stellen, von denen keine für sich allein etwas taugt.

| Was | Quelle | Was sie beiträgt |
|---|---|---|
| Startdauer und Phasen | `Microsoft-Windows-Diagnostics-Performance/Operational`, Ereignis 100 | Die Aufteilung des Starts in Kernel, Treiber, Geräte, Prefetch, SMSS, kritische Dienste, Profil, Explorer und Nachlauf — in Millisekunden. Dieselbe Grundlage, aus der der Task-Manager seine drei Stufen bildet |
| Auffällige Einzelposten | dasselbe Protokoll, 101 – 109 | Anwendung, Treiber, Dienst oder Gerät, das länger als üblich brauchte, mit `TotalTime` und `DegradationTime` |
| **Startkette** | `Microsoft-Windows-Shell-Core/Operational`, 9707/9708, 62408/62409, 62170/62171 | Start- und Endzeitstempel **je Autostart-Befehl**, samt vergebener PID |
| Dienst-Zeitlimits | System-Protokoll, 7009 und 7011 | Die gewartete Zeit als Zahl im Ereignis — 30 000 oder 90 000 ms, in denen der Start stillsteht |
| Umfeld | System-Protokoll 5719, GroupPolicy 8001/8002, Benutzerprofildienst 1/2 | Domänenanmeldung, Richtlinienverarbeitung, Profilladezeit — im Firmennetz die üblichen Verdächtigen |

**Die Startkette ist der Kern.** Ihr entscheidender Befund steckt nicht in den
Ereignissen selbst, sondern in ihren Zeitstempeln: das Ende-Ereignis eines
Befehls trägt denselben Zeitstempel wie das Start-Ereignis des nächsten. Der
Explorer arbeitet die Autostart-Einträge also **nacheinander** ab, und die Dauer
eines Glieds ist damit nicht nur seine eigene Startzeit, sondern die Wartezeit
aller folgenden. Ein Eintrag, dessen Startaufruf hängt, ist als langer Balken
sichtbar, ohne dass man raten muss. Das ist die einzige Stelle im System, an der
sich das rückwirkend nachlesen lässt.

**Gelesen wird ausschließlich über `EventData`, nie über den angezeigten Text.**
Der ist lokalisiert und bände die Auswertung an die Systemsprache — dieselbe
Überlegung wie bei `PdhAddEnglishCounterW` in §8.1. Die Felder tragen feste
englische Namen. Anbieter mit Manifest benennen sie (`Command`, `TotalTime`),
die alten Quellen — allen voran der Dienststeuerungs-Manager — schreiben
`param1`, `param2`; ganz alte lassen den Namen weg, dann zählt die Position.
Alle drei Formen werden abgefragt.

**Zwei Protokolle sind zugriffsgeschützt**, `Diagnostics-Performance` und
`GroupPolicy`. Die Anwendung läuft erhöht (§14) und kommt heran; trifft sie den
Fall dennoch an, wird er als Einschränkung gemeldet statt als leerer Abschnitt.
Geprüft wird das mit einem **echten Lesezugriff**: der naheliegende Weg über
`EventLogConfiguration` taugt nicht, weil Windows die Kanaldefinition auch
unerhöht herausgibt — gemessen meldet sie für beide gesperrten Protokolle Erfolg,
während der erste `ReadEvent` wirft.

#### Inventar

Erfasst werden dieselben Quellen wie bei Autoruns: Run- und RunOnce-Schlüssel
beider Zweige samt `WOW6432Node`, beide Startordner, geplante Aufgaben mit
Anmelde- oder Startauslöser, Dienste mit Starttyp „Automatisch" und die
Startaufgaben von Store-Anwendungen. Der Task-Manager zeigt davon die Hälfte;
geplante Aufgaben und Dienste laufen genauso beim Start an und sind
erfahrungsgemäß die teureren. Sie stehen deshalb dabei, aber getrennt benannt —
ein Dienst hält den Desktop anders auf als ein Run-Eintrag, und eine Aufgabe mit
einer Stunde Verzögerung hält ihn gar nicht auf.

Geplante Aufgaben werden aus den XML-Dateien unter `%windir%\System32\Tasks`
gelesen statt über die COM-Schnittstelle: Auslöser und Verzögerung stehen dort im
Klartext, und es spart eine Interop-Schicht für Daten, die ohnehin als XML
vorliegen. Der Ordner ist nur erhöht lesbar.

**Nicht jede Aufgabe startet ein Programm.** Der überwiegende Teil der
Windows-eigenen ruft über einen `ComHandler` eine DLL im Kontext des
Aufgabenplaners auf und hat gar kein `Exec`-Element. Solche Aufgaben durch die
Befehlszeilen-Auswertung zu schicken hieße, sie als „leerer Eintrag" zu melden —
auf der Referenzmaschine waren das **einundzwanzig von sechsundzwanzig
Befunden**, und die fünf echten gingen darin unter. Sie bekommen deshalb die
Klassenkennung des Handlers als Befehl und den Vermerk „COM-Handler" statt einer
Auffälligkeit.

**Ein Befehl ohne Verzeichnis ist keine fehlende Datei.** `winget.exe` etwa liegt
als Ausführungsalias unter `WindowsApps` und wird über den Suchpfad gefunden;
`File.Exists` prüft dagegen relativ zum Arbeitsverzeichnis und meldet
zuverlässig „fehlt". Solche Befehle werden erst im Suchpfad gesucht; bleibt es
dabei, dass sie nicht auffindbar sind, steht die Zelle **leer** statt einen
Befund zu behaupten — eine unbeantwortbare Frage ist keine Antwort.

**Der Zustand eines Eintrags steht nicht am Eintrag.** Er liegt unter
`Explorer\StartupApproved` als zwölf Byte: das erste trägt den Zustand, die
Bytes 4 bis 11 eine FILETIME. Für die Auswertung des ersten Bytes kursieren
mehrere Regeln; nachprüfbar ist sie am Protokoll. Auf der Referenzmaschine wurden
genau die Einträge mit **gerader** Zahl (2 und 6) vom Explorer ausgeführt und die
mit ungerader (1 und 3) nicht — gerade heißt aktiv. Der Zeitstempel ist der
Moment des Abschaltens und beantwortet die Frage „habe ich das selbst
abgeschaltet oder war das ein Programm".

Die Befehlszeile wird an der **ersten Endung `.exe`** zerlegt, nicht am ersten
Leerzeichen und nicht an der ersten existierenden Datei. Ein Schnitt am
Leerzeichen zerlegt `Docker Desktop.exe` in der Mitte; eine Suche nach der ersten
existierenden Datei findet für eine deinstallierte Anwendung gar nichts und
meldet dann `C:\Program` als fehlende Datei statt des tatsächlichen Pfades. Die
Endung trifft beide Fälle und schneidet zugleich bei
`Update.exe --processStart Discord.exe` an der richtigen der beiden Nennungen.

#### Wartekette und Handles

Was beim letzten Start in ein Zeitlimit lief, steht im Protokoll. Was *jetzt
gerade* hängt, steht nirgends: ein Prozess mit 0 % CPU-Last kann beschäftigt sein
oder blockiert, und von außen sieht beides gleich aus. `GetThreadWaitChain`
(`Native/WaitChain.cs`) — dieselbe Funktion hinter „Wartekette analysieren" im
Task-Manager — sagt, worauf ein Thread wartet und wer ihn hält, über
Prozessgrenzen hinweg und quer durch kritische Abschnitte, Mutexe, ALPC-Anfragen
und COM-Aufrufe. Ein Ring in der Kette ist eine echte Verklemmung.

Die Handle-Tabelle kommt aus einem einzigen
`NtQuerySystemInformation(SystemExtendedHandleInformation)`: ein Aufruf liefert
alle Handles des Systems mit besitzender PID und Objektart, es gibt also keinen
Aufruf je Prozess. Die Zuordnung von Objektartkennung zu Name geschieht
**durch Ausprobieren** — je gesuchter Art wird ein Objekt selbst angelegt und
nachgeschlagen, welche Kennung der eigene Prozess dafür bekam. Die Kennungen sind
nicht festgelegt und verschieben sich zwischen Windows-Fassungen; sie über
`ObjectTypesInformation` sauber aufzulösen hieße, eine Kette von Strukturen mit
variabler Länge und eigener Ausrichtungsregel zu lesen, die bei jeder Änderung
still falsche Namen liefern kann.

Beim Auflösen der Dateinamen lauert die bekannteste Falle dieser Schnittstelle:
`NtQueryObject` blockiert **für immer**, wenn der Handle auf eine synchrone Named
Pipe zeigt, deren Gegenstelle nicht liest. Process Explorer läuft dafür in einen
eigenen Thread und bricht ihn nach einem Zeitlimit ab. Hier wird der Fall
stattdessen vorher ausgeschlossen: `GetFileType` verrät ohne Blockierung, ob ein
Handle eine Datei, eine Pipe oder ein Zeichengerät ist, und nur bei einer echten
Datei wird nach dem Namen gefragt. Das ist kein Zeitlimit, sondern der Verzicht
auf die Frage, die hängen bleibt — und spart den Thread, den .NET ohnehin nicht
sicher abbrechen könnte.

#### Was in keinem Protokoll steht

Was ein einzelner Autostart-Vorgang an **Rechenzeit und Datenträgerzugriffen**
gekostet hat. Die Protokolle kennen nur Anfang und Ende; dazwischen sieht nur
eine Ablaufverfolgung. Zwei Quellen kommen infrage, und die erste ist die
überraschende:

1. **Windows zeichnet den Start gelegentlich selbst auf.** Der
   Diagnoserichtliniendienst legt
   `%windir%\System32\WDI\LogFiles\BootPerfDiagLogger.etl` an — dieselbe Spur,
   aus der auch die Ereignisse 100 bis 110 entstehen. Sie ist ohne Neustart und
   ohne Vorbereitung da; der Ordner ist nur erhöht lesbar, und die Datei ist in
   Benutzung und muss vor dem Lesen kopiert werden.

   **Sie stammt nicht zwangsläufig vom letzten Start.** Die Startdiagnose läuft
   nicht bei jedem Hochfahren, sondern wenn sie anspringt — auf der
   Referenzmaschine war die Datei ein volles Jahr alt, Sitzungszeitstempel und
   Dateizeit stimmten darin überein. Wer das nicht bemerkt, sucht die Ursache
   eines heutigen Problems in Zahlen, die es damals noch nicht gab. Die Dateizeit
   wird deshalb gegen den Einschaltzeitpunkt aus §8.9 geprüft, und passt sie
   nicht, sagt die Oberfläche das als Erstes.
2. **Eine eigene Aufzeichnung** über `wpr.exe -addboot` / `-stopboot`
   (`Startup/BootTrace.cs`). Ausführlicher, kostet aber einen Neustart und
   mehrere hundert Megabyte. `wpr.exe` liegt in jedem Windows 10 und 11; das
   früher übliche `xbootmgr` aus dem Windows Performance Toolkit ist entbehrlich.

Die Anwendung stellt scharf, sagt es, und **wartet**. Sie startet den Rechner
nicht selbst neu — ein Monitor, der den Rechner neu startet, ist ein Monitor, den
man nicht laufen lassen kann.

Die Rechenzeit aus einer solchen Spur ist eine **Schätzung aus Abtastungen**,
keine Messung: der Kernel unterbricht in festem Takt und notiert, welcher Thread
gerade läuft. Bei der üblichen Millisekunde je Abtastung und Kern entspricht eine
Abtastung rund einer CPU-Millisekunde. Über einen Startvorgang von Sekunden
mittelt sich das aus, für einen Vorgang von 20 ms wäre die Zahl wertlos — deshalb
steht die Zahl der Abtastungen mit im Ergebnis.

**Windows' eigene Aufzeichnung enthält keine Abtastungen.** Auf der
Referenzmaschine gemessen: 355 Prozesse und 43 000 Datenträgerzugriffe, aber
null Profilereignisse — der Diagnoserichtliniendienst schaltet die
Profilablaufverfolgung nicht ein. Aus dieser Quelle kommen also
Datenträgerzugriffe und Startzeitpunkte, aber keine Rechenzeit. Die Oberfläche
sagt das und lässt die Spalte leer, statt eine Spalte voller Nullen wie eine
Messung aussehen zu lassen.

Zwei weitere Eigenheiten dieser Quelle: Prozesse, die beim Beginn der
Aufzeichnung schon liefen, tauchen in keinem Start-Ereignis auf — ihre Namen
stehen ausschließlich in der Bestandsaufnahme, die ETW zu Beginn und Ende einer
Sitzung schreibt (`ProcessDCStart`/`ProcessDCStop`). Ohne sie steht im Ergebnis
„PID 4" statt „System", und gerade die langlebigen Systemprozesse haben die
meisten Zugriffe. Und der Zeitstempel der ETW-Sitzung ist unzuverlässig: er
meldete ein Datum ein Jahr vor dem letzten Start. Maßgeblich ist die
Änderungszeit der Datei, die deshalb daneben steht.

## 9. Sampling-Takte

| Intervall | Aufgabe |
|---|---|
| 1000 ms | PDH-Aggregat: CPU, RAM, GPU gesamt → Overlay |
| 2000 ms | LHM-Update: Temperaturen, Takt, Power, Lüfter, Akku |
| 2000 ms | Prozessliste, Fensterliste und Verbindungstabelle — **nur wenn das Detailfenster geöffnet ist** |
| 30 s | Dienst-Cache aktualisieren |
| — | Ordnerbelegung (§8.11): **kein Takt**, ausschließlich auf Knopfdruck |
| — | Startanalyse (§8.12): **kein Takt**. Sie liest Ereignisprotokolle, Registry und Aufgabenplanung und ändert sich zwischen zwei Systemstarts ohnehin nicht |

Der Monitor darf nicht selbst zum Lastverursacher werden. Prozess-Enumeration ist
der mit Abstand teuerste Teil und wird deshalb bedarfsgesteuert ausgeführt.

Ordner-Scan und Startanalyse sind die Datenquellen **ganz ohne** Takt. Der Scan
kostet je nach Medium Sekunden bis Minuten und wird deshalb nie von selbst
angestoßen, nie wiederholt und beim Schließen des Detailfensters abgebrochen. Die
Startanalyse läuft einmal, wenn ihr Reiter zum ersten Mal aufgeschlagen wird, und
danach nur noch auf Knopfdruck — ihr Gegenstand ist ein Ereignis der
Vergangenheit, ein zweiter Lauf lieferte dasselbe Ergebnis.

## 10. Ringpuffer

`RingBuffer<AggregateSample>` mit 300 Einträgen (5 Minuten bei 1 Hz). Gespeichert
werden nur CPU-Prozent, GPU-Prozent, RAM-Prozent und die beiden Temperaturen —
also wenige Fließkommazahlen pro Eintrag. Der Speicherbedarf ist vernachlässigbar.

Verwendung: Sparklines im Overlay und ein Verlaufsdiagramm im Detailfenster. Die
Struktur ist außerdem die Grundlage für die später geplanten Trigger-Snapshots;
eine Prozess-Historie wird bewusst **nicht** mitgeführt, da sie teuer wäre.

## 11. Overlay-Fenster

### WPF-Konfiguration

```xml
WindowStyle="None"
AllowsTransparency="True"
Background="Transparent"
Topmost="True"
ShowInTaskbar="False"
ResizeMode="NoResize"
```

Zusätzlich muss am `CoreWebView2Controller` die Eigenschaft
`DefaultBackgroundColor` auf `Colors.Transparent` gesetzt werden, sonst rendert
WebView2 ein deckend weißes Rechteck.

### Verschieben

`DragMove()` allein funktioniert nicht, weil WebView2 die Mausereignisse
abfängt. Ablauf:

1. JavaScript fängt `mousedown` auf der Kopfzeile ab
2. `chrome.webview.postMessage({ cmd: "drag" })`
3. C# behandelt `WebMessageReceived` und ruft `DragMove()` auf

### Asset-Einbindung

`CoreWebView2.SetVirtualHostNameToFolderMapping("app.local", wwwrootPath,
CoreWebView2HostResourceAccessKind.Allow)`, anschließend Navigation zu
`https://app.local/overlay.html`. Sauberer als `file://` und erhält normale
CORS-Semantik.

### Layout

Kompakte Karte, etwa 230 × 150 px:

- Drei Zeilen (CPU / GPU / RAM), je Balken, Prozentwert und Temperatur
- Optionale Sparkline pro Zeile
- Schaltfläche "Details" am unteren Rand
- Kopfzeile als Ziehbereich

### Klick-Durchlässigkeit (optional)

Über `SetWindowLong` mit `WS_EX_TRANSPARENT | WS_EX_LAYERED` umschaltbar. Der
sogenannte WorkerW-Trick zum Verankern auf Wallpaper-Ebene ist fragil und wird
in v1 nicht umgesetzt.

Die Einstellung braucht einen Weg zurück, sonst ist sie eine Einbahnstraße: sie
wird gespeichert, und ein Overlay, das keine Klicks annimmt, führt niemanden mehr
zu seiner eigenen Einstellung — die Schaltfläche „Details" ist ja mit
durchlässig. Deshalb gilt:

- Solange **Strg+Umschalt** gehalten wird, nimmt das Overlay wieder Klicks an.
  Ein Tastenzustand lässt sich nicht abonnieren, wenn die Tastatur woanders
  hingeht; ein Timer fragt ihn deshalb alle 120 ms mit `GetAsyncKeyState` ab —
  aber nur, solange die Durchlässigkeit eingeschaltet ist. Das Overlay zeigt den
  Zustand mit farbigem Rahmen und dem Zusatz „klickbar" an.
- **Die Karte selbst nennt die Tastenkombination.** Solange die Durchlässigkeit
  eingeschaltet ist, steht am unteren Rand eine Leiste mit
  „Strg + Umschalt halten zum Klicken"; wird die Kombination gehalten, wechselt
  sie zu „gehalten – Fenster ist klickbar". Sie ist die einzige Stelle, an der
  der Ausweg zu sehen ist, ohne ihn schon zu kennen: das Tray-Menü ist
  eingeklappt, und die Einstellungsseite erreicht man nur über eine
  Schaltfläche, die dann selbst nicht klickbar ist.
- Beim Einschalten und bei jedem Start mit eingeschalteter Durchlässigkeit sagt
  eine Tray-Meldung, wie man wieder herauskommt.
- Das Tray-Menü bleibt der zweite Weg; es ist von der Durchlässigkeit nicht
  betroffen.

## 12. Bridge-Protokoll

**C# → JavaScript:** `CoreWebView2.PostWebMessageAsJson(snapshotJson)`

**JavaScript → C#:** Ereignis `WebMessageReceived`, schmales Command-Set:

| Command | Nutzlast | Wirkung |
|---|---|---|
| `drag` | — | `DragMove()` auf dem Overlay |
| `openDetail` | — | Detailfenster öffnen |
| `setOpacity` | `value: 0..1` | Fenster-Deckkraft |
| `close` | — | Anwendung beenden |
| `startFolderScan` | `path: "C:\\"` | Ordner-Scan starten (§8.11). Der Host nimmt **nur Laufwerkswurzeln** an. |
| `cancelFolderScan` | — | Laufenden Scan abbrechen |
| `expandFolder` | `scan`, `node` | Kinder eines Knotens nachfordern |
| `openFolder` | `scan`, `node` | Im Explorer zeigen |
| `copyFolderPath` | `scan`, `node` | Pfad in die Zwischenablage |
| `requestStartup` | — | Startanalyse erheben (§8.12) |
| `bootTrace` | `key: arm\|cancel\|stop\|forget` | Startaufzeichnung schalten |
| `analyzeTrace` | `key: windows\|own` | Eine Aufzeichnung auswerten |
| `openTrace` | — | Die eigene Aufzeichnung im Explorer zeigen |
| `requestHandles` | — | Handles aller Prozesse zählen |
| `inspectProcess` | `pid`, `name` | Wartekette und offene Dateien eines Prozesses |

Nach dem Start reist **nie wieder ein Pfad nach innen**: alles Weitere läuft über
ganzzahlige Kennungen in einen Baum, den der Host selbst gebaut hat. Die
Laufwerkswurzel wird gegen `DriveInfo.GetDrives()` geprüft; Netzlaufwerke bleiben
draußen, ein Scan über eine langsame Leitung ist eine Falle und keine Funktion.
Sie stehen deshalb auch nicht in der Auswahl — dafür trägt `VolumeInfo` die
Laufwerksart mit. Ein Eintrag, der beim Anklicken jedes Mal abgelehnt würde, wäre
schlechter als keiner. Verglichen wird ohne abschließenden Trennstrich: die
Systemübersicht führt die Laufwerke als „C:", `DriveInfo` nennt sie „C:\".
Die `scan`-Kennung verhindert, dass ein Nachschlag aus einem überholten Lauf in
einen anderen Baum zeigt.

Der Rückweg kennt den Nachrichtentyp `scan` mit den Phasen `running`, `done`,
`children`, `cancelled` und `error`. Er reist **nicht** in der Messnutzlast mit:
die ist darauf gebaut auszulassen, was sich nicht geändert hat, ein Scan ist
stoßweise und unverwandt, und bis zu eine Sekunde Verzug auf den Übergang
„fertig" fühlte sich kaputt an. Der Fortschritt wird **geholt, nicht geschickt** —
der Scanner hält flüchtige Zähler, ein `DispatcherTimer` liest sie viermal je
Sekunde ab. Ein Rückruf je Ordner wären 290 000 Delegataufrufe durch den
Synchronisierungskontext.

Ein Command zum Beenden von Prozessen ist bewusst nicht enthalten.

## 13. Detailfenster

Normales WPF-Fenster mit WebView2. Tabelle in HTML; Sortierung, Filterung,
Gruppierung und Aggregation laufen vollständig in JavaScript.

Acht Reiter: **Prozesse**, **Energie**, **Verbindungen**, **System**,
**Speicher**, **System-Start**, **Logs**, **Einstellungen**. Die Kacheln über den
Reitern gelten für alle.

**Spaltenbreiten** sind in allen Tabellen an der rechten Kante der
Spaltenüberschrift ziehbar, Doppelklick setzt eine Spalte zurück. Solange nichts
gezogen wurde, bleibt es bei der inhaltsabhängigen Verteilung des Browsers
(`table-layout: auto`) — die passt sich der Fensterbreite an. Der erste Zug
friert die gerade gezeichneten Breiten ein und stellt auf feste Breiten um;
anders ließe sich eine einzelne Spalte nicht einstellen, weil bei automatischer
Verteilung der Inhalt jeden gesetzten Wert überschreibt. Ab dann scrollt die
Tabelle notfalls waagerecht. Die jeweils letzte Spalte bekommt keine feste
Breite und nimmt den Rest auf, sonst klaffte bei breitem Fenster eine Lücke
hinter der Tabelle. Gespeichert wird je Tabelle im `localStorage`.

### 13.1 Prozesse

Spalten: Name, PID, Benutzer, Zustand, CPU %, Threads, Arbeitsspeicher, Privat,
GPU %, GPU-Engine-Aufschlüsselung, VRAM, Download, Upload, E/A lesen,
E/A schreiben, Ports, Dienste, Datei, Notiz. Auswahl und Reihenfolge sind
einstellbar und bleiben im `localStorage` erhalten.

**Zustand** meldet, was hängt und was kürzlich weggebrochen ist (§8.9). Solche
Zeilen sind eingefärbt und überstehen den Filter „nur aktive" — ein hängender
Prozess erzeugt keine Last, und genau dann sucht man ihn.

**Ports** ist bewusst kurz gehalten: ab drei Ports steht nur noch die Anzahl. Die
vollständige Liste steht im Tooltip, geordnet im Reiter „Verbindungen".

Bedienelemente:

- Umschalter „Gleichnamige zusammenfassen": fasst Kindprozesse unter ihrem
  Elternprozess zusammen, **solange sie dieselbe ausführbare Datei sind**. Die
  Bedingung ist der springende Punkt. Ohne sie rollt jeder Prozess bis zum
  obersten noch lebenden Vorfahren, und unter Windows ist das für fast alles
  `wininit.exe`: von mehreren hundert Prozessen bleibt gut ein Dutzend Zeilen
  übrig, eine davon mit dem halben System als Kindern, und die Einteilung nach
  Art wird wertlos. Mit der Bedingung bleibt die Liste fast vollständig, und
  zusammengefasst wird genau dort, wo ein Programm viele gleichnamige
  Hilfsprozesse startet — ein Browser etwa mit einem Prozess je Tab. Das ist die
  Zusammenfassung, um die es in §1 geht.
- Die Einteilung nach Art ist **fest**, kein Schalter: Apps, Hintergrund- und
  Windows-Prozesse stehen als eigene Abschnitte untereinander, jeder mit einer
  klebenden Überschrift. **Sortiert wird weiter über die Spalten** — die Liste
  wird zunächst als Ganzes sortiert und dann in die Abschnitte aufgeteilt; eine
  Teilmenge einer sortierten Liste ist selbst sortiert, die gewählte Spalte gilt
  also in jedem Abschnitt. Ein Klick auf die Überschrift klappt den Abschnitt zu
  — wer eine App sucht, will die Hintergrundprozesse nicht durchscrollen. Welche
  Abschnitte zu sind, bleibt im `localStorage` erhalten.
- Textfilter über Name, Beschreibung, Benutzer, Fenstertitel, Dienst, Pfad,
  Ports, Zustand und Notiz
- Verlaufsdiagramm der letzten 5 Minuten oberhalb der Tabelle

Angeheftete Zeilen stehen weiter ganz oben und überstehen jeden Filter.

### 13.2 Energie

- Kacheln: gemessene Leistung gesamt, CPU-Paket (mit Sockeltemperatur),
  Grafikkarte und — sofern vorhanden — der Akku
- Verlauf der Leistungsaufnahme über 5 Minuten, Achse selbstskalierend
- Alle Temperatursensoren, nach Herkunft gruppiert (Prozessor, Grafikkarte,
  Mainboard, ACPI-Thermalzonen) — nebeneinander sähen die beiden „CPU"-Sensoren
  sonst nach zwei Messungen derselben Sache aus
- Lüfterliste mit Drehzahl und Ansteuerung
- Alle Leistungssensoren einzeln
- Akku im Einzelnen: Ladestand, Lade- oder Entladeleistung, Spannung,
  Restlaufzeit, heutige und ursprüngliche Kapazität, Verschleiß
- „Energieverbrauch je Prozess": ein Einflusswert aus CPU-, GPU-, Datenträger-
  und Netzlast, dazu eine Schätzung in Watt

Die Wattangabe je Prozess verteilt die **gemessene** Aufnahme von Prozessor und
Grafikkarte nach dem Lastanteil des Prozesses. Nenner ist die Summe der
Prozesslasten, nicht 100 % — sonst käme bei geringer Auslastung nur ein
Bruchteil der tatsächlich gemessenen Watt heraus. Das ist eine Schätzung und
wird im UI auch so benannt; eine Messung je Prozess gibt die Hardware nicht her.

### 13.3 Verbindungen

Portübersicht und Verbindungstabelle teilen sich die Höhe zu gleichen Teilen —
die Übersicht ist die interessantere der beiden und war als schmaler Streifen zu
klein. Die Ports laufen im **Spaltensatz**, erst die Spalte hinunter und dann in
der nächsten weiter: aufsteigend sortiert steht damit 80 unter 22 und nicht
daneben, und man liest eine Portliste der Reihe nach, nicht zeilenweise.

Oben die **Portübersicht**: eine Zeile je offenem Port statt einer je Socket.
Ein Dienst, der auf IPv4 und IPv6 lauscht, steht in der Verbindungstabelle
zweimal — hier einmal. Jede Zeile nennt Portnummer, Protokoll, den Dienst hinter
der Nummer (aus einer Tabelle der geläufigen Ports), den haltenden Prozess und
die Reichweite: `0.0.0.0` und `::` heißen „im Netz erreichbar", `127.0.0.1` und
`::1` heißen „nur lokal". Die Kopfzeile zählt beides zusammen. Das ist die
Antwort auf die Frage, die man mit „welche Ports sind offen" meint.

**UDP-Ports ab 49152 bleiben draußen.** Windows vergibt diesen Bereich für
ausgehende Verbindungen; ein UDP-Socket darin ist die Rückadresse einer eigenen
Anfrage, kein Dienst. Auf einem laufenden System stellen sie die große Mehrheit
der UDP-Einträge — sie mitzuzählen vervielfacht die Zahl der angeblich offenen
Ports und macht sie damit unbrauchbar. Die Kopfzeile nennt sie trotzdem, und
in der Tabelle darunter stehen sie weiterhin. TCP-Ports in diesem Bereich bleiben
sichtbar: dort liegen die dynamischen RPC-Endpunkte, und die lauschen wirklich.

Darunter die vollständige Tabelle aus §8.8: Protokoll, lokale Adresse und Port,
Gegenstelle und Port, Zustand, PID und Prozessname. Sortierbar über alle
Spalten, filterbar über Text, dazu Schalter für lauschende Ports, UDP und
Loopback. Die Nutzlast ist auf 2000 Einträge begrenzt, angezeigt werden 600 —
darüber hinaus ist die Liste ohnehin nicht mehr zu lesen.

### 13.4 System

Der feste Teil (Betriebssystem, Laufzeit, Prozessor, Grafik, Arbeitsspeicher,
Mainboard) und darunter die Geräte aus §8.10 als Karten mit Zustandspunkt:
Netzwerkadapter, Bluetooth, USB. Leere Gruppen bleiben stehen und sagen „auf
diesem Rechner nicht vorhanden" — bei einer Frage nach der Konnektivität ist das
eine Antwort, ein fehlender Abschnitt wäre keine. Eine Schaltfläche erhebt die
Übersicht neu.

**Laufzeit** nennt zwei Zeitpunkte, aber nur, wenn es wirklich zwei sind:
„Eingeschaltet" (Ereignis 27, §8.9) und „Letzter vollständiger Start" (aus
`GetTickCount64`). Bei einem Kaltstart fallen beide zusammen und der zweite
entfällt. Dieselbe Regel gilt für das letzte Herunterfahren: den Eintrag
„geordnet beendet" (6006) schreibt der Ereignisprotokolldienst nur, wenn er
wirklich stoppt — beim Schnellstart wird er mit der Kernelsitzung eingefroren.
Der jüngste 6006-Eintrag stammt deshalb fast immer von dem **Neustart**, mit dem
die laufende Sitzung begann, und trägt denselben Zeitstempel wie sie; dann steht
er nicht noch einmal unter eigenem Namen da. Ein *unerwartetes* Ende (6008) wird
dagegen immer gemeldet — mit dem Hinweis, dass dessen Zeitstempel der des
nächsten Starts ist, denn Windows kann den Eintrag erst schreiben, wenn es
wieder läuft.

**Cache-Größen** kommen aus `GetLogicalProcessorInformationEx` statt aus WMI:
`Win32_Processor` kennt L2 und L3, aber kein Feld für L1, und
`Win32_CacheMemory` wirft alle Caches einer Ebene zu einer Zahl zusammen. Die
Kernelfunktion liefert jeden Cache einzeln, samt der Trennung in Daten- und
Befehlscache, die genau bei L1 existiert: „384 KB (6 × 32 KB Daten + 6 × 32 KB
Befehle)". WMI bleibt Rückfallebene für L2 und L3.

### 13.5 Speicher

Beantwortet die Frage, die der System-Reiter offen lässt: nicht *dass* die
Partition eng wird, sondern **wo** der Platz liegt. Der Durchlauf (§8.11) startet
ausschließlich auf Knopfdruck; vorgewählt ist das vollste Laufwerk, denn deswegen
ist man hier.

Nebeneinander stehen **Baum und Kachelkarte** — der Baum trägt die Arbeit, die
Karte den Blick. Beide zeigen dieselben Knoten, die Auswahl ist gekoppelt: ein
Klick in die Karte markiert die Zeile und klappt ihre Vorfahren auf, ein
Doppelklick macht den Knoten zur neuen Kartenwurzel. Eines allein genügte nicht.
Die Karte beantwortet „wo liegt der Platz" in einem Blick, kann aber weder
sortieren noch durchsuchen und verschluckt alles Kleine; der Baum nennt Pfad und
Zahl genau, verlangt dafür aber, Ebene für Ebene hinabzusteigen.

**Baum:** je Ebene absteigend sortiert, mit Anteilsbalken. Der Balken bezieht sich
auf die **Wurzel des Scans**, nicht auf den Elternordner — sonst stünde in jedem
Zweig 100 % und die Zahl sagte nichts. Eine Zeile „übrige" fängt auf, was keine
eigene Zeile hat: die kleinen Dateien im Ordner und die Kinder, die für den Auszug
zu klein waren. Damit gehen die Prozente auf.

**Karte:** Squarified Treemap nach Bruls, Huizing und van Kesteren — die Kinder
werden zeilenweise verteilt, und eine Zeile wird genau so lange gefüllt, wie sich
das Seitenverhältnis der Kacheln dadurch verbessert. Ohne das entstünden lange
dünne Streifen, deren Flächen niemand vergleichen kann. Verschachtelt wird nach
**Kachelgröße, nicht nach Tiefe**: Ketten wie `Users → Stefan → AppData`
verbrauchen drei Ebenen, ohne etwas zu zeigen, und ausgerechnet darunter liegt
meist die Antwort. Weil jede Ebene 4 px Breite und 20 px Höhe abzieht, endet die
Rekursion von selbst.

Die Farben kommen aus den Schema-Variablen und wandern mit der Tiefe in Richtung
Textfarbe — im dunklen Schema wird es dadurch heller, im hellen dunkler, und der
Kontrast bleibt in beiden erhalten. Fest verdrahtet ist keine.

Unter der Leiste steht, was die Zahlen einschränkt: ein **abgebrochener** Lauf
(dessen halbfertige Zweige zu klein dastehen), der Anteil in Cloud-Platzhaltern
und die Differenz zu der Belegung, die Windows meldet. Einzelne Ordner tragen ein
Zeichen für nicht lesbar, Abzweigung, komprimiert oder Cloud. Ohne diese Angaben
wären die Zahlen darüber falsch zu lesen.

Kontextmenü auf Zeile und Kachel: **Im Explorer öffnen** und **Pfad kopieren**.
Gelöscht wird bewusst nicht aus der Anwendung heraus — sie läuft erhöht (§14), ein
Fehlgriff träfe also auch Systemordner, ohne Papierkorb und ohne Rückgängig. Der
Explorer kann beides besser.

Bei schmalem **und** flachem Fenster weicht die Karte: untereinander blieben ihr
rund 70 Pixel, darin ist keine Fläche mehr mit einer anderen zu vergleichen.

### 13.6 System-Start

Beantwortet die Frage, mit der man vor einem Rechner sitzt, der ewig zum Starten
braucht: **woran liegt es**. Nicht „welche Programme starten mit" — das zeigt der
Task-Manager auch — sondern welcher davon den Start tatsächlich aufgehalten hat
und um wie viele Sekunden.

Der Aufbau folgt der Reihenfolge, in der man fragt:

1. **Kacheln:** Startdauer, Hauptpfad, Nachlauf, Startart, Summe der Startkette.
   Die Startdauer nennt daneben, was üblich wäre — das steht nicht im Ereignis,
   ergibt sich aber aus `BootTime` minus `BootDegradationTime`.
2. **Phasenband:** die Aufteilung des Hauptpfads als ein waagerechter Balken.
   Beantwortet „wo ging die Zeit hin" in einem Blick. Die Einzelabschnitte decken
   den Hauptpfad nicht vollständig ab; was übrig bleibt, steht als eigener Posten
   „Übriger Hauptpfad" darin. Ohne ihn ergäbe die Summe der Balken weniger als
   die Kachel darüber, und das Band behauptete eine Vollständigkeit, die es nicht
   hat.
3. **Befunde:** die eigentliche Antwort. Nach verlorener Zeit geordnet, jeder mit
   Einstufung, Kosten in Sekunden, Erklärung und **Fundstelle**. Ein Befund ohne
   Fundstelle wäre eine Behauptung. Hinweise ohne Zeitkosten — verwaiste
   Einträge, leere Registry-Werte — sind standardmäßig ausgeblendet; sie machen
   die Liste lang und beantworten die gestellte Frage nicht.
4. **Startkette** als Zeitleiste über die volle Breite. Sie ist der Beweis zu den
   Befunden: dass der Explorer nacheinander abarbeitet, sieht man erst, wenn die
   Balken lückenlos aneinanderstoßen. Die Anmeldeaufgaben der Shell sind zu
   Dutzenden vorhanden und je wenige Millisekunden lang; sie stehen als eine
   gezählte Zeile am Ende statt als sechzig Striche.
5. **Autostart-Einträge** als Tabelle. Dienste sind standardmäßig ausgeblendet —
   auf einem Windows-Rechner sind es rund hundert, und sie starten vor der
   Anmeldung, halten die Autostart-Kette also nicht auf. „Läuft nicht" und
   „verzögert" gelten bei einem Dienst nicht als Auffälligkeit; das ist dort der
   Normalfall und färbte sonst die halbe Liste rot.
6. **Wartekette und Handles** für den laufenden Betrieb.
7. **Startaufzeichnung** samt der ausgewerteten Kosten je Prozess.
8. **Einschränkungen** — was die Zahlen darüber einschränkt, gleiche Regel wie
   beim Ordner-Scan (§13.5).

**Bericht kopieren** legt den vollständigen Befund als Text in die
Zwischenablage. Das ist der Grund, warum dieser Reiter auch auf einem fremden
Rechner nützt: wer ein Startproblem untersucht, sitzt selten davor.

### 13.7 Logs

Beantwortet eine einzige Frage: was wird gerade **nicht** gelesen, und warum.
Jeder Eintrag nennt Einstufung (fällt aus / eingeschränkt / Hinweis), Folge und
Grund. Drei Quellen speisen ihn:

1. **Zustandsmeldungen des Hosts** — welcher Zählersatz fehlt, ob der
   Sensortreiber geladen ist, ob der Prozess erhöht läuft. Dieselben Flags, aus
   denen auch die Hinweisleiste oben entsteht, hier aber vollständig und mit
   Erklärung statt nur als Warnung.
2. **`DiagnosticLog`** (`ResMon.Core/Diagnostics`) — die Sammelstelle für
   gefangene Ausnahmen. Die Datenquellen fangen ihre Fehler bewusst ab: eine
   gesperrte Registry, ein abgeschaltetes Ereignisprotokoll oder ein fehlender
   WMI-Anbieter darf die Anwendung nicht anhalten. Der Preis dafür war, dass
   niemand erfährt, *warum* eine Angabe fehlt — genau das steht jetzt hier.
   Gleiche Meldung derselben Quelle wird zusammengefasst und gezählt; eine
   Quelle, die wieder liefert, nimmt ihre Meldung über `Clear` zurück.
3. **Die Prozessliste selbst** — wie viele Zeilen ohne Konto oder ohne Pfad
   dastehen. Das ist kein Fehler, sondern die Grenze dessen, was Windows über
   geschützte Prozesse verrät; als leere Zelle fällt es trotzdem auf und will
   erklärt sein.

Das Protokoll geht nur mit der Nutzlast raus, wenn sich sein Zähler geändert hat
— es steht die meiste Zeit still. Ein Kontrollkästchen blendet zusätzlich alles
ein, was einwandfrei liefert, als Gegenprobe.

## 14. Rechte, Autostart, Konfiguration

**Elevation:** `app.manifest` mit
`<requestedExecutionLevel level="requireAdministrator" uiAccess="false" />`.
Erforderlich, weil LibreHardwareMonitorLib einen Kernel-Treiber lädt.

**Autostart:** *nicht* über den Registry-Run-Key — bei Anwendungen mit
Administratorrechten führt das bei jedem Anmelden zu einer UAC-Abfrage.
Stattdessen legt die Anwendung beim ersten Start eine Aufgabe in der
Aufgabenplanung an: Trigger "Bei Anmeldung", Option "Mit höchsten Privilegien
ausführen".

**Single-Instance:** benannter Mutex, zweite Instanz beendet sich sofort — aber
nicht wortlos. Sie setzt vorher das Ereignis `Local\ResMon.ShowDetail`, worauf
die laufende Instanz ihr Detailfenster zeigt. Ohne das sähe ein zweiter Start
aus, als sei gar nichts passiert: das Overlay hält sich aus Taskleiste und
Alt-Tab heraus. Das Ereignis liegt bewusst im sitzungslokalen Namensraum — das
Anlegen globaler Kernelobjekte setzt ein Recht voraus, das ein unerhöhter Lauf
nicht hat.

**Einstellungen:** `%AppData%\ResMon\settings.json`

```json
{
  "overlay": { "x": 40, "y": 40, "opacity": 0.9, "scale": 1.0, "clickThrough": false },
  "intervals": { "aggregateMs": 1000, "hardwareMs": 2000, "processMs": 2000, "serviceMs": 30000 },
  "visible": { "cpu": true, "gpu": true, "ram": true, "net": true, "disk": true, "temps": true },
  "chart": { "cpu": true, "gpu": true, "ram": true, "net": false, "disk": false },
  "theme": "dark",
  "autostart": true
}
```

`opacity` meint die Deckkraft des Kartenhintergrunds im Overlay, nicht die des
Fensters — Schrift und Kurven bleiben deckend, und `Window.Opacity` bleibt
unangetastet, weil sie das Overlay unklickbar machen würde (§11).

`scale` vergrößert Fenster und Inhalt gemeinsam; die Fensterhöhe ergibt sich aus
den eingeblendeten Zeilen, die die Seite selbst misst und meldet.

`theme` ist eines von `dark`, `light`, `blue`, `red`, `green`, `sepia` und gilt
für beide Fenster. Die Farben stehen doppelt: als CSS-Variablen je Schema und in
`Theme.cs` für Fensterhintergrund, Titelleiste und Rahmen des Detailfensters, die
über DWM-Attribute gesetzt werden. Jedes Schema setzt zusätzlich `color-scheme`,
damit auch die vom Browser gezeichneten Teile — Kontrollkästchen, Regler, das
Löschkreuz im Suchfeld — dazu passen.

**`dark` ist farbneutral**, reines Grau ohne Stich, und `blue` ein deutlich
gesättigtes Marineblau bis in die Flächen hinein. Vorher lagen beide bei einem
leicht bläulichen Dunkelgrau und unterschieden sich fast nur im Akzent — zwei
Schemata, die man nicht auseinanderhalten kann, sind eines. `sepia` ist das
zweite helle Schema: warmes Papier mit Tintenfarben als Akzenten.

Alle drei Oberflächen — Overlay, Einstellungsseite im Detailfenster und
Tray-Menü — schreiben in dieselben Einstellungen und werden nach jeder Änderung
gemeinsam nachgezogen.

## 15. Implementierungsreihenfolge

1. **`Native/PdhQuery.cs`** — englische Counter, Wildcard-Instanzen, Konsolen-Testharness der Rohwerte ausgibt
2. **`Sensors/HardwareSource.cs`** — LHM initialisieren, *alle* gefundenen Sensoren auflisten. Erst danach steht fest, welche Werte die Zielhardware überhaupt liefert
3. **`Collector`** und Datenmodell
4. **`OverlayWindow`** samt `wwwroot/overlay.*`
5. **`DetailWindow`** samt Prozesstabelle
6. Tray-Icon, Einstellungsdialog, Autostart-Aufgabe

Schritt 2 ist ein bewusster Kontrollpunkt: Welche Temperatur- und Lüftersensoren
tatsächlich verfügbar sind, ist hardwareabhängig und bestimmt das Overlay-Layout.

## 16. Risiken

| Risiko | Auswirkung | Umgang |
|---|---|---|
| WinRing0-Treiber wird von Antiviren- oder Anti-Cheat-Software beanstandet (bekanntes CVE-Umfeld) | Warnmeldungen, im Extremfall Blockade | Signierte Version aus LHM verwenden, Verhalten dokumentieren |
| `% Processor Utility` auf manchen Systemen nicht vorhanden | CPU-Anzeige leer | Fallback auf `% Processor Time` |
| `\GPU Engine` fehlt bei bestimmten Treiberkonstellationen | keine GPU-Werte | Erkennen und im UI ausgrauen; NVML als späterer Fallback |
| LHM findet Lüftersensoren nicht (bei Notebooks der Regelfall — Embedded Controller statt Super-I/O) | Lüfteranzeige fehlt | Feld ausblenden statt Null anzeigen; die Ursache am Akku unterscheiden, statt sie dem Treiber anzulasten |
| Blockierter WinRing0-Treiber (Speicherintegrität, Sperrliste) — auf aktuellen Windows-11-Installationen **der Regelfall** | Keine CPU-Temperatur, kein CPU-Takt, keine Package Power, keine Sockeltemperatur, keine Gehäuselüfter. Der Super-I/O-Chip erscheint gar nicht erst in der Hardwareliste; die CPU-Sensoren existieren, liefern aber nichts | Erkennen (`CpuSensorsBlocked`, `BoardSensorsAvailable`) und erklären, statt leere Felder oder 0 °C anzuzeigen. Temperatur und Takt ersatzweise treiberfrei holen (§8.6.1), als Ersatz gekennzeichnet. GPU-Werte kommen über NVAPI und bleiben vollständig |
| Prozessorgrafik meldet keinen eigenen Speicher und keine „GPU Core"-Last | VRAM-Zeile leer, GPU-Kachel ausgegraut | Auf `D3D Shared Memory` und `D3D 3D` zurückfallen — aber nur, wenn kein dedizierter Wert vorliegt |
| Prozess-Enumeration verursacht spürbare Last | Monitor wird selbst zum Problem | Nur bei geöffnetem Detailfenster, 2-Sekunden-Takt |
| Elevation verhindert Interaktion mit unprivilegierten Fenstern (UIPI) | Drag-and-Drop-Einschränkungen | Für v1 irrelevant, bei Bedarf Dienst-Split nachziehen |

## 17. Fertig-Kriterium für v1

- Overlay zeigt CPU-, GPU- und RAM-Auslastung sowie CPU- und GPU-Temperatur
- Overlay ist verschiebbar und merkt sich seine Position über Neustarts hinweg
- Anwendung startet ohne UAC-Abfrage automatisch mit Windows
- Schaltfläche "Details" öffnet eine sortierbare Prozessliste
- Prozessliste zeigt GPU-Last je Prozess mit Engine-Aufschlüsselung
- `svchost.exe`-Einträge nennen die konkret laufenden Dienste
- Anwendung verursacht im Leerlauf unter 1 % durchschnittliche CPU-Last

## 18. Ausblick

- **Trigger-Snapshots:** Schwellwertüberwachung mit automatischem Einfrieren der
  Top-Verbraucher; die Ringpuffer-Infrastruktur ist dafür bereits vorhanden
- **ETW-Integration:** Disk- und Netzwerk-I/O pro Prozess über
  `Microsoft.Diagnostics.Tracing.TraceEvent`, inklusive der tatsächlich
  gelesenen Dateipfade
- **Verdächtigen-Marker:** aktive Defender-Prüfung, laufendes Windows Update,
  SysMain, geplante Aufgaben — die häufigsten Ursachen für unerklärliche
  Lastspitzen
- **Dienst-Split:** Sensor-Erfassung als Windows-Dienst, UI ohne Elevation,
  Kommunikation über Named Pipe oder Shared Memory
- **Herstellerunabhängigkeit:** ADLX für AMD, Intel-spezifische Sensoren

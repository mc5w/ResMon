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
│  │  └─ Toolhelp.cs               Prozessbaum via CreateToolhelp32Snapshot
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
│  ├─ Config/AppSettings.cs
│  └─ Collector.cs                 Timer-Schleifen, Event SnapshotReady
└─ ResMon.App                      net9.0-windows, WinExe
   ├─ OverlayWindow.xaml(.cs)
   ├─ DetailWindow.xaml(.cs)
   ├─ TrayIcon.cs
   ├─ Bridge/WebBridge.cs          WebView2-Nachrichtenprotokoll
   ├─ app.manifest                 requireAdministrator
   └─ wwwroot/
      ├─ overlay.html / overlay.css / overlay.js
      └─ detail.html / detail.css / detail.js
```

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
    IReadOnlyList<string> ServiceNames);
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
`IsGpuEnabled`, `IsMemoryEnabled`, `IsMotherboardEnabled`. Der Aufruf
`Computer.Accept(visitor)` beziehungsweise `hardware.Update()` ist teuer und
gehört in einen eigenen, langsameren Takt.

## 9. Sampling-Takte

| Intervall | Aufgabe |
|---|---|
| 1000 ms | PDH-Aggregat: CPU, RAM, GPU gesamt → Overlay |
| 2000 ms | LHM-Update: Temperaturen, Takt, Power, Lüfter |
| 2000 ms | Prozessliste — **nur wenn das Detailfenster geöffnet ist** |
| 30 s | Dienst-Cache aktualisieren |

Der Monitor darf nicht selbst zum Lastverursacher werden. Prozess-Enumeration ist
der mit Abstand teuerste Teil und wird deshalb bedarfsgesteuert ausgeführt.

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

## 12. Bridge-Protokoll

**C# → JavaScript:** `CoreWebView2.PostWebMessageAsJson(snapshotJson)`

**JavaScript → C#:** Ereignis `WebMessageReceived`, schmales Command-Set:

| Command | Nutzlast | Wirkung |
|---|---|---|
| `drag` | — | `DragMove()` auf dem Overlay |
| `openDetail` | — | Detailfenster öffnen |
| `setOpacity` | `value: 0..1` | Fenster-Deckkraft |
| `close` | — | Anwendung beenden |

Ein Command zum Beenden von Prozessen ist bewusst nicht enthalten.

## 13. Detailfenster

Normales WPF-Fenster mit WebView2. Tabelle in HTML; Sortierung, Filterung und
Aggregation laufen vollständig in JavaScript.

Spalten: Name, PID, CPU %, Arbeitsspeicher, GPU %, GPU-Engine-Aufschlüsselung,
VRAM, Dienste.

Bedienelemente:

- Umschalter für Prozessbaum-Aggregation (Kindprozesse unter dem Elternprozess
  zusammenfassen)
- Textfilter über Prozessnamen
- Verlaufsdiagramm der letzten 5 Minuten oberhalb der Tabelle

## 14. Rechte, Autostart, Konfiguration

**Elevation:** `app.manifest` mit
`<requestedExecutionLevel level="requireAdministrator" uiAccess="false" />`.
Erforderlich, weil LibreHardwareMonitorLib einen Kernel-Treiber lädt.

**Autostart:** *nicht* über den Registry-Run-Key — bei Anwendungen mit
Administratorrechten führt das bei jedem Anmelden zu einer UAC-Abfrage.
Stattdessen legt die Anwendung beim ersten Start eine Aufgabe in der
Aufgabenplanung an: Trigger "Bei Anmeldung", Option "Mit höchsten Privilegien
ausführen".

**Single-Instance:** benannter Mutex, zweite Instanz beendet sich sofort.

**Einstellungen:** `%AppData%\ResMon\settings.json`

```json
{
  "overlay": { "x": 40, "y": 40, "opacity": 0.9, "clickThrough": false },
  "intervals": { "aggregateMs": 1000, "hardwareMs": 2000, "processMs": 2000 },
  "visible": { "cpu": true, "gpu": true, "ram": true, "temps": true },
  "autostart": true
}
```

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
| LHM findet Lüftersensoren nicht (häufig bei Notebooks) | Lüfteranzeige fehlt | Feld ausblenden statt Null anzeigen |
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

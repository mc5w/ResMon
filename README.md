# ResMon

Desktop-Overlay für Windows 11 mit Live-Anzeige von CPU-, GPU- und RAM-Auslastung
inklusive Temperaturen, plus Detailfenster mit genauerer Prozessaufschlüsselung
als der Task-Manager.

Umsetzung von [DESIGN.md](DESIGN.md).

## Aufbau

| Projekt | Zweck |
|---|---|
| `ResMon.Core` | Erfassung: PDH-P/Invoke, LibreHardwareMonitor, Prozess- und Dienstauflösung, Collector |
| `ResMon.App` | WPF-Host mit WebView2-Oberfläche, Overlay, Detailfenster, Tray-Icon |
| `ResMon.Probe` | Konsolen-Diagnose für die Rohdaten (Kontrollpunkt aus DESIGN.md §15) |
| `tools/` | `make-icon.ps1` erzeugt `ResMon.App/ResMon.ico` — dort steht auch die Form des Symbols |

## Voraussetzungen

- Windows 11 x64
- .NET 9 SDK (über `global.json` gepinnt)
- WebView2-Runtime (auf Windows 11 vorinstalliert)
- Administratorrechte zur Laufzeit — LibreHardwareMonitorLib lädt einen
  Kernel-Treiber für die Temperatursensoren

## Bauen

```bash
dotnet build ResMon.sln -c Release
```

## Starten

Die Anwendung fordert per Manifest Administratorrechte an; der Start löst also
eine UAC-Abfrage aus.

```bash
ResMon.App\bin\x64\Release\net9.0-windows\ResMon.exe
```

Beim Start erscheint das Overlay an der zuletzt gespeicherten Position und ein
Tray-Icon. Bedienung:

- **Kopfzeile ziehen** — Overlay verschieben, Position wird gespeichert
- **Mausrad über der Karte** — Deckkraft ändern
- **Details** — Prozessfenster öffnen (erst dann laufen Prozess-Enumeration und ETW)
- **Tray-Menü** — Deckkraft, sichtbare Zeilen, Klick-Durchlässigkeit, Autostart, Beenden

Das Detailfenster hat sechs Reiter:

| Reiter | Inhalt |
|---|---|
| **Prozesse** | Prozesstabelle mit Verlaufsdiagramm, fest nach Apps, Hintergrund- und Windows-Prozessen gegliedert |
| **Energie** | Leistungsaufnahme, Temperaturen, Lüfter, Akku und der Energieeinfluss je Prozess |
| **Verbindungen** | Übersicht der offenen Ports und darunter die vollständige TCP/UDP-Tabelle |
| **System** | Betriebssystem, Laufzeit, CPU samt Cache-Ebenen, Grafik, Arbeitsspeicher, Mainboard, Geräte und Datenträger |
| **Logs** | Was gerade *nicht* ausgelesen werden kann und warum — Zählersätze, Sensortreiber, gefangene Fehler |
| **Einstellungen** | Farbschema, Deckkraft und Größe des Overlays, sichtbare Zeilen, Reihen des Diagramms |

Bedienung der Tabellen:

- **Klick auf eine Zeile** heftet den Prozess oben an. Angeheftete Zeilen bleiben
  dort stehen, überstehen den Filter und wandern beim Sortieren nicht mehr
- **Klick auf eine Abschnittsüberschrift** klappt den Abschnitt zu, etwa wenn die
  Hintergrundprozesse gerade nicht interessieren
- **Rechte Kante einer Spaltenüberschrift ziehen** ändert die Spaltenbreite,
  Doppelklick setzt sie zurück; **Überschrift ziehen** verschiebt die Spalte
- **Dreieck vor dem Namen** klappt einen zusammengefassten Prozessbaum auf: der
  Elternprozess bleibt oben, darunter erscheinen sein eigener Anteil und die
  Kindprozesse, nach Abstand zum Elternprozess eingerückt
- **Rechtsklick auf eine Zeile** öffnet ein Menü mit Anheften, Aufklappen, Pfad
  kopieren und Prozess beenden
- **Doppelklick auf die Notizspalte** (Stiftsymbol) öffnet ein Eingabefeld.
  Notizen hängen am Prozessnamen und bleiben über Neustarts erhalten
- **Spalten ▾** blendet Spalten ein und aus
- **Mauszeiger über einer Spaltenüberschrift** erklärt, was der Wert bedeutet;
  dasselbe gilt für die GPU-Engine-Chips in der GPU-Kachel
- Meldungen über fehlende Zähler lassen sich per ✕ dauerhaft ausblenden

Sechs Farbschemata (dunkel, hell, blau, rot, grün, sepia) gelten für beide
Fenster einschließlich Titelleiste und Rahmen.

Einstellungen liegen in `%AppData%\ResMon\settings.json`. Spaltenauswahl,
Spaltenbreiten, zugeklappte Abschnitte und Notizen liegen im `localStorage` der
WebView unter `%LocalAppData%\ResMon\WebView2`.

## Diagnose

`ResMon.Probe` gibt die Rohdaten aus, ohne Oberfläche:

```bash
ResMon.Probe\bin\x64\Release\net9.0\ResMon.Probe.exe sensors
```

| Modus | Ausgabe |
|---|---|
| `sensors` | Alle von LibreHardwareMonitor gefundenen Sensoren (für Temperaturen als Administrator ausführen) |
| `counters [n]` | CPU-, RAM- und GPU-Aggregate im Sekundentakt |
| `gpu [n]` | Rohe GPU-Engine-Instanzen mit PID und Engine-Typ |
| `processes [n]` | Top-15-Prozesse nach CPU inklusive Dienstauflösung |
| `paths` | Welche der benötigten PDH-Zählerpfade dieses System kennt |

Für die Arbeit an der Oberfläche ohne Elevation lässt sich `wwwroot` als
statische Seite ausliefern, etwa mit

```bash
python -m http.server 8123 --directory ResMon.App/wwwroot
```

Die Seiten rendern dann ohne Daten; `renderTiles(…)`, `renderTable()` und die
übrigen Zeichenfunktionen lassen sich in der Konsole des Browsers von Hand
füttern.

## Abweichungen vom Entwurfsdokument

Beim Umsetzen haben sich zwei Annahmen aus DESIGN.md §8.2 als nicht zutreffend
erwiesen (geprüft mit `ResMon.Probe paths` auf Windows 11 26200):

1. **`\Process V2(*)\ID Process` existiert nicht.** Im Zählersatz `Process V2`
   heißt der Zähler `Process ID`. `ResMon.Core` probiert beide Schreibweisen und
   fällt notfalls auf den älteren `Process`-Satz zurück.
2. **Die Instanznamen von `Process V2` haben die Form `name:pid`**, nicht
   `name_pid` — relevant für die Rückfallebene der Namensauflösung.

Zusätzlich liefert LibreHardwareMonitor bei NVIDIA-Karten neben
`GPU Memory Used` auch `D3D Dedicated Memory Used`; für den VRAM-Wert wird
gezielt der erste Sensor gewählt, sonst wird nur ein Teil des belegten Speichers
angezeigt.

Zwei weitere Annahmen aus WMI haben sich als unbrauchbar erwiesen:
`ASSOCIATORS OF` wirft bei Datenträgern ohne Partitionen während der Aufzählung
„Nicht gefunden" — die Zuordnung Datenträger→Laufwerk läuft deshalb über
`Win32_LogicalDiskToPartition`. Und `Win32_VideoController.AdapterRAM` ist ein
32-Bit-Feld, das ab 4 GB VRAM den gedeckelten Wert 4293918720 meldet; Werte nahe
der Grenze werden verworfen.

## Abweichung von DESIGN.md §12

Das Command-Set der Bridge enthält entgegen §12 ein Kommando zum **Beenden von
Prozessen**. Es ist auf ausdrücklichen Wunsch nachgezogen worden und hat zwei
Sicherungen: einen Bestätigungsdialog im Host, der Name und PID nennt, und eine
Sperre für die Prozesse, deren Ende Windows zum Absturz bringt (`smss.exe`,
`csrss.exe`, `wininit.exe`, `winlogon.exe`, `services.exe`, `lsass.exe`, PID ≤ 4).

Hinzugekommen ist außerdem `requestSystemInfo`: die Systemübersicht wird genau
einmal gesendet, und die Oberfläche kann sie nachfordern, falls sie die Nachricht
verpasst hat.

## CPU-Temperatur und Speicherintegrität

Ist die **Speicherintegrität** (Kernisolierung) oder die **Sperrliste für
verwundbare Treiber** aktiv, lädt LibreHardwareMonitors Kernel-Treiber
`WinRing0` nicht — er steht auf Microsofts Sperrliste. Die CPU-Sensoren
existieren dann zwar, liefern aber nichts: je nach Windows-Fassung melden sie
konstant 0 oder gar keinen Wert.

ResMon erkennt beide Formen und zeigt „–" statt einer erfundenen Null. Zu
prüfen ist der Zustand mit:

```bash
reg query "HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity" /v Enabled
```

Die Speicherintegrität abzuschalten würde die Sensoren aktivieren, senkt aber
das Schutzniveau des Systems spürbar. Das ist eine bewusste Abwägung — ResMon
nimmt sie niemandem ab und kommt stattdessen so weit wie möglich ohne den
Treiber aus:

| Wert | ohne Treiber | Quelle |
|---|---|---|
| CPU-Temperatur | ersatzweise | ACPI-Thermalzone `\_TZ.CPU…` über PDH `\Thermal Zone Information` |
| CPU-Takt | ersatzweise | Basistakt × `% Processor Performance`, wie im Task-Manager |
| CPU-Leistung | nein | steht nur in den Energiezählern des Prozessors (RAPL) |
| GPU-Werte | ja | die Sensorbibliothek liest sie im User-Mode |
| Sockeltemperatur, Gehäuselüfter | nein | Super-I/O-Chip des Mainboards |

Die Ersatzwerte sind als solche gekennzeichnet: die Temperatur trägt „(ACPI-Zone)",
der gerechnete Takt ein „≈". Eine Thermalzone misst die Umgebung des Prozessors,
nicht seinen Die — sie liegt niedriger und folgt Lastspitzen träger.

## Notebooks

Auf Notebooks fehlen **Sockeltemperatur und Gehäuselüfter** unabhängig vom
Treiber. An der Stelle des Super-I/O-Chips sitzt dort ein Embedded Controller,
den jeder Hersteller anders anspricht; die Sensorbibliothek kennt dafür keinen
allgemeinen Weg, und Windows selbst kennt die Drehzahl ebenso wenig. ResMon
benennt das als eigene Ursache, statt es dem gesperrten Treiber anzulasten —
erkennbar am vorhandenen Akku.

Die ACPI-Thermalzonen sind auf Notebooks meist gut belegt und stehen im Reiter
„Energie" als eigene Gruppe. Zonennamen vergibt der Hersteller; übersetzt werden
nur die eindeutigen (`CPUZ`, `GFXZ`, `PCHZ`, `BATZ`, `SKIN`), alle anderen
behalten ihren Bezeichner.

Grafik im Prozessor hat keinen eigenen Speicher: dort zeigt ResMon den Anteil am
Arbeitsspeicher (`D3D Shared Memory`), und zwar nur dann, wenn die Karte keinen
eigenen meldet.

## Netzwerk und Datenträger

Der Netz-Gesamtdurchsatz kommt aus `\Network Interface` über PDH, der
Datenträgerdurchsatz aus `\PhysicalDisk(_Total)`. Beides ist immer verfügbar.

Der Netzdurchsatz **pro Prozess** stammt aus einer Kernel-ETW-Sitzung
(`Microsoft.Diagnostics.Tracing.TraceEvent`, Keyword `NetworkTCPIP`) — PDH kennt
dafür keine Zähler. Die Sitzung läuft nur, solange das Detailfenster offen ist,
und erfordert Administratorrechte. Ohne sie bleiben die Spalten leer und das
Detailfenster nennt den Grund.

Die Spalten **E/A lesen** und **E/A schreiben** stammen dagegen aus
`\Process V2(*)\IO Read Bytes/sec` bzw. `IO Write Bytes/sec`. Diese Zähler
umfassen *alle* Ein- und Ausgaben eines Prozesses — Dateien, Netzwerk und Geräte
zusammen —, sind also nicht deckungsgleich mit dem reinen Datenträgerzugriff.
Sie sind dafür immer korrekt dem verursachenden Prozess zugeordnet, was für
Datenträger-Ereignisse aus ETW nicht durchgängig gilt. Der reine
Datenträgerdurchsatz steht in der Kachel oben.

## Lizenz

[MIT](LICENSE).

Die eingebundenen Pakete stehen unter ihren eigenen Lizenzen und werden über
NuGet bezogen, nicht mitgeliefert:

| Paket | Lizenz |
|---|---|
| LibreHardwareMonitorLib | MPL-2.0 |
| Microsoft.Diagnostics.Tracing.TraceEvent | MIT |
| Microsoft.Web.WebView2 | Microsoft Software License |
| System.Management, System.Diagnostics.EventLog | MIT |

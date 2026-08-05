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

Im Detailfenster:

- **Reiter Prozesse / System** — Prozesstabelle oder Übersicht über Betriebssystem,
  CPU, Grafik, Arbeitsspeicher, Mainboard und die Datenträger samt Laufwerken
- **Klick auf eine Zeile** heftet den Prozess oben an. Angeheftete Zeilen bleiben
  dort stehen, überstehen den Filter und wandern beim Sortieren nicht mehr
- **Doppelklick auf die Notizspalte** (Stiftsymbol) öffnet ein Eingabefeld.
  Notizen hängen am Prozessnamen und bleiben über Neustarts erhalten
- **Spalten ▾** blendet Spalten ein und aus
- **Mauszeiger über einer Spaltenüberschrift** erklärt, was der Wert bedeutet;
  dasselbe gilt für die GPU-Engine-Chips in der GPU-Kachel
- Meldungen über fehlende Zähler lassen sich per ✕ dauerhaft ausblenden

Einstellungen liegen in `%AppData%\ResMon\settings.json`. Spaltenauswahl und
Notizen liegen im `localStorage` der WebView unter
`%LocalAppData%\ResMon\WebView2`.

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
statische Seite ausliefern; die Seiten rendern dann
ohne Daten, `render(…)` bzw. `renderTiles(…)` lassen sich in der Konsole füttern.

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

## CPU-Temperatur und Speicherintegrität

Ist die **Speicherintegrität** (Kernisolierung) oder die **Sperrliste für
verwundbare Treiber** aktiv, lädt LibreHardwareMonitors Kernel-Treiber
`WinRing0` nicht — er steht auf Microsofts Sperrliste. Die CPU-Sensoren
existieren dann zwar, melden aber konstant 0.

ResMon erkennt das und zeigt „–" statt einer erfundenen Null; das Detailfenster
blendet einen Hinweis ein. GPU-Werte kommen über NVAPI im User-Mode und sind
nicht betroffen. Zu prüfen ist der Zustand mit:

```bash
reg query "HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity" /v Enabled
```

Die Speicherintegrität abzuschalten würde die Temperaturanzeige aktivieren,
senkt aber das Schutzniveau des Systems spürbar. Das ist eine bewusste
Abwägung — ResMon nimmt sie niemandem ab.

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

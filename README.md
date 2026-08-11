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

Das Detailfenster hat sieben Reiter:

| Reiter | Inhalt |
|---|---|
| **Prozesse** | Prozesstabelle mit Verlaufsdiagramm, fest nach Apps, Hintergrund- und Windows-Prozessen gegliedert |
| **Energie** | Leistungsaufnahme, Temperaturen, Lüfter, Akku und der Energieeinfluss je Prozess |
| **Verbindungen** | Übersicht der offenen Ports und darunter die vollständige TCP/UDP-Tabelle |
| **System** | Betriebssystem, Laufzeit, CPU samt Cache-Ebenen, Grafik, Arbeitsspeicher, Mainboard, Geräte und Datenträger |
| **Speicher** | Welche Ordner eine Partition füllen — sortierter Baum und Kachelkarte nebeneinander |
| **Logs** | Was gerade *nicht* ausgelesen werden kann und warum — Zählersätze, Sensortreiber, gefangene Fehler |
| **Einstellungen** | Farbschema, Deckkraft und Größe des Overlays, sichtbare Zeilen, Reihen des Diagramms |

### Reiter „Speicher"

Der System-Reiter sagt, *dass* eine Partition eng wird; dieser sagt, **wo** der
Platz liegt. Der Durchlauf läuft nur auf Knopfdruck — er ist die einzige
Datenquelle der Anwendung ohne Takt. Gemessen auf einem vollen `C:` mit 1,04 Mio.
Dateien und 291 000 Ordnern: rund 7 Sekunden warm, rund 31 Sekunden kalt auf einer
NVMe. Auf einer Festplatte dauert es ein Vielfaches, weshalb der Grad der
Parallelität dort auf zwei Threads sinkt.

Links der Baum, je Ebene nach Größe sortiert, mit Anteilsbalken; rechts eine
Kachelkarte, deren Flächen den Größen entsprechen. Die Auswahl ist gekoppelt.
Klick in die Karte markiert die Zeile, Doppelklick zoomt hinein, die Brotkrumen
führen zurück. Rechtsklick auf Zeile oder Kachel bietet **Im Explorer öffnen** und
**Pfad kopieren** — gelöscht wird bewusst nicht aus der Anwendung heraus, sie läuft
erhöht, und ein Fehlgriff träfe auch Systemordner ohne Papierkorb.

Dateien ab 16 MB bekommen einen eigenen Eintrag; `hiberfil.sys` und `pagefile.sys`
stehen also mit in der Liste. Alles Kleinere zählt in die Summe seines Ordners.

Zu lesen ist das Ergebnis mit den Vorbehalten, die unter der Leiste stehen: siehe
den nächsten Abschnitt.

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
| `scan [Laufwerk]` | Ordnerbelegung messen: Dauer, Einträge/s, Zuweisungen, Größe der Nutzlast und die 30 größten Pfade |

`scan` ist zugleich die Messbank für den Reiter „Speicher" — Laufzeit,
Speicherbedarf und die Wirkung der Schwellwerte lassen sich damit prüfen, ohne die
Oberfläche zu starten.

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

## Was der Ordner-Scan nicht messen kann

Der Reiter „Speicher" meldet die **logische** Größe der Dateien, nicht ihre
Belegung auf dem Datenträger — dieselbe Zahl, die auch der Explorer unter „Größe"
zeigt, also gegenprüfbar. Fünf Dinge gehen dabei auseinander:

1. **Harte Verknüpfungen zählen doppelt.** Der Komponentenspeicher `WinSxS` teilt
   sich seine Cluster größtenteils mit `System32`; beide Namen zeigen auf dieselben
   Daten, und der Scan zählt sie unter jedem. Auf einem gewöhnlichen Windows sind
   das 5–15 GB zu viel. Es zu erkennen bräuchte den Dateiindex **jeder** Datei,
   also ein `CreateFile` je Eintrag — das vervielfachte die Laufzeit. Der Explorer
   rechnet übrigens genauso.
2. **Komprimierte und dünn besetzte Dateien** belegen weniger, als hier steht. Die
   betroffenen Ordner tragen ein Zeichen.
3. **Cloud-Platzhalter** (OneDrive) melden die Größe der vollständigen Datei,
   obwohl auf dem Datenträger fast nichts davon liegt. Ihr Anteil wird gesondert
   ausgewiesen.
4. **Nicht lesbare Ordner** fehlen in der Summe. Die Anzahl steht in der Kopfzeile,
   die Ursache im Reiter „Logs".
5. **Abzweigungen werden nicht verfolgt** — `C:\Users\All Users` zeigt auf
   `C:\ProgramData`, eingehängte Volumes gehören zu einer anderen Partition. Sie
   erscheinen mit 0 Byte und einem Zeichen.

Die Summe weicht deshalb von der Belegung ab, die Windows meldet, und die
Oberfläche nennt die Differenz ausdrücklich. Sie ist eine Auskunft, kein Fehler:
harte Verknüpfungen treiben sie nach oben, während Cluster-Verschnitt, `$MFT`,
`$LogFile`, die Verzeichnisindizes, verweigerte Teilbäume und vor allem
**Schattenkopien** in `System Volume Information` fehlen. Letztere sind auf einer
nahezu vollen Partition regelmäßig unter den größten Posten und lassen sich
keinem Ordner zurechnen; `vssadmin list shadowstorage` nennt ihren Umfang.

Ausdrücklich nicht getan: `SeBackupPrivilege` für die Dauer des Scans zu
aktivieren. Es würde fast alle verweigerten Pfade öffnen — der Prozess läuft
erhöht, das Recht liegt schlafend vor —, ist aber eine prozessweite Änderung am
Token ohne Thread-Begrenzung. Ein Überwachungswerkzeug, das sich still
Sicherungsrechte erteilt, ist eine Überraschung.

## Abweichung von DESIGN.md §12

Das Command-Set der Bridge enthält entgegen §12 ein Kommando zum **Beenden von
Prozessen**. Es ist auf ausdrücklichen Wunsch nachgezogen worden und hat zwei
Sicherungen: einen Bestätigungsdialog im Host, der Name und PID nennt, und eine
Sperre für die Prozesse, deren Ende Windows zum Absturz bringt (`smss.exe`,
`csrss.exe`, `wininit.exe`, `winlogon.exe`, `services.exe`, `lsass.exe`, PID ≤ 4).

Hinzugekommen ist außerdem `requestSystemInfo`: die Systemübersicht wird genau
einmal gesendet, und die Oberfläche kann sie nachfordern, falls sie die Nachricht
verpasst hat.

Für den Reiter „Speicher" sind fünf weitere Kommandos hinzugekommen —
`startFolderScan`, `cancelFolderScan`, `expandFolder`, `openFolder` und
`copyFolderPath`. `startFolderScan` ist das einzige, das einen Pfad
entgegennimmt, und der Host nimmt ausschließlich Laufwerkswurzeln an, die er gegen
`DriveInfo.GetDrives()` prüft. Alles Weitere läuft über ganzzahlige Kennungen in
einen Baum, den der Host selbst gebaut hat: die Oberfläche kann ihn also nicht
dazu bringen, einen beliebigen Pfad abzulaufen.

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

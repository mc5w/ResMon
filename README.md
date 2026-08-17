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
- .NET 10 SDK (über `global.json` gepinnt)
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
ResMon.App\bin\x64\Release\net10.0-windows\ResMon.exe
```

Beim Start erscheint das Overlay an der zuletzt gespeicherten Position und ein
Tray-Icon. Bedienung:

- **Kopfzeile ziehen** — Overlay verschieben, Position wird gespeichert
- **Mausrad über der Karte** — Deckkraft ändern
- **Details** — Prozessfenster öffnen (erst dann laufen Prozess-Enumeration und ETW)
- **Tray-Menü** — Deckkraft, sichtbare Zeilen, Klick-Durchlässigkeit, Autostart, Beenden

Das Detailfenster hat neun Reiter:

| Reiter | Inhalt |
|---|---|
| **Prozesse** | Prozesstabelle mit Verlaufsdiagramm, fest nach Apps, Hintergrund- und Windows-Prozessen gegliedert |
| **Energie** | Leistungsaufnahme, Temperaturen, Lüfter, Akku und der Energieeinfluss je Prozess |
| **Verbindungen** | Übersicht der offenen Ports und darunter die vollständige TCP/UDP-Tabelle |
| **System** | Betriebssystem, Laufzeit, CPU samt Cache-Ebenen, Grafik, Arbeitsspeicher, Mainboard, Geräte und Datenträger |
| **Speicher** | Welche Ordner eine Partition füllen — sortierter Baum und Kachelkarte nebeneinander, darunter die Befunde mit Begründung, Handgriff und Vorbehalt |
| **Programme** | Was installiert ist, wie groß es wirklich ist und wann es zuletzt lief |
| **System-Start** | Woran der Systemstart hängt: Phasen, gemessene Startkette, Befunde mit Fundstelle, Autostart-Einträge, Wartekette |
| **Logs** | Was gerade *nicht* ausgelesen werden kann und warum — Zählersätze, Sensortreiber, gefangene Fehler |
| **Einstellungen** | Farbschema, Deckkraft und Größe des Overlays, sichtbare Zeilen, Reihen des Diagramms, Hinweisleiste an oder aus, Höchstgrenze der Startaufzeichnung |

„Logs" und „Einstellungen" sitzen abgesetzt am rechten Rand: sie sind keine
Datenblätter wie die Reihe davor, sondern zeigen, was die Anwendung an sich
selbst bemerkt hat, und stellen sie ein. Jeder Reiter erklärt im Hover, welche
Frage er beantwortet und wann man ihn braucht.

### Reiter „Speicher"

Der System-Reiter sagt, *dass* eine Partition eng wird; dieser sagt, **wo** der
Platz liegt. Der Durchlauf läuft nur auf Knopfdruck — er ist die einzige
Datenquelle der Anwendung ohne Takt. Gemessen auf einem vollen `C:` mit 1,04 Mio.
Dateien und 291 000 Ordnern: rund 7 Sekunden warm, rund 31 Sekunden kalt auf einer
NVMe. Auf einer Festplatte dauert es ein Vielfaches, weshalb der Grad der
Parallelität dort auf zwei Threads sinkt.

In der Laufwerksauswahl steht je Partition der freie Platz, und der wird alle
zwei Sekunden fortgeschrieben — beim Aufräumen sieht man ihn also wachsen, ohne
neu zu durchsuchen. Der Scan selbst bleibt ausdrücklich ohne Takt; aufgefrischt
wird nur die Kapazität, ein Wert, den das Dateisystem ohnehin mitführt.

Links der Baum, je Ebene nach Größe sortiert, mit Anteilsbalken; rechts eine
Kachelkarte, deren Flächen den Größen entsprechen. Die Auswahl ist gekoppelt.
Klick in die Karte markiert die Zeile, Doppelklick zoomt hinein, die Brotkrumen
führen zurück. Rechtsklick auf Zeile oder Kachel bietet **Im Explorer öffnen** und
**Pfad kopieren** — gelöscht wird bewusst nicht aus der Anwendung heraus, sie läuft
erhöht, und ein Fehlgriff träfe auch Systemordner ohne Papierkorb.

Dateien ab 16 MB bekommen einen eigenen Eintrag; `hiberfil.sys` und `pagefile.sys`
stehen also mit in der Liste. Alles Kleinere zählt in die Summe seines Ordners.

Unter Baum und Karte stehen die **Befunde**: was der große Posten ist, wofür er
steht, **warum der Vorschlag ausgerechnet hier auftaucht**, der Handgriff Schritt
für Schritt — und darunter, was er kostet. Jeder Schritt trägt seine eigene
Erklärung: nicht nur `net stop wuauserv`, sondern auch, dass das den Dienst
anhält, weil er die Dateien sonst offen hält. Mehrschrittige Handgriffe stehen
als nummerierte Folge da, weil der erste Schritt allein oft nichts bringt, ohne
dass es auffiele: `docker system prune` räumt einen virtuellen Datenträger
**innen** auf, und die Datei bleibt außen exakt so groß, bis `Optimize-VHD` sie
kompaktiert. Zwei Regeln existieren allein des Vorbehalts wegen:
`Windows\Installer` und `WinSxS` sind groß, stehen in jeder Anleitung im Netz als
Löschkandidat und sind keiner. Bei WinSxS kommt hinzu, dass die gemessene Zahl
gar nicht stimmt — der größte Teil sind harte Verknüpfungen auf Dateien in
System32, die hier ein zweites Mal zählen.

**Ein Befund kommt nicht aus dem Ordnerbaum**, und das ist bei ihm der Punkt:
läuft eine Startaufzeichnung, steht sie ganz oben mit der Menge, die sie bereits
geschrieben hat. Ein Scan kann sie nicht finden — die `.etl`-Datei steht im
Verzeichnis mit 0 Byte, weil ETW ihre Größe erst beim Beenden einträgt. Wenn der
freie Platz schrumpft, ohne dass im Baum etwas wächst, ist fast immer das die
Erklärung. Auf der Referenzmaschine waren so unbemerkt 87 GB zusammengekommen,
mit rund 12 MB/s. Gegen eine Wiederholung steht eine Höchstgrenze unter
Einstellungen (Vorgabe 2 GB), bei deren Überschreiten ResMon die Aufzeichnung
abbricht und es im Reiter Logs vermerkt.

Ein Befund richtet sich nach der Hardware: die **Ruhezustandsdatei** ist auf
einem Notebook etwas anderes als auf einem Standrechner. Gibt es einen Akku,
steht dort kein Handgriff, sondern die Erklärung, warum die Datei zu Recht so
groß ist — sie ist die Funktion, für die man das Gerät zuklappt. Ohne Akku wird
`powercfg /h /type reduced` vorgeschlagen: das halbiert sie und lässt den
Schnellstart stehen, der an derselben Datei hängt. Auf der Referenzmaschine
(32 GB RAM, kein Akku) waren das 13,7 → 6,9 GB, Schnellstart weiterhin
verfügbar.

Ist der Handgriff schon getan, verschwindet er: die Ruhezustandsdatei wird gegen
den Arbeitsspeicher gehalten (volle Form 40 %, verkleinerte 20 %), und bei einer
bereits verkleinerten steht kein Befehl mehr da. Ein Vorschlag, den man längst
befolgt hat, ist schlimmer als keiner — er lässt an der ganzen Liste zweifeln.

Wo sich das nicht messen lässt, **fragt der erste Schritt**. Beim
Komponentenspeicher ist von außen nicht zu erkennen, ob etwas freizumachen wäre;
deshalb steht dort `AnalyzeComponentStore` als Frage vor dem eigentlichen
Aufräumbefehl — dieselbe Form wie beim virtuellen Datenträger, wo `docker system
df` zuerst kommt.

Kein Befund empfiehlt etwas, das Platz *kostet*. Ein Reparaturlauf für den
Komponentenspeicher etwa schreibt fehlende Dateien nach und macht ihn größer —
er gehört deshalb nicht in einen Reiter, der die Frage beantwortet, wo Platz
liegt, auch nicht als Vorstufe zum Aufräumen.

Ausgeführt wird auch hier nichts. Neben jedem Befehl stehen zwei Knöpfe:
**Kopieren** legt ihn in die Zwischenablage, **In PowerShell öffnen** startet ein
Fenster, in dem er bereits fertig getippt in der Eingabezeile steht. Abgeschickt
wird er nicht — den Tastendruck tut der Benutzer, und er sieht vorher genau, was
er abschickt.

**Verwaiste Temp-Reste.** Der Befund zum Temp-Ordner zählt die größten Posten
namentlich auf, statt es bei „gehört zu Programmen, die längst beendet sind" zu
belassen. Ein Knopf darunter geht noch einen Schritt weiter und hält jeden Posten
gegen die installierten Programme und die laufenden Prozesse. Der Gedanke
dahinter: ein Temp-Ordner wird nicht von Windows aufgeräumt, sondern von dem
Programm, das ihn angelegt hat — ist das deinstalliert, räumt niemand mehr auf.
Solche Reste liegen für immer da.

Jeder Posten bekommt eine Einstufung mit Begründung im Klartext: *in Benutzung*
(ein Prozess dieses Namens läuft), *zugeordnet* (passt zu einem installierten
Programm oder zu Windows), *namenlos* (GUID oder `tmpXXXX.tmp` — sagt nichts),
*zu frisch* (unter 7 Tagen) oder *verwaist*. Nur die letzten lassen sich
ankreuzen und löschen; alle anderen stehen gesperrt da, damit die Zuordnung
nachprüfbar bleibt. Vor dem Löschen fragt ein Fenster nach und nennt Zahl, Menge
und die größten Posten beim Namen. Gelöscht wird endgültig und nicht in den
Papierkorb — sonst würde kein Platz frei.

Es ist die einzige Stelle, an der ResMon etwas löscht, und die Ausnahme ist eng
gezogen: es geht nur, was unmittelbar in `%Temp%` oder `%WinDir%\Temp` liegt,
Haken für Haken ausgewählt. Auf der Referenzmaschine: 48 Posten mit zusammen
8,5 GB, davon 10 MB in zwei Posten als verwaist eingestuft. Dass die Ausbeute
klein ist, ist das Ergebnis und kein Mangel.

**Suche.** Das Feld in der Leiste hebt jede Zeile hervor, in deren Namen der Text
vorkommt, klappt die Ordner darüber auf und markiert dieselben Treffer in der
Kachelkarte. Die Statuszeile nennt die Trefferzahl.

In der Karte ist die Marke ein doppelter Rahmen aus Schwarz und Weiß, der zu
Anfang anderthalb Sekunden blinkt und danach stehen bleibt. Eine einzelne Farbe
taugt dort nicht: die Karte trägt die ganze Palette in mehreren Helligkeiten
nebeneinander, und was auf der einen Kachel heraussticht, verschwindet auf der
nächsten. Ein Rahmen und keine Füllung — die Fläche einer Kachel *ist* ihre
Größe.

**Zustand des Datenträgers.** „Fragmentierung messen" liest über
`Win32_Volume.DefragAnalysis()` aus, wie zerstückelt die gewählte Partition ist
— auf einer 300-GB-Partition rund 8 Sekunden, erhöhte Rechte vorausgesetzt.

Der zweite Knopf stößt an, was Windows für dieses Medium vorsieht, und heißt
entsprechend: auf einer Festplatte **Defragmentieren**, auf einer SSD **TRIM
ausführen**. Das ist kein Ersatz, sondern der Punkt — auf einer SSD gibt es keine
Kopfbewegung, die eine zerstückelte Datei langsamer machte. Ein erzwungenes
Defragmentieren brächte dort keine Geschwindigkeit und kostete Schreibzyklen;
ResMon bietet es deshalb nicht an. Vor dem Start fragt ein Dialog nach und nennt,
was tatsächlich läuft.

Zu lesen ist das Ergebnis mit den Vorbehalten, die unter der Leiste stehen: siehe
den nächsten Abschnitt.

### Reiter „Programme"

Was der Speicher-Reiter offen lässt: nicht wo der Platz liegt, sondern was man
loswerden kann. Die Liste stammt aus denselben Uninstall-Schlüsseln wie „Apps und
Features", zeigt aber zwei Dinge anders.

**Die Größe wird gemessen, nicht geglaubt.** Der Registry-Wert `EstimatedSize`
steht nur bei 60 von 108 Programmen und ist auch dort selbstgemeldet. Stattdessen
wird der Installationsordner im Baum eines gelaufenen Scans nachgeschlagen oder
eigens durchlaufen. Ohne hinterlegten Ordner bleibt die Zelle leer statt null.

**Dazu kommt, wann das Programm zuletzt lief** — aus dem Prefetch-Ordner und aus
UserAssist. Erst diese Spalte macht die Liste zu einer Entscheidungsgrundlage:
„groß" allein ist kein Grund, „groß und seit anderthalb Jahren nicht gestartet"
schon. Filter für „nur ab 1 GB" und „nur lange nicht benutzt" (ab 180 Tagen).

Deinstalliert wird nicht aus der Anwendung heraus, aus demselben Grund, aus dem
nicht gelöscht wird.

### Reiter „System-Start"

Beantwortet nicht „welche Programme starten mit" — das zeigt der Task-Manager
auch — sondern **welches davon den Start aufgehalten hat und um wie viele
Sekunden**.

Die tragende Quelle ist `Microsoft-Windows-Shell-Core/Operational`. Der Explorer
schreibt dort zu jedem Autostart-Befehl ein Start- und ein Ende-Ereignis mit
Zeitstempel und vergebener PID. Der entscheidende Befund steckt in den
Zeitstempeln: das Ende eines Befehls trägt dieselbe Zeit wie der Start des
nächsten. Der Explorer arbeitet also **nacheinander** ab — die Dauer eines Glieds
ist damit die Wartezeit aller folgenden, und ein hängender Eintrag ist als langer
Balken sichtbar, ohne dass man raten muss.

Dazu kommen Windows' eigene Startmessung (Phasenaufteilung in Millisekunden), die
Zeitlimits des Dienststeuerungs-Managers — `Das Zeitlimit (90000 ms) wurde beim
Verbindungsversuch mit dem Dienst … erreicht` ist die Zeile, die man bei einem
unerklärlich langen Start sucht — sowie Netlogon, Gruppenrichtlinien und
Benutzerprofildienst.

Jeder Befund nennt Kosten in Sekunden, Erklärung und **Fundstelle**. Ein Befund
ohne Fundstelle wäre eine Behauptung. **Bericht kopieren** legt alles als Text in
die Zwischenablage — wer ein Startproblem untersucht, sitzt selten vor dem
betroffenen Rechner.

Darunter beantworten Wartekettenanalyse (`GetThreadWaitChain`, dieselbe Funktion
wie im Task-Manager) und die systemweite Handle-Tabelle die Frage für den
laufenden Betrieb: worauf wartet ein Prozess gerade, wer hält ihn, und was hat er
offen.

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
- Meldungen über fehlende Zähler lassen sich per ✕ dauerhaft ausblenden — oder
  die ganze Leiste unter Einstellungen abschalten

Sechs Farbschemata (dunkel, hell, blau, rot, grün, sepia) gelten für beide
Fenster einschließlich Titelleiste und Rahmen.

Einstellungen liegen in `%AppData%\ResMon\settings.json`. Spaltenauswahl,
Spaltenbreiten, zugeklappte Abschnitte und Notizen liegen im `localStorage` der
WebView unter `%LocalAppData%\ResMon\WebView2`.

## Diagnose

`ResMon.Probe` gibt die Rohdaten aus, ohne Oberfläche:

```bash
ResMon.Probe\bin\x64\Release\net10.0\ResMon.Probe.exe sensors
```

| Modus | Ausgabe |
|---|---|
| `sensors` | Alle von LibreHardwareMonitor gefundenen Sensoren (für Temperaturen als Administrator ausführen) |
| `counters [n]` | CPU-, RAM- und GPU-Aggregate im Sekundentakt |
| `gpu [n]` | Rohe GPU-Engine-Instanzen mit PID und Engine-Typ |
| `processes [n]` | Top-15-Prozesse nach CPU inklusive Dienstauflösung |
| `paths` | Welche der benötigten PDH-Zählerpfade dieses System kennt |
| `scan [Laufwerk]` | Ordnerbelegung messen: Dauer, Einträge/s, Zuweisungen, Größe der Nutzlast, die 30 größten Pfade und die Befunde mit Begründung, Befehl und Vorbehalt |
| `programs [Lw]` | Installierte Programme mit gemessener Größe und letzter Nutzung. Mit Laufwerksangabe kommen die Größen aus einem vorher laufenden Scan (**für Prefetch erhöht ausführen**) |
| `temp` | Temp-Reste einstufen — verwaist, zugeordnet, in Benutzung, namenlos, zu frisch — jeweils mit Begründung. Zeigt nur an, löscht nichts (**für `C:\Windows\Temp` erhöht ausführen**) |
| `defrag [Lw]` | Zerstückelung einer Partition, samt **aller** Felder, die WMI liefert (**erhöht ausführen**) |
| `startup` | Startanalyse als Text: Phasen, Startkette mit Dauern, Befunde, Inventar, Einschränkungen |
| `boottrace [datei]` | ETW-Startaufzeichnung auswerten — ohne Argument die, die Windows bei jedem Hochfahren selbst anlegt (**erhöht ausführen**) |

`scan` ist zugleich die Messbank für den Reiter „Speicher" — Laufzeit,
Speicherbedarf und die Wirkung der Schwellwerte lassen sich damit prüfen, ohne die
Oberfläche zu starten.

`temp` ist der Prüfstand für die Einstufung der Temp-Reste, und der einzige Weg,
sie ohne die Oberfläche zu beurteilen: entscheidend ist nicht, dass die Erhebung
durchläuft, sondern *was* am Ende unter „verwaist" steht. Zwei Regeln sind genau
aus dieser Ausgabe entstanden — ohne sie galten die 8,1 GB Mitschnitte der
eigenen Startaufzeichnung und `DEL5795.tmp` als verwaist.

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

## Was der Programm-Reiter nicht wissen kann

Die Spalte **zuletzt benutzt** hat vier Lücken, und alle vier führen zu
demselben Fehlschluss, wenn man sie nicht kennt. Ein fehlendes Datum heißt
„keine Quelle kennt die Hauptanwendung" und **nicht** „nie benutzt". Auf der
Referenzmaschine trifft das 80 von 108 Programmen.

1. **Prefetch braucht Administratorrechte.** Ohne sie bleibt `C:\Windows\Prefetch`
   gesperrt, und es trägt allein UserAssist. Auf der Referenzmaschine sank die
   Zahl der Programme mit bekanntem Datum dadurch von 28 auf 21.
2. **UserAssist gilt nur für den angemeldeten Benutzer** und nur für Starts über
   die Oberfläche. Was ein Dienst oder ein Skript aufruft, steht dort nicht.
3. **Spiele werden über ihre Plattform gestartet.** Steam ruft die Exe des Spiels
   auf, aber der Eintrag in der Registry zeigt auf eine Symboldatei — die
   Zuordnung von Programm zu ausführbarer Datei misslingt dann. Genau deshalb
   haben die größten Posten der Liste kein Datum.
4. **Portabel entpackte Programme fehlen ganz.** Sie haben keinen
   Uninstall-Eintrag und tauchen in der Liste nicht auf, auch wenn sie Platz
   belegen.

Die Spalte **Größe** hat eine eigene Lücke: 55 von 108 Programmen tragen keinen
`InstallLocation` in der Registry. Dort steht „nicht messbar" — sie sind nicht
etwa klein.

Nicht benutzt wird `Win32_Product`. Die WMI-Klasse liefert zwar Größen, löst aber
bei jeder Abfrage eine Konsistenzprüfung **jedes** installierten MSI-Pakets aus;
das dauert Minuten und kann Reparaturen anstoßen, die niemand angefordert hat.

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

Für den Reiter „System-Start" sind sechs weitere hinzugekommen —
`requestStartup`, `bootTrace`, `analyzeTrace`, `openTrace`, `requestHandles` und
`inspectProcess`. Auch hier reist **kein Pfad nach innen**: welche Aufzeichnung
ausgewertet wird, entscheidet ein Schlüsselwort (`windows` oder `own`), und den
Dateipfad kennt allein der Host.

Drei weitere kamen mit den Befunden und den Temp-Resten dazu: `openShell`
schreibt einen Befehl in ein frisch geöffnetes PowerShell-Fenster, ohne ihn
abzuschicken; `requestTemp` erhebt die Temp-Ordner; `removeTemp` löscht daraus.
Auch `removeTemp` bekommt **keine Pfade**, sondern Indizes in die zuletzt
gesendete Erhebung — der Pfad, der tatsächlich entfernt wird, stammt damit
ausschließlich aus einer Liste, die der Host selbst aufgestellt hat. Vor dem
Löschen prüft er zusätzlich bei jedem einzelnen Posten, dass dieser unmittelbar
in einem der beiden Temp-Ordner liegt.

## Was die Startanalyse nicht messen kann

Der Reiter „System-Start" liest aus, was Windows über den letzten Start
aufgeschrieben hat. Fünf Dinge stehen dort nicht:

1. **Rechenzeit je Autostart-Eintrag.** Die Ereignisprotokolle kennen nur Anfang
   und Ende. Alles dazwischen sieht nur eine ETW-Ablaufverfolgung. Windows legt
   gelegentlich selbst eine an (`BootPerfDiagLogger.etl`, ohne Neustart
   auswertbar), aber mit zwei Vorbehalten: sie enthält **keine
   Profilablaufverfolgung** — daraus kommen Datenträgerzugriffe und
   Startzeitpunkte, keine Rechenzeit —, und sie stammt **nicht zwangsläufig vom
   letzten Start**. Die Startdiagnose läuft nicht bei jedem Hochfahren; auf der
   Referenzmaschine war die Datei ein Jahr alt. ResMon prüft die Dateizeit gegen
   den Einschaltzeitpunkt und sagt es, wenn sie nicht passt. Für belastbare
   Rechenzeiten braucht es eine eigene Aufzeichnung — und auch dann ist die Zahl
   eine Schätzung aus Abtastungen, keine Messung.
2. **Was vor dem Anmelden hängt, ist nur teilweise zuzuordnen.** Ein
   Dienst-Zeitlimit steht mit Namen und Sekunden im Protokoll; eine
   Netzwerkkarte, die 20 Sekunden auf DHCP wartet, oft nicht.
3. **Der Schnellstart verfälscht den Vergleich.** Windows lädt die Kernelsitzung
   aus `hiberfil.sys` zurück, statt sie neu aufzubauen; Treiber- und
   Dienstphasen fallen dadurch kürzer aus als bei einem Kaltstart. Die
   Startart steht deshalb als eigene Kachel da, und die Einschränkung wird
   ausdrücklich genannt.
4. **Die Startkette rollt aus dem Protokoll heraus.** `Shell-Core/Operational`
   ist ein Ringpuffer. Wer den Rechner seit dem Anmelden lange laufen ließ und
   viel installiert hat, findet die Einträge des letzten Starts nicht mehr.
5. **Herausgeber ≠ Signatur.** Die Spalte „Herausgeber" kommt aus der
   Dateiversion, nicht aus einer geprüften Signatur. Sie sagt, was die Datei über
   sich behauptet. Eine Signaturprüfung wäre eine andere Aussage und wird hier
   bewusst nicht angedeutet — die meisten Windows-Dateien sind katalogsigniert
   und trügen sonst fälschlich „nicht signiert".

Ausdrücklich nicht getan: den Rechner selbst neu zu starten, wenn eine
Startaufzeichnung scharfgestellt ist. Die Anwendung richtet sie ein, sagt es, und
wartet. Ein Überwachungswerkzeug, das den Rechner neu startet, ist eines, das man
nicht laufen lassen kann.

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

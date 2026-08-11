// Detailfenster. Sortierung, Filterung, Aggregation, Spaltenauswahl, Notizen und
// die Systemübersicht laufen vollständig hier, der Host liefert nur Rohdaten
// (DESIGN.md §13).

const host = window.chrome && window.chrome.webview;

const STORAGE_COLUMNS = 'resmon.columns';
const STORAGE_ORDER = 'resmon.columnOrder';
const STORAGE_WIDTHS = 'resmon.columnWidths';
const STORAGE_COLLAPSED = 'resmon.collapsedGroups';
const STORAGE_NOTES = 'resmon.notes';
const STORAGE_NOTICES = 'resmon.dismissedNotices';

// ---------- Formatierung ----------

const nf1 = new Intl.NumberFormat('de-DE', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
const nf0 = new Intl.NumberFormat('de-DE', { maximumFractionDigits: 0 });

function formatBytes(bytes) {
    if (!bytes) {
        return '–';
    }
    if (bytes >= 1099511627776) {
        return `${nf1.format(bytes / 1099511627776)} TB`;
    }
    if (bytes >= 1073741824) {
        return `${nf1.format(bytes / 1073741824)} GB`;
    }
    return `${nf0.format(bytes / 1048576)} MB`;
}

function formatRate(bytesPerSecond) {
    if (!bytesPerSecond || bytesPerSecond < 128) {
        return '–';
    }
    if (bytesPerSecond >= 1048576) {
        return `${nf1.format(bytesPerSecond / 1048576)} MB/s`;
    }
    return `${nf0.format(bytesPerSecond / 1024)} kB/s`;
}

function formatPercent(value) {
    return value > 0 ? nf1.format(value) : '–';
}

function optional(value, suffix, digits = 0) {
    if (value === null || value === undefined) {
        return null;
    }
    return `${(digits === 0 ? nf0 : nf1).format(value)} ${suffix}`;
}

/**
 * Die CPU-Temperatur, sofern nötig mit ihrer Herkunft. Ein Wert aus einer
 * ACPI-Thermalzone misst die Umgebung des Prozessors, nicht seinen Die — er
 * unkommentiert neben einem echten Sensorwert wäre eine falsche Auskunft.
 */
function cpuTempText(cpu) {
    const text = optional(cpu.tempC, '°C');
    if (!text) {
        return null;
    }
    return cpu.tempOrigin === 'acpiZone' ? `${text} (ACPI-Zone)` : text;
}

/** Ein gerechneter Takt bekommt sein Ungefähr-Zeichen; ein gemessener nicht. */
function cpuClockText(cpu) {
    const text = optional(cpu.clockMhz, 'MHz');
    if (!text) {
        return null;
    }
    return cpu.clockEstimated ? `≈ ${text}` : text;
}

function cpuSubTooltip(cpu) {
    const parts = [];
    if (cpu.tempOrigin === 'acpiZone') {
        parts.push(
            'Die Temperatur stammt aus der ACPI-Thermalzone des Prozessors, nicht aus ihm selbst: die '
            + 'Firmware misst in seiner Umgebung. Der Wert liegt niedriger als die Die-Temperatur und '
            + 'folgt Lastspitzen mit Verzögerung.');
    } else if (cpu.tempOrigin === 'socket') {
        parts.push('Die Temperatur kommt vom Super-I/O-Chip des Mainboards und ist am Sockel gemessen, nicht am Die.');
    }
    if (cpu.clockEstimated) {
        parts.push(
            'Der Takt ist aus Basistakt und "% Processor Performance" gerechnet, weil der Sensortreiber '
            + 'ihn nicht liefert — derselbe Weg, den auch der Task-Manager geht.');
    }
    return parts.join(' ');
}

function engineList(row) {
    return Object.entries(row.gpuEngines || {})
        .sort((a, b) => b[1] - a[1])
        .map(([engine, value]) => `${engine} ${nf1.format(value)}`)
        .join(', ');
}

function formatWatts(value) {
    return value === null || value === undefined ? '–' : `${nf1.format(value)} W`;
}

/**
 * Der Kontoname ohne seine Domäne. In der Tabelle ist die Domäne fast immer
 * dieselbe und kostet nur Platz; der vollständige Name steht im Tooltip.
 */
function shortUser(account) {
    if (!account) {
        return '–';
    }
    const cut = account.lastIndexOf('\\');
    return cut < 0 ? account : account.slice(cut + 1);
}

/**
 * Die Ports eines Prozesses für die Tabellenzelle. Bewusst kurz: die volle
 * Liste sprengt jede Spaltenbreite und steht im Tooltip und im Reiter
 * „Verbindungen". Hier zählt, ob überhaupt etwas offen ist und wie viel.
 */
function portList(row) {
    const tcp = row.tcpPorts || [];
    const udp = row.udpPorts || [];
    const parts = [];

    if (tcp.length) {
        parts.push(tcp.length <= 2 ? `TCP ${tcp.join(', ')}` : `${tcp.length} TCP`);
    }
    if (udp.length) {
        parts.push(udp.length <= 2 ? `UDP ${udp.join(', ')}` : `${udp.length} UDP`);
    }
    if (row.connections) {
        parts.push(`↔ ${row.connections}`);
    }
    return parts.join('  ·  ') || '–';
}

/** Die ungekürzte Fassung für den Tooltip. */
function portDetail(row) {
    const tcp = row.tcpPorts || [];
    const udp = row.udpPorts || [];
    const parts = [];

    if (tcp.length) {
        parts.push(`Wartet auf TCP-Port ${tcp.join(', ')}`);
    }
    if (udp.length) {
        parts.push(`Gebundene UDP-Ports ${udp.join(', ')}`);
    }
    if (row.connections) {
        parts.push(`${row.connections} bestehende TCP-Verbindungen`);
    }
    return parts.join('\n') || 'Keine offenen Ports und keine Verbindungen.';
}

/** Der Zustand eines Prozesses: hängt er, ist er kürzlich abgestürzt? */
function statusText(row) {
    if (row.hung) {
        return 'reagiert nicht';
    }
    return row.fault ? row.fault : '';
}

function statusRank(row) {
    if (row.hung) {
        return 2;
    }
    return row.fault ? 1 : 0;
}

function portCount(row) {
    return (row.tcpPorts ? row.tcpPorts.length : 0)
        + (row.udpPorts ? row.udpPorts.length : 0)
        + (row.connections || 0);
}

// ---------- Erklärungen ----------

/** Was die einzelnen GPU-Engine-Typen tatsächlich bearbeiten. */
const ENGINE_HELP = {
    '3D': 'Rendering: Spiele, Desktop-Komposition, Browser und alles, was zeichnet. In der Regel der aussagekräftigste Wert.',
    'Copy': 'Datentransfer zwischen Arbeits- und Grafikspeicher — etwa das Laden von Texturen. Kurze Ausschläge sind normal.',
    'VideoDecode': 'Hardware-Dekodierung von Video. Steigt beim Abspielen von Videos und in Videokonferenzen.',
    'VideoEncode': 'Hardware-Kodierung von Video: Aufnahme, Streaming, Bildschirmübertragung.',
    'VideoProcessing': 'Nachbearbeitung von Video: Skalieren, Farbraumwandlung, Deinterlacing.',
    'Compute': 'Allgemeine Berechnungen auf der GPU (CUDA, OpenCL, DirectML) — KI-Modelle, Bildbearbeitung, Simulationen.',
    'Security': 'Geschützte Inhalte: kopiergeschützte Videowiedergabe.',
    'Overlay': 'Hardware-Overlays, etwa für Videoebenen ohne Umweg über den Desktop-Compositor.',
    'VR': 'Wiedergabe für VR-Headsets.',
};

function engineTooltip(name) {
    return ENGINE_HELP[name] || `GPU-Engine "${name}". Windows zählt die Auslastung getrennt nach Engine-Typ.`;
}

// ---------- Spaltendefinition ----------

const COLUMNS = [
    {
        key: 'name', label: 'Name', align: 'left', locked: true, width: 270, text: row => row.name,
        help: 'Name der ausführbaren Datei, daneben die Dateibeschreibung aus der Versionsressource.',
    },
    {
        key: 'pid', label: 'PID', align: 'right', width: 66, text: row => String(row.pid),
        help: 'Prozesskennung. Windows vergibt sie beim Start und verwendet sie nach dem Ende eines Prozesses wieder — sie identifiziert einen Prozess also nur, solange er läuft.',
    },
    {
        key: 'user', label: 'Benutzer', align: 'left', width: 120,
        text: row => shortUser(row.user), title: row => row.user || 'Konto nicht lesbar',
        help: 'Konto, unter dem der Prozess gestartet wurde. Angezeigt wird der Name ohne Domäne, der vollständige steht im Tooltip. Bleibt leer, wenn der Prozess sein Token nicht herausgibt — das ist bei geschützten Systemprozessen wie csrss.exe der Normalfall und kein Fehler.',
    },
    {
        key: 'status', label: 'Zustand', align: 'left', width: 120, text: statusText,
        title: row => row.hung
            ? 'Der Prozess holt seit mindestens fünf Sekunden keine Fensternachrichten mehr ab. Windows schreibt in diesem Fall „(Keine Rückmeldung)" in die Titelleiste. Das heißt nicht zwingend, dass er abgestürzt ist — er kann auch nur beschäftigt sein.'
            : row.fault
                ? `Windows hat dazu einen Eintrag im Anwendungsprotokoll: ${row.fault}. Er gilt für den Dateinamen, nicht für diesen einen Prozess — ein Neustart der Anwendung räumt ihn nicht weg.`
                : 'Der Prozess reagiert und hat in den letzten sechs Stunden keinen Fehler gemeldet.',
        help: 'Zeigt an, ob eine Anwendung hängt oder kürzlich abgestürzt ist. „Reagiert nicht" kommt aus derselben Prüfung, mit der auch der Explorer „(Keine Rückmeldung)" anzeigt. Abstürze und Hänger stammen aus dem Anwendungsprotokoll der letzten sechs Stunden.',
    },
    {
        key: 'cpu', label: 'CPU %', align: 'right', load: true, width: 74, text: row => formatPercent(row.cpu),
        help: 'Anteil an der gesamten Rechenkapazität, über alle Kerne gemittelt. 100 % bedeutet, dass alle logischen Prozessoren voll ausgelastet sind.',
    },
    {
        key: 'threads', label: 'Threads', align: 'right', width: 78, text: row => nf0.format(row.threads || 0),
        help: 'Anzahl der Threads dieses Prozesses, aus dem Prozessbaum des Systems. Bei zusammengefassten Zeilen die Summe über den ganzen Baum. Viele Threads sind für sich genommen kein Problem — die meisten warten.',
    },
    {
        key: 'ws', label: 'Arbeitsspeicher', align: 'right', width: 118, text: row => formatBytes(row.ws),
        help: 'Privater Arbeitssatz: Speicher, der exklusiv diesem Prozess gehört und gerade tatsächlich im RAM liegt. Das ist die Spalte, die der Task-Manager "Arbeitsspeicher" nennt.',
    },
    {
        key: 'priv', label: 'Privat', align: 'right', off: true, width: 96, text: row => formatBytes(row.priv),
        help: 'Private Bytes: Speicher, den der Prozess exklusiv belegt hat — einschließlich der Teile, die Windows in die Auslagerungsdatei geschoben hat. Deshalb meist größer als der Arbeitsspeicher.',
    },
    {
        key: 'gpu', label: 'GPU %', align: 'right', load: true, width: 74, text: row => formatPercent(row.gpu),
        help: 'Auslastung der Grafikkarte durch diesen Prozess: das Maximum über die Engine-Typen, nicht deren Summe.',
    },
    {
        key: 'engines', label: 'GPU-Engines', align: 'left', width: 150, text: row => engineList(row) || '–',
        help: 'Aufschlüsselung der GPU-Last nach Engine-Typ (3D, Copy, VideoDecode …). Windows zählt sie getrennt, der Task-Manager fasst sie zu einem Wert zusammen. Ein Mauszeiger über den Chips oben erklärt die einzelnen Typen.',
    },
    {
        key: 'gpuMem', label: 'VRAM', align: 'right', width: 92, text: row => formatBytes(row.gpuMem),
        help: 'Grafikspeicher, den dieser Prozess auf der Karte belegt (Zähler "GPU Process Memory / Local Usage").',
    },
    {
        key: 'rx', label: '↓ Download', align: 'right', width: 104, text: row => formatRate(row.rx),
        help: 'Empfangene Bytes pro Sekunde, aus einer Kernel-ETW-Sitzung (TCP und UDP). Läuft nur, solange dieses Fenster offen ist.',
    },
    {
        key: 'tx', label: '↑ Upload', align: 'right', width: 104, text: row => formatRate(row.tx),
        help: 'Gesendete Bytes pro Sekunde, aus einer Kernel-ETW-Sitzung (TCP und UDP).',
    },
    {
        key: 'ioRead', label: 'E/A lesen', align: 'right', width: 104, text: row => formatRate(row.ioRead),
        help: 'Gelesene Bytes pro Sekunde über alle Ein-/Ausgabekanäle — Dateien, Netzwerk und Geräte zusammen. Nicht ausschließlich Datenträgerzugriff; der reine Datenträgerdurchsatz steht in der Kachel oben.',
    },
    {
        key: 'ioWrite', label: 'E/A schreiben', align: 'right', width: 116, text: row => formatRate(row.ioWrite),
        help: 'Geschriebene Bytes pro Sekunde über alle Ein-/Ausgabekanäle — Dateien, Netzwerk und Geräte zusammen.',
    },
    {
        key: 'ports', label: 'Ports', align: 'left', width: 140, text: portList, title: portDetail,
        help: 'Ports, auf denen der Prozess auf Verbindungen wartet, dazu hinter dem Doppelpfeil die Zahl seiner bestehenden TCP-Verbindungen. Ab drei Ports steht nur noch die Anzahl; die vollständige Liste zeigt der Tooltip, geordnet nach Port der Reiter "Verbindungen".',
    },
    {
        key: 'services', label: 'Dienste', align: 'left', width: 170, text: row => (row.services || []).join(', ') || '–',
        help: 'Windows-Dienste, die in diesem Prozess laufen. Löst "Diensthost: lokales System" zu den konkret laufenden Diensten auf.',
    },
    {
        key: 'path', label: 'Datei', align: 'left', width: 300, text: row => row.path || '–',
        help: 'Vollständiger Pfad der ausgeführten Datei. Bei Systemprozessen ohne Leserechte bleibt die Spalte leer.',
    },
    {
        key: 'note', label: 'Notiz', align: 'left', width: 160, text: row => notes[row.name] || '',
        help: 'Eigene Notiz zum Prozess. Doppelklick zum Bearbeiten. Die Notiz hängt am Prozessnamen und bleibt über Neustarts erhalten.',
    },
];

/**
 * Die Abschnitte der gruppierten Tabelle, in der Reihenfolge des Task-Managers:
 * erst das, was der Benutzer selbst geöffnet hat, zuletzt das System.
 */
const CATEGORIES = [
    {
        key: 'app', label: 'Apps',
        help: 'Prozesse eines Benutzerkontos mit sichtbarem Fenster — das, was als Anwendung auf dem Bildschirm steht.',
    },
    {
        key: 'background', label: 'Hintergrundprozesse',
        help: 'Prozesse eines Benutzerkontos ohne Fenster: Aktualisierungsdienste, Hilfsprozesse von Browsern, Autostart-Programme im Infobereich.',
    },
    {
        key: 'windows', label: 'Windows-Prozesse',
        help: 'Prozesse unter einem Systemkonto — lokales System, lokaler Dienst, Netzwerkdienst und die virtuellen Dienstkonten. Dazu die geschützten Prozesse, deren Konto sich nicht auslesen lässt.',
    },
];

const state = {
    processes: [],
    connections: [],
    connectionTotal: 0,
    energy: null,
    // Der letzte vollständige Messpunkt, damit ein Reiterwechsel sofort etwas
    // anzeigen kann und nicht bis zum nächsten Takt leer bleibt.
    last: null,
    history: { cpu: [], gpu: [], ram: [], net: [], disk: [], cpuPower: [], gpuPower: [] },
    // Wird vom Host überschrieben, sobald die Einstellungen eintreffen.
    chart: { cpu: true, gpu: true, ram: true, net: false, disk: false },
    sortKey: 'cpu',
    sortAsc: false,
    filter: '',
    // Standardmäßig zusammengefasst und auf aktive Prozesse beschränkt — so ist
    // die Liste beim Öffnen kurz genug, um etwas darauf zu erkennen.
    aggregate: true,
    onlyActive: true,
    // Zugeklappte Abschnitte der Prozesstabelle, als Menge von Art-Schlüsseln.
    collapsed: new Set(),
    pinned: new Set(),
    expanded: new Set(),
    editing: null,
    view: 'processes',
    systemLoaded: false,
    connSort: { key: 'localPort', asc: true },
    connFilter: '',
    connListening: true,
    connUdp: true,
    connLoopback: true,
    diag: {},
    logs: [],
    logAll: false,
};

function loadJson(key, fallback) {
    try {
        return JSON.parse(localStorage.getItem(key)) ?? fallback;
    } catch {
        return fallback;
    }
}

const notes = loadJson(STORAGE_NOTES, {});
const hiddenColumns = new Set(loadJson(STORAGE_COLUMNS, COLUMNS.filter(c => c.off).map(c => c.key)));
const dismissedNotices = new Set(loadJson(STORAGE_NOTICES, []));

state.collapsed = new Set(loadJson(STORAGE_COLLAPSED, []));

// ---------- Spaltenbreiten ----------

/** Die gezogenen Breiten, je Tabelle: { processes: { cpu: 90, … }, … }. */
const columnWidths = loadJson(STORAGE_WIDTHS, {});

/** Schmaler geht es nicht, sonst bleibt von der Überschrift nichts übrig. */
const MIN_COLUMN_WIDTH = 46;

function widthOf(table, column) {
    const stored = columnWidths[table]?.[column.key];
    return Number.isFinite(stored) ? stored : column.width;
}

function setWidth(table, key, width) {
    if (!columnWidths[table]) {
        columnWidths[table] = {};
    }
    columnWidths[table][key] = Math.round(width);
}

function resetWidth(table, key) {
    delete columnWidths[table]?.[key];
}

/** Gespeichert wird erst beim Loslassen, nicht bei jedem Mausschritt. */
function saveWidths() {
    localStorage.setItem(STORAGE_WIDTHS, JSON.stringify(columnWidths));
}

function isResized(tableKey) {
    const stored = columnWidths[tableKey];
    return Boolean(stored) && Object.keys(stored).length > 0;
}

/**
 * Legt die Breiten auf die Kopfzellen und die Tabelle.
 *
 * Solange nichts gezogen wurde, bleibt die Tabelle bei der Breitenverteilung des
 * Browsers: die richtet sich nach dem Inhalt und passt sich der Fensterbreite an.
 * Erst mit dem ersten Zug wird auf feste Breiten umgestellt — anders lässt sich
 * eine einzelne Spalte nicht gezielt einstellen, denn bei automatischer
 * Verteilung überschriebe der Inhalt jeden gesetzten Wert. Ab dann ist die
 * Tabelle so breit wie die Summe ihrer Spalten und scrollt notfalls waagerecht.
 *
 * Die letzte Spalte bekommt auch dann keine feste Breite: sie nimmt den Rest
 * auf, sonst klaffte bei breitem Fenster eine Lücke hinter der Tabelle.
 */
function applyColumnWidths(table, tableKey, columns) {
    const cells = table.tHead?.rows[0]?.cells;
    if (!cells) {
        return;
    }

    if (!isResized(tableKey)) {
        table.style.tableLayout = '';
        table.style.width = '';
        for (const th of cells) {
            th.style.width = '';
        }
        return;
    }

    let total = 0;
    columns.forEach((column, index) => {
        const width = widthOf(tableKey, column);
        total += width;
        const th = cells[index];
        if (th) {
            th.style.width = index === columns.length - 1 ? '' : `${width}px`;
        }
    });

    table.style.tableLayout = 'fixed';
    table.style.width = `${total}px`;
}

/**
 * Übernimmt die gerade gezeichneten Breiten als Ausgangswerte. Ohne diesen
 * Schritt spränge die Tabelle beim ersten Zug auf die hinterlegten Standardmaße
 * um — man zöge an einer Spalte und alle anderen bewegten sich mit.
 */
function freezeWidths(tableKey, columns, cells) {
    if (isResized(tableKey)) {
        return;
    }

    columns.forEach((column, index) => {
        const th = cells[index];
        if (th) {
            setWidth(tableKey, column.key, th.getBoundingClientRect().width);
        }
    });
}

/**
 * Hängt den Ziehgriff an die rechte Kante einer Kopfzelle. Der Griff schluckt
 * Klick und Ziehen: sonst würde jedes Verbreitern nebenbei die Sortierung
 * umstellen oder die Spalte verschieben.
 */
function addResizeHandle(th, tableKey, columns, column, apply) {
    const grip = document.createElement('span');
    grip.className = 'col-resize';
    grip.title = 'Breite ziehen. Doppelklick setzt die Spalte auf ihre Ausgangsbreite zurück.';

    grip.addEventListener('pointerdown', event => {
        event.preventDefault();
        event.stopPropagation();

        const startX = event.clientX;
        const startWidth = th.getBoundingClientRect().width;
        freezeWidths(tableKey, columns, th.parentElement.cells);
        // Während des Ziehens darf die Kopfzelle nicht zugleich verschoben werden.
        const draggable = th.draggable;
        th.draggable = false;
        grip.classList.add('active');
        grip.setPointerCapture(event.pointerId);

        const move = moved => {
            setWidth(tableKey, column.key, Math.max(MIN_COLUMN_WIDTH, startWidth + moved.clientX - startX));
            apply();
        };

        const finish = () => {
            grip.removeEventListener('pointermove', move);
            grip.removeEventListener('pointerup', finish);
            grip.removeEventListener('pointercancel', finish);
            grip.classList.remove('active');
            th.draggable = draggable;
            saveWidths();
        };

        grip.addEventListener('pointermove', move);
        grip.addEventListener('pointerup', finish);
        grip.addEventListener('pointercancel', finish);
    });

    grip.addEventListener('click', event => event.stopPropagation());
    grip.addEventListener('dblclick', event => {
        event.stopPropagation();
        resetWidth(tableKey, column.key);
        saveWidths();
        apply();
    });

    th.append(grip);
}

/** Die gespeicherte Spaltenreihenfolge, als Liste von Schlüsseln. */
let columnOrder = loadJson(STORAGE_ORDER, []);

/**
 * Die Spalten in der Reihenfolge des Benutzers. Unbekannte Schlüssel aus einer
 * älteren Fassung fallen heraus, neu hinzugekommene Spalten hängen hinten an —
 * eine gespeicherte Reihenfolge darf keine Spalte verschlucken.
 */
function orderedColumns() {
    const byKey = new Map(COLUMNS.map(column => [column.key, column]));
    const ordered = columnOrder.map(key => byKey.get(key)).filter(Boolean);
    const known = new Set(ordered.map(column => column.key));
    return [...ordered, ...COLUMNS.filter(column => !known.has(column.key))];
}

function activeColumns() {
    return orderedColumns().filter(column => column.locked || !hiddenColumns.has(column.key));
}

/** Verschiebt eine Spalte vor oder hinter eine andere und merkt sich das. */
function moveColumn(fromKey, toKey) {
    if (fromKey === toKey) {
        return;
    }

    const keys = orderedColumns().map(column => column.key);
    const from = keys.indexOf(fromKey);
    const to = keys.indexOf(toKey);
    if (from < 0 || to < 0) {
        return;
    }

    keys.splice(from, 1);
    // Nach dem Entfernen rutscht ein Ziel rechts der Ausgangsposition um eins nach
    // vorn. Einfügen an derselben Indexzahl heißt deshalb: nach links gezogen
    // landet die Spalte vor dem Ziel, nach rechts dahinter — also genau dort, wo
    // sie losgelassen wurde.
    keys.splice(to, 0, fromKey);

    columnOrder = keys;
    localStorage.setItem(STORAGE_ORDER, JSON.stringify(columnOrder));

    // Die Zellen einer Zeile entstehen in Spaltenreihenfolge; der Zwischenspeicher
    // wäre danach falsch sortiert.
    rowCache.clear();
    renderHead();
    renderTable();
    buildColumnsMenu();
}

function saveColumns() {
    localStorage.setItem(STORAGE_COLUMNS, JSON.stringify([...hiddenColumns]));
}

function saveNotes() {
    localStorage.setItem(STORAGE_NOTES, JSON.stringify(notes));
}

function saveDismissed() {
    localStorage.setItem(STORAGE_NOTICES, JSON.stringify([...dismissedNotices]));
}

const elements = {
    notices: document.getElementById('notices'),
    cpuPercent: document.getElementById('cpu-percent'),
    cpuSub: document.getElementById('cpu-sub'),
    cpuThreads: document.getElementById('cpu-threads'),
    cpuCores: document.getElementById('cpu-cores'),
    gpuPercent: document.getElementById('gpu-percent'),
    gpuSub: document.getElementById('gpu-sub'),
    gpuEngines: document.getElementById('gpu-engines'),
    ramPercent: document.getElementById('ram-percent'),
    ramSub: document.getElementById('ram-sub'),
    ramCommit: document.getElementById('ram-commit'),
    netRx: document.getElementById('net-rx'),
    netRxUnit: document.getElementById('net-rx-unit'),
    netSub: document.getElementById('net-sub'),
    diskBusy: document.getElementById('disk-busy'),
    diskSub: document.getElementById('disk-sub'),
    chart: document.getElementById('history'),
    chartLegend: document.getElementById('chart-legend'),
    headRow: document.getElementById('head-row'),
    tbody: document.querySelector('#processes tbody'),
    status: document.getElementById('status'),
    columnsButton: document.getElementById('columns-button'),
    columnsMenu: document.getElementById('columns-menu'),
    systemGroups: document.getElementById('system-groups'),
    systemDevices: document.getElementById('system-devices'),
    systemDrives: document.getElementById('system-drives'),
    systemEmpty: document.getElementById('system-empty'),
    refreshDevices: document.getElementById('refresh-devices'),
    tempList: document.getElementById('temp-list'),
    tempEmpty: document.getElementById('temp-empty'),
    portsList: document.getElementById('ports-list'),
    portsEmpty: document.getElementById('ports-empty'),
    portsSummary: document.getElementById('ports-summary'),
    energyTotal: document.getElementById('energy-total'),
    energyTotalSub: document.getElementById('energy-total-sub'),
    energyCpu: document.getElementById('energy-cpu'),
    energyCpuSub: document.getElementById('energy-cpu-sub'),
    energyGpu: document.getElementById('energy-gpu'),
    energyGpuSub: document.getElementById('energy-gpu-sub'),
    energyChart: document.getElementById('energy-history'),
    energyLegend: document.getElementById('energy-legend'),
    batteryTile: document.getElementById('battery-tile'),
    batteryPercent: document.getElementById('battery-percent'),
    batterySub: document.getElementById('battery-sub'),
    batteryFill: document.getElementById('battery-fill'),
    batteryCard: document.getElementById('battery-card'),
    batteryDetails: document.getElementById('battery-details'),
    fanList: document.getElementById('fan-list'),
    fanEmpty: document.getElementById('fan-empty'),
    railList: document.getElementById('rail-list'),
    railEmpty: document.getElementById('rail-empty'),
    energyHead: document.getElementById('energy-head'),
    energyProcesses: document.querySelector('#energy-processes tbody'),
    connHead: document.getElementById('conn-head'),
    connBody: document.querySelector('#connections tbody'),
    connStatus: document.getElementById('conn-status'),
    logList: document.getElementById('log-list'),
    logSummary: document.getElementById('log-summary'),
    logStatus: document.getElementById('log-status'),
};

// ---------- Kacheln ----------

function renderTiles(data) {
    elements.cpuPercent.textContent = nf1.format(data.cpu.percent);
    elements.cpuSub.textContent = [
        cpuTempText(data.cpu),
        // Nur zeigen, wenn beide Werte da sind — sonst steht die Sockeltemperatur
        // schon vorn als einziger Temperaturwert.
        data.cpu.socketTempC && data.cpu.tempC && data.cpu.socketTempC !== data.cpu.tempC
            ? `Sockel ${nf0.format(data.cpu.socketTempC)} °C`
            : null,
        cpuClockText(data.cpu),
        optional(data.cpu.powerW, 'W', 1),
    ].filter(Boolean).join('  ·  ') || 'keine Sensordaten';
    elements.cpuSub.title = cpuSubTooltip(data.cpu);

    const system = data.system || {};
    elements.cpuThreads.textContent = system.threads > 0
        ? `${nf0.format(system.processes)} Prozesse  ·  ${nf0.format(system.threads)} Threads`
        : 'Prozesse werden erfasst …';

    renderCores(data.cpu.cores);

    if (data.gpu.available) {
        elements.gpuPercent.textContent = nf1.format(data.gpu.percent);
        elements.gpuSub.textContent = [
            optional(data.gpu.tempC, '°C'),
            data.gpu.memTotalBytes > 0
                ? `${formatBytes(data.gpu.memUsedBytes)} / ${formatBytes(data.gpu.memTotalBytes)}`
                : null,
            optional(data.gpu.powerW, 'W', 1),
            data.gpu.fanRpm ? optional(data.gpu.fanRpm, 'rpm') : null,
        ].filter(Boolean).join('  ·  ') || 'keine Sensordaten';
    } else {
        elements.gpuPercent.textContent = '–';
        elements.gpuSub.textContent = 'GPU-Zähler nicht verfügbar';
    }

    renderEngines(data.gpu.byEngineType);

    elements.ramPercent.textContent = nf1.format(data.ram.percent);
    elements.ramSub.textContent = `${formatBytes(data.ram.usedBytes)} / ${formatBytes(data.ram.totalBytes)} belegt`;
    elements.ramCommit.textContent = `${formatBytes(data.ram.committedBytes)} zugesichert`;

    const rx = data.net.rx || 0;
    elements.netRx.textContent = rx >= 1048576 ? nf1.format(rx / 1048576) : nf0.format(rx / 1024);
    elements.netRxUnit.textContent = rx >= 1048576 ? 'MB/s ↓' : 'kB/s ↓';
    elements.netSub.textContent = data.net.available
        ? `${formatRate(data.net.tx)} Upload`
        : 'Netz-Zähler nicht verfügbar';

    if (data.disk.available) {
        elements.diskBusy.textContent = nf0.format(data.disk.busyPercent);
        elements.diskSub.textContent =
            `${formatRate(data.disk.read)} lesen  ·  ${formatRate(data.disk.write)} schreiben`;
    } else {
        elements.diskBusy.textContent = '–';
        elements.diskSub.textContent = 'Datenträger-Zähler nicht verfügbar';
    }
}

function renderCores(cores) {
    if (elements.cpuCores.childElementCount !== cores.length) {
        elements.cpuCores.replaceChildren(...cores.map(() => document.createElement('i')));
    }
    cores.forEach((value, index) => {
        const bar = elements.cpuCores.children[index];
        bar.style.height = `${Math.max(4, value)}%`;
        bar.title = `Kern ${index}: ${nf0.format(value)} %`;
    });
}

function renderEngines(byEngineType) {
    const entries = Object.entries(byEngineType || {});
    elements.gpuEngines.replaceChildren(...entries.map(([name, value]) => {
        const chip = document.createElement('span');
        chip.className = 'chip';
        chip.textContent = `${name} ${nf1.format(value)} %`;
        chip.title = engineTooltip(name);
        return chip;
    }));
}

// ---------- Meldungen ----------

function noticesFor(diag, cpu = {}) {
    const list = [];
    const add = (id, text) => list.push({ id, text });

    // Zwei getrennte Meldungen, weil es zwei verschiedene Ursachen sind: der
    // Prozessor gibt seine Werte nur über einen Kernel-Treiber heraus, der
    // Super-I/O-Chip existiert auf Notebooks überhaupt nicht in lesbarer Form.
    if (diag.cpuSensorsBlocked) {
        const substitutes = [
            cpu.tempOrigin === 'acpiZone' ? 'die ACPI-Thermalzone des Prozessors' : null,
            cpu.clockEstimated ? 'ein aus dem Leistungszähler gerechneter Takt' : null,
        ].filter(Boolean);

        const replacement = substitutes.length
            ? ` Ersatzweise ${substitutes.length > 1 ? 'stehen' : 'steht'} hier ${substitutes.join(' und ')}`
              + ' — ohne Treiber lesbar, dafür träger und gröber.'
            : '';

        add('cpu-sensors',
            'Temperatur, Takt und Leistungsaufnahme des Prozessors stehen in seinen Modellregistern, und ' +
            'dorthin kommt allein der Kernel-Treiber WinRing0 — den die Speicherintegrität und die ' +
            'Sperrliste für verwundbare Treiber blockieren.' + replacement +
            ' Die GPU-Werte liest die Sensorbibliothek ohne eigenen Treiber und sind nicht betroffen.');
    }

    if (diag.boardSensorsMissing) {
        add('board-sensors', diag.hasBattery
            ? 'Sockeltemperatur und Gehäuselüfter meldet dieses Gerät nicht. In Notebooks hängen beide am ' +
              'Embedded Controller, und den spricht jeder Hersteller anders an — die Sensorbibliothek ' +
              'kennt dafür keinen allgemeinen Weg. Ein geladener Sensortreiber würde daran nichts ändern; ' +
              'die Drehzahl zeigt nur das Werkzeug des Herstellers.'
            : 'Sockeltemperatur und Gehäuselüfter sind nicht lesbar: sie kommen aus dem Super-I/O-Chip des ' +
              'Mainboards, und der taucht ohne den Kernel-Treiber WinRing0 in der Hardwareliste gar nicht ' +
              'erst auf. Blockiert wird er von der Speicherintegrität und der Sperrliste für verwundbare ' +
              'Treiber.');
    }
    if (diag.gpuCountersMissing) {
        add('gpu-counters',
            'Der Zählersatz "GPU Engine" fehlt — vermutlich eine Treiberkonstellation, die ihn nicht ' +
            'registriert. GPU-Last und die Engine-Aufschlüsselung bleiben leer.');
    }
    if (diag.networkCountersMissing) {
        add('net-counters',
            'Der Zählersatz "Network Interface" fehlt. Der Gesamtdurchsatz des Netzwerks kann nicht ' +
            'ermittelt werden.');
    }
    if (diag.diskCountersMissing) {
        add('disk-counters',
            'Der Zählersatz "PhysicalDisk" fehlt. Der Datenträgerdurchsatz kann nicht ermittelt werden.');
    }
    if (diag.processCountersMissing) {
        add('process-counters',
            'Weder "Process V2" noch "Process" liefern Zähler — die Prozessliste bleibt leer.');
    }
    if (diag.legacyProcessCounters) {
        add('legacy-process-counters',
            'Der Zählersatz "Process V2" fehlt; es wird der ältere "Process" verwendet. Gleichnamige ' +
            'Prozesse teilen sich dort eine Zählerinstanz, einzelne Werte können dadurch ungenau sein.');
    }
    if (diag.networkTraceError) {
        add('net-trace', `Netzverkehr pro Prozess nicht verfügbar: ${diag.networkTraceError}`);
    }

    return list;
}

function renderNotices(diag, cpu) {
    const wanted = noticesFor(diag, cpu).filter(notice => !dismissedNotices.has(notice.id));
    const shown = [...elements.notices.children].map(node => node.dataset.id);

    // Nur neu aufbauen, wenn sich wirklich etwas geändert hat — sonst flackert
    // die Leiste im Sekundentakt.
    if (shown.length === wanted.length && wanted.every((notice, i) => notice.id === shown[i])) {
        return;
    }

    elements.notices.replaceChildren(...wanted.map(notice => {
        const box = document.createElement('p');
        box.className = 'notice';
        box.dataset.id = notice.id;

        const text = document.createElement('span');
        text.textContent = notice.text;

        const close = document.createElement('button');
        close.type = 'button';
        close.className = 'notice-close';
        close.textContent = '✕';
        close.title = 'Meldung dauerhaft ausblenden';
        close.addEventListener('click', () => {
            dismissedNotices.add(notice.id);
            saveDismissed();
            box.remove();
        });

        box.append(text, close);
        return box;
    }));
}

// ---------- Verlaufsdiagramm ----------

/**
 * Die auswählbaren Reihen. Prozentwerte teilen sich die Achse 0–100; Raten haben
 * keine Obergrenze, werden deshalb als Fläche auf ihr eigenes Maximum skaliert
 * und schreiben dieses Maximum in die Legende — ohne die Zahl wäre eine
 * selbstskalierende Fläche nicht lesbar.
 */
const CHART_SERIES = [
    { key: 'cpu', label: 'CPU', variable: '--cpu', rate: false },
    { key: 'gpu', label: 'GPU', variable: '--gpu', rate: false },
    { key: 'ram', label: 'RAM', variable: '--ram', rate: false },
    { key: 'net', label: 'Netz', variable: '--net', rate: true },
    { key: 'disk', label: 'Datenträger', variable: '--disk', rate: true },
];

function seriesColor(series) {
    return getComputedStyle(document.documentElement).getPropertyValue(series.variable).trim();
}

function drawChart() {
    const canvas = elements.chart;
    const context = canvas.getContext('2d');
    const ratio = window.devicePixelRatio || 1;
    const width = canvas.clientWidth;
    const height = canvas.clientHeight;

    if (width === 0 || height === 0) {
        return;
    }

    if (canvas.width !== width * ratio || canvas.height !== height * ratio) {
        canvas.width = width * ratio;
        canvas.height = height * ratio;
    }

    const style = getComputedStyle(document.documentElement);
    context.setTransform(ratio, 0, 0, ratio, 0, 0);
    context.clearRect(0, 0, width, height);

    context.strokeStyle = style.getPropertyValue('--grid').trim() || 'rgba(255,255,255,0.06)';
    context.lineWidth = 1;
    for (const fraction of [0.25, 0.5, 0.75]) {
        const y = Math.round(height * fraction) + 0.5;
        context.beginPath();
        context.moveTo(0, y);
        context.lineTo(width, y);
        context.stroke();
    }

    // Die x-Achse ist immer 300 Sekunden breit, auch wenn erst wenige Punkte
    // vorliegen — sonst wandert die Kurve beim Füllen des Puffers.
    const capacity = 300;
    const path = (values, scale) => {
        const step = width / (capacity - 1);
        const offset = width - (values.length - 1) * step;
        const toY = value => height - (Math.min(scale, Math.max(0, value)) / scale) * (height - 2) - 1;

        context.beginPath();
        context.moveTo(offset, toY(values[0]));
        for (let i = 1; i < values.length; i++) {
            context.lineTo(offset + i * step, toY(values[i]));
        }
        return { first: offset, last: offset + (values.length - 1) * step };
    };

    const selected = CHART_SERIES.filter(series => state.chart[series.key]);

    // Flächen zuerst, damit die Linien darüber liegen und lesbar bleiben.
    for (const series of selected.filter(s => s.rate)) {
        const values = state.history[series.key];
        if (!values || values.length < 2) {
            continue;
        }

        const { first, last } = path(values, Math.max(...values, 1));
        context.lineTo(last, height);
        context.lineTo(first, height);
        context.closePath();
        context.fillStyle = seriesColor(series);
        context.globalAlpha = 0.16;
        context.fill();
        context.globalAlpha = 1;

        path(values, Math.max(...values, 1));
        context.strokeStyle = seriesColor(series);
        context.lineWidth = 1;
        context.globalAlpha = 0.55;
        context.stroke();
        context.globalAlpha = 1;
    }

    // Von hinten nach vorn: CPU ist die wichtigste Kurve und liegt oben.
    for (const series of selected.filter(s => !s.rate).reverse()) {
        const values = state.history[series.key];
        if (!values || values.length < 2) {
            continue;
        }

        path(values, 100);
        context.strokeStyle = seriesColor(series);
        context.lineWidth = 1.5;
        context.stroke();
    }

    renderLegend(selected);
}

function renderLegend(selected) {
    const parts = selected.map(series => {
        const marker = document.createElement('i');
        marker.dataset.metric = series.key;
        if (series.rate) {
            marker.classList.add('area');
        }

        const label = document.createElement('span');
        if (series.rate) {
            const peak = Math.max(...(state.history[series.key] || [0]), 0);
            label.textContent = `${series.label} max. ${formatRate(peak) === '–' ? '0' : formatRate(peak)}`;
        } else {
            label.textContent = series.label;
        }

        const wrap = document.createDocumentFragment();
        wrap.append(marker, label);
        return wrap;
    });

    elements.chartLegend.replaceChildren(...parts);
}

// ---------- Aggregation ----------

/**
 * Fasst gleichnamige Kindprozesse unter ihrem Elternprozess zusammen — die
 * Dutzend Renderer eines Browsers werden zu einer Zeile.
 *
 * Entscheidend ist die Abbruchbedingung: gerollt wird nur, solange der
 * Elternprozess **dieselbe ausführbare Datei** ist. Ohne diese Bedingung endet
 * jeder Prozess beim obersten noch lebenden Vorfahren, und unter Windows ist das
 * für fast alles `wininit.exe`: von mehreren hundert Prozessen bliebe gut ein
 * Dutzend Zeilen übrig, davon eine mit dem halben System als Kindern. Programme,
 * die ihre Hilfsprozesse unter anderem Namen starten, stehen dafür einzeln — das
 * ist die ehrlichere Auskunft.
 */
function aggregateTree(processes) {
    const byPid = new Map(processes.map(p => [p.pid, p]));

    // Liefert den obersten gleichnamigen Vorfahren samt Abstand zu ihm, für die
    // Einrückung beim Aufklappen.
    const rootOf = process => {
        let current = process;
        let depth = 0;
        // Tiefenbegrenzung als Schutz vor recycelten PIDs, die einen Zyklus bilden.
        while (depth < 32) {
            const parent = current.parentPid === null || current.parentPid === undefined
                ? undefined
                : byPid.get(current.parentPid);
            if (!parent || parent === current || parent.name !== current.name) {
                break;
            }
            current = parent;
            depth++;
        }
        return { root: current, depth };
    };

    const groups = new Map();
    for (const process of processes) {
        const { root, depth } = rootOf(process);
        let group = groups.get(root.pid);
        if (!group) {
            group = {
                pid: root.pid, parentPid: null, name: root.name, description: root.description,
                path: root.path, cpu: 0, ws: 0, priv: 0, gpu: 0, gpuEngines: {}, gpuMem: 0,
                rx: 0, tx: 0, ioRead: 0, ioWrite: 0, threads: 0, services: [], children: 0, members: [],
                // Konto, Art und Fenstertitel kommen vom Elternprozess: eine
                // zusammengefasste Zeile trägt seinen Namen, also gilt auch sein
                // Konto. Die Ports dagegen werden über den ganzen Baum gesammelt —
                // bei Browsern lauscht selten der Elternprozess.
                user: root.user, category: root.category, window: root.window,
                tcpPorts: [], udpPorts: [], connections: 0,
                // Ein hängender Kindprozess färbt die ganze Gruppe: sonst
                // verschwindet die Meldung genau dann, wenn sie gebraucht wird.
                hung: false, fault: null,
            };
            groups.set(root.pid, group);
        }

        if (process.pid !== root.pid) {
            group.members.push({ ...process, depth });
        }

        group.cpu += process.cpu;
        group.ws += process.ws;
        group.priv += process.priv;
        group.gpuMem += process.gpuMem;
        group.rx += process.rx || 0;
        group.tx += process.tx || 0;
        group.ioRead += process.ioRead || 0;
        group.ioWrite += process.ioWrite || 0;
        group.threads += process.threads || 0;
        group.connections += process.connections || 0;
        group.hung = group.hung || Boolean(process.hung);
        group.fault = group.fault || process.fault || null;
        for (const [engine, value] of Object.entries(process.gpuEngines || {})) {
            group.gpuEngines[engine] = (group.gpuEngines[engine] || 0) + value;
        }
        for (const service of process.services || []) {
            if (!group.services.includes(service)) {
                group.services.push(service);
            }
        }
        for (const port of process.tcpPorts || []) {
            if (!group.tcpPorts.includes(port)) {
                group.tcpPorts.push(port);
            }
        }
        for (const port of process.udpPorts || []) {
            if (!group.udpPorts.includes(port)) {
                group.udpPorts.push(port);
            }
        }
        if (process.pid !== root.pid) {
            group.children++;
        }
    }

    for (const group of groups.values()) {
        group.cpu = Math.min(100, group.cpu);
        // Wie bei der Gesamtlast: Maximum über die Engine-Typen, nicht Summe
        // (DESIGN.md §8.3).
        const values = Object.values(group.gpuEngines);
        group.gpu = values.length ? Math.min(100, Math.max(...values)) : 0;
        group.tcpPorts.sort((a, b) => a - b);
        group.udpPorts.sort((a, b) => a - b);
    }

    return [...groups.values()];
}

// ---------- Zeilenauswahl ----------

function matchesFilter(row, needle) {
    return row.name.toLowerCase().includes(needle)
        || (row.description || '').toLowerCase().includes(needle)
        || (row.path || '').toLowerCase().includes(needle)
        || (row.user || '').toLowerCase().includes(needle)
        || (row.window || '').toLowerCase().includes(needle)
        || (notes[row.name] || '').toLowerCase().includes(needle)
        || (row.services || []).some(service => service.toLowerCase().includes(needle))
        || portList(row).toLowerCase().includes(needle)
        || statusText(row).toLowerCase().includes(needle)
        || String(row.pid) === needle;
}

function compare(a, b, key) {
    switch (key) {
        case 'name':
            return a.name.localeCompare(b.name, 'de');
        case 'path':
            return (a.path || '').localeCompare(b.path || '', 'de');
        case 'user':
            return shortUser(a.user).localeCompare(shortUser(b.user), 'de');
        case 'note':
            return (notes[a.name] || '').localeCompare(notes[b.name] || '', 'de');
        case 'services':
            return (a.services || []).join().localeCompare((b.services || []).join(), 'de');
        case 'engines':
            return Object.keys(a.gpuEngines || {}).length - Object.keys(b.gpuEngines || {}).length;
        case 'ports':
            return portCount(a) - portCount(b);
        // Absteigend sortiert stehen damit die auffälligen Zeilen oben: erst was
        // hängt, dann was abgestürzt ist, dann der Rest.
        case 'status':
            return statusRank(a) - statusRank(b);
        default:
            return (a[key] || 0) - (b[key] || 0);
    }
}

function sortRows(rows) {
    const direction = state.sortAsc ? 1 : -1;
    // Die PID als zweites Kriterium: bei gleichen Werten bleibt die Reihenfolge
    // sonst dem Zufall überlassen und die Tabelle zappelt.
    return [...rows].sort((a, b) => direction * compare(a, b, state.sortKey) || a.pid - b.pid);
}

function visibleRows() {
    const all = state.aggregate ? aggregateTree(state.processes) : state.processes;
    const needle = state.filter.toLowerCase();

    const pinned = [];
    const rest = [];
    for (const row of all) {
        // Angeheftete Zeilen überstehen jeden Filter — genau dafür sind sie da.
        if (state.pinned.has(row.pid)) {
            pinned.push(row);
            continue;
        }
        if (needle && !matchesFilter(row, needle)) {
            continue;
        }
        // Ein hängender oder abgestürzter Prozess bleibt stehen, auch wenn er
        // keine Last erzeugt — gerade dann ist er interessant.
        if (state.onlyActive && row.cpu < 0.1 && row.gpu < 0.1 && !row.rx && !row.tx
            && !row.hung && !row.fault) {
            continue;
        }
        rest.push(row);
    }

    return { pinned: sortRows(pinned), rest: sortRows(rest), total: all.length };
}

/** Prozesse aus einer älteren Nutzlast tragen keine Art; sie gelten als System. */
function categoryOf(row) {
    return row.category || 'windows';
}

// ---------- Tabelle ----------

const rowCache = new Map();

/**
 * Merkt sich die gezogene Spalte. Zugleich die Bremse für den Klick, den der
 * Browser nach dem Loslassen noch schickt — ohne sie würde jedes Verschieben
 * nebenbei die Sortierung umstellen.
 */
const drag = { key: null, moved: false };

function applyProcessWidths() {
    applyColumnWidths(document.getElementById('processes'), 'processes', activeColumns());
}

function renderHead() {
    const columns = activeColumns();
    const cells = columns.map((column, index) => {
        const th = document.createElement('th');
        th.textContent = column.label;
        th.dataset.sort = column.key;
        th.className = column.align === 'right' ? 'num' : '';
        th.title = `${column.help}\n\nKlicken sortiert, Ziehen verschiebt die Spalte, die rechte Kante ändert die Breite.`;
        th.draggable = true;

        // Die letzte Spalte füllt den Rest der Tabelle und hat deshalb keine
        // eigene Breite, an der man ziehen könnte.
        if (index < columns.length - 1) {
            addResizeHandle(th, 'processes', columns, column, applyProcessWidths);
        }
        if (state.sortKey === column.key) {
            th.classList.add(state.sortAsc ? 'sorted-asc' : 'sorted-desc');
        }

        th.addEventListener('click', () => {
            if (drag.moved) {
                drag.moved = false;
                return;
            }
            if (state.sortKey === column.key) {
                state.sortAsc = !state.sortAsc;
            } else {
                state.sortKey = column.key;
                // Zahlenspalten absteigend beginnen, Textspalten aufsteigend.
                state.sortAsc = column.align === 'left';
            }
            renderHead();
            renderTable();
        });

        th.addEventListener('dragstart', event => {
            drag.key = column.key;
            drag.moved = false;
            event.dataTransfer.effectAllowed = 'move';
            // Ohne Nutzdaten kommt in manchen Umgebungen kein drop-Ereignis an.
            event.dataTransfer.setData('text/plain', column.key);
            th.classList.add('dragging');
        });

        th.addEventListener('dragover', event => {
            if (drag.key === null || drag.key === column.key) {
                return;
            }
            event.preventDefault();
            event.dataTransfer.dropEffect = 'move';

            // Die Kante zeigt, wo die Spalte landet.
            const keys = activeColumns().map(active => active.key);
            const before = keys.indexOf(drag.key) > keys.indexOf(column.key);
            th.classList.toggle('drop-before', before);
            th.classList.toggle('drop-after', !before);
        });

        th.addEventListener('dragleave', () => th.classList.remove('drop-before', 'drop-after'));

        th.addEventListener('drop', event => {
            event.preventDefault();
            th.classList.remove('drop-before', 'drop-after');
            if (drag.key !== null) {
                drag.moved = true;
                moveColumn(drag.key, column.key);
                drag.key = null;
            }
        });

        th.addEventListener('dragend', () => {
            drag.key = null;
            for (const other of elements.headRow.children) {
                other.classList.remove('dragging', 'drop-before', 'drop-after');
            }
        });

        return th;
    });

    elements.headRow.replaceChildren(...cells);
    applyProcessWidths();

    // Die Abschnittsüberschriften kleben unter der Kopfzeile. Deren Höhe hängt an
    // Schriftgröße und Zoomstufe und wird deshalb gemessen, statt sie in der
    // Formatvorlage zu raten — ein zu kleiner Wert schöbe sie darunter.
    requestAnimationFrame(() => {
        const height = elements.headRow.getBoundingClientRect().height;
        if (height > 0) {
            document.documentElement.style.setProperty('--head-height', `${Math.ceil(height)}px`);
        }
    });
}

function loadClass(value) {
    if (value >= 50) {
        return ' hot';
    }
    return value >= 15 ? ' warm' : '';
}

/**
 * Baut oder aktualisiert eine Tabellenzeile. Bestehende Zeilen werden
 * wiederverwendet und nur dort beschrieben, wo sich der Text geändert hat —
 * sonst flackert die Tabelle bei jedem Takt.
 */
function rowElement(item, columns) {
    const row = item.row;
    let entry = rowCache.get(item.key);
    if (!entry || entry.columns !== columns.length) {
        const tr = document.createElement('tr');
        const cells = new Map();
        for (const column of columns) {
            const td = document.createElement('td');
            td.dataset.key = column.key;
            tr.append(td);
            cells.set(column.key, td);
        }
        entry = { tr, cells, columns: columns.length };
        rowCache.set(item.key, entry);
    }

    entry.tr.dataset.pid = row.pid;
    entry.tr.dataset.child = item.child ? '1' : '';
    entry.tr.classList.toggle('pinned', !item.child && state.pinned.has(row.pid));
    entry.tr.classList.toggle('child-row', Boolean(item.child));
    entry.tr.classList.toggle('hung', Boolean(row.hung));
    entry.tr.classList.toggle('faulted', !row.hung && Boolean(row.fault));

    for (const column of columns) {
        const td = entry.cells.get(column.key);
        if (!td || (column.key === 'note' && state.editing === row.pid)) {
            continue;
        }

        const className =
            (column.align === 'right' ? 'num' : '') +
            (column.load ? loadClass(row[column.key]) : '') +
            (column.key === 'services' && (row.services || []).length ? ' services' : '') +
            (column.key === 'engines' ? ' engine-list' : '') +
            (column.key === 'path' ? ' path' : '') +
            (column.key === 'note' ? ' note' : '') +
            (column.key === 'status' ? (row.hung ? ' hot' : row.fault ? ' warm' : '') : '');

        if (td.className !== className.trim()) {
            td.className = className.trim();
        }

        if (column.key === 'name') {
            renderNameCell(td, item);
        } else if (column.key === 'note') {
            renderNoteCell(td, row);
        } else {
            const text = column.text(row);
            if (td.textContent !== text) {
                td.textContent = text;
                // Manche Spalten kürzen für die Anzeige und halten den
                // vollständigen Wert im Tooltip bereit.
                td.title = column.title ? column.title(row) : text;
            }
        }
    }

    return entry.tr;
}

/**
 * Eine Abschnittsüberschrift der gruppierten Tabelle. Sie ist eine eigene Zeile
 * über die volle Breite — mit einem zweiten <tbody> je Gruppe ließe sich die
 * Reihenfolge nicht mehr in einem Durchgang aufbauen. Ein Klick klappt den
 * Abschnitt zu: wer nach einer App sucht, will die Hintergrundprozesse nicht
 * durchscrollen müssen.
 */
function headerElement(item, columnCount) {
    const collapsed = state.collapsed.has(item.group);

    const tr = document.createElement('tr');
    tr.className = collapsed ? 'group-head collapsed' : 'group-head';
    tr.dataset.group = item.group;

    const td = document.createElement('td');
    td.colSpan = columnCount;
    td.title = `${item.help || ''}\n\nKlicken klappt den Abschnitt ${collapsed ? 'auf' : 'zu'}.`.trim();

    const toggle = document.createElement('span');
    toggle.className = 'group-toggle';
    toggle.textContent = collapsed ? '▸' : '▾';

    const label = document.createElement('span');
    label.className = 'group-label';
    label.textContent = item.label;

    const count = document.createElement('span');
    count.className = 'group-count';
    count.textContent = collapsed
        ? `${nf0.format(item.count)} — zugeklappt`
        : nf0.format(item.count);

    td.append(toggle, label, count);
    tr.append(td);
    return tr;
}

function renderNameCell(td, item) {
    const row = item.row;
    const expandable = Boolean(item.group && row.children);
    const expanded = expandable && state.expanded.has(row.pid);
    const signature = `${row.name}|${row.children || 0}|${row.description || ''}|${expanded}|${item.depth}|${item.own || false}`;
    if (td.dataset.signature === signature) {
        return;
    }
    td.dataset.signature = signature;

    const wrap = document.createElement('div');
    wrap.className = 'name-cell';
    wrap.style.paddingLeft = item.depth ? `${item.depth * 16}px` : '';

    if (expandable) {
        const toggle = document.createElement('button');
        toggle.type = 'button';
        toggle.className = 'expander';
        toggle.dataset.expand = row.pid;
        toggle.textContent = expanded ? '▾' : '▸';
        toggle.title = expanded
            ? 'Kindprozesse wieder einklappen'
            : `${row.children} Kindprozesse einzeln anzeigen`;
        wrap.append(toggle);
    } else if (item.group) {
        const spacer = document.createElement('span');
        spacer.className = 'expander-spacer';
        wrap.append(spacer);
    }

    const strong = document.createElement('span');
    strong.textContent = row.name;
    wrap.append(strong);

    if (item.own) {
        const own = document.createElement('span');
        own.className = 'children';
        own.textContent = 'nur dieser Prozess';
        own.title = 'Der Elternprozess ohne die aufsummierten Werte seiner Kindprozesse.';
        wrap.append(own);
    } else if (expandable) {
        const children = document.createElement('span');
        children.className = 'children';
        children.textContent = `+${row.children}`;
        children.title = `${row.children} Kindprozesse zusammengefasst`;
        wrap.append(children);
    }

    if (row.description) {
        const desc = document.createElement('span');
        desc.className = 'desc';
        desc.textContent = row.description;
        wrap.append(desc);
    }

    td.replaceChildren(wrap);
    td.title = row.description ? `${row.name} — ${row.description}` : row.name;
}

/** Die Notizzelle trägt immer einen Stift, damit erkennbar ist, dass man hier schreiben kann. */
function renderNoteCell(td, row) {
    const value = notes[row.name] || '';
    if (td.dataset.note === value) {
        return;
    }
    td.dataset.note = value;

    const pencil = document.createElement('span');
    pencil.className = 'note-pencil';
    pencil.textContent = '✎';

    const text = document.createElement('span');
    text.className = 'note-text';
    text.textContent = value || 'Notiz …';
    if (!value) {
        text.classList.add('empty');
    }

    td.replaceChildren(pencil, text);
    td.title = value
        ? `${value}\n\nDoppelklick zum Bearbeiten.`
        : `Doppelklick, um eine Notiz zu ${row.name} zu hinterlegen.`;
}

function renderTable() {
    // Ein Neuaufbau würde das Eingabefeld aus dem Fokus reißen und die Notiz
    // mitten im Tippen abschließen.
    if (state.editing !== null) {
        return;
    }

    const { pinned, rest, total } = visibleRows();
    const columns = activeColumns();
    const shown = rest.slice(0, 400);

    const items = [];
    const append = rows => {
        for (const group of rows) {
            items.push(...expandGroup(group));
        }
    };

    // Jeder Abschnitt bekommt die Zeilen seiner Art. Sortiert wurde vorher über
    // die ganze Liste — eine Teilmenge einer sortierten Liste ist selbst
    // sortiert, die gewählte Spalte gilt also in jedem Abschnitt.
    if (pinned.length) {
        items.push({ key: 'h:pinned', header: true, group: 'pinned', label: 'Angeheftet', count: pinned.length,
            help: 'Zeilen, die durch Anklicken oben festgehalten wurden. Sie überstehen jeden Filter.' });
        if (!state.collapsed.has('pinned')) {
            append(pinned);
        }
    }

    for (const category of CATEGORIES) {
        const rows = shown.filter(row => categoryOf(row) === category.key);
        if (!rows.length) {
            continue;
        }
        items.push({
            key: `h:${category.key}`, header: true, group: category.key, label: category.label,
            count: rows.length, help: category.help,
        });
        if (!state.collapsed.has(category.key)) {
            append(rows);
        }
    }

    const fragment = document.createDocumentFragment();
    for (const item of items) {
        fragment.append(item.header ? headerElement(item, columns.length) : rowElement(item, columns));
    }
    elements.tbody.replaceChildren(fragment);

    // Zeilen beendeter Prozesse aus dem Zwischenspeicher werfen.
    const alive = new Set(items.map(item => item.key));
    for (const key of rowCache.keys()) {
        if (!alive.has(key)) {
            rowCache.delete(key);
        }
    }

    const pinnedNote = pinned.length ? `, ${pinned.length} angeheftet` : '';
    const cut = rest.length > 400 ? ' (400 angezeigt)' : '';
    elements.status.textContent = `${pinned.length + rest.length} von ${total} Prozessen${pinnedNote}${cut}`;
}

/**
 * Eine zusammengefasste Zeile wird beim Aufklappen zu mehreren: der
 * Elternprozess bleibt oben stehen, darunter erscheinen sein eigener Anteil und
 * die Kindprozesse, nach Abstand zum Elternprozess eingerückt.
 */
function expandGroup(group) {
    const head = { row: group, key: `g${group.pid}`, depth: 0, group: true };
    if (!state.aggregate || !state.expanded.has(group.pid) || !group.members?.length) {
        return [head];
    }

    const items = [head];

    const own = state.processes.find(process => process.pid === group.pid);
    if (own) {
        items.push({ row: own, key: `c${own.pid}`, depth: 1, child: true, own: true });
    }

    for (const member of sortRows(group.members)) {
        items.push({
            row: member,
            key: `c${member.pid}`,
            // Tiefer als vier Ebenen wird die Einrückung nur noch unleserlich.
            depth: Math.min(4, member.depth),
            child: true,
        });
    }

    return items;
}

// ---------- Anheften und Notizen ----------

elements.tbody.addEventListener('click', event => {
    const expander = event.target.closest('.expander');
    if (expander) {
        // Aufklappen darf nicht nebenbei das Anheften umschalten.
        event.stopPropagation();
        const pid = Number(expander.dataset.expand);
        if (state.expanded.has(pid)) {
            state.expanded.delete(pid);
        } else {
            state.expanded.add(pid);
        }
        renderTable();
        return;
    }

    const tr = event.target.closest('tr');
    if (!tr || event.target.closest('input')) {
        return;
    }

    // Abschnittsüberschriften tragen keine PID: dort klappt der Klick den
    // Abschnitt zu, statt etwas anzuheften.
    if (tr.classList.contains('group-head')) {
        const group = tr.dataset.group;
        if (state.collapsed.has(group)) {
            state.collapsed.delete(group);
        } else {
            state.collapsed.add(group);
        }
        localStorage.setItem(STORAGE_COLLAPSED, JSON.stringify([...state.collapsed]));
        renderTable();
        return;
    }

    // Die Notizspalte gehört dem Doppelklick — sonst schaltet der erste Klick
    // des Bearbeitens nebenbei das Anheften um.
    if (event.target.closest('td')?.dataset.key === 'note') {
        return;
    }

    // Kindzeilen gehören zu ihrer aufgeklappten Gruppe; sie einzeln anzuheften
    // ergäbe eine Zeile ohne Zusammenhang.
    if (tr.dataset.child) {
        return;
    }

    const pid = Number(tr.dataset.pid);
    if (state.pinned.has(pid)) {
        state.pinned.delete(pid);
    } else {
        state.pinned.add(pid);
    }
    renderTable();
});

elements.tbody.addEventListener('dblclick', event => {
    const td = event.target.closest('td');
    const tr = event.target.closest('tr');
    if (!td || !tr || td.dataset.key !== 'note') {
        return;
    }

    event.stopPropagation();
    const pid = Number(tr.dataset.pid);
    const row = state.processes.find(p => p.pid === pid)
        || aggregateTree(state.processes).find(p => p.pid === pid);
    if (row) {
        startEditingNote(td, pid, row.name);
    }
});

function startEditingNote(td, pid, name) {
    state.editing = pid;

    const input = document.createElement('input');
    input.type = 'text';
    input.className = 'note-input';
    input.value = notes[name] || '';
    input.placeholder = `Notiz zu ${name}`;
    input.maxLength = 200;

    const finish = commit => {
        if (state.editing !== pid) {
            return;
        }
        state.editing = null;

        if (commit) {
            const value = input.value.trim();
            // Notizen hängen am Prozessnamen, nicht an der PID — sonst wären sie
            // nach dem nächsten Neustart wertlos.
            if (value) {
                notes[name] = value;
            } else {
                delete notes[name];
            }
            saveNotes();
        }

        delete td.dataset.note;
        renderTable();
    };

    input.addEventListener('keydown', event => {
        if (event.key === 'Enter') {
            finish(true);
        } else if (event.key === 'Escape') {
            finish(false);
        }
    });
    input.addEventListener('blur', () => finish(true));

    td.replaceChildren(input);
    input.focus();
    input.select();
}

// ---------- Kontextmenü ----------

const rowMenu = document.getElementById('row-menu');

function send(cmd, extra) {
    if (host) {
        host.postMessage(Object.assign({ cmd }, extra));
    }
}

function closeRowMenu() {
    rowMenu.hidden = true;
}

elements.tbody.addEventListener('contextmenu', event => {
    const tr = event.target.closest('tr');
    if (!tr || tr.classList.contains('group-head')) {
        return;
    }

    event.preventDefault();
    const pid = Number(tr.dataset.pid);

    // Eine Gruppenzeile und der gleichnamige Einzelprozess haben dieselbe PID —
    // je nach Zeilentyp ist die eine oder die andere gemeint.
    const source = !tr.dataset.child && state.aggregate
        ? aggregateTree(state.processes)
        : state.processes;
    const row = source.find(candidate => candidate.pid === pid);
    if (!row) {
        return;
    }

    const entries = [];

    if (!tr.dataset.child && state.aggregate && row.children) {
        entries.push([
            state.expanded.has(pid) ? 'Kindprozesse einklappen' : `Kindprozesse einzeln anzeigen (${row.children})`,
            () => {
                if (state.expanded.has(pid)) {
                    state.expanded.delete(pid);
                } else {
                    state.expanded.add(pid);
                }
                renderTable();
            },
        ]);
    }

    if (!tr.dataset.child) {
        entries.push([
            state.pinned.has(pid) ? 'Anheftung lösen' : 'Oben anheften',
            () => {
                if (state.pinned.has(pid)) {
                    state.pinned.delete(pid);
                } else {
                    state.pinned.add(pid);
                }
                renderTable();
            },
        ]);
    }

    if (row.path) {
        entries.push(['Pfad kopieren', () => navigator.clipboard?.writeText(row.path)]);
    }

    // Der Host fragt vor dem Beenden noch einmal nach.
    entries.push([`„${row.name}“ beenden …`, () => send('killProcess', { pid, name: row.name }), 'danger']);

    rowMenu.replaceChildren(...entries.map(([label, action, variant]) => {
        const button = document.createElement('button');
        button.type = 'button';
        if (variant) {
            button.className = variant;
        }
        button.textContent = label;
        button.addEventListener('click', () => {
            closeRowMenu();
            action();
        });
        return button;
    }));

    rowMenu.hidden = false;

    // Innerhalb des Fensters halten.
    const width = rowMenu.offsetWidth;
    const height = rowMenu.offsetHeight;
    rowMenu.style.left = `${Math.min(event.clientX, window.innerWidth - width - 8)}px`;
    rowMenu.style.top = `${Math.min(event.clientY, window.innerHeight - height - 8)}px`;
});

document.addEventListener('click', closeRowMenu);
document.addEventListener('contextmenu', event => {
    if (!event.target.closest('#processes tbody')) {
        closeRowMenu();
    }
});
window.addEventListener('blur', closeRowMenu);
document.addEventListener('keydown', event => {
    if (event.key === 'Escape') {
        closeRowMenu();
    }
});

// ---------- Spaltenauswahl ----------

function buildColumnsMenu() {
    const hint = document.createElement('p');
    hint.className = 'menu-hint';
    hint.textContent = 'Reihenfolge: Spaltenüberschrift an die gewünschte Stelle ziehen.';

    const items = orderedColumns().filter(column => !column.locked).map(column => {
        const label = document.createElement('label');
        label.title = column.help;

        const checkbox = document.createElement('input');
        checkbox.type = 'checkbox';
        checkbox.checked = !hiddenColumns.has(column.key);
        checkbox.addEventListener('change', () => {
            if (checkbox.checked) {
                hiddenColumns.delete(column.key);
            } else {
                hiddenColumns.add(column.key);
            }
            saveColumns();
            rowCache.clear();
            renderHead();
            renderTable();
        });

        label.append(checkbox, document.createTextNode(column.label));
        return label;
    });

    elements.columnsMenu.replaceChildren(hint, ...items);
}

elements.columnsButton.addEventListener('click', event => {
    event.stopPropagation();
    elements.columnsMenu.hidden = !elements.columnsMenu.hidden;
});

document.addEventListener('click', () => {
    elements.columnsMenu.hidden = true;
});

elements.columnsMenu.addEventListener('click', event => event.stopPropagation());

// ---------- Systemübersicht ----------

function renderSystemInfo(data) {
    state.systemLoaded = (data.groups || []).length > 0 || (data.drives || []).length > 0;
    elements.systemEmpty.hidden = state.systemLoaded;

    elements.refreshDevices.disabled = false;
    elements.refreshDevices.textContent = 'Aktualisieren';

    renderDevices(data.devices || []);

    elements.systemGroups.replaceChildren(...(data.groups || []).map(group => {
        const card = document.createElement('section');
        card.className = 'info-card';

        const title = document.createElement('h4');
        title.textContent = group.title;
        card.append(title);

        const list = document.createElement('dl');
        for (const item of group.items) {
            const label = document.createElement('dt');
            label.textContent = item.label;
            const value = document.createElement('dd');
            value.textContent = item.value;
            // Erklärungsbedürftige Angaben bringen ihren Tooltip mit; sonst steht
            // der volle Wert darin, weil die Zelle abgeschnitten sein kann.
            label.title = item.help || '';
            value.title = item.help || item.value;
            if (item.help) {
                label.classList.add('has-help');
            }
            list.append(label, value);
        }

        card.append(list);
        return card;
    }));

    elements.systemDrives.replaceChildren(...(data.drives || []).map(drive => {
        const card = document.createElement('section');
        card.className = 'info-card';

        const title = document.createElement('h4');
        title.textContent = drive.model;
        card.append(title);

        const meta = document.createElement('p');
        meta.className = 'drive-meta';
        meta.textContent = [
            drive.sizeBytes > 0 ? `${nf0.format(drive.sizeBytes / 1000000000)} GB` : null,
            drive.interfaceType,
            drive.mediaType,
        ].filter(Boolean).join('  ·  ');
        card.append(meta);

        for (const volume of drive.volumes) {
            const row = document.createElement('div');
            row.className = 'volume';

            const head = document.createElement('div');
            head.className = 'volume-head';
            head.textContent = volume.label ? `${volume.name}  ${volume.label}` : volume.name;

            const size = document.createElement('span');
            size.className = 'volume-size';
            size.textContent = `${formatBytes(volume.usedBytes)} / ${formatBytes(volume.totalBytes)}`;
            head.append(size);

            const bar = document.createElement('div');
            bar.className = 'volume-bar';
            const fill = document.createElement('i');
            fill.style.width = `${volume.usedPercent}%`;
            if (volume.usedPercent >= 90) {
                fill.classList.add('full');
            } else if (volume.usedPercent >= 75) {
                fill.classList.add('high');
            }
            bar.append(fill);

            const foot = document.createElement('div');
            foot.className = 'volume-foot';
            foot.textContent =
                `${nf0.format(volume.usedPercent)} % belegt  ·  ${formatBytes(volume.freeBytes)} frei  ·  ${volume.fileSystem}`;

            row.append(head, bar, foot);
            card.append(row);
        }

        return card;
    }));
}

function renderDevices(groups) {
    elements.systemDevices.replaceChildren(...groups.map(group => {
        const card = document.createElement('section');
        card.className = 'info-card';

        const title = document.createElement('h4');
        title.textContent = `${group.title}${group.items.length ? ` (${group.items.length})` : ''}`;
        title.title = group.hint || '';
        card.append(title);

        if (!group.items.length) {
            const empty = document.createElement('p');
            empty.className = 'field-hint';
            empty.textContent = 'Auf diesem Rechner nicht vorhanden.';
            card.append(empty);
            return card;
        }

        for (const device of group.items) {
            const row = document.createElement('div');
            row.className = 'device';

            const head = document.createElement('div');
            head.className = 'device-head';

            const dot = document.createElement('i');
            dot.className = `device-dot ${device.health}`;

            const name = document.createElement('span');
            name.className = 'device-name';
            name.textContent = device.name;
            name.title = device.name;

            const status = document.createElement('span');
            status.className = 'device-status';
            status.textContent = device.status;

            head.append(dot, name, status);
            row.append(head);

            if (device.details.length) {
                const list = document.createElement('dl');
                list.className = 'device-details';
                for (const item of device.details) {
                    const label = document.createElement('dt');
                    label.textContent = item.label;
                    const value = document.createElement('dd');
                    value.textContent = item.value;
                    value.title = item.value;
                    list.append(label, value);
                }
                row.append(list);
            }

            card.append(row);
        }

        return card;
    }));
}

// ---------- Energie ----------

/**
 * Der Energieeinfluss verrechnet die vier Lastarten zu einer Zahl. Die
 * Gewichte spiegeln wider, wie viel Leistung eine Einheit davon üblicherweise
 * kostet: ein Prozent CPU zieht mehr als ein Prozent GPU-Engine-Zeit, und
 * Datenträger- und Netzverkehr fallen dagegen kaum ins Gewicht. Es ist eine
 * Rangfolge, keine Messung — dieselbe Art Kennzahl, die auch der Task-Manager
 * unter „Energieverbrauch" anzeigt, ohne seine Formel zu nennen.
 */
function impactOf(row) {
    return (row.cpu || 0)
        + (row.gpu || 0) * 0.6
        + ((row.ioRead || 0) + (row.ioWrite || 0)) / 1048576 * 0.6
        + ((row.rx || 0) + (row.tx || 0)) / 1048576 * 0.4;
}

function impactLabel(value) {
    if (value >= 15) {
        return 'sehr hoch';
    }
    if (value >= 5) {
        return 'hoch';
    }
    if (value >= 1) {
        return 'mäßig';
    }
    return value >= 0.2 ? 'niedrig' : 'sehr niedrig';
}

function impactClass(value) {
    if (value >= 15) {
        return 'hot';
    }
    return value >= 5 ? 'warm' : '';
}

function renderEnergy(data) {
    const energy = data.energy || {};

    elements.energyTotal.textContent = energy.measuredW === null || energy.measuredW === undefined
        ? '–'
        : nf1.format(energy.measuredW);
    elements.energyTotalSub.textContent = energy.measuredW === null || energy.measuredW === undefined
        ? 'keine Leistungssensoren'
        : 'Prozessor und Grafikkarte zusammen. Mainboard, Datenträger, Lüfter und Netzteilverluste sind nicht enthalten.';

    elements.energyCpu.textContent = energy.cpuW === null || energy.cpuW === undefined
        ? '–'
        : nf1.format(energy.cpuW);
    elements.energyCpuSub.textContent = [
        optional(data.cpu.tempC, '°C'),
        data.cpu.socketTempC ? `Sockel ${nf0.format(data.cpu.socketTempC)} °C` : null,
        optional(data.cpu.clockMhz, 'MHz'),
        `${nf1.format(data.cpu.percent)} % Last`,
    ].filter(Boolean).join('  ·  ');

    elements.energyGpu.textContent = energy.gpuW === null || energy.gpuW === undefined
        ? '–'
        : nf1.format(energy.gpuW);
    elements.energyGpuSub.textContent = data.gpu.available
        ? [
            optional(data.gpu.tempC, '°C'),
            data.gpu.fanRpm ? optional(data.gpu.fanRpm, 'rpm') : null,
            `${nf1.format(data.gpu.percent)} % Last`,
        ].filter(Boolean).join('  ·  ')
        : 'GPU-Zähler nicht verfügbar';

    renderBattery(energy.battery);
    renderTemperatures(energy.temperatures || []);
    renderFans(energy.fans || []);
    renderRails(energy.rails || []);
    renderEnergyProcesses(data, energy);
    drawEnergyChart();
}

/** Woher eine Temperatur kommt — der Unterschied zwischen Die und Sockel zählt. */
const TEMPERATURE_SOURCES = {
    cpu: 'Prozessor',
    gpu: 'Grafikkarte',
    board: 'Mainboard',
    acpi: 'ACPI-Thermalzonen',
    other: 'Sonstige',
};

const TEMPERATURE_SOURCE_HINTS = {
    cpu: 'Sensoren im Prozessor selbst. Sie messen am Die und schlagen bei Lastspitzen sofort aus.',
    board: 'Sensoren des Super-I/O-Chips auf dem Mainboard. Der mit „CPU" bezeichnete misst am Sockel, '
        + 'nicht im Prozessor — er liegt niedriger und reagiert träger.',
    acpi: 'Zonen, die die Firmware für ihre eigene Kühlregelung führt. Sie kommen über ACPI und brauchen '
        + 'keinen Sensortreiber, messen aber die Umgebung einer Komponente statt sie selbst. Die '
        + 'Zonennamen vergibt der Hersteller; nur die eindeutigen sind hier übersetzt.',
};

function renderTemperatures(temperatures) {
    elements.tempEmpty.hidden = temperatures.length > 0;

    // Nach Herkunft gruppiert: „CPU" heißt am Mainboard etwas anderes als im
    // Prozessor, und nebeneinander sähe es nach zwei Messungen derselben Sache aus.
    const order = ['cpu', 'gpu', 'board', 'acpi', 'other'];
    const nodes = [];

    for (const source of order) {
        const group = temperatures.filter(entry => (entry.source || 'other') === source);
        if (!group.length) {
            continue;
        }

        const heading = document.createElement('div');
        heading.className = 'temp-source';
        heading.textContent = TEMPERATURE_SOURCES[source];
        heading.title = TEMPERATURE_SOURCE_HINTS[source] || '';
        nodes.push(heading);

        for (const entry of group) {
            const row = document.createElement('div');
            row.className = 'temp-row';

            const name = document.createElement('span');
            name.className = 'temp-name';
            name.textContent = entry.name;
            name.title = `${entry.name} — gemeldet von ${entry.hardware}`;

            const value = document.createElement('span');
            value.className = `temp-value${tempClass(entry.celsius)}`;
            value.textContent = `${nf1.format(entry.celsius)} °C`;

            row.append(name, value);
            nodes.push(row);
        }
    }

    elements.tempList.replaceChildren(...nodes);
}

function tempClass(celsius) {
    if (celsius >= 85) {
        return ' hot';
    }
    return celsius >= 70 ? ' warm' : '';
}

function renderBattery(battery) {
    const present = Boolean(battery);
    elements.batteryTile.hidden = !present;
    elements.batteryCard.hidden = !present;
    if (!present) {
        return;
    }

    const percent = battery.percent;
    elements.batteryPercent.textContent = percent === null || percent === undefined
        ? '–'
        : nf0.format(percent);
    elements.batteryFill.style.width = `${Math.min(100, Math.max(0, percent || 0))}%`;
    elements.batteryFill.classList.toggle('low', (percent || 100) < 20);

    // Die Lade- und Entladeleistung ist vorzeichenbehaftet: positiv beim Laden.
    const rate = battery.rateW;
    const flowing = rate !== null && rate !== undefined && Math.abs(rate) > 0.1;
    elements.batterySub.textContent = [
        battery.charging ? 'lädt' : battery.onAc ? 'am Netz' : 'Akkubetrieb',
        flowing ? `${nf1.format(Math.abs(rate))} W ${rate > 0 ? 'Ladung' : 'Entnahme'}` : null,
        formatMinutes(battery.remainingMinutes),
    ].filter(Boolean).join('  ·  ');

    const details = [
        ['Zustand', battery.charging ? 'wird geladen' : battery.onAc ? 'am Netzteil' : 'im Akkubetrieb', null],
        ['Ladestand', percent === null || percent === undefined ? '–' : `${nf1.format(percent)} %`, null],
        ['Leistung', flowing ? `${nf1.format(Math.abs(rate))} W` : '–',
            'Wie viel Leistung gerade in den Akku fließt oder aus ihm entnommen wird.'],
        ['Spannung', battery.voltageV ? `${nf1.format(battery.voltageV)} V` : '–', null],
        ['Restlaufzeit', formatMinutes(battery.remainingMinutes) || '–',
            'Windows schätzt sie aus dem Verbrauch der letzten Minuten. Nach einem Lastwechsel dauert es einen Moment, bis der Wert wieder stimmt.'],
        ['Ladung', battery.remainingWh ? `${nf1.format(battery.remainingWh)} Wh` : '–', null],
        ['Kapazität', battery.fullWh ? `${nf1.format(battery.fullWh)} Wh` : '–',
            'Wie viel der Akku heute vollgeladen fasst.'],
        ['Neuzustand', battery.designedWh ? `${nf1.format(battery.designedWh)} Wh` : '–',
            'Die Kapazität, für die der Akku ausgelegt wurde.'],
        ['Verschleiß', battery.degradation === null || battery.degradation === undefined
            ? '–' : `${nf1.format(battery.degradation)} %`,
            'Der Anteil der ursprünglichen Kapazität, den der Akku verloren hat.'],
    ];

    elements.batteryDetails.replaceChildren(...details.flatMap(([label, value, help]) => {
        const term = document.createElement('dt');
        term.textContent = label;
        term.title = help || '';
        if (help) {
            term.classList.add('has-help');
        }

        const definition = document.createElement('dd');
        definition.textContent = value;
        definition.title = help || value;
        return [term, definition];
    }));
}

function formatMinutes(minutes) {
    if (!minutes || minutes <= 0) {
        return null;
    }
    const hours = Math.floor(minutes / 60);
    return hours > 0 ? `noch ${hours} Std ${minutes % 60} Min` : `noch ${minutes} Min`;
}

function renderFans(fans) {
    elements.fanEmpty.hidden = fans.length > 0;

    elements.fanList.replaceChildren(...fans.map(fan => {
        const row = document.createElement('div');
        row.className = 'fan';

        const head = document.createElement('div');
        head.className = 'fan-head';
        head.textContent = fan.name;
        head.title = `${fan.name} — gemeldet von ${fan.hardware}`;

        const value = document.createElement('span');
        value.className = 'fan-value';

        // 0 rpm heißt bei modernen Grafikkarten nicht „kaputt", sondern
        // „steht" — unter etwa 50 °C schalten sie die Lüfter ganz ab.
        const stopped = fan.rpm === 0 && (!fan.percent || fan.percent === 0);
        value.textContent = stopped
            ? 'steht'
            : [
                fan.rpm === null || fan.rpm === undefined ? null : `${nf0.format(fan.rpm)} rpm`,
                fan.percent === null || fan.percent === undefined ? null : `${nf0.format(fan.percent)} %`,
            ].filter(Boolean).join('  ·  ') || '–';

        if (stopped) {
            value.classList.add('stopped');
            head.title = `${fan.name} — gemeldet von ${fan.hardware}. Der Lüfter steht: ` +
                'Grafikkarten und viele Gehäuselüfter schalten unterhalb einer Schwelltemperatur ' +
                'vollständig ab.';
        }

        head.append(value);

        row.append(head);

        // Ein Balken braucht eine Obergrenze. Die Ansteuerung liefert sie
        // direkt; für die Drehzahl gibt es keine, deshalb dient 2500 rpm als
        // grober Maßstab — genug, um Ruhe von Vollgas zu unterscheiden.
        const share = fan.percent !== null && fan.percent !== undefined
            ? fan.percent
            : Math.min(100, ((fan.rpm || 0) / 2500) * 100);

        const bar = document.createElement('div');
        bar.className = 'fan-bar';
        const fill = document.createElement('i');
        fill.style.width = `${Math.max(1, share)}%`;
        bar.append(fill);
        row.append(bar);

        return row;
    }));
}

function renderRails(rails) {
    elements.railEmpty.hidden = rails.length > 0;

    elements.railList.replaceChildren(...rails.flatMap(rail => {
        const term = document.createElement('dt');
        term.textContent = rail.name;
        term.title = `${rail.name} — gemeldet von ${rail.hardware}`;

        const value = document.createElement('dd');
        value.textContent = `${nf1.format(rail.watts)} W`;
        return [term, value];
    }));
}

/**
 * Die Spalten der Prozesstabelle im Reiter „Energie". Sie ist weder sortierbar
 * noch umstellbar — die Reihenfolge ist die Aussage —, in der Breite aber
 * genauso einstellbar wie die anderen Tabellen.
 */
const ENERGY_COLUMNS = [
    { key: 'name', label: 'Name', align: 'left', width: 300, help: 'Prozess samt Dateibeschreibung, Prozessbäume zusammengefasst.' },
    { key: 'cpu', label: 'CPU %', align: 'right', width: 80, help: 'Anteil an der gesamten Rechenkapazität.' },
    { key: 'gpu', label: 'GPU %', align: 'right', width: 80, help: 'Auslastung der Grafikkarte durch diesen Prozess.' },
    { key: 'io', label: 'E/A', align: 'right', width: 100, help: 'Lesen und Schreiben zusammen, über alle Ein-/Ausgabekanäle.' },
    { key: 'net', label: 'Netz', align: 'right', width: 100, help: 'Empfangen und gesendet zusammen.' },
    { key: 'impact', label: 'Einfluss', align: 'right', width: 140, help: 'Die vier Lastarten zu einer Kennzahl verrechnet — eine Rangfolge, keine Messung.' },
    { key: 'watts', label: 'geschätzt', align: 'right', width: 100, help: 'Die gemessene Leistungsaufnahme von Prozessor und Grafikkarte, nach Lastanteil verteilt.' },
];

function applyEnergyWidths() {
    applyColumnWidths(document.getElementById('energy-processes'), 'energy', ENERGY_COLUMNS);
}

function renderEnergyHead() {
    elements.energyHead.replaceChildren(...ENERGY_COLUMNS.map((column, index) => {
        const th = document.createElement('th');
        th.textContent = column.label;
        th.className = column.align === 'right' ? 'num' : '';
        th.title = `${column.help}\n\nDie rechte Kante ändert die Breite.`;

        if (index < ENERGY_COLUMNS.length - 1) {
            addResizeHandle(th, 'energy', ENERGY_COLUMNS, column, applyEnergyWidths);
        }

        return th;
    }));

    applyEnergyWidths();
}

function renderEnergyProcesses(data, energy) {
    // Immer zusammengefasst: die Frage lautet „was zieht Strom", und die
    // Antwort ist der Browser, nicht sein siebter Renderer-Prozess.
    const rows = aggregateTree(state.processes)
        .map(row => ({ row, impact: impactOf(row) }))
        .filter(entry => entry.impact > 0.05)
        .sort((a, b) => b.impact - a.impact)
        .slice(0, 20);

    // Die gemessene Leistung wird nach Lastanteil verteilt. Nenner ist die Summe
    // über alle Prozesse, nicht 100 % — sonst käme bei geringer Last nur ein
    // Bruchteil der tatsächlich gemessenen Watt heraus.
    const cpuSum = Math.max(state.processes.reduce((sum, p) => sum + (p.cpu || 0), 0), data.cpu.percent, 0.1);
    const gpuSum = Math.max(state.processes.reduce((sum, p) => sum + (p.gpu || 0), 0), 0.1);
    const cpuW = energy.cpuW;
    const gpuW = energy.gpuW;
    const measurable = (cpuW !== null && cpuW !== undefined) || (gpuW !== null && gpuW !== undefined);

    elements.energyProcesses.replaceChildren(...rows.map(({ row, impact }) => {
        const watts = (cpuW || 0) * (row.cpu / cpuSum) + (gpuW || 0) * (row.gpu / gpuSum);

        const tr = document.createElement('tr');
        const cells = [
            [row.description ? `${row.name} — ${row.description}` : row.name, ''],
            [formatPercent(row.cpu), 'num'],
            [formatPercent(row.gpu), 'num'],
            [formatRate((row.ioRead || 0) + (row.ioWrite || 0)), 'num'],
            [formatRate((row.rx || 0) + (row.tx || 0)), 'num'],
            [`${impactLabel(impact)} (${nf1.format(impact)})`, `num ${impactClass(impact)}`],
            [measurable ? `${nf1.format(watts)} W` : '–', 'num'],
        ];

        for (const [text, className] of cells) {
            const td = document.createElement('td');
            td.textContent = text;
            td.title = text;
            if (className) {
                td.className = className.trim();
            }
            tr.append(td);
        }

        return tr;
    }));
}

/**
 * Zwei Kurven in Watt. Anders als beim Verlauf der Prozentwerte gibt es keine
 * feste Obergrenze — die Achse skaliert deshalb auf das Maximum der letzten
 * fünf Minuten, und der Wert steht in der Legende.
 */
function drawEnergyChart() {
    const canvas = elements.energyChart;
    const width = canvas.clientWidth;
    const height = canvas.clientHeight;
    if (width === 0 || height === 0) {
        return;
    }

    const context = canvas.getContext('2d');
    const ratio = window.devicePixelRatio || 1;
    if (canvas.width !== width * ratio || canvas.height !== height * ratio) {
        canvas.width = width * ratio;
        canvas.height = height * ratio;
    }

    const style = getComputedStyle(document.documentElement);
    context.setTransform(ratio, 0, 0, ratio, 0, 0);
    context.clearRect(0, 0, width, height);

    context.strokeStyle = style.getPropertyValue('--grid').trim() || 'rgba(255,255,255,0.06)';
    context.lineWidth = 1;
    for (const fraction of [0.25, 0.5, 0.75]) {
        const y = Math.round(height * fraction) + 0.5;
        context.beginPath();
        context.moveTo(0, y);
        context.lineTo(width, y);
        context.stroke();
    }

    const series = [
        { key: 'cpuPower', label: 'Prozessor', color: style.getPropertyValue('--cpu').trim() },
        { key: 'gpuPower', label: 'Grafikkarte', color: style.getPropertyValue('--gpu').trim() },
    ];

    const peak = Math.max(
        1,
        ...series.flatMap(entry => state.history[entry.key] || [0]));

    const capacity = 300;
    for (const entry of series) {
        const values = state.history[entry.key] || [];
        if (values.length < 2) {
            continue;
        }

        const step = width / (capacity - 1);
        const offset = width - (values.length - 1) * step;
        const toY = value => height - (Math.min(peak, Math.max(0, value)) / peak) * (height - 2) - 1;

        context.beginPath();
        context.moveTo(offset, toY(values[0]));
        for (let i = 1; i < values.length; i++) {
            context.lineTo(offset + i * step, toY(values[i]));
        }
        context.strokeStyle = entry.color;
        context.lineWidth = 1.5;
        context.stroke();
    }

    elements.energyLegend.replaceChildren(...series.flatMap(entry => {
        const marker = document.createElement('i');
        marker.style.background = entry.color;

        const label = document.createElement('span');
        const values = state.history[entry.key] || [];
        const last = values.length ? values[values.length - 1] : 0;
        label.textContent = `${entry.label} ${nf1.format(last)} W`;
        return [marker, label];
    }));

    const scale = document.createElement('span');
    scale.textContent = `Achse bis ${nf1.format(peak)} W`;
    scale.className = 'legend-scale';
    elements.energyLegend.append(scale);
}

// ---------- Verbindungen ----------

/** Die Zustände aus MIB_TCPROW; „none" steht für das verbindungslose UDP. */
const CONNECTION_STATES = {
    none: 'UDP',
    closed: 'Geschlossen',
    listen: 'Abhören',
    synSent: 'SYN gesendet',
    synReceived: 'SYN empfangen',
    established: 'Hergestellt',
    finWait1: 'Wird beendet (1)',
    finWait2: 'Wird beendet (2)',
    closeWait: 'Warten auf Schließen',
    closing: 'Wird geschlossen',
    lastAck: 'Letzte Bestätigung',
    timeWait: 'Wartezeit',
    deleteTcb: 'Wird abgebaut',
};

const CONNECTION_COLUMNS = [
    {
        key: 'protocol', label: 'Protokoll', align: 'left', width: 90, text: c => c.protocol,
        help: 'TCP oder UDP, mit dem Zusatz v6 für IPv6.',
    },
    {
        key: 'local', label: 'Lokale Adresse', align: 'left', width: 180, text: c => c.local,
        help: 'Die Adresse dieses Rechners. 0.0.0.0 beziehungsweise :: heißt „auf allen Netzwerkkarten".',
    },
    {
        key: 'localPort', label: 'Port', align: 'right', width: 78, text: c => String(c.localPort),
        help: 'Der lokale Port. Bei lauschenden Einträgen ist das der Port, unter dem der Dienst erreichbar ist.',
    },
    {
        key: 'remote', label: 'Gegenstelle', align: 'left', width: 180, text: c => c.remote || '–',
        help: 'Die Adresse der Gegenstelle. Bei lauschenden Einträgen und bei UDP steht dort nichts.',
    },
    {
        key: 'remotePort', label: 'Port', align: 'right', width: 78, text: c => c.remotePort ? String(c.remotePort) : '–',
        help: 'Der Port der Gegenstelle.',
    },
    {
        key: 'state', label: 'Zustand', align: 'left', width: 140, text: c => CONNECTION_STATES[c.state] || c.state,
        help: 'Der Zustand der TCP-Verbindung. „Abhören" wartet auf Verbindungen, „Hergestellt" überträgt gerade, „Wartezeit" ist beendet und wird noch kurz freigehalten.',
    },
    {
        key: 'pid', label: 'PID', align: 'right', width: 72, text: c => String(c.pid),
        help: 'Der Prozess, dem die Verbindung gehört.',
    },
    {
        key: 'process', label: 'Prozess', align: 'left', width: 180, text: c => processNames().get(c.pid) || '–',
        help: 'Name des besitzenden Prozesses, aufgelöst über die Prozessliste.',
    },
];

let processNameCache = null;

/** PID → Prozessname, für die Beschriftung der Verbindungen. */
function processNames() {
    if (processNameCache === null) {
        processNameCache = new Map(state.processes.map(process => [process.pid, process.name]));
    }
    return processNameCache;
}

function compareConnections(a, b, key) {
    switch (key) {
        case 'localPort':
        case 'remotePort':
        case 'pid':
            return (a[key] || 0) - (b[key] || 0);
        case 'process':
            return (processNames().get(a.pid) || '').localeCompare(processNames().get(b.pid) || '', 'de');
        case 'state':
            return (CONNECTION_STATES[a.state] || '').localeCompare(CONNECTION_STATES[b.state] || '', 'de');
        default:
            return String(a[key] || '').localeCompare(String(b[key] || ''), 'de');
    }
}

function isLoopback(connection) {
    return connection.local.startsWith('127.') || connection.local === '::1';
}

function visibleConnections() {
    const needle = state.connFilter.toLowerCase();

    return state.connections.filter(connection => {
        // UDP hat keinen Zustand und wird allein über sein eigenes Kästchen
        // gesteuert; "lauschend" meint die wartenden TCP-Sockets.
        if (!state.connUdp && connection.state === 'none') {
            return false;
        }
        if (!state.connListening && connection.state === 'listen') {
            return false;
        }
        if (!state.connLoopback && isLoopback(connection)) {
            return false;
        }
        if (!needle) {
            return true;
        }

        return connection.local.includes(needle)
            || String(connection.localPort) === needle
            || (connection.remote || '').includes(needle)
            || String(connection.remotePort || '') === needle
            || connection.protocol.toLowerCase().includes(needle)
            || (CONNECTION_STATES[connection.state] || '').toLowerCase().includes(needle)
            || (processNames().get(connection.pid) || '').toLowerCase().includes(needle)
            || String(connection.pid) === needle;
    });
}

/**
 * Was hinter den geläufigen Portnummern steckt. Eine Portnummer allein sagt
 * nichts; erst der Dienstname macht aus „5357" eine Aussage darüber, was der
 * Rechner nach außen anbietet.
 */
const WELL_KNOWN_PORTS = {
    21: 'FTP', 22: 'SSH', 23: 'Telnet', 25: 'SMTP', 53: 'DNS', 67: 'DHCP', 80: 'HTTP',
    110: 'POP3', 123: 'NTP', 135: 'RPC-Endpunktzuordnung', 137: 'NetBIOS-Namen',
    138: 'NetBIOS-Datagramme', 139: 'NetBIOS-Sitzung', 143: 'IMAP', 443: 'HTTPS',
    445: 'SMB-Dateifreigabe', 500: 'IPsec', 515: 'Druckerdienst', 548: 'AFP',
    554: 'RTSP', 587: 'SMTP (Einlieferung)', 631: 'IPP-Druck', 993: 'IMAPS', 995: 'POP3S',
    1433: 'SQL Server', 1701: 'L2TP', 1723: 'PPTP', 1883: 'MQTT', 1900: 'SSDP (UPnP)',
    2869: 'UPnP-Ereignisse', 3306: 'MySQL', 3389: 'Remotedesktop', 5000: 'UPnP',
    5040: 'Verbundene Benutzererfahrungen', 5060: 'SIP', 5353: 'mDNS (Bonjour)',
    5355: 'LLMNR', 5357: 'WSD-Geräteerkennung', 5432: 'PostgreSQL', 5985: 'WinRM (HTTP)',
    5986: 'WinRM (HTTPS)', 6379: 'Redis', 7680: 'Übermittlungsoptimierung',
    8080: 'HTTP (alternativ)', 8443: 'HTTPS (alternativ)', 27017: 'MongoDB',
};

/** Lauscht der Port nur auf diesem Rechner oder nimmt er Verbindungen aus dem Netz an? */
function reachability(address) {
    if (address.startsWith('127.') || address === '::1') {
        return { label: 'nur lokal', open: false };
    }
    if (address === '0.0.0.0' || address === '::') {
        return { label: 'im Netz erreichbar', open: true };
    }
    return { label: `nur über ${address}`, open: true };
}

/**
 * Die Übersicht der offenen Ports: eine Zeile je Port statt einer je Socket.
 * Ein Dienst, der auf IPv4 und IPv6 lauscht, steht in der Verbindungstabelle
 * zweimal — hier einmal, und die Zeile beantwortet die Frage, die man
 * tatsächlich hat: welcher Port ist offen, wer hält ihn, und kommt jemand von
 * außen daran.
 */
/**
 * Ab 49152 vergibt Windows die Ports für ausgehende Verbindungen. Ein UDP-Socket
 * in diesem Bereich ist keine Gegenstelle, auf die jemand zugreifen kann,
 * sondern die Rückadresse einer Anfrage, die der Rechner selbst gestellt hat —
 * eine DNS-Abfrage etwa. Davon sind auf einem laufenden System hunderte offen,
 * und sie als „offene Ports" mitzuzählen macht die Zahl unbrauchbar.
 */
const EPHEMERAL_PORT_START = 49152;

function renderPortsOverview() {
    const byPort = new Map();
    let ephemeral = 0;

    for (const connection of state.connections) {
        const listening = connection.state === 'listen' || connection.state === 'none';
        if (!listening) {
            continue;
        }

        const udp = connection.state === 'none';
        if (udp && connection.localPort >= EPHEMERAL_PORT_START) {
            ephemeral++;
            continue;
        }

        const key = `${udp ? 'UDP' : 'TCP'}:${connection.localPort}:${connection.pid}`;
        const reach = reachability(connection.local);

        const known = byPort.get(key);
        if (known) {
            // Derselbe Port auf mehreren Adressen: die offenste gewinnt, denn sie
            // bestimmt, worüber der Dienst erreichbar ist.
            if (reach.open && !known.open) {
                known.open = true;
                known.reach = reach.label;
            }
            known.addresses.add(connection.local);
            continue;
        }

        byPort.set(key, {
            protocol: udp ? 'UDP' : 'TCP',
            port: connection.localPort,
            pid: connection.pid,
            open: reach.open,
            reach: reach.label,
            addresses: new Set([connection.local]),
        });
    }

    const rows = [...byPort.values()].sort((a, b) => a.port - b.port || a.protocol.localeCompare(b.protocol));
    elements.portsEmpty.hidden = rows.length > 0;

    const exposed = rows.filter(row => row.open).length;
    const aside = ephemeral
        ? `  ·  ${ephemeral} kurzlebige UDP-Bindungen ausgeblendet`
        : '';
    elements.portsSummary.textContent = rows.length
        ? `${rows.length} Ports offen, davon ${exposed} aus dem Netz erreichbar${aside}`
        : '';
    elements.portsSummary.title = ephemeral
        ? 'Ausgeblendet sind UDP-Ports ab 49152. Windows vergibt diesen Bereich für ausgehende ' +
          'Verbindungen: das sind die Rückadressen eigener Anfragen, keine Dienste, auf die jemand ' +
          'zugreifen kann. Sie stehen weiterhin in der Tabelle darunter.'
        : '';

    elements.portsList.replaceChildren(...rows.map(row => {
        const item = document.createElement('div');
        item.className = `port${row.open ? ' exposed' : ''}`;
        item.title =
            `${row.protocol}-Port ${row.port}, gehalten von ${processNames().get(row.pid) || 'PID ' + row.pid}.\n` +
            `Gebunden an ${[...row.addresses].join(', ')}.\n` +
            (row.open
                ? 'Diese Bindung nimmt Verbindungen von anderen Rechnern an.'
                : 'Diese Bindung ist nur von diesem Rechner aus erreichbar.');

        const number = document.createElement('span');
        number.className = 'port-number';
        number.textContent = String(row.port);

        const protocol = document.createElement('span');
        protocol.className = 'port-protocol';
        protocol.textContent = row.protocol;

        const service = document.createElement('span');
        service.className = 'port-service';
        service.textContent = WELL_KNOWN_PORTS[row.port] || '';

        const owner = document.createElement('span');
        owner.className = 'port-owner';
        owner.textContent = processNames().get(row.pid) || `PID ${row.pid}`;

        const reach = document.createElement('span');
        reach.className = 'port-reach';
        reach.textContent = row.reach;

        item.append(number, protocol, service, owner, reach);
        return item;
    }));
}

function applyConnectionWidths() {
    applyColumnWidths(document.getElementById('connections'), 'connections', CONNECTION_COLUMNS);
}

function renderConnectionHead() {
    elements.connHead.replaceChildren(...CONNECTION_COLUMNS.map((column, index) => {
        const th = document.createElement('th');
        th.textContent = column.label;
        th.className = column.align === 'right' ? 'num' : '';
        th.title = `${column.help}\n\nKlicken sortiert, die rechte Kante ändert die Breite.`;
        if (state.connSort.key === column.key) {
            th.classList.add(state.connSort.asc ? 'sorted-asc' : 'sorted-desc');
        }

        if (index < CONNECTION_COLUMNS.length - 1) {
            addResizeHandle(th, 'connections', CONNECTION_COLUMNS, column, applyConnectionWidths);
        }

        th.addEventListener('click', () => {
            if (state.connSort.key === column.key) {
                state.connSort.asc = !state.connSort.asc;
            } else {
                state.connSort = { key: column.key, asc: column.align === 'left' };
            }
            renderConnectionHead();
            renderConnections();
        });

        return th;
    }));

    applyConnectionWidths();
}

function renderConnections() {
    const rows = visibleConnections();
    const direction = state.connSort.asc ? 1 : -1;
    rows.sort((a, b) =>
        direction * compareConnections(a, b, state.connSort.key) || a.localPort - b.localPort);

    const shown = rows.slice(0, 600);
    const fragment = document.createDocumentFragment();

    for (const connection of shown) {
        const tr = document.createElement('tr');
        tr.dataset.pid = connection.pid;

        for (const column of CONNECTION_COLUMNS) {
            const td = document.createElement('td');
            td.textContent = column.text(connection);
            td.title = td.textContent;
            if (column.align === 'right') {
                td.className = 'num';
            }
            if (column.key === 'state') {
                td.classList.add(`conn-${connection.state}`);
            }
            tr.append(td);
        }

        fragment.append(tr);
    }

    elements.connBody.replaceChildren(fragment);

    const capped = rows.length > shown.length ? ` (${shown.length} angezeigt)` : '';
    const truncated = state.connectionTotal > state.connections.length
        ? `, ${state.connectionTotal} insgesamt`
        : '';
    elements.connStatus.textContent = `${rows.length} Einträge${capped}${truncated}`;
}

// ---------- Logs ----------

/**
 * Der Reiter „Logs" beantwortet eine einzige Frage: was wird gerade *nicht*
 * gelesen, und warum. Er speist sich aus drei Quellen — den Zustandsmeldungen
 * des Hosts (welcher Zählersatz, welcher Treiber fehlt), dem Fehlerprotokoll des
 * Erfassungsteils (was beim Lesen geworfen hat) und der Prozessliste selbst
 * (wie viele Zeilen unvollständig sind). Jeder Eintrag nennt die Folge und den
 * Grund; Vermutungen stehen als solche da.
 */
const LOG_LEVELS = {
    error: 'Fällt aus',
    warn: 'Eingeschränkt',
    info: 'Hinweis',
    ok: 'Liefert',
};

const LOG_ORDER = { error: 0, warn: 1, info: 2, ok: 3 };

/** Was der Host über die Verfügbarkeit seiner Quellen meldet. */
function capabilityEntries() {
    const diag = state.diag || {};
    const energy = state.last?.energy || {};
    const list = [];
    const add = (level, title, what, why) => list.push({ level, title, what, why });

    if (diag.elevated === false) {
        add('error', 'Keine Administratorrechte',
            'Sensoren und der Netzverkehr je Prozess bleiben leer.',
            'Der Sensor-Treiber lässt sich nur erhöht laden, und eine Kernel-ETW-Sitzung darf nur ' +
            'ein erhöhter Prozess starten. Die Anwendung fordert die Rechte über ihr Manifest an — ' +
            'wurde die Abfrage abgelehnt, läuft sie eingeschränkt weiter.');
    } else if (state.logAll) {
        add('ok', 'Administratorrechte', 'Der Prozess läuft erhöht.',
            'Voraussetzung für Sensor-Treiber und ETW-Sitzung.');
    }

    if (diag.sensorDriverError) {
        add('error', 'Sensorbibliothek nicht geöffnet',
            'Temperaturen, Lüfter, Taktraten und Leistungsaufnahme fehlen vollständig.',
            `Beim Öffnen meldete LibreHardwareMonitor: ${diag.sensorDriverError}`);
    }

    const cpu = state.last?.cpu || {};

    if (diag.cpuSensorsBlocked) {
        add('warn', 'CPU-Temperatur, -Takt und -Leistung',
            'Die Sensoren sind angelegt, liefern aber nichts — je nach Windows-Fassung melden sie ' +
            'konstant 0 oder gar keinen Wert. Beides wird ausgeblendet statt angezeigt.',
            'Der Sensortreiber WinRing0 wird von der Speicherintegrität und der Windows-Sperrliste ' +
            'für verwundbare Treiber blockiert. Er ist der einzige Weg zu den Modellregistern des ' +
            'Prozessors, in denen Die-Temperatur, Takt und die Energiezähler stehen. Die GPU-Werte ' +
            'liest die Sensorbibliothek ohne eigenen Treiber und sind nicht betroffen.');
    } else if (state.logAll) {
        add('ok', 'CPU-Sensoren', 'Temperatur, Takt und Leistungsaufnahme werden gelesen.', 'Über den Sensor-Treiber.');
    }

    if (cpu.tempOrigin === 'acpiZone') {
        add('info', 'CPU-Temperatur aus ACPI',
            'Angezeigt wird die Thermalzone des Prozessors statt seiner Die-Temperatur.',
            'Die Zone kommt aus der Firmware über den ACPI-Treiber von Windows und braucht keinen ' +
            'eigenen Sensortreiber. Sie misst in der Umgebung des Prozessors: der Wert liegt niedriger ' +
            'als die Die-Temperatur und folgt Lastspitzen mit Verzögerung.');
    }

    if (cpu.clockEstimated) {
        add('info', 'CPU-Takt gerechnet',
            'Der Takt ist aus Basistakt und „% Processor Performance" gerechnet, nicht gemessen.',
            'Denselben Weg geht der Task-Manager. Er trifft den Mittelwert über alle Kerne im ' +
            'Messintervall; einzelne Kerne können darüber oder darunter liegen.');
    } else if (diag.clockEstimateAvailable === false && state.logAll) {
        add('info', 'Taktschätzung', 'Steht als Rückfall nicht zur Verfügung.',
            'Entweder fehlt der Zähler „% Processor Performance" oder der Basistakt ließ sich nicht lesen.');
    }

    if (diag.boardSensorsMissing) {
        add(diag.hasBattery ? 'info' : 'warn', 'Mainboard-Sensoren',
            'Sockeltemperatur und Gehäuselüfter fehlen.',
            diag.hasBattery
                ? 'Dieses Gerät hat einen Akku, ist also ein Notebook. Dort sitzt statt eines '
                + 'Super-I/O-Chips ein Embedded Controller, den jeder Hersteller anders anspricht — die '
                + 'Sensorbibliothek kennt dafür keinen allgemeinen Weg. Das liegt nicht am gesperrten '
                + 'Kernel-Treiber und ließe sich auch mit ihm nicht beheben.'
                : 'Der Super-I/O-Chip des Mainboards taucht in der Hardwareliste nicht auf. Auch er ist '
                + 'nur über den blockierten Kernel-Treiber erreichbar.');
    }

    if (diag.thermalZonesAvailable) {
        if (state.logAll) {
            add('ok', 'ACPI-Thermalzonen', 'Werden gelesen.',
                'Der Zählersatz „Thermal Zone Information" liefert die Kühlzonen der Firmware — ohne '
                + 'Kernel-Treiber und damit unabhängig von der Speicherintegrität.');
        }
    } else {
        add('info', 'ACPI-Thermalzonen', 'Der Zählersatz „Thermal Zone Information" fehlt.',
            'Er ist der treiberfreie Rückfall für Temperaturen. Ohne ihn bleibt nur, was die '
            + 'Sensorbibliothek meldet.');
    }

    if (!(energy.temperatures || []).length) {
        add('warn', 'Temperatursensoren', 'Es wird kein einziger Temperaturwert gemeldet.',
            'Ohne geladenen Sensor-Treiber meldet weder Prozessor noch Mainboard etwas; Grafikkarten ' +
            'melden ihre Temperatur normalerweise auch ohne ihn, und die ACPI-Thermalzonen kämen ganz ' +
            'ohne Treiber aus — wenn dieses System sie führt.');
    } else if (state.logAll) {
        add('ok', 'Temperatursensoren', `${(energy.temperatures || []).length} Sensoren werden gelesen.`, '');
    }

    if (!(energy.fans || []).length) {
        add('info', 'Lüfter', 'Es werden keine Drehzahlen gemeldet.',
            diag.hasBattery
                ? 'Notebooks führen ihre Lüfter am Embedded Controller und geben die Drehzahl nicht '
                + 'über eine allgemeine Schnittstelle heraus, sondern nur an das Werkzeug des '
                + 'Herstellers. Windows selbst kennt sie ebenso wenig.'
                : 'Gehäuselüfter hängen am Super-I/O-Chip des Mainboards; ohne geladenen Sensortreiber '
                + 'ist er nicht erreichbar.');
    } else if (state.logAll) {
        add('ok', 'Lüfter', `${(energy.fans || []).length} Lüfter werden gelesen.`, '');
    }

    if (!(energy.rails || []).length) {
        add('info', 'Leistungssensoren', 'Die Leistungsaufnahme lässt sich nicht messen.',
            'Ohne Zugriff auf die Energiezähler von Prozessor und Grafikkarte bleibt der Reiter ' +
            '„Energie" bei Schätzungen aus der Last.');
    } else if (state.logAll) {
        add('ok', 'Leistungssensoren', `${(energy.rails || []).length} Sensoren werden gelesen.`, '');
    }

    const counters = [
        [diag.gpuCountersMissing, 'GPU Engine', 'GPU-Last und die Aufschlüsselung nach Engine-Typ bleiben leer.'],
        [diag.networkCountersMissing, 'Network Interface', 'Der Gesamtdurchsatz des Netzwerks fehlt.'],
        [diag.diskCountersMissing, 'PhysicalDisk', 'Der Datenträgerdurchsatz fehlt.'],
        [diag.processCountersMissing, 'Process V2 und Process', 'Die Prozessliste bleibt leer.'],
    ];

    for (const [missing, name, effect] of counters) {
        if (missing) {
            add(name === 'Process V2 und Process' ? 'error' : 'warn', `Zählersatz „${name}"`, effect,
                'Der Zählersatz ist auf diesem System nicht registriert — meist eine Treiber- oder ' +
                'Windows-Konstellation, die ihn nicht anlegt. Wiederherstellen lässt er sich mit ' +
                '„lodctr /R" in einer erhöhten Eingabeaufforderung.');
        } else if (state.logAll) {
            add('ok', `Zählersatz „${name}"`, 'Liefert Werte.', '');
        }
    }

    if (diag.legacyProcessCounters) {
        add('warn', 'Zählersatz „Process V2"',
            'Es wird der ältere Satz „Process" verwendet; gleichnamige Prozesse teilen sich dort eine ' +
            'Zählerinstanz, einzelne Werte können dadurch ungenau sein.',
            'Process V2 gibt es erst ab Windows 10 2004 und nur, wenn der Satz registriert ist.');
    }

    if (diag.networkTraceError) {
        add('warn', 'Netzverkehr je Prozess (ETW)',
            'Die Spalten Download und Upload bleiben leer.',
            `Die Kernel-Sitzung meldete: ${diag.networkTraceError}`);
    } else if (state.logAll) {
        add('ok', 'Netzverkehr je Prozess (ETW)', 'Die Kernel-Sitzung läuft.', '');
    }

    if (!energy.battery) {
        add('info', 'Akku', 'Es wird kein Akku gemeldet.',
            'Auf einem Desktop-Rechner ist das der Normalfall und kein Fehler.');
    }

    return list;
}

/**
 * Was in der Prozessliste fehlt. Das sind keine Fehler, sondern die Grenze
 * dessen, was Windows einem Prozess über andere Prozesse verrät — aber es ist
 * genau das, was in der Tabelle als leere Zelle auffällt.
 */
function processGapEntries() {
    if (!state.processes.length) {
        return [];
    }

    const total = state.processes.length;
    const list = [];
    const missingUser = state.processes.filter(row => !row.user).length;
    const missingPath = state.processes.filter(row => !row.path).length;

    if (missingUser) {
        list.push({
            level: 'info', title: 'Benutzerkonto nicht lesbar',
            what: `Bei ${nf0.format(missingUser)} von ${nf0.format(total)} Prozessen bleibt die Spalte „Benutzer" leer.`,
            why: 'Geschützte Prozesse wie csrss.exe geben ihr Zugriffstoken nicht heraus. Das gilt auch ' +
                 'für einen erhöhten Prozess und ist der Normalfall, kein Rechteproblem.',
        });
    }

    if (missingPath) {
        list.push({
            level: 'info', title: 'Dateipfad nicht lesbar',
            what: `Bei ${nf0.format(missingPath)} von ${nf0.format(total)} Prozessen bleibt die Spalte „Datei" leer.`,
            why: 'Aus demselben Grund: der Pfad wird aus dem Prozesshandle gelesen, und geschützte ' +
                 'Prozesse geben keines heraus.',
        });
    }

    return list;
}

/** Die vom Erfassungsteil gemeldeten Fehler, jüngste zuerst. */
function runtimeLogEntries() {
    return (state.logs || []).map(entry => ({
        level: entry.severity === 'error' ? 'error' : entry.severity === 'info' ? 'info' : 'warn',
        title: entry.source,
        what: entry.message,
        why: '',
        first: entry.first,
        last: entry.last,
        count: entry.count,
    }));
}

function formatLogTime(value) {
    const date = new Date(value);
    return Number.isNaN(date.getTime())
        ? ''
        : date.toLocaleString('de-DE', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });
}

function logCard(entry) {
    const card = document.createElement('article');
    card.className = 'log-card';
    card.dataset.level = entry.level;

    const head = document.createElement('div');
    head.className = 'log-head';

    const level = document.createElement('span');
    level.className = 'log-level';
    level.textContent = LOG_LEVELS[entry.level];

    const title = document.createElement('h4');
    title.textContent = entry.title;

    head.append(level, title);

    // Nur die Einträge aus dem Fehlerprotokoll haben einen Zeitpunkt; die
    // Zustandsmeldungen gelten jetzt.
    if (entry.last) {
        const meta = document.createElement('span');
        meta.className = 'log-meta';
        meta.textContent = entry.count > 1
            ? `${formatLogTime(entry.last)}  ·  ${nf0.format(entry.count)} ×`
            : formatLogTime(entry.last);
        meta.title = entry.count > 1
            ? `Zuerst ${formatLogTime(entry.first)}, zuletzt ${formatLogTime(entry.last)}, insgesamt ${entry.count} Mal.`
            : '';
        head.append(meta);
    }

    card.append(head);

    const what = document.createElement('p');
    what.className = 'log-what';
    what.textContent = entry.what;
    card.append(what);

    if (entry.why) {
        const why = document.createElement('p');
        why.className = 'log-why';
        why.textContent = entry.why;
        card.append(why);
    }

    return card;
}

function renderLogs() {
    const entries = [...capabilityEntries(), ...processGapEntries(), ...runtimeLogEntries()]
        .sort((a, b) => LOG_ORDER[a.level] - LOG_ORDER[b.level]);

    elements.logList.replaceChildren(...entries.map(logCard));

    const broken = entries.filter(entry => entry.level === 'error').length;
    const limited = entries.filter(entry => entry.level === 'warn').length;

    elements.logSummary.textContent = broken || limited
        ? `${broken + limited} Quellen liefern nicht vollständig`
        : 'Alle Quellen liefern';
    elements.logSummary.dataset.level = broken ? 'error' : limited ? 'warn' : 'ok';
    elements.logStatus.textContent = `${entries.length} Einträge`;
}

// ---------- Einstellungen ----------

/** Die Zeilen der kleinen Ansicht, in der Reihenfolge des Overlays. */
const OVERLAY_ROWS = [
    { key: 'cpu', label: 'CPU' },
    { key: 'gpu', label: 'GPU' },
    { key: 'ram', label: 'Arbeitsspeicher' },
    { key: 'net', label: 'Netzwerk' },
    { key: 'disk', label: 'Datenträger' },
    { key: 'temps', label: 'Temperaturen' },
];

/** Die Farbwerte sind nur für die Vorschaufelder; es gilt, was die CSS-Schemata sagen. */
const THEMES = [
    { key: 'dark', label: 'Dunkel', bg: '#111214', accent: '#60a5fa' },
    { key: 'light', label: 'Hell', bg: '#f3f5f9', accent: '#2563eb' },
    { key: 'blue', label: 'Blau', bg: '#06182e', accent: '#38bdf8' },
    { key: 'red', label: 'Rot', bg: '#191012', accent: '#f87171' },
    { key: 'green', label: 'Grün', bg: '#0b1712', accent: '#34d399' },
    { key: 'sepia', label: 'Sepia', bg: '#f4ede1', accent: '#1d4e89' },
];

const settingsUi = {
    themePicker: document.getElementById('theme-picker'),
    opacity: document.getElementById('set-opacity'),
    opacityValue: document.getElementById('opacity-value'),
    scale: document.getElementById('set-scale'),
    scaleValue: document.getElementById('scale-value'),
    clickThrough: document.getElementById('set-clickthrough'),
    overlayRows: document.getElementById('overlay-rows'),
    chartRows: document.getElementById('chart-rows'),
};

function checkboxRow(id, label, onChange) {
    const wrap = document.createElement('label');

    const input = document.createElement('input');
    input.type = 'checkbox';
    input.id = id;
    input.addEventListener('change', () => onChange(input.checked));

    const text = document.createElement('span');
    text.textContent = label;

    wrap.append(input, text);
    return wrap;
}

function buildSettingsPage() {
    settingsUi.themePicker.replaceChildren(...THEMES.map(theme => {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'theme-option';
        button.dataset.theme = theme.key;
        button.style.setProperty('--swatch-bg', theme.bg);
        button.style.setProperty('--swatch-accent', theme.accent);

        const swatch = document.createElement('span');
        swatch.className = 'theme-swatch';
        button.append(swatch, document.createTextNode(theme.label));
        button.addEventListener('click', () => send('setTheme', { key: theme.key }));
        return button;
    }));

    settingsUi.overlayRows.replaceChildren(...OVERLAY_ROWS.map(row =>
        checkboxRow(`row-${row.key}`, row.label, on => send('setOverlayRow', { key: row.key, on }))));

    settingsUi.chartRows.replaceChildren(...CHART_SERIES.map(series =>
        checkboxRow(`chart-${series.key}`, series.label, on => send('setChartRow', { key: series.key, on }))));
}

/** Der Host schickt jede Änderung zurück; die Seite zeigt nie einen eigenen Stand. */
function applySettings(data) {
    document.documentElement.dataset.theme = data.theme || 'dark';
    for (const button of settingsUi.themePicker.children) {
        button.classList.toggle('active', button.dataset.theme === data.theme);
    }

    const percent = value => `${Math.round(value * 100)} %`;
    setInput(settingsUi.opacity, Math.round(data.overlay.opacity * 100));
    settingsUi.opacityValue.textContent = percent(data.overlay.opacity);
    setInput(settingsUi.scale, Math.round(data.overlay.scale * 100));
    settingsUi.scaleValue.textContent = percent(data.overlay.scale);
    settingsUi.clickThrough.checked = data.overlay.clickThrough;

    for (const row of OVERLAY_ROWS) {
        document.getElementById(`row-${row.key}`).checked = Boolean(data.visible[row.key]);
    }
    for (const series of CHART_SERIES) {
        document.getElementById(`chart-${series.key}`).checked = Boolean(data.chart[series.key]);
    }

    state.chart = data.chart;
    drawChart();
}

/** Einen Regler, den der Benutzer gerade festhält, nicht unter der Hand verstellen. */
function setInput(input, value) {
    if (document.activeElement !== input) {
        input.value = value;
    }
}

settingsUi.opacity.addEventListener('input', event => {
    const value = Number(event.target.value);
    settingsUi.opacityValue.textContent = `${value} %`;
    send('setOpacity', { value: value / 100 });
});

settingsUi.scale.addEventListener('input', event => {
    const value = Number(event.target.value);
    settingsUi.scaleValue.textContent = `${value} %`;
    send('setScale', { value: value / 100 });
});

settingsUi.clickThrough.addEventListener('change', event =>
    send('setClickThrough', { on: event.target.checked }));

// ---------- Ansichten ----------

for (const tab of document.querySelectorAll('.tab')) {
    tab.addEventListener('click', () => {
        state.view = tab.dataset.view;
        for (const other of document.querySelectorAll('.tab')) {
            other.classList.toggle('active', other === tab);
        }
        for (const view of document.querySelectorAll('.view')) {
            view.hidden = view.id !== `view-${state.view}`;
        }

        // Das Canvas hat im ausgeblendeten Zustand die Größe 0 und muss nach dem
        // Einblenden neu gezeichnet werden. Dasselbe gilt für die Tabellen der
        // anderen Reiter: sie werden nur gefüllt, solange sie sichtbar sind.
        if (state.view === 'processes') {
            drawChart();
            renderTable();
        } else if (state.view === 'energy') {
            if (state.last) {
                renderEnergy(state.last);
            }
        } else if (state.view === 'connections') {
            renderPortsOverview();
            renderConnections();
        } else if (state.view === 'logs') {
            renderLogs();
        } else if (state.view === 'system' && !state.systemLoaded) {
            // Die Übersicht wird genau einmal gesendet. Ist sie nicht angekommen
            // — etwa weil die Seite beim Senden noch lud —, hier nachfragen.
            send('requestSystemInfo');
        }
    });
}

// ---------- Bedienelemente ----------

document.getElementById('filter').addEventListener('input', event => {
    state.filter = event.target.value.trim();
    renderTable();
});

document.getElementById('aggregate').addEventListener('change', event => {
    state.aggregate = event.target.checked;
    rowCache.clear();
    renderTable();
});

document.getElementById('only-active').addEventListener('change', event => {
    state.onlyActive = event.target.checked;
    renderTable();
});

elements.refreshDevices.addEventListener('click', () => {
    elements.refreshDevices.disabled = true;
    elements.refreshDevices.textContent = 'wird erhoben …';
    send('refreshSystemInfo');
});

document.getElementById('conn-filter').addEventListener('input', event => {
    state.connFilter = event.target.value.trim();
    renderConnections();
});

for (const [id, key] of [
    ['conn-listening', 'connListening'],
    ['conn-udp', 'connUdp'],
    ['conn-loopback', 'connLoopback'],
]) {
    document.getElementById(id).addEventListener('change', event => {
        state[key] = event.target.checked;
        renderConnections();
    });
}

document.getElementById('log-all').addEventListener('change', event => {
    state.logAll = event.target.checked;
    renderLogs();
});

window.addEventListener('resize', () => {
    drawChart();
    drawEnergyChart();
});

buildColumnsMenu();
buildSettingsPage();
renderHead();
renderConnectionHead();
renderEnergyHead();

// Der Host schickt die Einstellungen von sich aus; die Nachfrage kostet nichts
// und deckt den Fall ab, dass die Seite beim Senden noch nicht stand.
send('requestSettings');

// ---------- Host-Nachrichten ----------

if (host) {
    host.addEventListener('message', event => {
        const data = event.data;
        if (!data) {
            return;
        }

        if (data.type === 'system') {
            renderSystemInfo(data);
            return;
        }

        if (data.type === 'settings') {
            applySettings(data);
            return;
        }

        if (data.type !== 'detail') {
            return;
        }

        state.last = data;
        state.diag = data.diag || {};
        renderTiles(data);
        renderNotices(state.diag, data.cpu);
        state.history = data.history;

        // Das Protokoll kommt nur mit, wenn es sich geändert hat.
        if (data.logs) {
            state.logs = data.logs;
        }
        if (state.view === 'processes') {
            drawChart();
        }

        // processes ist null, wenn sich seit dem letzten Takt nichts geändert hat.
        if (data.processes) {
            state.processes = data.processes;
            processNameCache = null;
            if (state.view === 'processes') {
                renderTable();
            }
        }

        if (data.connections) {
            state.connections = data.connections;
            state.connectionTotal = data.connectionTotal || data.connections.length;
        }

        if (state.view === 'energy') {
            renderEnergy(data);
        } else if (state.view === 'connections' && data.connections) {
            renderPortsOverview();
            renderConnections();
        } else if (state.view === 'logs') {
            renderLogs();
        }
    });
}

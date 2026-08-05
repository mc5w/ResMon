// Detailfenster. Sortierung, Filterung, Aggregation, Spaltenauswahl, Notizen und
// die Systemübersicht laufen vollständig hier, der Host liefert nur Rohdaten
// (DESIGN.md §13).

const host = window.chrome && window.chrome.webview;

const STORAGE_COLUMNS = 'resmon.columns';
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

function engineList(row) {
    return Object.entries(row.gpuEngines || {})
        .sort((a, b) => b[1] - a[1])
        .map(([engine, value]) => `${engine} ${nf1.format(value)}`)
        .join(', ');
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
        key: 'name', label: 'Name', align: 'left', locked: true, text: row => row.name,
        help: 'Name der ausführbaren Datei, daneben die Dateibeschreibung aus der Versionsressource.',
    },
    {
        key: 'pid', label: 'PID', align: 'right', text: row => String(row.pid),
        help: 'Prozesskennung. Windows vergibt sie beim Start und verwendet sie nach dem Ende eines Prozesses wieder — sie identifiziert einen Prozess also nur, solange er läuft.',
    },
    {
        key: 'cpu', label: 'CPU %', align: 'right', load: true, text: row => formatPercent(row.cpu),
        help: 'Anteil an der gesamten Rechenkapazität, über alle Kerne gemittelt. 100 % bedeutet, dass alle logischen Prozessoren voll ausgelastet sind.',
    },
    {
        key: 'ws', label: 'Arbeitsspeicher', align: 'right', text: row => formatBytes(row.ws),
        help: 'Privater Arbeitssatz: Speicher, der exklusiv diesem Prozess gehört und gerade tatsächlich im RAM liegt. Das ist die Spalte, die der Task-Manager "Arbeitsspeicher" nennt.',
    },
    {
        key: 'priv', label: 'Privat', align: 'right', off: true, text: row => formatBytes(row.priv),
        help: 'Private Bytes: Speicher, den der Prozess exklusiv belegt hat — einschließlich der Teile, die Windows in die Auslagerungsdatei geschoben hat. Deshalb meist größer als der Arbeitsspeicher.',
    },
    {
        key: 'gpu', label: 'GPU %', align: 'right', load: true, text: row => formatPercent(row.gpu),
        help: 'Auslastung der Grafikkarte durch diesen Prozess: das Maximum über die Engine-Typen, nicht deren Summe.',
    },
    {
        key: 'engines', label: 'GPU-Engines', align: 'left', text: row => engineList(row) || '–',
        help: 'Aufschlüsselung der GPU-Last nach Engine-Typ (3D, Copy, VideoDecode …). Windows zählt sie getrennt, der Task-Manager fasst sie zu einem Wert zusammen. Ein Mauszeiger über den Chips oben erklärt die einzelnen Typen.',
    },
    {
        key: 'gpuMem', label: 'VRAM', align: 'right', text: row => formatBytes(row.gpuMem),
        help: 'Grafikspeicher, den dieser Prozess auf der Karte belegt (Zähler "GPU Process Memory / Local Usage").',
    },
    {
        key: 'rx', label: '↓ Download', align: 'right', text: row => formatRate(row.rx),
        help: 'Empfangene Bytes pro Sekunde, aus einer Kernel-ETW-Sitzung (TCP und UDP). Läuft nur, solange dieses Fenster offen ist.',
    },
    {
        key: 'tx', label: '↑ Upload', align: 'right', text: row => formatRate(row.tx),
        help: 'Gesendete Bytes pro Sekunde, aus einer Kernel-ETW-Sitzung (TCP und UDP).',
    },
    {
        key: 'ioRead', label: 'E/A lesen', align: 'right', text: row => formatRate(row.ioRead),
        help: 'Gelesene Bytes pro Sekunde über alle Ein-/Ausgabekanäle — Dateien, Netzwerk und Geräte zusammen. Nicht ausschließlich Datenträgerzugriff; der reine Datenträgerdurchsatz steht in der Kachel oben.',
    },
    {
        key: 'ioWrite', label: 'E/A schreiben', align: 'right', text: row => formatRate(row.ioWrite),
        help: 'Geschriebene Bytes pro Sekunde über alle Ein-/Ausgabekanäle — Dateien, Netzwerk und Geräte zusammen.',
    },
    {
        key: 'services', label: 'Dienste', align: 'left', text: row => (row.services || []).join(', ') || '–',
        help: 'Windows-Dienste, die in diesem Prozess laufen. Löst "Diensthost: lokales System" zu den konkret laufenden Diensten auf.',
    },
    {
        key: 'path', label: 'Datei', align: 'left', text: row => row.path || '–',
        help: 'Vollständiger Pfad der ausgeführten Datei. Bei Systemprozessen ohne Leserechte bleibt die Spalte leer.',
    },
    {
        key: 'note', label: 'Notiz', align: 'left', text: row => notes[row.name] || '',
        help: 'Eigene Notiz zum Prozess. Doppelklick zum Bearbeiten. Die Notiz hängt am Prozessnamen und bleibt über Neustarts erhalten.',
    },
];

const state = {
    processes: [],
    history: { cpu: [], gpu: [], ram: [] },
    sortKey: 'cpu',
    sortAsc: false,
    filter: '',
    // Standardmäßig zusammengefasst und auf aktive Prozesse beschränkt — so ist
    // die Liste beim Öffnen kurz genug, um etwas darauf zu erkennen.
    aggregate: true,
    onlyActive: true,
    pinned: new Set(),
    expanded: new Set(),
    editing: null,
    view: 'processes',
    systemLoaded: false,
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

function activeColumns() {
    return COLUMNS.filter(column => column.locked || !hiddenColumns.has(column.key));
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
    headRow: document.getElementById('head-row'),
    tbody: document.querySelector('#processes tbody'),
    status: document.getElementById('status'),
    columnsButton: document.getElementById('columns-button'),
    columnsMenu: document.getElementById('columns-menu'),
    systemGroups: document.getElementById('system-groups'),
    systemDrives: document.getElementById('system-drives'),
    systemEmpty: document.getElementById('system-empty'),
};

// ---------- Kacheln ----------

function renderTiles(data) {
    elements.cpuPercent.textContent = nf1.format(data.cpu.percent);
    elements.cpuSub.textContent = [
        optional(data.cpu.tempC, '°C'),
        optional(data.cpu.clockMhz, 'MHz'),
        optional(data.cpu.powerW, 'W', 1),
    ].filter(Boolean).join('  ·  ') || 'keine Sensordaten';

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

function noticesFor(diag) {
    const list = [];
    const add = (id, text) => list.push({ id, text });

    if (diag.cpuSensorsBlocked) {
        add('cpu-sensors',
            'CPU-Temperatur, -Takt und -Leistung sind nicht lesbar: der Sensor-Treiber WinRing0 ' +
            'wird von der Speicherintegrität und der Sperrliste für verwundbare Treiber blockiert. ' +
            'GPU-Werte kommen über NVAPI und sind davon nicht betroffen.');
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

function renderNotices(diag) {
    const wanted = noticesFor(diag).filter(notice => !dismissedNotices.has(notice.id));
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

    context.setTransform(ratio, 0, 0, ratio, 0, 0);
    context.clearRect(0, 0, width, height);

    context.strokeStyle = 'rgba(255,255,255,0.06)';
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
    const line = (series, color) => {
        if (!series || series.length < 2) {
            return;
        }
        const step = width / (capacity - 1);
        const offset = width - (series.length - 1) * step;
        const toY = value => height - (Math.min(100, Math.max(0, value)) / 100) * (height - 2) - 1;

        context.beginPath();
        context.moveTo(offset, toY(series[0]));
        for (let i = 1; i < series.length; i++) {
            context.lineTo(offset + i * step, toY(series[i]));
        }
        context.strokeStyle = color;
        context.lineWidth = 1.5;
        context.stroke();
    };

    line(state.history.ram, '#fb923c');
    line(state.history.gpu, '#4ade80');
    line(state.history.cpu, '#60a5fa');
}

// ---------- Aggregation ----------

/**
 * Fasst Kindprozesse unter ihrem Elternprozess zusammen. Es wird transitiv bis
 * zum obersten Vorfahren gerollt, der selbst noch in der Liste steht — bei
 * bereits beendeten Elternprozessen endet die Kette von allein.
 */
function aggregateTree(processes) {
    const byPid = new Map(processes.map(p => [p.pid, p]));

    // Liefert den obersten Vorfahren samt Abstand zu ihm, für die Einrückung
    // beim Aufklappen.
    const rootOf = process => {
        let current = process;
        let depth = 0;
        // Tiefenbegrenzung als Schutz vor recycelten PIDs, die einen Zyklus bilden.
        while (depth < 32) {
            const parent = current.parentPid === null || current.parentPid === undefined
                ? undefined
                : byPid.get(current.parentPid);
            if (!parent || parent === current) {
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
                rx: 0, tx: 0, ioRead: 0, ioWrite: 0, services: [], children: 0, members: [],
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
        for (const [engine, value] of Object.entries(process.gpuEngines || {})) {
            group.gpuEngines[engine] = (group.gpuEngines[engine] || 0) + value;
        }
        for (const service of process.services || []) {
            if (!group.services.includes(service)) {
                group.services.push(service);
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
    }

    return [...groups.values()];
}

// ---------- Zeilenauswahl ----------

function matchesFilter(row, needle) {
    return row.name.toLowerCase().includes(needle)
        || (row.description || '').toLowerCase().includes(needle)
        || (row.path || '').toLowerCase().includes(needle)
        || (notes[row.name] || '').toLowerCase().includes(needle)
        || (row.services || []).some(service => service.toLowerCase().includes(needle))
        || String(row.pid) === needle;
}

function compare(a, b, key) {
    switch (key) {
        case 'name':
            return a.name.localeCompare(b.name, 'de');
        case 'path':
            return (a.path || '').localeCompare(b.path || '', 'de');
        case 'note':
            return (notes[a.name] || '').localeCompare(notes[b.name] || '', 'de');
        case 'services':
            return (a.services || []).join().localeCompare((b.services || []).join(), 'de');
        case 'engines':
            return Object.keys(a.gpuEngines || {}).length - Object.keys(b.gpuEngines || {}).length;
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
        // Angeheftete Zeilen überstehen Filter und "nur aktive" — genau dafür
        // sind sie da.
        if (state.pinned.has(row.pid)) {
            pinned.push(row);
            continue;
        }
        if (needle && !matchesFilter(row, needle)) {
            continue;
        }
        if (state.onlyActive && row.cpu < 0.1 && row.gpu < 0.1 && !row.rx && !row.tx) {
            continue;
        }
        rest.push(row);
    }

    return { pinned: sortRows(pinned), rest: sortRows(rest), total: all.length };
}

// ---------- Tabelle ----------

const rowCache = new Map();

function renderHead() {
    const cells = activeColumns().map(column => {
        const th = document.createElement('th');
        th.textContent = column.label;
        th.dataset.sort = column.key;
        th.className = column.align === 'right' ? 'num' : '';
        th.title = column.help;
        if (state.sortKey === column.key) {
            th.classList.add(state.sortAsc ? 'sorted-asc' : 'sorted-desc');
        }
        th.addEventListener('click', () => {
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
        return th;
    });

    elements.headRow.replaceChildren(...cells);
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
            (column.key === 'note' ? ' note' : '');

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
                td.title = text;
            }
        }
    }

    return entry.tr;
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
    const groups = [...pinned, ...rest.slice(0, 400)];

    const items = [];
    for (const group of groups) {
        items.push(...expandGroup(group));
    }

    const fragment = document.createDocumentFragment();
    for (const item of items) {
        fragment.append(rowElement(item, columns));
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
    if (!tr) {
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
    const items = COLUMNS.filter(column => !column.locked).map(column => {
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

    elements.columnsMenu.replaceChildren(...items);
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
            value.title = item.value;
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

// ---------- Ansichten ----------

for (const tab of document.querySelectorAll('.tab')) {
    tab.addEventListener('click', () => {
        state.view = tab.dataset.view;
        for (const other of document.querySelectorAll('.tab')) {
            other.classList.toggle('active', other === tab);
        }
        document.getElementById('view-processes').hidden = state.view !== 'processes';
        document.getElementById('view-system').hidden = state.view !== 'system';

        // Das Canvas hat im ausgeblendeten Zustand die Größe 0 und muss nach dem
        // Einblenden neu gezeichnet werden.
        if (state.view === 'processes') {
            drawChart();
        } else if (!state.systemLoaded) {
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

window.addEventListener('resize', drawChart);

buildColumnsMenu();
renderHead();

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

        if (data.type !== 'detail') {
            return;
        }

        renderTiles(data);
        renderNotices(data.diag || {});
        state.history = data.history;
        if (state.view === 'processes') {
            drawChart();
        }

        // processes ist null, wenn sich seit dem letzten Takt nichts geändert hat.
        if (data.processes) {
            state.processes = data.processes;
            renderTable();
        }
    });
}

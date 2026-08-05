// Detailfenster. Sortierung, Filterung und Baum-Aggregation laufen vollständig
// hier, der Host liefert nur Rohdaten (DESIGN.md §13).

const host = window.chrome && window.chrome.webview;

const state = {
    processes: [],
    history: { cpu: [], gpu: [], ram: [] },
    sortKey: 'cpu',
    sortAsc: false,
    filter: '',
    aggregate: false,
    onlyActive: false,
};

const elements = {
    cpuPercent: document.getElementById('cpu-percent'),
    cpuSub: document.getElementById('cpu-sub'),
    cpuCores: document.getElementById('cpu-cores'),
    gpuPercent: document.getElementById('gpu-percent'),
    gpuSub: document.getElementById('gpu-sub'),
    gpuEngines: document.getElementById('gpu-engines'),
    ramPercent: document.getElementById('ram-percent'),
    ramSub: document.getElementById('ram-sub'),
    chart: document.getElementById('history'),
    tbody: document.querySelector('#processes tbody'),
    status: document.getElementById('status'),
};

// ---------- Formatierung ----------

const nf1 = new Intl.NumberFormat('de-DE', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
const nf0 = new Intl.NumberFormat('de-DE', { maximumFractionDigits: 0 });

function formatBytes(bytes) {
    if (!bytes) {
        return '–';
    }
    if (bytes >= 1073741824) {
        return `${nf1.format(bytes / 1073741824)} GB`;
    }
    return `${nf0.format(bytes / 1048576)} MB`;
}

function formatPercent(value) {
    return value > 0 ? nf1.format(value) : '–';
}

function optional(value, suffix, digits = 0) {
    if (value === null || value === undefined) {
        return null;
    }
    const formatter = digits === 0 ? nf0 : nf1;
    return `${formatter.format(value)} ${suffix}`;
}

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
    elements.ramSub.textContent =
        `${formatBytes(data.ram.usedBytes)} / ${formatBytes(data.ram.totalBytes)}  ·  ` +
        `Commit ${formatBytes(data.ram.committedBytes)}`;
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
    if (entries.length === 0) {
        elements.gpuEngines.replaceChildren();
        return;
    }

    elements.gpuEngines.replaceChildren(...entries.map(([name, value]) => {
        const chip = document.createElement('span');
        chip.className = 'chip';
        chip.textContent = `${name} ${nf1.format(value)} %`;
        return chip;
    }));
}

// ---------- Verlaufsdiagramm ----------

function drawChart() {
    const canvas = elements.chart;
    const context = canvas.getContext('2d');
    const ratio = window.devicePixelRatio || 1;
    const width = canvas.clientWidth;
    const height = canvas.clientHeight;

    if (canvas.width !== width * ratio || canvas.height !== height * ratio) {
        canvas.width = width * ratio;
        canvas.height = height * ratio;
    }

    context.setTransform(ratio, 0, 0, ratio, 0, 0);
    context.clearRect(0, 0, width, height);

    // Rasterlinien bei 25/50/75 %.
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

// ---------- Prozesstabelle ----------

/**
 * Fasst Kindprozesse unter ihrem Elternprozess zusammen. Es wird transitiv bis
 * zum obersten Vorfahren gerollt, der selbst noch in der Liste steht — bei
 * bereits beendeten Elternprozessen endet die Kette von allein.
 */
function aggregateTree(processes) {
    const byPid = new Map(processes.map(p => [p.pid, p]));

    const rootOf = process => {
        let current = process;
        // Tiefenbegrenzung als Schutz vor recycelten PIDs, die einen Zyklus bilden.
        for (let depth = 0; depth < 32; depth++) {
            const parent = current.parentPid !== null && current.parentPid !== undefined
                ? byPid.get(current.parentPid)
                : undefined;
            if (!parent || parent === current) {
                break;
            }
            current = parent;
        }
        return current;
    };

    const groups = new Map();
    for (const process of processes) {
        const root = rootOf(process);
        let group = groups.get(root.pid);
        if (!group) {
            group = {
                pid: root.pid,
                parentPid: null,
                name: root.name,
                description: root.description,
                cpu: 0,
                ws: 0,
                priv: 0,
                gpu: 0,
                gpuEngines: {},
                gpuMem: 0,
                services: [],
                children: 0,
            };
            groups.set(root.pid, group);
        }

        group.cpu += process.cpu;
        group.ws += process.ws;
        group.priv += process.priv;
        group.gpuMem += process.gpuMem;
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

function visibleRows() {
    let rows = state.aggregate ? aggregateTree(state.processes) : state.processes;

    if (state.filter) {
        const needle = state.filter.toLowerCase();
        rows = rows.filter(row =>
            row.name.toLowerCase().includes(needle) ||
            (row.description || '').toLowerCase().includes(needle) ||
            (row.services || []).some(service => service.toLowerCase().includes(needle)) ||
            String(row.pid) === needle);
    }

    if (state.onlyActive) {
        rows = rows.filter(row => row.cpu >= 0.1 || row.gpu >= 0.1);
    }

    const key = state.sortKey;
    const direction = state.sortAsc ? 1 : -1;
    return [...rows].sort((a, b) => direction * compare(a, b, key));
}

function compare(a, b, key) {
    switch (key) {
        case 'name':
            return a.name.localeCompare(b.name, 'de');
        case 'services':
            return (a.services || []).join().localeCompare((b.services || []).join(), 'de');
        case 'engines':
            return Object.keys(a.gpuEngines || {}).length - Object.keys(b.gpuEngines || {}).length;
        default:
            return (a[key] || 0) - (b[key] || 0);
    }
}

function loadClass(value) {
    if (value >= 50) {
        return 'hot';
    }
    return value >= 15 ? 'warm' : '';
}

function renderTable() {
    const rows = visibleRows();
    const fragment = document.createDocumentFragment();

    for (const row of rows.slice(0, 400)) {
        const tr = document.createElement('tr');

        const name = document.createElement('td');
        name.className = 'col-name';
        const nameCell = document.createElement('div');
        nameCell.className = 'name-cell';

        const strong = document.createElement('span');
        strong.textContent = row.name;
        nameCell.append(strong);

        if (row.children) {
            const badge = document.createElement('span');
            badge.className = 'children';
            badge.textContent = `+${row.children}`;
            badge.title = `${row.children} Kindprozesse zusammengefasst`;
            nameCell.append(badge);
        }

        if (row.description) {
            const desc = document.createElement('span');
            desc.className = 'desc';
            desc.textContent = row.description;
            nameCell.append(desc);
        }

        name.append(nameCell);
        tr.append(name);

        tr.append(cell(String(row.pid), 'num'));
        tr.append(cell(formatPercent(row.cpu), `num ${loadClass(row.cpu)}`));
        tr.append(cell(formatBytes(row.ws), 'num'));
        tr.append(cell(formatPercent(row.gpu), `num ${loadClass(row.gpu)}`));

        const engines = Object.entries(row.gpuEngines || {})
            .sort((a, b) => b[1] - a[1])
            .map(([engine, value]) => `${engine} ${nf1.format(value)}`)
            .join(', ');
        tr.append(cell(engines || '–', 'engine-list'));

        tr.append(cell(formatBytes(row.gpuMem), 'num'));
        tr.append(cell((row.services || []).join(', ') || '–', row.services && row.services.length ? 'services' : ''));

        fragment.append(tr);
    }

    elements.tbody.replaceChildren(fragment);
    elements.status.textContent =
        `${rows.length} von ${state.processes.length} Prozessen` +
        (rows.length > 400 ? ' (400 angezeigt)' : '');
}

function cell(text, className) {
    const td = document.createElement('td');
    if (className) {
        td.className = className;
    }
    td.textContent = text;
    td.title = text;
    return td;
}

// ---------- Bedienelemente ----------

document.getElementById('filter').addEventListener('input', event => {
    state.filter = event.target.value.trim();
    renderTable();
});

document.getElementById('aggregate').addEventListener('change', event => {
    state.aggregate = event.target.checked;
    renderTable();
});

document.getElementById('only-active').addEventListener('change', event => {
    state.onlyActive = event.target.checked;
    renderTable();
});

for (const header of document.querySelectorAll('#processes thead th')) {
    header.addEventListener('click', () => {
        const key = header.dataset.sort;
        if (state.sortKey === key) {
            state.sortAsc = !state.sortAsc;
        } else {
            state.sortKey = key;
            // Zahlenspalten absteigend beginnen, Namen aufsteigend.
            state.sortAsc = key === 'name' || key === 'services';
        }

        for (const other of document.querySelectorAll('#processes thead th')) {
            other.classList.remove('sorted-asc', 'sorted-desc');
        }
        header.classList.add(state.sortAsc ? 'sorted-asc' : 'sorted-desc');
        renderTable();
    });
}

window.addEventListener('resize', drawChart);

// ---------- Host-Nachrichten ----------

if (host) {
    host.addEventListener('message', event => {
        const data = event.data;
        if (!data || data.type !== 'detail') {
            return;
        }

        renderTiles(data);
        state.history = data.history;
        drawChart();

        // processes ist null, wenn sich seit dem letzten Takt nichts geändert hat.
        if (data.processes) {
            state.processes = data.processes;
            renderTable();
        }
    });
}

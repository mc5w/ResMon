// Overlay-Oberfläche. Empfängt im Sekundentakt eine Nachricht vom Host und
// schreibt sie in die drei Zeilen; Kommandos gehen als schmales Command-Set
// zurück (DESIGN.md §12).

const host = window.chrome && window.chrome.webview;

const COLORS = { cpu: '#60a5fa', gpu: '#4ade80', ram: '#fb923c', net: '#a78bfa', disk: '#22d3ee' };

const rows = {};
for (const element of document.querySelectorAll('.row')) {
    rows[element.dataset.metric] = {
        root: element,
        fill: element.querySelector('.fill'),
        value: element.querySelector('.value'),
        temp: element.querySelector('.temp'),
        spark: element.querySelector('.spark'),
        down: element.querySelector('.rate.down'),
        up: element.querySelector('.rate.up'),
    };
}

const ramDetail = document.getElementById('ram-detail');

function send(cmd, extra) {
    if (host) {
        host.postMessage(Object.assign({ cmd }, extra));
    }
}

// Das Verschieben im Host braucht den Anstoß von hier, weil WebView2 die
// Mausereignisse abfängt (DESIGN.md §11).
document.getElementById('grip').addEventListener('mousedown', event => {
    // Schaltflächen in der Kopfzeile ausnehmen: der Verschiebe-Loop des Systems
    // verschluckt sonst das mouseup, und das click-Ereignis entsteht nie.
    if (event.button === 0 && !event.target.closest('button')) {
        send('drag');
    }
});

document.getElementById('btn-detail').addEventListener('click', () => send('openDetail'));
document.getElementById('btn-close').addEventListener('click', () => send('close'));

// Mausrad auf der Karte regelt die Deckkraft.
let opacity = 0.9;
document.addEventListener('wheel', event => {
    opacity = Math.min(1, Math.max(0.2, opacity + (event.deltaY < 0 ? 0.05 : -0.05)));
    send('setOpacity', { value: Number(opacity.toFixed(2)) });
}, { passive: true });

function formatPercent(value) {
    return value === null || value === undefined ? '–' : `${value.toFixed(0)} %`;
}

function formatTemp(value) {
    return value === null || value === undefined ? '' : `${value.toFixed(0)} °C`;
}

function formatGiB(bytes) {
    return `${(bytes / 1073741824).toFixed(1)} GB`;
}

/** Datenrate kompakt: unter 1 MB/s in kB/s, darüber in MB/s. */
function formatRate(bytesPerSecond) {
    if (!bytesPerSecond || bytesPerSecond < 512) {
        return '0';
    }
    if (bytesPerSecond < 1048576) {
        return `${(bytesPerSecond / 1024).toFixed(0)} kB/s`;
    }
    return `${(bytesPerSecond / 1048576).toFixed(1)} MB/s`;
}

/**
 * Zeichnet eine Sparkline. Ohne max wird auf 0–100 skaliert, damit die Zeilen
 * vergleichbar bleiben; Netzraten haben keine Obergrenze und skalieren sich auf
 * ihr eigenes Maximum.
 */
function drawSparkline(canvas, series, color, max = 100) {
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

    if (!series || series.length < 2) {
        return;
    }

    const step = width / (series.length - 1);
    const scale = Math.max(max, 1);
    const toY = value => height - (Math.min(scale, Math.max(0, value)) / scale) * (height - 1) - 0.5;

    context.beginPath();
    context.moveTo(0, toY(series[0]));
    for (let i = 1; i < series.length; i++) {
        context.lineTo(i * step, toY(series[i]));
    }

    context.strokeStyle = color;
    context.lineWidth = 1;
    context.globalAlpha = 0.85;
    context.stroke();

    // Fläche unter der Kurve, dezent gefüllt.
    context.lineTo(width, height);
    context.lineTo(0, height);
    context.closePath();
    context.globalAlpha = 0.15;
    context.fillStyle = color;
    context.fill();
    context.globalAlpha = 1;
}

function updateRow(key, percent, temp, showTemps, visible, available = true) {
    const row = rows[key];
    if (!row) {
        return;
    }

    row.root.hidden = !visible;
    if (!visible) {
        return;
    }

    row.root.classList.toggle('stale', !available);
    row.fill.style.width = `${Math.min(100, Math.max(0, percent || 0))}%`;
    row.value.textContent = available ? formatPercent(percent) : '–';

    const label = showTemps ? formatTemp(temp) : '';
    row.temp.textContent = label;
    row.temp.hidden = label === '';
}

/** Zeilen ohne Prozentwert: zwei Raten und eine selbstskalierende Sparkline. */
function updateRateRow(key, visible, available, first, second, history) {
    const row = rows[key];
    row.root.hidden = !visible;
    if (!visible) {
        return;
    }

    row.root.classList.toggle('stale', !available);
    row.down.textContent = formatRate(first);
    row.up.textContent = formatRate(second);
    drawSparkline(row.spark, history, COLORS[key], Math.max(...history, 1));
}

function render(data) {
    const showTemps = data.visible.temps;

    updateRow('cpu', data.cpu.percent, data.cpu.tempC, showTemps, data.visible.cpu);
    updateRow('gpu', data.gpu.percent, data.gpu.tempC, showTemps, data.visible.gpu, data.gpu.available);
    updateRow('ram', data.ram.percent, null, false, data.visible.ram);

    updateRateRow('net', data.visible.net, data.net.available, data.net.rx, data.net.tx, data.history.net);
    updateRateRow('disk', data.visible.disk, data.disk.available, data.disk.read, data.disk.write, data.history.disk);

    drawSparkline(rows.cpu.spark, data.history.cpu, COLORS.cpu);
    drawSparkline(rows.gpu.spark, data.history.gpu, COLORS.gpu);
    drawSparkline(rows.ram.spark, data.history.ram, COLORS.ram);

    const parts = [`${formatGiB(data.ram.usedBytes)} / ${formatGiB(data.ram.totalBytes)}`];
    if (data.gpu.available && data.gpu.memTotalBytes > 0) {
        parts.push(`VRAM ${formatGiB(data.gpu.memUsedBytes)}`);
    }
    ramDetail.textContent = parts.join('  ·  ');
}

if (host) {
    host.addEventListener('message', event => {
        const data = event.data;
        if (data && data.type === 'overlay') {
            render(data);
        }
    });
}

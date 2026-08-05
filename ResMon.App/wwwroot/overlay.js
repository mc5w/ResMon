// Overlay-Oberfläche. Empfängt im Sekundentakt eine Nachricht vom Host und
// schreibt sie in die drei Zeilen; Kommandos gehen als schmales Command-Set
// zurück (DESIGN.md §12).

const host = window.chrome && window.chrome.webview;

const COLORS = { cpu: '#60a5fa', gpu: '#4ade80', ram: '#fb923c' };

const rows = {};
for (const element of document.querySelectorAll('.row')) {
    rows[element.dataset.metric] = {
        root: element,
        fill: element.querySelector('.fill'),
        value: element.querySelector('.value'),
        temp: element.querySelector('.temp'),
        spark: element.querySelector('.spark'),
    };
}

const ramDetail = document.getElementById('ram-detail');

function send(cmd, extra) {
    if (host) {
        host.postMessage(Object.assign({ cmd }, extra));
    }
}

// DragMove() im Host braucht den Anstoß von hier, weil WebView2 die
// Mausereignisse abfängt (DESIGN.md §11).
document.getElementById('grip').addEventListener('mousedown', event => {
    if (event.button === 0) {
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

/** Zeichnet eine Sparkline in feste 0–100-Skalierung, damit Zeilen vergleichbar bleiben. */
function drawSparkline(canvas, series, color) {
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
    const toY = value => height - (Math.min(100, Math.max(0, value)) / 100) * (height - 1) - 0.5;

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

function render(data) {
    const showTemps = data.visible.temps;

    updateRow('cpu', data.cpu.percent, data.cpu.tempC, showTemps, data.visible.cpu);
    updateRow('gpu', data.gpu.percent, data.gpu.tempC, showTemps, data.visible.gpu, data.gpu.available);
    updateRow('ram', data.ram.percent, null, false, data.visible.ram);

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

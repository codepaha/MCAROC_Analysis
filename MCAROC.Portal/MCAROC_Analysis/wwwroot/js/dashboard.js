// Small, reusable Chart.js construction helpers for the Dashboard view. The view itself only builds the
// JSON payload and calls into these — keeps the Razor view from turning into a JS file.

// Palette aligned with the app.css design-system tokens (steel-blue brand + muted semantics).
const MCAROC_CHART = {
    brand: '#2f7df6',
    brandFill: 'rgba(47,125,246,0.12)',
    success: '#3f9374',
    warning: '#c98a3c',
    danger: '#cf6063',
    muted: '#667085'
};

function renderRequestTrendChart(canvasId, labels, created) {
    return new Chart(document.getElementById(canvasId), {
        type: 'line',
        data: {
            labels: labels,
            datasets: [{ label: 'Requests Created', data: created, borderColor: MCAROC_CHART.brand, backgroundColor: MCAROC_CHART.brandFill, fill: true, tension: 0.2 }]
        },
        options: {
            responsive: true,
            scales: { y: { beginAtZero: true, ticks: { precision: 0 } } },
            plugins: { legend: { display: false } }
        }
    });
}

function renderPriorityDistributionChart(canvasId, labels, counts, colors) {
    return new Chart(document.getElementById(canvasId), {
        type: 'bar',
        data: { labels: labels, datasets: [{ label: 'Requests', data: counts, backgroundColor: colors }] },
        options: {
            indexAxis: 'y',
            responsive: true,
            scales: { x: { beginAtZero: true, ticks: { precision: 0 } } },
            plugins: { legend: { display: false } }
        }
    });
}

function renderFindingsBySectionChart(canvasId, labels, datasets) {
    return new Chart(document.getElementById(canvasId), {
        type: 'bar',
        data: { labels: labels, datasets: datasets },
        options: {
            indexAxis: 'y',
            responsive: true,
            scales: { x: { stacked: true, beginAtZero: true, ticks: { precision: 0 } }, y: { stacked: true } }
        }
    });
}

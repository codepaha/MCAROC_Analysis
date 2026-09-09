// Small, reusable Chart.js construction helpers for the Dashboard view. The view itself only builds the
// JSON payload and calls into these — keeps the Razor view from turning into a JS file.

function renderRequestTrendChart(canvasId, labels, created) {
    return new Chart(document.getElementById(canvasId), {
        type: 'line',
        data: {
            labels: labels,
            datasets: [{ label: 'Requests Created', data: created, borderColor: '#0d6efd', backgroundColor: 'rgba(13,110,253,0.1)', fill: true, tension: 0.2 }]
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

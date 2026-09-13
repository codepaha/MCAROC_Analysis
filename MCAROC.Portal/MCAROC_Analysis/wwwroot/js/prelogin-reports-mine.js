/**
 * DOM-facing half of the pre-login pipeline's client-remembered "My Reports" list. Loaded from both
 * History.cshtml (records the current batch) and Mine.cshtml (renders the remembered list) — each
 * behavior is gated by the presence of its own DOM anchor, so one shared file can be safely referenced
 * from both pages without branching on which page it is (same idea as amount-unit.js's init() early
 * return when its own markers aren't present).
 *
 * This never becomes a server-side query (#47) — History.cshtml only emits the JSON data island for a
 * batch the browser already legitimately knows about, and Mine.cshtml only ever reads back from this
 * browser's own localStorage. All actual storage logic lives in prelogin-reports-mine-core.js.
 */
import { loadEntries, recordBatch, clearEntries } from './prelogin-reports-mine-core.js';

function recordCurrentBatch() {
  const island = document.getElementById('pi-batch-summary');
  if (!island) return;

  let data;
  try {
    data = JSON.parse(island.textContent);
  } catch {
    return;
  }

  const cins = Array.isArray(data.cins) ? data.cins : [];
  const label = cins.length <= 1 ? (cins[0] || '') : `${cins[0]} + ${cins.length - 1} more`;

  recordBatch({
    batch: data.batch,
    createdUtc: data.createdUtc,
    label,
    count: cins.length,
    formats: typeof data.formats === 'string' ? data.formats : '',
  });
}

function renderRow(entry) {
  const tr = document.createElement('tr');

  const labelCell = document.createElement('td');
  labelCell.textContent = entry.label;
  tr.appendChild(labelCell);

  const formatCell = document.createElement('td');
  formatCell.textContent = entry.formats;
  tr.appendChild(formatCell);

  const countCell = document.createElement('td');
  countCell.textContent = String(entry.count);
  tr.appendChild(countCell);

  const createdCell = document.createElement('td');
  const created = new Date(entry.createdUtc);
  createdCell.textContent = Number.isNaN(created.getTime()) ? entry.createdUtc : created.toLocaleString();
  tr.appendChild(createdCell);

  const actionCell = document.createElement('td');
  const link = document.createElement('a');
  link.className = 'btn btn-sm btn-outline-primary';
  link.textContent = 'View';
  // batch is already GUID-validated by loadEntries; encodeURIComponent is defense in depth for the URL.
  link.href = `/pre-login-reports/${encodeURIComponent(entry.batch)}/history`;
  actionCell.appendChild(link);
  tr.appendChild(actionCell);

  return tr;
}

function clearChildren(el) {
  while (el.firstChild) {
    el.removeChild(el.firstChild);
  }
}

function renderMineList() {
  const rowsEl = document.getElementById('pi-mine-rows');
  const emptyEl = document.getElementById('pi-mine-empty');
  const wrapEl = document.getElementById('pi-mine-table-wrap');
  if (!rowsEl) return;

  const entries = loadEntries();
  clearChildren(rowsEl);
  entries.forEach((entry) => rowsEl.appendChild(renderRow(entry)));

  const isEmpty = entries.length === 0;
  if (emptyEl) emptyEl.hidden = !isEmpty;
  if (wrapEl) wrapEl.hidden = isEmpty;
}

function initMinePage() {
  const rowsEl = document.getElementById('pi-mine-rows');
  if (!rowsEl) return;

  renderMineList();

  const clearBtn = document.getElementById('pi-mine-clear');
  clearBtn?.addEventListener('click', () => {
    clearEntries();
    renderMineList();
  });
}

function init() {
  recordCurrentBatch();
  initMinePage();
}

if (typeof document !== 'undefined') {
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
}

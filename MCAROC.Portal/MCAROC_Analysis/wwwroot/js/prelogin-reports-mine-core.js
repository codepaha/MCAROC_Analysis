/**
 * Pure storage logic for the pre-login pipeline's client-remembered "My Reports" list. This list lives
 * only in this browser's `localStorage` — never a server-side query — because that pipeline has no
 * login: a `batch` Guid is the only access credential (see PreLoginReportsController.cs's own doc
 * comments on #47, the IDOR a global unscoped listing used to be). Mirrors amount-unit.js's
 * load/save/safeLocalStorage split.
 *
 * `loadEntries` is a schema validator, not a bare `JSON.parse` — every entry read back from storage
 * (which could be hand-edited, corrupted, or written by a different version of this module) is checked
 * against the same rules `recordBatch` enforces going in, and anything that fails is dropped, never
 * repaired or trusted as-is.
 */

export const STORAGE_KEY = 'mcaroc-prelogin-reports';
export const MAX_ENTRIES = 20;

const GUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const MAX_LABEL_LENGTH = 200;
const MAX_FORMATS_LENGTH = 100;
// Mirrors this pipeline's own per-batch CIN cap (PreLoginReportJobService.QueueBatchAsync's `.Take(25)`).
const MAX_COUNT = 25;

function isValidEntry(entry) {
  if (!entry || typeof entry !== 'object') return false;
  if (typeof entry.batch !== 'string' || !GUID_RE.test(entry.batch)) return false;
  if (typeof entry.createdUtc !== 'string' || Number.isNaN(Date.parse(entry.createdUtc))) return false;
  if (!Number.isInteger(entry.count) || entry.count <= 0 || entry.count > MAX_COUNT) return false;
  if (typeof entry.label !== 'string' || entry.label.length === 0 || entry.label.length > MAX_LABEL_LENGTH) return false;
  if (typeof entry.formats !== 'string' || entry.formats.length > MAX_FORMATS_LENGTH) return false;
  return true;
}

function dedupeAndCap(entries) {
  const seen = new Set();
  const deduped = [];
  for (const entry of entries) {
    const key = entry.batch.toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    deduped.push(entry);
  }
  return deduped.slice(0, MAX_ENTRIES);
}

function safeLocalStorage() {
  try {
    return typeof localStorage === 'undefined' ? null : localStorage;
  } catch {
    return null;
  }
}

/**
 * Reads and validates the stored list. Never throws; drops any entry that fails schema validation
 * (non-GUID batch, unparseable timestamp, out-of-range count, oversized label/formats) instead of
 * repairing it, then re-dedupes/re-caps the survivors and, if that cleanup actually changed anything,
 * persists the cleaned list back so a corrupted or hand-edited stored value self-heals going forward.
 * @param {Storage|null} [storage]
 */
export function loadEntries(storage = safeLocalStorage()) {
  let raw;
  try {
    raw = storage?.getItem(STORAGE_KEY);
  } catch {
    return [];
  }
  if (!raw) return [];

  let parsed;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return [];
  }
  if (!Array.isArray(parsed)) return [];

  const cleaned = dedupeAndCap(parsed.filter(isValidEntry));

  if (JSON.stringify(cleaned) !== JSON.stringify(parsed)) {
    saveEntries(cleaned, storage);
  }

  return cleaned;
}

export function saveEntries(entries, storage = safeLocalStorage()) {
  try {
    storage?.setItem(STORAGE_KEY, JSON.stringify(entries));
  } catch {
    // Private browsing / blocked storage — nothing to persist across a reload.
  }
}

export function clearEntries(storage = safeLocalStorage()) {
  try {
    storage?.removeItem(STORAGE_KEY);
  } catch {
    // Private browsing / blocked storage.
  }
}

/**
 * Records (or re-surfaces) one batch as the most recent entry — dedupes by `batch` (moved to front,
 * never duplicated), prepends, prunes past `MAX_ENTRIES`, and persists. An entry that fails schema
 * validation itself is ignored (defense in depth — callers should only ever pass a well-formed entry)
 * rather than being allowed to corrupt the stored list.
 * @param {{batch:string,createdUtc:string,label:string,count:number,formats:string}} entry
 * @param {Storage|null} [storage]
 */
export function recordBatch(entry, storage = safeLocalStorage()) {
  const existing = loadEntries(storage);
  if (!isValidEntry(entry)) return existing;

  const withoutThisBatch = existing.filter((e) => e.batch.toLowerCase() !== entry.batch.toLowerCase());
  const updated = dedupeAndCap([entry, ...withoutThisBatch]);
  saveEntries(updated, storage);
  return updated;
}

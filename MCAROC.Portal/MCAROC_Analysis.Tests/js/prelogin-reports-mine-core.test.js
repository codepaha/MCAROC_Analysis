import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { loadEntries, saveEntries, clearEntries, recordBatch, STORAGE_KEY, MAX_ENTRIES } from '../../MCAROC_Analysis/wwwroot/js/prelogin-reports-mine-core.js';

const __dirname = dirname(fileURLToPath(import.meta.url));

const GUID_A = '3fa85f64-5717-4562-b3fc-2c963f66afa6';
const GUID_B = '4fa85f64-5717-4562-b3fc-2c963f66afa7';
const GUID_C = '5fa85f64-5717-4562-b3fc-2c963f66afa8';

function validEntry(batch, overrides = {}) {
  return {
    batch,
    createdUtc: '2026-09-13T10:00:00.000Z',
    label: 'U74899DL1991PTC043274',
    count: 1,
    formats: 'SBI',
    ...overrides,
  };
}

function mockStorage(initial = {}) {
  const data = { ...initial };
  return {
    getItem: (key) => (key in data ? data[key] : null),
    setItem: (key, value) => { data[key] = value; },
    removeItem: (key) => { delete data[key]; },
    _raw: data,
  };
}

test('loadEntries returns [] for a missing key', () => {
  const storage = mockStorage();
  assert.deepEqual(loadEntries(storage), []);
});

test('loadEntries returns [] (never throws) for corrupt JSON', () => {
  const storage = mockStorage({ [STORAGE_KEY]: '{not json' });
  assert.deepEqual(loadEntries(storage), []);
});

test('loadEntries returns [] for valid JSON that is not an array', () => {
  const storage = mockStorage({ [STORAGE_KEY]: JSON.stringify({ oops: true }) });
  assert.deepEqual(loadEntries(storage), []);
});

test('recordBatch adds a new entry to the front of an empty list', () => {
  const storage = mockStorage();
  const result = recordBatch(validEntry(GUID_A), storage);
  assert.equal(result.length, 1);
  assert.equal(result[0].batch, GUID_A);
});

test('recordBatch moves an already-present batch to the front instead of duplicating it', () => {
  const storage = mockStorage();
  recordBatch(validEntry(GUID_A), storage);
  recordBatch(validEntry(GUID_B), storage);
  const result = recordBatch(validEntry(GUID_A, { label: 'updated label' }), storage);

  assert.equal(result.length, 2);
  assert.equal(result[0].batch, GUID_A);
  assert.equal(result[0].label, 'updated label');
  assert.equal(result[1].batch, GUID_B);
});

test('recordBatch prunes the oldest entry once the list exceeds MAX_ENTRIES', () => {
  const storage = mockStorage();
  for (let i = 0; i < MAX_ENTRIES; i++) {
    const guid = `00000000-0000-0000-0000-${String(i).padStart(12, '0')}`;
    recordBatch(validEntry(guid), storage);
  }
  const overflowGuid = '11111111-0000-0000-0000-000000000000';
  const result = recordBatch(validEntry(overflowGuid), storage);

  assert.equal(result.length, MAX_ENTRIES);
  assert.equal(result[0].batch, overflowGuid);
  // The very first one recorded (oldest) must have been pruned.
  assert.ok(!result.some((e) => e.batch === '00000000-0000-0000-0000-000000000000'));
});

test('recordBatch ignores an entry that fails schema validation, leaving the existing list untouched', () => {
  const storage = mockStorage();
  recordBatch(validEntry(GUID_A), storage);
  const result = recordBatch({ batch: 'not-a-guid', createdUtc: '2026-09-13T10:00:00.000Z', label: 'x', count: 1, formats: 'SBI' }, storage);

  assert.equal(result.length, 1);
  assert.equal(result[0].batch, GUID_A);
});

test('saveEntries/clearEntries swallow a storage whose setItem/removeItem throws', () => {
  const throwingStorage = {
    getItem: () => null,
    setItem: () => { throw new Error('quota exceeded'); },
    removeItem: () => { throw new Error('blocked'); },
  };

  assert.doesNotThrow(() => saveEntries([validEntry(GUID_A)], throwingStorage));
  assert.doesNotThrow(() => clearEntries(throwingStorage));
});

test('loadEntries drops an entry with a non-GUID batch, keeping valid siblings', () => {
  const storage = mockStorage({
    [STORAGE_KEY]: JSON.stringify([validEntry(GUID_A), validEntry('not-a-guid')]),
  });
  const result = loadEntries(storage);
  assert.equal(result.length, 1);
  assert.equal(result[0].batch, GUID_A);
});

test('loadEntries drops an entry with an unparseable createdUtc', () => {
  const storage = mockStorage({
    [STORAGE_KEY]: JSON.stringify([validEntry(GUID_A), validEntry(GUID_B, { createdUtc: 'not-a-date' })]),
  });
  const result = loadEntries(storage);
  assert.equal(result.length, 1);
  assert.equal(result[0].batch, GUID_A);
});

test('loadEntries drops entries with an out-of-range count (zero, negative, non-integer, over 25)', () => {
  const storage = mockStorage({
    [STORAGE_KEY]: JSON.stringify([
      validEntry(GUID_A),
      validEntry(GUID_B, { count: 0 }),
      validEntry(GUID_C, { count: -1 }),
      validEntry('6fa85f64-5717-4562-b3fc-2c963f66afa9', { count: 1.5 }),
      validEntry('7fa85f64-5717-4562-b3fc-2c963f66afa0', { count: 26 }),
    ]),
  });
  const result = loadEntries(storage);
  assert.equal(result.length, 1);
  assert.equal(result[0].batch, GUID_A);
});

test('loadEntries drops entries with an empty or oversized label, or an oversized formats string', () => {
  const storage = mockStorage({
    [STORAGE_KEY]: JSON.stringify([
      validEntry(GUID_A),
      validEntry(GUID_B, { label: '' }),
      validEntry(GUID_C, { label: 'x'.repeat(201) }),
      validEntry('6fa85f64-5717-4562-b3fc-2c963f66afa9', { formats: 'x'.repeat(101) }),
    ]),
  });
  const result = loadEntries(storage);
  assert.equal(result.length, 1);
  assert.equal(result[0].batch, GUID_A);
});

test('loadEntries re-dedupes and re-caps a hand-corrupted stored array, persisting the cleaned result', () => {
  const overCap = Array.from({ length: MAX_ENTRIES + 5 }, (_, i) =>
    validEntry(`00000000-0000-0000-0000-${String(i).padStart(12, '0')}`));
  // Also inject a duplicate of the first entry, hand-edited into the array.
  const withDuplicate = [overCap[0], ...overCap];
  const storage = mockStorage({ [STORAGE_KEY]: JSON.stringify(withDuplicate) });

  const result = loadEntries(storage);

  assert.equal(result.length, MAX_ENTRIES);
  const batches = result.map((e) => e.batch);
  assert.equal(new Set(batches).size, batches.length, 'no duplicate batches survive');
  // The cleaned list must have been persisted back.
  const persisted = JSON.parse(storage.getItem(STORAGE_KEY));
  assert.equal(persisted.length, MAX_ENTRIES);
});

test('hostile label content survives loadEntries unchanged as an inert string (sanitization is a rendering-time concern)', () => {
  const hostileLabel = '<img src=x onerror=alert(1)>';
  const storage = mockStorage({
    [STORAGE_KEY]: JSON.stringify([validEntry(GUID_A, { label: hostileLabel })]),
  });
  const result = loadEntries(storage);
  assert.equal(result.length, 1);
  assert.equal(result[0].label, hostileLabel);
});

test('prelogin-reports-mine.js never assigns innerHTML and renders entry-derived text via textContent', () => {
  const source = readFileSync(join(__dirname, '../../MCAROC_Analysis/wwwroot/js/prelogin-reports-mine.js'), 'utf8');

  assert.ok(!source.includes('.innerHTML'), 'must never build markup from entry-derived text via innerHTML');
  assert.ok(source.includes('labelCell.textContent'), 'label must be rendered via textContent');
  assert.ok(source.includes('formatCell.textContent'), 'formats must be rendered via textContent');
  assert.ok(source.includes('encodeURIComponent(entry.batch)'), 'the history link must encode the batch value');
});

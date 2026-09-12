import test from 'node:test';
import assert from 'node:assert/strict';
import { parsePageFragment, clampPage } from '../../MCAROC_Analysis/wwwroot/js/pdf-viewer-core.js';

test('parsePageFragment extracts positive integer page numbers', () => {
  assert.equal(parsePageFragment('#page=3'), 3);
  assert.equal(parsePageFragment('#page=100'), 100);
  assert.equal(parsePageFragment('#PAGE=42'), 42);
  assert.equal(parsePageFragment('#page=5&zoom=100'), 5);
  assert.equal(parsePageFragment('page=7'), 7);
  assert.equal(parsePageFragment('&page=12'), 12);
});

test('parsePageFragment returns null for invalid or missing fragments', () => {
  assert.equal(parsePageFragment(null), null);
  assert.equal(parsePageFragment(undefined), null);
  assert.equal(parsePageFragment(''), null);
  assert.equal(parsePageFragment('#'), null);
  assert.equal(parsePageFragment('#other-hash'), null);
  assert.equal(parsePageFragment('#page='), null);
  assert.equal(parsePageFragment('#page=abc'), null);
  assert.equal(parsePageFragment('#page=0'), null);
  assert.equal(parsePageFragment('#page=-5'), null);
});

test('clampPage handles normal within-range pages', () => {
  const result = clampPage(3, 10);
  assert.equal(result.page, 3);
  assert.equal(result.isClamped, false);
  assert.equal(result.message, null);
});

test('clampPage handles boundary pages correctly', () => {
  const first = clampPage(1, 10);
  assert.equal(first.page, 1);
  assert.equal(first.isClamped, false);
  assert.equal(first.message, null);

  const last = clampPage(10, 10);
  assert.equal(last.page, 10);
  assert.equal(last.isClamped, false);
  assert.equal(last.message, null);
});

test('clampPage clamps requested pages less than 1 to 1 with warning', () => {
  const result = clampPage(0, 10);
  assert.equal(result.page, 1);
  assert.equal(result.isClamped, true);
  assert.match(result.message, /before page 1/i);

  const negative = clampPage(-5, 10);
  assert.equal(negative.page, 1);
  assert.equal(negative.isClamped, true);
});

test('clampPage clamps requested pages exceeding totalPages with warning', () => {
  const result = clampPage(15, 10);
  assert.equal(result.page, 10);
  assert.equal(result.isClamped, true);
  assert.match(result.message, /exceeds total pages \(10\)/i);
});

test('clampPage handles null or undefined requestedPage gracefully', () => {
  const nullResult = clampPage(null, 5);
  assert.equal(nullResult.page, 1);
  assert.equal(nullResult.isClamped, false);
  assert.equal(nullResult.message, null);

  const undefResult = clampPage(undefined, 5);
  assert.equal(undefResult.page, 1);
  assert.equal(undefResult.isClamped, false);
  assert.equal(undefResult.message, null);
});

test('clampPage handles single-page documents correctly', () => {
  const normal = clampPage(1, 1);
  assert.equal(normal.page, 1);
  assert.equal(normal.isClamped, false);

  const overflow = clampPage(2, 1);
  assert.equal(overflow.page, 1);
  assert.equal(overflow.isClamped, true);
  assert.equal(overflow.page, 1);
});

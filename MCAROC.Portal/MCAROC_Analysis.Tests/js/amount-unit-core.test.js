import test from 'node:test';
import assert from 'node:assert/strict';
import {
  SCALE,
  toScaledBigInt,
  formatUnits,
  convert,
  formatAmount,
  unitLabelText,
} from '../../MCAROC_Analysis/wwwroot/js/amount-unit-core.js';

test('toScaledBigInt parses plain invariant decimal strings exactly', () => {
  assert.equal(toScaledBigInt('0').toString(), '0');
  assert.equal(toScaledBigInt('123').toString(), (123n * 10n ** BigInt(SCALE)).toString());
  assert.equal(toScaledBigInt('1.5').toString(), (15n * 10n ** BigInt(SCALE - 1)).toString());
  assert.equal(toScaledBigInt('-1.5').toString(), (-15n * 10n ** BigInt(SCALE - 1)).toString());
});

test('toScaledBigInt throws — never silently truncates — beyond its supported precision', () => {
  const tooPrecise = `1.${'1'.repeat(SCALE + 1)}`;
  assert.throws(() => toScaledBigInt(tooPrecise), /fractional digits/);
  // Exactly at the limit is fine.
  assert.doesNotThrow(() => toScaledBigInt(`1.${'1'.repeat(SCALE)}`));
});

test('toScaledBigInt rejects non-plain-decimal input', () => {
  assert.throws(() => toScaledBigInt('1,234.56'));
  assert.throws(() => toScaledBigInt('abc'));
  assert.throws(() => toScaledBigInt(''));
  assert.throws(() => toScaledBigInt(null));
});

test('convert: Crore -> Rupee is exact for a large value, digit for digit', () => {
  assert.equal(convert('98765432.10', 'rupee'), '98,76,54,32,10,00,000');
});

test('convert: rounds to 2dp half-away-from-zero at an exact boundary, both signs', () => {
  assert.equal(convert('1.005', 'crore'), '1.01');
  assert.equal(convert('-1.005', 'crore'), '-1.01');
});

test('convert: zero converts to zero in every unit, never a dash', () => {
  assert.equal(convert('0', 'crore'), '0.00');
  assert.equal(convert('0', 'lakh'), '0.00');
  assert.equal(convert('0', 'rupee'), '0');
});

test('convert: a negative value keeps its sign through every unit', () => {
  assert.equal(convert('-2.5', 'crore'), '-2.50');
  assert.equal(convert('-2.5', 'lakh'), '-250.00');
  assert.equal(convert('-2.5', 'rupee'), '-2,50,00,000');
});

test('convert: Crore -> Lakh is an exact ×100 shift', () => {
  assert.equal(convert('1', 'lakh'), '100.00');
  assert.equal(convert('0.01', 'lakh'), '1.00');
});

test('round-trip Crore -> Rupee -> Crore recovers the original value exactly when no rounding occurs', () => {
  const originalCrore = '2.5';
  const rupeeText = convert(originalCrore, 'rupee'); // 2.5 * 1e7 = 25,000,000 exactly, no rounding
  const rupeeDigits = rupeeText.replace(/,/g, '');
  const backToScaledCrore = BigInt(rupeeDigits) * 10n ** BigInt(SCALE) / 10000000n;
  assert.equal(backToScaledCrore.toString(), toScaledBigInt(originalCrore).toString());
});

test('formatUnits groups western (3-digit) for crore/lakh and Indian (3-then-2) for rupee', () => {
  assert.equal(formatUnits(123456789n, 2, 'crore'), '1,234,567.89');
  assert.equal(formatUnits(1234500n, 0, 'rupee'), '12,34,500');
  assert.equal(formatUnits(500n, 0, 'rupee'), '500');
  assert.equal(formatUnits(1000n, 0, 'rupee'), '1,000');
});

test('formatAmount mirrors DetailsFormat.Money()\'s "₹{v:N2} Cr" shape for crore, and the equivalents for lakh/rupee', () => {
  assert.equal(formatAmount('12.5', 'crore'), '₹12.50 Cr');
  assert.equal(formatAmount('12.5', 'lakh'), '₹1,250.00 L');
  assert.equal(formatAmount('12.5', 'rupee'), '₹12,50,00,000');
});

test('unitLabelText returns the right text for every unit/variant pair', () => {
  assert.equal(unitLabelText('crore', 'long'), '₹ Crore');
  assert.equal(unitLabelText('lakh', 'long'), '₹ Lakh');
  assert.equal(unitLabelText('rupee', 'long'), '₹');
  assert.equal(unitLabelText('crore', 'suffix'), 'Cr');
  assert.equal(unitLabelText('lakh', 'suffix'), 'L');
  assert.equal(unitLabelText('rupee', 'suffix'), '₹');
  assert.equal(unitLabelText('crore', 'suffix-symbol'), '₹ Cr');
  assert.equal(unitLabelText('lakh', 'suffix-symbol'), '₹ L');
  assert.equal(unitLabelText('rupee', 'suffix-symbol'), '₹');
});

test('unitLabelText and convert throw on an unknown unit', () => {
  assert.throws(() => unitLabelText('dollar', 'long'));
  assert.throws(() => convert('1', 'dollar'));
});

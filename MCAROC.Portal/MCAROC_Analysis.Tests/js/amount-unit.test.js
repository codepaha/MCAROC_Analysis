import test from 'node:test';
import assert from 'node:assert/strict';
import { SCALE } from '../../MCAROC_Analysis/wwwroot/js/amount-unit-core.js';
import { planUnitSwitch, applyPlan, switchUnit, handleBeforePrint, handleAfterPrint } from '../../MCAROC_Analysis/wwwroot/js/amount-unit.js';

// A value with one fractional digit more than amount-unit-core.js's precision guard supports —
// toScaledBigInt throws on this, which is exactly the failure mode the atomic-switch guarantee exists
// to contain (see the review that prompted this file: a per-element try/catch that only logged to the
// console left one amount showing the old unit while its label already showed the new one).
const OVER_SCALE_VALUE = `1.${'1'.repeat(SCALE + 1)}`;

function mockAmountEl(value, initialText) {
  return {
    getAttribute: (name) => (name === 'data-amount-crore' ? value : null),
    textContent: initialText,
  };
}

function mockLabelEl(variant, initialText) {
  return {
    getAttribute: (name) => (name === 'data-amount-unit-label' ? variant : null),
    textContent: initialText,
  };
}

function mockRadioEl(value, checked = false) {
  return {
    value,
    checked,
    setAttribute(k, v) { this[k] = v; },
    getAttribute(k) { return this[k]; }
  };
}

function mockDoc(amountEls, labelEls, radios = []) {
  return {
    querySelectorAll(selector) {
      if (selector === '[data-amount-crore]') return amountEls;
      if (selector === '[data-amount-unit-label]') return labelEls;
      if (selector.includes('input[name="mca-amount-unit"]')) return radios;
      throw new Error(`Unexpected selector in test double: ${selector}`);
    },
    querySelector(selector) {
      if (selector === 'input[name="mca-amount-unit"]:checked') {
        return radios.find(r => r.checked) || null;
      }
      const match = selector.match(/input\[name="mca-amount-unit"\]\[value="([^"]+)"\]/);
      if (match) {
        return radios.find(r => r.value === match[1]) || null;
      }
      return null;
    }
  };
}

test('planUnitSwitch throws before computing anything is mutated when one amount cannot be converted', () => {
  const good = mockAmountEl('12.5', '₹12.50 Cr');
  const bad = mockAmountEl(OVER_SCALE_VALUE, '₹0.00 Cr');

  assert.throws(() => planUnitSwitch([good, bad], [], 'lakh'));
  // planUnitSwitch never assigns textContent itself — this just re-confirms the contract by construction.
  assert.equal(good.textContent, '₹12.50 Cr');
  assert.equal(bad.textContent, '₹0.00 Cr');
});

test('applyPlan commits every computed update', () => {
  const el = mockAmountEl('12.5', 'stale');
  const plan = planUnitSwitch([el], [], 'crore');
  applyPlan(plan);
  assert.equal(el.textContent, '₹12.50 Cr');
});

test('switchUnit succeeds and updates every amount and label when all values convert', () => {
  const amount1 = mockAmountEl('12.5', '₹12.50 Cr');
  const amount2 = mockAmountEl('-3.2', '₹-3.20 Cr');
  const label = mockLabelEl('long', '₹ Crore');
  const doc = mockDoc([amount1, amount2], [label]);

  const result = switchUnit(doc, 'lakh');

  assert.equal(result.ok, true);
  assert.equal(amount1.textContent, '₹1,250.00 L');
  assert.equal(amount2.textContent, '₹-320.00 L');
  assert.equal(label.textContent, '₹ Lakh');
});

test('switchUnit leaves every amount AND every label completely untouched when one amount fails — never a mixed-unit page', () => {
  const goodAmount = mockAmountEl('12.5', '₹12.50 Cr');
  const badAmount = mockAmountEl(OVER_SCALE_VALUE, '₹0.00 Cr');
  const label = mockLabelEl('suffix', 'Cr');
  const doc = mockDoc([goodAmount, badAmount], [label]);

  const result = switchUnit(doc, 'lakh');

  assert.equal(result.ok, false);
  assert.ok(result.error instanceof Error);
  // Not just the failing element — the GOOD amount and the unrelated LABEL must also stay exactly as
  // they were, so the page never shows "₹12.50 Cr" next to a header that already says "₹ Lakh".
  assert.equal(goodAmount.textContent, '₹12.50 Cr');
  assert.equal(badAmount.textContent, '₹0.00 Cr');
  assert.equal(label.textContent, 'Cr');
});

test('switchUnit leaves everything untouched when a label variant is unknown, even if every amount is fine', () => {
  const amount = mockAmountEl('12.5', '₹12.50 Cr');
  const badLabel = mockLabelEl('not-a-real-variant', 'Cr');
  const doc = mockDoc([amount], [badLabel]);

  const result = switchUnit(doc, 'lakh');

  assert.equal(result.ok, false);
  assert.equal(amount.textContent, '₹12.50 Cr');
  assert.equal(badLabel.textContent, 'Cr');
});

test('handleBeforePrint snapshots active unit, switches to crore, and handleAfterPrint restores unit', () => {
  const amount = mockAmountEl('12.5', '₹1,250.00 L');
  const label = mockLabelEl('long', '₹ Lakh');
  const radioCrore = mockRadioEl('crore', false);
  const radioLakh = mockRadioEl('lakh', true);
  const radioRupee = mockRadioEl('rupee', false);
  const doc = mockDoc([amount], [label], [radioCrore, radioLakh, radioRupee]);

  // Trigger beforeprint
  handleBeforePrint(doc);

  assert.equal(amount.textContent, '₹12.50 Cr', 'Amount switched to canonical ₹ Crore for printing');
  assert.equal(label.textContent, '₹ Crore', 'Label switched to canonical ₹ Crore for printing');
  assert.equal(radioCrore.checked, true, 'Crore radio marked checked');

  // Trigger afterprint
  handleAfterPrint(doc);

  assert.equal(amount.textContent, '₹1,250.00 L', 'Amount restored to ₹ Lakh after printing');
  assert.equal(label.textContent, '₹ Lakh', 'Label restored to ₹ Lakh after printing');
  assert.equal(radioLakh.checked, true, 'Lakh radio restored to checked');
});

test('handleBeforePrint does nothing when unit is already crore', () => {
  const amount = mockAmountEl('12.5', '₹12.50 Cr');
  const label = mockLabelEl('long', '₹ Crore');
  const radioCrore = mockRadioEl('crore', true);
  const radioLakh = mockRadioEl('lakh', false);
  const doc = mockDoc([amount], [label], [radioCrore, radioLakh]);

  handleBeforePrint(doc);

  assert.equal(amount.textContent, '₹12.50 Cr');
  assert.equal(label.textContent, '₹ Crore');
  assert.equal(radioCrore.checked, true);

  handleAfterPrint(doc);

  assert.equal(amount.textContent, '₹12.50 Cr');
  assert.equal(label.textContent, '₹ Crore');
});


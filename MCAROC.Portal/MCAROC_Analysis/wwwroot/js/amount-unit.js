/**
 * DOM-facing half of the Crore/Lakh/₹ amount unit toggle (#123, C5b). Loaded only from
 * Requests/Details.cshtml — this control and its markers exist only on that page. All the actual
 * conversion math lives in amount-unit-core.js (pure, independently unit-tested).
 */
import { formatAmount, unitLabelText } from './amount-unit-core.js';

const STORAGE_KEY = 'mcaroc-amount-unit';
const RADIO_GROUP_NAME = 'mca-amount-unit';
const VALID_UNITS = ['crore', 'lakh', 'rupee'];

function loadUnit() {
  try {
    const stored = localStorage.getItem(STORAGE_KEY);
    return VALID_UNITS.includes(stored) ? stored : 'crore';
  } catch {
    return 'crore';
  }
}

function saveUnit(unit) {
  try {
    localStorage.setItem(STORAGE_KEY, unit);
  } catch {
    // Private browsing / blocked storage — the toggle still works for this page view, it just
    // won't persist across a reload.
  }
}

function applyUnit(unit) {
  document.querySelectorAll('[data-amount-crore]').forEach((el) => {
    try {
      el.textContent = formatAmount(el.getAttribute('data-amount-crore'), unit);
    } catch (err) {
      console.error('amount-unit: failed to convert an amount', err);
    }
  });

  document.querySelectorAll('[data-amount-unit-label]').forEach((el) => {
    try {
      el.textContent = unitLabelText(unit, el.getAttribute('data-amount-unit-label'));
    } catch (err) {
      console.error('amount-unit: failed to set a unit label', err);
    }
  });
}

function init() {
  const radios = document.querySelectorAll(`input[name="${RADIO_GROUP_NAME}"]`);
  if (radios.length === 0) {
    return;
  }

  const unit = loadUnit();
  const matching = document.querySelector(`input[name="${RADIO_GROUP_NAME}"][value="${unit}"]`);
  if (matching) {
    matching.checked = true;
  }
  applyUnit(unit);

  radios.forEach((radio) => {
    radio.addEventListener('change', () => {
      if (!radio.checked) return;
      saveUnit(radio.value);
      applyUnit(radio.value);
    });
  });
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', init);
} else {
  init();
}

/**
 * DOM-facing half of the Crore/Lakh/₹ amount unit toggle (#123, C5b). Loaded only from
 * Requests/Details.cshtml — this control and its markers exist only on that page. All the actual
 * conversion math lives in amount-unit-core.js (pure, independently unit-tested).
 *
 * Switching units is deliberately atomic: every amount and every unit-label is *planned* (converted
 * to its new text) before anything touches the DOM. If a single value can't be converted — e.g. a
 * value with more fractional digits than amount-unit-core.js's precision guard supports — the whole
 * switch is aborted and nothing changes, rather than leaving some amounts in the old unit while their
 * surrounding captions/headers already say the new one. A page silently showing "₹12.50 Cr" next to a
 * header that says "₹ Lakh" is a worse failure mode than refusing the switch outright.
 */
import { formatAmount, unitLabelText } from './amount-unit-core.js';

export const STORAGE_KEY = 'mcaroc-amount-unit';
export const RADIO_GROUP_NAME = 'mca-amount-unit';
const VALID_UNITS = ['crore', 'lakh', 'rupee'];

export function loadUnit(storage = safeLocalStorage()) {
  try {
    const stored = storage?.getItem(STORAGE_KEY);
    return VALID_UNITS.includes(stored) ? stored : 'crore';
  } catch {
    return 'crore';
  }
}

export function saveUnit(unit, storage = safeLocalStorage()) {
  try {
    storage?.setItem(STORAGE_KEY, unit);
  } catch {
    // Private browsing / blocked storage — the toggle still works for this page view, it just
    // won't persist across a reload.
  }
}

function safeLocalStorage() {
  try {
    return typeof localStorage === 'undefined' ? null : localStorage;
  } catch {
    return null;
  }
}

/**
 * Computes the new text for every amount and label element for `unit` — pure, no DOM mutation.
 * Throws (before returning anything) if a single element's value can't be converted, so a caller can
 * never apply a partial plan.
 * @param {{getAttribute:(name:string)=>string|null}[]} amountEls elements carrying data-amount-crore
 * @param {{getAttribute:(name:string)=>string|null}[]} labelEls elements carrying data-amount-unit-label
 * @param {'crore'|'lakh'|'rupee'} unit
 */
export function planUnitSwitch(amountEls, labelEls, unit) {
  const amountUpdates = amountEls.map((el) => ({
    el,
    text: formatAmount(el.getAttribute('data-amount-crore'), unit),
  }));
  const labelUpdates = labelEls.map((el) => ({
    el,
    text: unitLabelText(unit, el.getAttribute('data-amount-unit-label')),
  }));
  return { amountUpdates, labelUpdates };
}

/** Commits an already-computed plan to the DOM. Assumes planUnitSwitch has already succeeded. */
export function applyPlan(plan) {
  plan.amountUpdates.forEach(({ el, text }) => { el.textContent = text; });
  plan.labelUpdates.forEach(({ el, text }) => { el.textContent = text; });
}

/**
 * Switches every `[data-amount-crore]` / `[data-amount-unit-label]` element under `doc` to `unit`,
 * atomically. Returns `{ ok: true }` on success (the DOM has been updated) or `{ ok: false, error }`
 * on failure (the DOM was NOT touched at all — every element still shows whatever it showed before
 * this call).
 * @param {Document} doc
 * @param {'crore'|'lakh'|'rupee'} unit
 */
export function switchUnit(doc, unit) {
  const amountEls = Array.from(doc.querySelectorAll('[data-amount-crore]'));
  const labelEls = Array.from(doc.querySelectorAll('[data-amount-unit-label]'));
  try {
    const plan = planUnitSwitch(amountEls, labelEls, unit);
    applyPlan(plan);
    return { ok: true };
  } catch (error) {
    return { ok: false, error };
  }
}

function errorBanner(doc) {
  let el = doc.getElementById('mca-amount-unit-error');
  if (el) return el;

  el = doc.createElement('div');
  el.id = 'mca-amount-unit-error';
  el.className = 'mca-amount-unit-error';
  el.setAttribute('role', 'alert');
  el.hidden = true;

  const toggle = doc.querySelector(`input[name="${RADIO_GROUP_NAME}"]`)?.closest('.mca-unit-toggle');
  if (toggle?.parentNode) {
    toggle.insertAdjacentElement('afterend', el);
  } else {
    doc.body.prepend(el);
  }
  return el;
}

function showSwitchError(doc) {
  const el = errorBanner(doc);
  el.textContent = 'Could not switch the amount unit — one or more values could not be converted. The previously selected unit is still shown.';
  el.hidden = false;
}

function clearSwitchError(doc) {
  const el = doc.getElementById('mca-amount-unit-error');
  if (el) el.hidden = true;
}

function checkRadio(doc, unit) {
  const radio = doc.querySelector(`input[name="${RADIO_GROUP_NAME}"][value="${unit}"]`);
  if (radio) radio.checked = true;
}

function init() {
  const radios = document.querySelectorAll(`input[name="${RADIO_GROUP_NAME}"]`);
  if (radios.length === 0) {
    return;
  }

  const persisted = loadUnit();
  checkRadio(document, persisted);

  if (persisted !== 'crore') {
    // The server always renders Crore text; only actually re-render when a different unit was
    // persisted from an earlier visit.
    const result = switchUnit(document, persisted);
    if (!result.ok) {
      console.error('amount-unit: could not restore the persisted unit', result.error);
      checkRadio(document, 'crore');
      saveUnit('crore');
    }
  }

  radios.forEach((radio) => {
    radio.addEventListener('change', () => {
      if (!radio.checked) return;

      const previous = loadUnit();
      const result = switchUnit(document, radio.value);
      if (result.ok) {
        saveUnit(radio.value);
        clearSwitchError(document);
      } else {
        console.error('amount-unit: refusing to switch — a value could not be converted', result.error);
        checkRadio(document, previous);
        showSwitchError(document);
      }
    });
  });
}

let prePrintUnit = null;

export function handleBeforePrint(doc = (typeof document !== 'undefined' ? document : null)) {
  if (!doc) return;
  const checkedRadio = doc.querySelector?.(`input[name="${RADIO_GROUP_NAME}"]:checked`);
  prePrintUnit = checkedRadio?.value || 'crore';
  if (prePrintUnit !== 'crore') {
    switchUnit(doc, 'crore');
    checkRadio(doc, 'crore');
  }
}

export function handleAfterPrint(doc = (typeof document !== 'undefined' ? document : null)) {
  if (!doc) return;
  const unitToRestore = prePrintUnit;
  prePrintUnit = null;
  if (unitToRestore && unitToRestore !== 'crore') {
    switchUnit(doc, unitToRestore);
    checkRadio(doc, unitToRestore);
  }
}

if (typeof document !== 'undefined') {
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
}

if (typeof window !== 'undefined') {
  window.addEventListener('beforeprint', () => handleBeforePrint(typeof document !== 'undefined' ? document : null));
  window.addEventListener('afterprint', () => handleAfterPrint(typeof document !== 'undefined' ? document : null));
}

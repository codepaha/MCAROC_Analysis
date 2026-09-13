/**
 * Pure, exact-decimal Crore/Lakh/₹ conversion for the amount unit toggle (#123, C5b).
 * No `Number`/`parseFloat`/`toLocaleString` anywhere in this module — every step is either plain
 * string parsing or BigInt arithmetic, so a monetary value can never round-trip through a float.
 * Designed for both browser runtime and Node.js testing (mirrors pdf-viewer-core.js's split).
 */

/** Internal fixed-point scale: how many fractional digits a crore value is held at before any unit
 *  conversion. 12 is generous headroom over any realistic decimal(18,2)/decimal(18,4) money column
 *  today — this is a precision *guard*, not a truncation point: see toScaledBigInt below. */
export const SCALE = 12;

/** Exact integer multipliers — Crore→Lakh and Crore→Rupee are both exact powers of ten, so this
 *  multiplication never loses precision. */
const UNIT_FACTORS = { crore: 1n, lakh: 100n, rupee: 10000000n };

/** Display decimals per unit — whole rupees are never shown with paise. */
const UNIT_DECIMALS = { crore: 2, lakh: 2, rupee: 0 };

const UNIT_LABELS = {
  crore: { long: '₹ Crore', suffix: 'Cr', 'suffix-symbol': '₹ Cr' },
  lakh: { long: '₹ Lakh', suffix: 'L', 'suffix-symbol': '₹ L' },
  rupee: { long: '₹', suffix: '₹', 'suffix-symbol': '₹' },
};

const DECIMAL_STRING = /^(-?)(\d+)(?:\.(\d+))?$/;

/**
 * Parses a plain `-?\d+(\.\d+)?` decimal string (exactly what
 * `decimal.ToString(CultureInfo.InvariantCulture)` produces) into a BigInt at the fixed internal
 * scale above — string parsing only, never `Number()`/`parseFloat`.
 * @param {string} decimalString
 * @returns {bigint} the value scaled by 10^SCALE
 * @throws if the string isn't a plain invariant decimal, or has more than SCALE fractional digits
 *   (thrown, never silently truncated — a value that hits this needs a wider SCALE, not quiet data loss).
 */
export function toScaledBigInt(decimalString) {
  if (typeof decimalString !== 'string') {
    throw new TypeError(`toScaledBigInt expects a string, got ${typeof decimalString}`);
  }
  const match = DECIMAL_STRING.exec(decimalString);
  if (!match) {
    throw new Error(`Not a plain invariant decimal string: "${decimalString}"`);
  }
  const [, sign, intDigits, fracDigits = ''] = match;
  if (fracDigits.length > SCALE) {
    throw new Error(
      `"${decimalString}" has ${fracDigits.length} fractional digits, more than the supported ${SCALE} — refusing to silently truncate precision.`
    );
  }
  const magnitude = BigInt(intDigits + fracDigits.padEnd(SCALE, '0'));
  return sign === '-' ? -magnitude : magnitude;
}

/** Rounds |num| / |den| to the nearest integer, half away from zero, then re-applies num's sign. */
function divideRoundHalfAwayFromZero(num, den) {
  if (num === 0n) return 0n;
  const negative = num < 0n;
  const absNum = negative ? -num : num;
  const quotient = absNum / den;
  const remainder = absNum % den;
  const rounded = remainder * 2n >= den ? quotient + 1n : quotient;
  return negative ? -rounded : rounded;
}

function groupWestern(digits) {
  return digits.replace(/\B(?=(\d{3})+(?!\d))/g, ',');
}

/** Indian digit grouping: the last 3 digits form one group, then groups of 2 to the left. */
function groupIndian(digits) {
  if (digits.length <= 3) return digits;
  const last3 = digits.slice(-3);
  const rest = digits.slice(0, -3).replace(/\B(?=(\d{2})+(?!\d))/g, ',');
  return `${rest},${last3}`;
}

/**
 * Renders a BigInt already scaled to `decimals` fractional digits as a grouped, signed string.
 * Pure string/BigInt operations only.
 * @param {bigint} unitsBigInt value * 10^decimals
 * @param {number} decimals
 * @param {'crore'|'lakh'|'rupee'} unit western grouping for crore/lakh, Indian for rupee
 */
export function formatUnits(unitsBigInt, decimals, unit) {
  const negative = unitsBigInt < 0n;
  const abs = negative ? -unitsBigInt : unitsBigInt;
  const scale = 10n ** BigInt(decimals);
  const intPart = abs / scale;
  const fracPart = abs % scale;
  const grouped = unit === 'rupee' ? groupIndian(intPart.toString()) : groupWestern(intPart.toString());
  const fracStr = decimals > 0 ? `.${fracPart.toString().padStart(decimals, '0')}` : '';
  return `${negative ? '-' : ''}${grouped}${fracStr}`;
}

/**
 * Converts a crore-denominated decimal string to the target unit's grouped numeric text (no ₹/Cr/L
 * decoration — see formatAmount for the full display string). Exact for the multiply step; the only
 * rounding is the single, named round-half-away-from-zero applied once at the unit's own display
 * decimals.
 * @param {string} croreDecimalString
 * @param {'crore'|'lakh'|'rupee'} unit
 */
export function convert(croreDecimalString, unit) {
  const factor = UNIT_FACTORS[unit];
  if (factor === undefined) {
    throw new Error(`Unknown amount unit: "${unit}"`);
  }
  const scaled = toScaledBigInt(croreDecimalString);
  const convertedScaled = scaled * factor; // exact: factor is an integer power of ten
  const decimals = UNIT_DECIMALS[unit];
  const divisor = 10n ** BigInt(SCALE - decimals);
  const units = divideRoundHalfAwayFromZero(convertedScaled, divisor);
  return formatUnits(units, decimals, unit);
}

/**
 * The full display string for a `[data-amount-crore]` element's text — mirrors
 * `DetailsFormat.Money()`'s "₹{v:N2} Cr" shape, generalized across all three units.
 * @param {string} croreDecimalString
 * @param {'crore'|'lakh'|'rupee'} unit
 */
export function formatAmount(croreDecimalString, unit) {
  const number = convert(croreDecimalString, unit);
  const suffix = unit === 'crore' ? ' Cr' : unit === 'lakh' ? ' L' : '';
  return `₹${number}${suffix}`;
}

/**
 * The text for a `[data-amount-unit-label]` span in the given unit.
 * @param {'crore'|'lakh'|'rupee'} unit
 * @param {'long'|'suffix'|'suffix-symbol'} variant
 */
export function unitLabelText(unit, variant) {
  const forUnit = UNIT_LABELS[unit];
  if (!forUnit) {
    throw new Error(`Unknown amount unit: "${unit}"`);
  }
  const text = forUnit[variant];
  if (text === undefined) {
    throw new Error(`Unknown amount unit-label variant: "${variant}"`);
  }
  return text;
}

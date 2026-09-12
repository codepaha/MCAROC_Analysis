/**
 * Core pure functions for PDF viewing, fragment parsing, and page clamping.
 * Designed for both browser runtime and Node.js testing.
 */

/**
 * Parses a URL hash string (e.g. "#page=3" or "page=10") to extract a 1-based page number.
 * @param {string|null|undefined} hash 
 * @returns {number|null} 1-based integer page number, or null if missing/invalid.
 */
export function parsePageFragment(hash) {
  if (!hash || typeof hash !== 'string') {
    return null;
  }

  // Match #page=N or page=N (case-insensitive)
  const match = hash.match(/(?:^|[#&])page=(\d+)(?:&|$)/i);
  if (!match) {
    return null;
  }

  const parsed = parseInt(match[1], 10);
  if (isNaN(parsed) || parsed < 1) {
    return null;
  }

  return parsed;
}

/**
 * Clamps a requested page number to [1, totalPages].
 * @param {number|null|undefined} requestedPage 1-based page number requested
 * @param {number} totalPages Total number of pages in the PDF (>= 1)
 * @returns {{ page: number, isClamped: boolean, message: string|null }}
 */
export function clampPage(requestedPage, totalPages) {
  const safeTotal = (typeof totalPages === 'number' && totalPages > 0) ? Math.floor(totalPages) : 1;

  if (requestedPage === null || requestedPage === undefined || isNaN(requestedPage)) {
    return {
      page: 1,
      isClamped: false,
      message: null
    };
  }

  const intPage = Math.floor(requestedPage);

  if (intPage < 1) {
    return {
      page: 1,
      isClamped: true,
      message: `Requested page (${intPage}) is before page 1. Displaying page 1.`
    };
  }

  if (intPage > safeTotal) {
    return {
      page: safeTotal,
      isClamped: true,
      message: `Requested page (${intPage}) exceeds total pages (${safeTotal}). Displaying page ${safeTotal}.`
    };
  }

  return {
    page: intPage,
    isClamped: false,
    message: null
  };
}

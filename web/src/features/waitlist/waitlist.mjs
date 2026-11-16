// A waiting party's lines on the host screen (M11b, Design §17.3). Plain JavaScript with JSDoc types and no imports,
// so `npm test` (node --test) can test it directly.

/**
 * Whole minutes since the party was notified, on the business clock: `at` is the waitlist answer's "now" (the API's
 * business clock), never the browser's, so the demo clock moves it too. Never negative.
 * @param {string | null} notifiedAt when the "table ready" Notify was made (ISO instant), if any
 * @param {string} at the waitlist answer's business time (ISO instant)
 * @returns {number | null} null when the party hasn't been notified
 */
export const minutesSinceNotify = (notifiedAt, at) => {
  if (!notifiedAt) return null;
  const elapsed = Date.parse(at) - Date.parse(notifiedAt);
  return Number.isNaN(elapsed) ? null : Math.max(0, Math.floor(elapsed / 60_000));
};

/**
 * The quote no longer applies once the party has been told their table is ready: the line reads "was quoted …".
 * @param {string} status the entry's status
 * @returns {boolean}
 */
export const quoteIsStale = (status) => status === "notified";

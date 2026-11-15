// The host's "Bill paid" total (AC6.6, Design §18.9). Plain JavaScript with JSDoc types and no imports, so
// `npm test` (node --test) can test it directly.

/** NZ$100,000.00, the API's limit (`total_cents_out_of_range`, Design §6.7). */
export const maxBillCents = 10_000_000;

/** The pre-filled total: a plausible NZ$45.00 a head, which the host can change. */
export const suggestedBillCents = (/** @type {number} */ partySize) =>
  Math.max(1, partySize) * 4_500;

/** Cents as the field shows them: `9000` → `"90.00"`. */
export const centsToField = (/** @type {number} */ cents) => (cents / 100).toFixed(2);

/**
 * The typed total in whole cents, or why not: `"186"` → 18600, `"186.5"` → 18650, `"1,234.50"` → 123450.
 * Digits with at most two decimals; more than 0 and at most NZ$100,000.00.
 * @param {string} value
 * @returns {{ cents: number } | { error: "required" | "invalid" | "tooSmall" | "tooLarge" }}
 */
export const parseBillTotal = (value) => {
  const typed = value
    .trim()
    .replace(/^NZ\$|^\$/i, "")
    .replaceAll(",", "")
    .trim();
  if (typed === "") return { error: "required" };
  const match = /^(\d+)(?:\.(\d{1,2}))?$/.exec(typed);
  if (!match) return { error: "invalid" };
  const cents = Number(match[1]) * 100 + Number((match[2] ?? "").padEnd(2, "0"));
  if (cents <= 0) return { error: "tooSmall" };
  if (cents > maxBillCents) return { error: "tooLarge" };
  return { cents };
};

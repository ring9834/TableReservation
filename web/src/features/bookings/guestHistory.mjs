// The guest's history on a booking's details (AC6.6, Design §18.9). Plain JavaScript with JSDoc types and no
// imports, so `npm test` (node --test) can test it directly.

/** The English ordinal forms; each has a label under `host.history.nthVisit` in both languages. */
export const ordinalForms = /** @type {const} */ (["one", "two", "few", "other"]);

/**
 * What the history line says. `visitCount` counts completed visits, so a booking still to come or seated is visit
 * `visitCount + 1`: 0 → "First visit", 2 → "3rd visit". A completed booking was already counted, so it shows the
 * count instead ("2 visits"). `null` (no guest) shows nothing.
 * @param {number | null | undefined} visitCount
 * @param {string} bookingStatus the API's `BookingStatus`
 * @param {string} locale an `Intl` locale, e.g. `en-NZ` or `zh-CN`
 * @returns {null | { kind: "first" } | { kind: "nth", n: number, form: (typeof ordinalForms)[number] } | { kind: "count", n: number }}
 */
export const visitLine = (visitCount, bookingStatus, locale) => {
  if (visitCount === null || visitCount === undefined) return null;
  if (bookingStatus === "completed") return { kind: "count", n: visitCount };
  if (visitCount === 0) return { kind: "first" };
  const n = visitCount + 1;
  const form = /** @type {(typeof ordinalForms)[number]} */ (
    new Intl.PluralRules(locale, { type: "ordinal" }).select(n)
  );
  return { kind: "nth", n, form: ordinalForms.includes(form) ? form : "other" };
};

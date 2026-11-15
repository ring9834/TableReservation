// "Send reminders now" on the presenter screen (M10b, AC5.1, BR-13, Design §7.3, §18.10). Plain JavaScript with JSDoc
// types and no imports, so `npm test` (node --test) can test it directly.

/**
 * Every reason the API gives for skipping a booking (`ReminderEligibility` without `eligible`); each has a
 * count-neutral label under `presenter.reminders.reason` ("3 starting within 2 hours"), so both languages share keys.
 */
export const skipReasons = /** @type {const} */ ([
  "alreadyReminded",
  "alreadyConfirmed",
  "notBooked",
  "noGuest",
  "bookedLessThan24hAhead",
  "startsWithin2h",
]);

/**
 * What the status line says after a press: how many were sent (`none`, `one` or `many`) and the skipped counts in
 * the API's order, reasons the page doesn't know left out.
 * @param {{ sent: number, skipped: { reason: string, count: number }[] }} response
 * @returns {{ sent: "none" | "one" | "many", count: number, skippedTotal: number, skipped: { reason: (typeof skipReasons)[number], count: number }[] }}
 */
export const reminderSummary = (response) => {
  const skipped = /** @type {{ reason: (typeof skipReasons)[number], count: number }[]} */ (
    response.skipped.filter(
      (s) => s.count > 0 && /** @type {readonly string[]} */ (skipReasons).includes(s.reason),
    )
  );
  return {
    sent: response.sent === 0 ? "none" : response.sent === 1 ? "one" : "many",
    count: response.sent,
    skippedTotal: skipped.reduce((total, s) => total + s.count, 0),
    skipped,
  };
};

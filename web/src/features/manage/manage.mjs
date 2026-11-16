// The guest's manage page (M10b, Requirements step 5, BR-07, BR-13, Design §7.3, §18.10). Plain JavaScript with JSDoc
// types and no imports, so `npm test` (node --test) can test it directly. Every rule reads the API's answer; the page
// never works out a refund itself.

/** Guest-facing status keys under `manage.status`; `locales.test.mjs` checks each has a label in both languages. */
export const statusKeys = /** @type {const} */ ([
  "pendingPayment",
  "booked",
  "confirmed",
  "seated",
  "completed",
  "cancelled",
]);

/** What cancelling now does to the deposit; each has a sentence under `manage.consequence`. */
export const consequenceKeys = /** @type {const} */ (["refundFull", "kept", "noDeposit"]);

/** Whole-page link problems; each has a message under `errors`. */
export const linkErrorCodes = /** @type {const} */ (["invalid_link", "link_expired"]);

/**
 * @typedef {{
 *   status: string,
 *   deposit: null | { amount: { amountCents: number, currency: string }, status: string | null },
 *   cancellingNowKeepsDeposit: boolean,
 *   refundIfCancelledNow: string | null,
 *   allowedActions: string[],
 * }} GuestBooking
 */

/**
 * The buttons to show: only what the API allows with this link now, Confirm before Cancel.
 * @param {GuestBooking} booking
 * @returns {("confirm" | "cancel")[]}
 */
export const visibleActions = (booking) =>
  /** @type {const} */ (["confirm", "cancel"]).filter((action) =>
    booking.allowedActions.includes(action),
  );

/**
 * What the second Cancel step says will happen to the deposit (BR-07, BR-13), from the API's answer:
 * - a paid deposit that cancelling now refunds in full → `refundFull` ("Your NZ$120.00 deposit will be refunded in full");
 * - a paid deposit inside the free-cancellation window (or terms that never refund) → `kept` ("Cancelling now keeps
 *   your deposit of NZ$120.00");
 * - nothing paid (no deposit, or still awaiting payment) → `noDeposit`.
 * @param {GuestBooking} booking
 * @returns {{ key: (typeof consequenceKeys)[number], amountCents?: number, currency?: string }}
 */
export const cancelConsequence = (booking) => {
  const deposit = booking.deposit;
  if (!deposit || deposit.status !== "captured") return { key: "noDeposit" };
  const money = { amountCents: deposit.amount.amountCents, currency: deposit.amount.currency };
  if (!booking.cancellingNowKeepsDeposit && booking.refundIfCancelledNow === "full") {
    return { key: "refundFull", ...money };
  }
  return { key: "kept", ...money };
};

/** Statuses a booking can still be cancelled from (Design §3.1); after its start the link has expired anyway. */
const cancellableStatuses = ["pendingPayment", "booked", "confirmed"];

/**
 * The cancellation terms ("Can't make it? Please cancel by 6:30 pm.") only while the booking can still be cancelled
 * (M11a): not once it is cancelled, seated or completed. Read from the status, not the link's actions, so a Confirm
 * link still shows the terms the guest agreed to.
 * @param {{ status: string }} booking
 */
export const showsTerms = (booking) => cancellableStatuses.includes(booking.status);

/**
 * The late-cancellation warning shown before any tap (BR-13): only while cancelling is still possible.
 * @param {GuestBooking} booking
 */
export const showsLateWarning = (booking) =>
  booking.cancellingNowKeepsDeposit && booking.allowedActions.includes("cancel");

/**
 * The lines after a successful tap: Confirm → "Thanks, you're confirmed"; Cancel → "Your booking is cancelled", plus
 * "Your refund is on its way" when the API decided a full refund.
 * @param {"confirm" | "cancel"} action
 * @param {{ status: string, refundDecision: string | null }} response
 * @returns {string[]} keys under `manage.result`
 */
export const resultKeys = (action, response) => {
  if (action === "confirm") return ["confirmed"];
  return response.refundDecision === "full" ? ["cancelled", "refundOnItsWay"] : ["cancelled"];
};

/**
 * How the page treats an API error code:
 * - `invalid_link` / `link_expired` → `link`: the whole page is that message, no booking;
 * - `invalid_transition` / `version_conflict` on a tap → `changed`: show the booking's current status, no buttons;
 * - anything else → `other`: the shared message for the code, buttons stay.
 * @param {string} code
 * @returns {"link" | "changed" | "other"}
 */
export const errorKind = (code) => {
  if (/** @type {readonly string[]} */ (linkErrorCodes).includes(code)) return "link";
  if (code === "invalid_transition" || code === "version_conflict") return "changed";
  return "other";
};

/**
 * The page's language for the booking's (`en` / `zh`).
 * @param {string | null | undefined} language
 * @returns {"en" | "zh-CN"}
 */
export const pageLanguage = (language) => (language === "zh" ? "zh-CN" : "en");

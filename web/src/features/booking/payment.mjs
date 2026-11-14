// The guest payment step's rules (M8b, Design §18.8) and the host's deposit wording. Plain JavaScript with JSDoc types
// and no imports, so `npm test` (node --test) can test it directly; TypeScript reads the types through `allowJs`.

/** How often the payment step asks the API for the booking's state (ADR-002: polling, every 2 s). */
export const pollIntervalMs = 2000;

/** How long after paying the page waits for `booked` before saying it is still confirming. */
export const stillConfirmingAfterMs = 60_000;

/** The moments (seconds left) the countdown is announced to screen readers: 5 min, 1 min and the end. */
export const announceAt = /** @type {const} */ ([300, 60, 0]);

/**
 * "9:41", "0:05", "10:00": minutes and seconds left. Never a clock time: the demo's story time differs from real time.
 * @param {number} seconds whole seconds left (negative reads as 0)
 */
export function formatCountdown(seconds) {
  const total = Math.max(0, Math.ceil(seconds));
  const minutes = Math.floor(total / 60);
  const rest = total % 60;
  return `${minutes}:${String(rest).padStart(2, "0")}`;
}

/**
 * Seconds left now, from the server's last answer and how long ago it came (so the guest's own clock never matters,
 * only the time that has passed since).
 * @param {number} serverSeconds `holdSecondsRemaining` from the last status poll
 * @param {number} elapsedMs milliseconds since that answer arrived
 */
export function secondsLeft(serverSeconds, elapsedMs) {
  return Math.max(0, Math.ceil(serverSeconds - Math.max(0, elapsedMs) / 1000));
}

/**
 * The announcement mark crossed going from `previous` to `next` seconds left, or `null`. A first reading (previous
 * `null`) announces nothing except an already ended hold, so opening the page doesn't announce "5 minutes left".
 * @param {number | null} previous
 * @param {number} next
 * @returns {(typeof announceAt)[number] | null}
 */
export function announcementFor(previous, next) {
  if (previous === null) {
    return next <= 0 ? 0 : null;
  }

  for (const mark of announceAt) {
    if (previous > mark && next <= mark) {
      return next <= 0 ? 0 : mark;
    }
  }

  return null;
}

/**
 * What the payment form shows. The form and its button disappear as soon as the hold ends, whatever else is going
 * on; while a payment is in flight the button is disabled and reads "Paying…", so a double tap can't pay twice.
 * @param {{ secondsLeft: number | null, paying: boolean, paid: boolean }} state `secondsLeft` null until the first poll
 * @returns {{ showForm: boolean, buttonDisabled: boolean, buttonLabel: "pay" | "paying" }}
 */
export function payControls({ secondsLeft: left, paying, paid }) {
  const holdEnded = left !== null && left <= 0;
  return {
    showForm: !holdEnded && !paid,
    buttonDisabled: holdEnded || paying || paid,
    buttonLabel: paying ? "paying" : "pay",
  };
}

/**
 * What the page shows for the latest status poll.
 * - `booked`: the deposit is paid (booked or anything after it).
 * - `holdEnded`: the hold ran out before payment (cancelled, `holdExpired`). After paying, the guest is told any
 *   payment will be refunded (the API refunds a late payment, Design §8.4).
 * - `cancelled`: cancelled for another reason.
 * - `stillConfirming`: paid, but not booked yet after `stillConfirmingAfterMs`.
 * - `waiting`: anything else.
 * @param {{ status: string, cancelReason?: string | null } | undefined} status the poll's answer
 * @param {number | null} paidForMs milliseconds since the guest paid, `null` before paying
 * @returns {"booked" | "holdEnded" | "cancelled" | "stillConfirming" | "waiting"}
 */
export function pollOutcome(status, paidForMs) {
  if (status) {
    if (["booked", "confirmed", "seated", "completed"].includes(status.status)) {
      return "booked";
    }

    if (status.status === "cancelled") {
      return status.cancelReason === "holdExpired" ? "holdEnded" : "cancelled";
    }
  }

  return paidForMs !== null && paidForMs >= stillConfirmingAfterMs ? "stillConfirming" : "waiting";
}

/** Every key under `deposit.status` the host screens can show; `locales.test.mjs` checks each has a label. */
export const depositLabelKeys = /** @type {const} */ ([
  "awaiting",
  "paid",
  "refundPending",
  "refunded",
  "kept",
  "released",
]);

/**
 * The host's word for a deposit (M8b): the API has only `pending`, `captured`, `refunded` and `released`, so a captured
 * deposit on a cancelled booking reads from the cancellation's refund decision: `full` → "Refund pending" until
 * `charge.refunded` makes it `refunded`; `none` → "Kept (cancelled late)".
 * @param {string | null | undefined} depositStatus
 * @param {string} bookingStatus
 * @param {string | null | undefined} refundDecision
 * @returns {(typeof depositLabelKeys)[number] | null} `null` when there is no deposit
 */
export function depositLabelKey(depositStatus, bookingStatus, refundDecision) {
  switch (depositStatus) {
    case "pending":
      return "awaiting";
    case "refunded":
      return "refunded";
    case "released":
      return "released";
    case "captured":
      if (bookingStatus !== "cancelled") {
        return "paid";
      }
      return refundDecision === "none" ? "kept" : "refundPending";
    default:
      return null;
  }
}

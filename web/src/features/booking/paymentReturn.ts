import type { CreateBookingResponse } from "@/api/model";

/**
 * Coming back to /book/ after Stripe sent the guest away for 3-D Secure (M8b, Design §18.8). Before paying, the page
 * keeps the booking summary (never the client secret) in session storage, so the confirmation can show the reference
 * afterwards. Storage may be missing or blocked: then the page polls the status without the summary.
 */
const storageKey = (bookingId: string) => `reservation.payment.${bookingId}`;

export type BookingSummary = Omit<CreateBookingResponse, "clientSecret">;

export function rememberBooking(booking: CreateBookingResponse) {
  try {
    // JSON leaves out undefined, so the client secret is never stored.
    const summary = { ...booking, clientSecret: undefined };
    sessionStorage.setItem(storageKey(booking.bookingId), JSON.stringify(summary));
  } catch {
    // Storage blocked (private window, previews): the return still works, without the reference.
  }
}

function recalledBooking(bookingId: string): BookingSummary | null {
  try {
    const stored = sessionStorage.getItem(storageKey(bookingId));
    const summary = stored ? (JSON.parse(stored) as BookingSummary) : null;
    return summary?.bookingId === bookingId ? summary : null;
  } catch {
    return null;
  }
}

export type PaymentReturn = {
  booking: CreateBookingResponse;
  /** Stripe's `redirect_status` was `failed`: the guest pays again with the same client secret. */
  failed: boolean;
};

/**
 * The booking the guest is coming back to, read from `?bookingId=…&payment_intent=…&redirect_status=…`, or `null` on
 * an ordinary visit. Clears the parameters from the address bar so a reload starts afresh.
 */
export function readPaymentReturn(): PaymentReturn | null {
  const params = new URLSearchParams(window.location.search);
  const bookingId = params.get("bookingId");
  if (!bookingId || !params.get("payment_intent")) {
    return null;
  }

  const failed = params.get("redirect_status") === "failed";
  const clientSecret = failed ? params.get("payment_intent_client_secret") : null;
  window.history.replaceState(null, "", window.location.pathname);

  const summary = recalledBooking(bookingId);
  const booking: CreateBookingResponse = summary
    ? { ...summary, clientSecret }
    : {
        bookingId,
        reference: "",
        status: "pendingPayment",
        version: 0,
        startsAt: "",
        endsAt: "",
        partySize: 0,
        tableIds: [],
        deposit: null,
        holdExpiresAt: null,
        clientSecret,
      };
  return { booking, failed };
}

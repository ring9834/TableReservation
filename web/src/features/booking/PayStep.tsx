"use client";

import { useEffect, useId, useState } from "react";
import { useTranslation } from "react-i18next";
import { useGetBookingStatus } from "@/api/endpoints/bookings/bookings";
import { useGetPaymentConfig } from "@/api/endpoints/payments/payments";
import type { CreateBookingResponse, VenueResponse } from "@/api/model";
import { venueId } from "@/lib/config";
import { formatMoney } from "@/lib/format";
import { useApiError } from "@/lib/useApiError";
import { Confirmation } from "./Confirmation";
import { Countdown } from "./Countdown";
import { FakePayment } from "./FakePayment";
import { partyLabel } from "./party";
import { pollIntervalMs, pollOutcome, secondsLeft as secondsLeftFrom } from "./payment.mjs";
import { rememberBooking } from "./paymentReturn";
import { StripePayment } from "./StripePayment";
import { useFocusHeading } from "./useFocusHeading";
import styles from "./booking.module.css";

type Props = Readonly<{
  venue: VenueResponse;
  booking: CreateBookingResponse;
  /** Back from Stripe's 3-D Secure page: the payment was sent, so go straight to polling. */
  returned: boolean;
  /** Back from Stripe with `redirect_status=failed`: pay again. */
  returnFailed: boolean;
  onStartAgain: () => void;
}>;

/**
 * The deposit step (M8b, AC1.5, Design §10.1, §18.8). Polls `getBookingStatus` every 2 s (ADR-002) from the start: the
 * answer's `holdSecondsRemaining` (the server's infrastructure clock) drives the countdown, which ticks locally in
 * between, so the guest's own clock never matters. The form is Stripe Elements or, with the Fake provider, a
 * "Simulate payment (test mode)" button. After paying, the page polls until `booked`; a hold that ran out first is
 * cancelled by the API and any payment refunded. A booking without a client secret (a replay of one no longer waiting,
 * or coming back from 3-D Secure) only polls.
 */
export function PayStep({ venue, booking, returned, returnFailed, onStartAgain }: Props) {
  const { t, i18n } = useTranslation();
  const messageFor = useApiError();
  const headingRef = useFocusHeading<HTMLHeadingElement>(true);
  const id = useId();
  const language = i18n.resolvedLanguage ?? "en";
  const canPay = booking.clientSecret !== null;
  const [paidAt, setPaidAt] = useState<number | null>(() =>
    returned && !returnFailed ? Date.now() : canPay ? null : Date.now(),
  );
  const [now, setNow] = useState(() => Date.now());

  const config = useGetPaymentConfig({ query: { staleTime: Infinity, enabled: canPay } });
  const status = useGetBookingStatus(venueId, booking.bookingId, {
    query: {
      refetchInterval: (query) => {
        const outcome = pollOutcome(query.state.data, null);
        return outcome === "booked" || outcome === "holdEnded" || outcome === "cancelled"
          ? false
          : pollIntervalMs;
      },
      refetchIntervalInBackground: true,
      staleTime: 0,
    },
  });

  // One tick a second for the countdown and the "still confirming" wait.
  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, []);

  const serverSeconds = status.data?.holdSecondsRemaining;
  const left =
    serverSeconds === null || serverSeconds === undefined
      ? null
      : secondsLeftFrom(serverSeconds, now - status.dataUpdatedAt);
  const outcome = pollOutcome(status.data, paidAt === null ? null : now - paidAt);
  const deposit = booking.deposit;
  const amount = deposit ? formatMoney(deposit.amountCents, deposit.currency, language) : "";

  if (outcome === "booked") {
    return <Confirmation venue={venue} booking={booking} paid onBookAnother={onStartAgain} />;
  }

  const ended = (message: string) => (
    <section aria-labelledby={`${id}-heading`} className={styles.step}>
      <h2 id={`${id}-heading`} ref={headingRef} tabIndex={-1}>
        {t("pay.holdEndedHeading")}
      </h2>
      <div role="alert" className={styles.alert} data-testid="hold-ended">
        <p>{message}</p>
      </div>
      <button type="button" className={styles.primary} onClick={onStartAgain}>
        {t("pay.startAgain")}
      </button>
    </section>
  );

  if (outcome === "holdEnded") {
    return ended(paidAt === null ? t("pay.holdEndedText") : t("pay.holdEndedAfterPayment"));
  }
  if (outcome === "cancelled") {
    return ended(t("pay.cancelledText"));
  }
  if (paidAt === null && left === 0) {
    return ended(t("pay.holdEndedText"));
  }

  if (paidAt !== null) {
    const stillConfirming = outcome === "stillConfirming";
    return (
      <section aria-labelledby={`${id}-heading`} className={styles.step}>
        <h2 id={`${id}-heading`} ref={headingRef} tabIndex={-1}>
          {t("pay.confirmingHeading")}
        </h2>
        <p role="status" aria-live="polite" data-testid="payment-confirming">
          {stillConfirming ? t("pay.stillConfirming") : t("pay.confirming")}
        </p>
        {stillConfirming && booking.reference && (
          <dl className={styles.details}>
            <div>
              <dt>{t("confirmation.reference")}</dt>
              <dd className={styles.reference} data-testid="booking-reference">
                {booking.reference}
              </dd>
            </div>
          </dl>
        )}
      </section>
    );
  }

  return (
    <section aria-labelledby={`${id}-heading`} className={styles.step} data-testid="pay-step">
      <h2 id={`${id}-heading`} ref={headingRef} tabIndex={-1}>
        {t("pay.heading")}
      </h2>
      <div className={styles.summary}>
        <p data-testid="deposit-summary">
          {t("pay.depositFor", { amount, party: partyLabel(t, booking.partySize) })}
        </p>
      </div>
      {left !== null && <Countdown seconds={left} />}
      <p className={styles.hint}>{t("pay.why")}</p>

      {config.isPending && (
        <p role="status" aria-live="polite">
          {t("common.loading")}
        </p>
      )}
      {config.isError && (
        <div role="alert" className={styles.alert}>
          <p>{messageFor(config.error)}</p>
          <button type="button" className={styles.secondary} onClick={() => void config.refetch()}>
            {t("common.retry")}
          </button>
        </div>
      )}
      {config.data?.provider === "fake" && (
        <FakePayment
          bookingId={booking.bookingId}
          secondsLeft={left}
          onPaid={() => setPaidAt(Date.now())}
        />
      )}
      {config.data?.provider === "stripe" && config.data.publishableKey && booking.clientSecret && (
        <StripePayment
          publishableKey={config.data.publishableKey}
          clientSecret={booking.clientSecret}
          bookingId={booking.bookingId}
          amount={amount}
          secondsLeft={left}
          initialError={returnFailed ? t("pay.returnFailed") : null}
          onBeforePay={() => rememberBooking(booking)}
          onPaid={() => setPaidAt(Date.now())}
        />
      )}
    </section>
  );
}

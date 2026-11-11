"use client";

import { useId } from "react";
import { useTranslation } from "react-i18next";
import type { CreateBookingResponse, VenueResponse } from "@/api/model";
import { formatInstant, formatInstantTime, formatMoney } from "@/lib/format";
import { partyLabel } from "./party";
import { useFocusHeading } from "./useFocusHeading";
import styles from "./booking.module.css";

type Props = Readonly<{
  venue: VenueResponse;
  booking: CreateBookingResponse;
  onBookAnother: () => void;
}>;

/**
 * The booking reference (AC1.6) and its details in venue time. A party of 6+ is `pendingPayment` with a deposit and
 * a hold (BR-06); the payment step arrives in M8, so this says it is coming next.
 */
export function Confirmation({ venue, booking, onBookAnother }: Props) {
  const { t, i18n } = useTranslation();
  const headingRef = useFocusHeading<HTMLHeadingElement>(true);
  const id = useId();
  const language = i18n.resolvedLanguage ?? "en";
  const deposit = booking.deposit;

  return (
    <section aria-labelledby={`${id}-heading`} className={styles.step}>
      <h2 id={`${id}-heading`} ref={headingRef} tabIndex={-1}>
        {deposit ? t("confirmation.headingPending") : t("confirmation.heading")}
      </h2>
      <dl className={styles.details} data-testid="booking-details">
        <div>
          <dt>{t("confirmation.reference")}</dt>
          <dd className={styles.reference} data-testid="booking-reference">
            {booking.reference}
          </dd>
        </div>
        <div>
          <dt>{t("confirmation.when")}</dt>
          <dd>{formatInstant(booking.startsAt, venue.timeZone, language)}</dd>
        </div>
        <div>
          <dt>{t("confirmation.party")}</dt>
          <dd>{partyLabel(t, booking.partySize)}</dd>
        </div>
      </dl>

      {deposit && (
        <div className={styles.notice}>
          <h3>{t("confirmation.depositHeading")}</h3>
          <p>
            {t("confirmation.depositText", {
              amount: formatMoney(deposit.amountCents, deposit.currency, language),
              time: booking.holdExpiresAt
                ? formatInstantTime(booking.holdExpiresAt, venue.timeZone, language)
                : "",
            })}
          </p>
          <p>{t("confirmation.paymentNext")}</p>
        </div>
      )}

      <button type="button" className={styles.secondary} onClick={onBookAnother}>
        {t("confirmation.bookAnother")}
      </button>
    </section>
  );
}

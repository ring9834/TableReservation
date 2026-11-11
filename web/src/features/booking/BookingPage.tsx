"use client";

import { useEffect } from "react";
import { useTranslation } from "react-i18next";
import { useGetVenue } from "@/api/endpoints/venues/venues";
import { LanguageSwitch } from "@/i18n/LanguageSwitch";
import { venueId } from "@/lib/config";
import { BookingFlow } from "./BookingFlow";
import { useApiError } from "./useApiError";
import styles from "./booking.module.css";

/** The page around the flow: language switch, heading, venue details from `getVenue` (Design §18.4). */
export function BookingPage() {
  const { t } = useTranslation();
  const messageFor = useApiError();
  const venue = useGetVenue(venueId, { query: { staleTime: 5 * 60_000 } });
  const venueName = venue.data?.name ?? "";

  useEffect(() => {
    if (venueName) {
      document.title = t("app.title", { venue: venueName });
    }
  }, [t, venueName]);

  return (
    <div className={styles.page}>
      <header className={styles.header}>
        <LanguageSwitch />
      </header>
      <main className={styles.main}>
        <h1>{t("book.title")}</h1>
        {venue.data && (
          <p className={styles.intro}>{t("book.intro", { venue: venue.data.name })}</p>
        )}
        {venue.isPending && (
          <p role="status" aria-live="polite">
            {t("common.loading")}
          </p>
        )}
        {venue.isError && (
          <div role="alert" className={styles.alert}>
            <p>{messageFor(venue.error)}</p>
            <button type="button" className={styles.secondary} onClick={() => void venue.refetch()}>
              {t("common.retry")}
            </button>
          </div>
        )}
        {venue.data && <BookingFlow venue={venue.data} />}
      </main>
    </div>
  );
}

"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useCallback, useEffect } from "react";
import { useTranslation } from "react-i18next";
import { useGetMessages } from "@/api/endpoints/messages/messages";
import { useGetVenue } from "@/api/endpoints/venues/venues";
import { useRedirectOnSignedOut } from "@/features/host/useRedirectOnSignedOut";
import { LanguageSwitch } from "@/i18n/LanguageSwitch";
import { isApiError } from "@/lib/apiFetch";
import { venueId } from "@/lib/config";
import { useApiError } from "@/lib/useApiError";
import { useVenueConnection } from "@/realtime/useVenueConnection";
import { PhonePanel } from "./PhonePanel";
import styles from "./presenter.module.css";

/** A host session may open `/presenter/`; the message log answers 403 (Design §17, §18.7). */
const isForbidden = (error: unknown) => isApiError(error) && error.status === 403;

/**
 * The presenter screen (Design §17, §18.7), for a laptop or projector: the phone panel on the left. The rest of the
 * page stays free for the POS log (M9) and the demo controls (M11). `messageLogChanged` refetches the log; a reconnect
 * refetches everything (Design §15).
 */
export function PresenterPage() {
  const { t } = useTranslation();
  const router = useRouter();
  const messageFor = useApiError();

  const venue = useGetVenue(venueId, { query: { staleTime: 5 * 60_000 } });
  const messages = useGetMessages(venueId);
  const signedOut = useRedirectOnSignedOut(messages.error);
  const forbidden = isForbidden(messages.error);

  const goToSignIn = useCallback(() => router.replace("/demo-login/"), [router]);
  const connection = useVenueConnection(venueId, goToSignIn);

  useEffect(() => {
    document.title = t("presenter.documentTitle", { venue: venue.data?.name ?? "" });
  }, [t, venue.data?.name]);

  if (signedOut) {
    return (
      <p role="status" className={styles.page}>
        {t("host.signedOut")}
      </p>
    );
  }

  return (
    <div className={styles.page}>
      <header className={styles.header}>
        <h1>{t("presenter.title", { venue: venue.data?.name ?? "" })}</h1>
        <LanguageSwitch />
      </header>

      {forbidden ? (
        <div role="alert" className={styles.forbidden} data-testid="presenter-only">
          <p>{t("presenter.presenterOnly")}</p>
          <p className={styles.links}>
            <Link href="/demo-login/">{t("presenter.signInAgain")}</Link>
            <Link href="/host/">{t("presenter.openHost")}</Link>
          </p>
        </div>
      ) : (
        <>
          <div role="status" aria-live="polite">
            {connection === "reconnecting" && (
              <p className={styles.banner} data-testid="connection-banner">
                {t("host.reconnecting")}
              </p>
            )}
          </div>
          <div className={styles.layout}>
            <PhonePanel
              log={messages.data}
              pending={messages.isPending}
              error={messages.error ? messageFor(messages.error) : null}
              venueName={venue.data?.name ?? ""}
            />
            {/* Kept free for the POS log (M9) and the demo controls (M11). */}
            <div className={styles.rest} />
          </div>
        </>
      )}
    </div>
  );
}

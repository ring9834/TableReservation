"use client";

import { useRouter } from "next/navigation";
import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useGetTonightBookings } from "@/api/endpoints/bookings/bookings";
import { useGetFloorPlan } from "@/api/endpoints/floor-plan/floor-plan";
import { useGetVenue } from "@/api/endpoints/venues/venues";
import { TonightList } from "@/features/bookings/TonightList";
import { FloorPlan } from "@/features/floor-plan/FloorPlan";
import { TableDetailsPanel } from "@/features/floor-plan/TableDetailsPanel";
import { ZoomPanContainer } from "@/features/floor-plan/ZoomPanContainer";
import { LanguageSwitch } from "@/i18n/LanguageSwitch";
import { venueId } from "@/lib/config";
import { useApiError } from "@/lib/useApiError";
import { useVenueConnection } from "@/realtime/useVenueConnection";
import { useRedirectOnSignedOut } from "./useRedirectOnSignedOut";
import styles from "./host.module.css";

/**
 * The host screen (Requirements step 2, Design §18): floor plan and tonight's list, kept live by SignalR notices.
 * Server state lives only in the query cache; the selected table is the only local state (Design §18.2).
 */
export function HostPage() {
  const { t } = useTranslation();
  const router = useRouter();
  const messageFor = useApiError();
  const [selectedTableId, setSelectedTableId] = useState<string | null>(null);

  const venue = useGetVenue(venueId, { query: { staleTime: 5 * 60_000 } });
  const plan = useGetFloorPlan(venueId);
  const tonight = useGetTonightBookings(venueId);
  const signedOut = useRedirectOnSignedOut(plan.error, tonight.error);

  const goToSignIn = useCallback(() => router.replace("/demo-login/"), [router]);
  const connection = useVenueConnection(venueId, goToSignIn);

  useEffect(() => {
    document.title = t("host.documentTitle", { venue: venue.data?.name ?? "" });
  }, [t, venue.data?.name]);

  const bookingsById = useMemo(
    () => new Map((tonight.data?.items ?? []).map((item) => [item.id, item])),
    [tonight.data],
  );
  const selectedTable = plan.data?.tables.find((table) => table.id === selectedTableId) ?? null;

  if (signedOut) {
    return (
      <p role="status" className={styles.page}>
        {t("host.signedOut")}
      </p>
    );
  }

  const error = plan.error ?? tonight.error;

  return (
    <div className={styles.page}>
      <header className={styles.header}>
        <h1>{t("host.title", { venue: venue.data?.name ?? "" })}</h1>
        <LanguageSwitch />
      </header>

      <div role="status" aria-live="polite">
        {connection === "reconnecting" && (
          <p className={styles.banner} data-testid="connection-banner">
            {t("host.reconnecting")}
          </p>
        )}
      </div>

      {error && (
        <div role="alert" className={styles.alert}>
          <p>{messageFor(error)}</p>
          <button
            type="button"
            onClick={() => {
              void plan.refetch();
              void tonight.refetch();
            }}
          >
            {t("common.retry")}
          </button>
        </div>
      )}

      <div className={styles.layout}>
        <section className={styles.plan} aria-labelledby="plan-heading">
          <h2 id="plan-heading" className={styles.sectionHeading}>
            {t("host.planHeading")}
          </h2>
          {plan.isPending && <p>{t("common.loading")}</p>}
          {plan.data && (
            <>
              <ZoomPanContainer>
                <FloorPlan
                  plan={plan.data}
                  selectedTableId={selectedTableId}
                  onSelect={setSelectedTableId}
                  label={t("host.planHeading")}
                />
              </ZoomPanContainer>
              <Legend />
            </>
          )}
        </section>

        <aside className={styles.side}>
          {selectedTable && plan.data && (
            <TableDetailsPanel
              table={selectedTable}
              timeZone={plan.data.timeZone}
              bookingsById={bookingsById}
              onClose={() => setSelectedTableId(null)}
            />
          )}
          <section aria-labelledby="list-heading">
            <h2 id="list-heading" className={styles.sectionHeading}>
              {t("host.list.heading")}
            </h2>
            {tonight.isPending && <p>{t("common.loading")}</p>}
            {tonight.data && (
              <TonightList
                items={tonight.data.items}
                timeZone={tonight.data.timeZone}
                selectedTableId={selectedTableId}
                onSelectTable={setSelectedTableId}
              />
            )}
          </section>
        </aside>
      </div>
    </div>
  );
}

const legendStates = ["free", "booked", "arrivingSoon", "seated"] as const;

function Legend() {
  const { t } = useTranslation();
  return (
    <ul className={styles.legend} aria-label={t("host.legend")}>
      {legendStates.map((state) => (
        <li key={state}>
          <span className={`${styles.swatch} ${styles[state]}`} aria-hidden="true" />
          {t(`tableState.${state}`)}
        </li>
      ))}
      <li>
        <span className={styles.allergySwatch} aria-hidden="true">
          !
        </span>
        {t("host.allergy")}
      </li>
    </ul>
  );
}

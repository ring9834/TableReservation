"use client";

import { useRouter } from "next/navigation";
import { keepPreviousData } from "@tanstack/react-query";
import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useGetTonightBookings } from "@/api/endpoints/bookings/bookings";
import { useGetFloorPlan } from "@/api/endpoints/floor-plan/floor-plan";
import { useGetVenue } from "@/api/endpoints/venues/venues";
import { useGetWaitlist } from "@/api/endpoints/waitlist/waitlist";
import type { WaitlistChangedNotice } from "@/api/model";
import { suggestedEntryId as resolveSuggestedEntryId } from "@/features/bookings/tableChoice.mjs";
import { TonightList } from "@/features/bookings/TonightList";
import { WalkInDialog } from "@/features/bookings/WalkInDialog";
import { FloorPlan } from "@/features/floor-plan/FloorPlan";
import { TableDetailsPanel } from "@/features/floor-plan/TableDetailsPanel";
import { ZoomPanContainer } from "@/features/floor-plan/ZoomPanContainer";
import { JoinWaitlistDialog } from "@/features/waitlist/JoinWaitlistDialog";
import { WaitlistPanel } from "@/features/waitlist/WaitlistPanel";
import { LanguageSwitch } from "@/i18n/LanguageSwitch";
import { venueId } from "@/lib/config";
import { useApiError } from "@/lib/useApiError";
import { useVenueConnection } from "@/realtime/useVenueConnection";
import { useRedirectOnSignedOut } from "./useRedirectOnSignedOut";
import styles from "./host.module.css";
import actionStyles from "./actions.module.css";

type OpenDialog = { kind: "walkIn" } | { kind: "joinWaitlist"; partySize?: number } | null;

/** A table freed for a waiting party (AC4.2): from a `waitlistChanged` notice, until it no longer applies. */
type Freed = { tableId: string; entryId: string | null };

/** The waitlist's waited minutes move with the clock; refresh them once a minute (Design §18.6). */
const waitlistRefreshMs = 60_000;

/**
 * The host screen (Requirements steps 2-4, Design §18): floor plan, waitlist and tonight's list, kept live by SignalR
 * notices, with walk-ins, seating, clearing and the waitlist (Design §18.6). Server state lives only in the query
 * cache; the selected table, the open dialog and the freed table are local state (Design §18.2).
 */
export function HostPage() {
  const { t } = useTranslation();
  const router = useRouter();
  const messageFor = useApiError();
  const [selectedTableId, setSelectedTableId] = useState<string | null>(null);
  const [dialog, setDialog] = useState<OpenDialog>(null);
  const [freed, setFreed] = useState<Freed | null>(null);
  const [announcement, setAnnouncement] = useState("");

  const venue = useGetVenue(venueId, { query: { staleTime: 5 * 60_000 } });
  const plan = useGetFloorPlan(venueId);
  const tonight = useGetTonightBookings(venueId);
  // With a freed table the API also answers which party it suggests for it (Design §14.2), so a screen that
  // reconnects (every query refetches) gets the highlight back from the API.
  const waitlist = useGetWaitlist(venueId, freed ? { tableId: freed.tableId } : undefined, {
    query: { refetchInterval: waitlistRefreshMs, placeholderData: keepPreviousData },
  });
  const signedOut = useRedirectOnSignedOut(plan.error, tonight.error, waitlist.error);

  const goToSignIn = useCallback(() => router.replace("/demo-login/"), [router]);
  const onWaitlistChanged = useCallback((notice: WaitlistChangedNotice) => {
    if (notice.freedTableId && notice.suggestedEntryId) {
      setFreed({ tableId: notice.freedTableId, entryId: notice.suggestedEntryId });
    }
  }, []);
  const connection = useVenueConnection(venueId, goToSignIn, { onWaitlistChanged });

  const answered = !!freed && waitlist.isSuccess && !waitlist.isPlaceholderData;
  const suggestedEntryId: string | null = resolveSuggestedEntryId(freed, waitlist.data, answered);
  // The API no longer suggests anyone for the freed table (seated, taken, or the party left): drop the highlight.
  // Set while rendering, React's pattern for state that follows other data (no effect, no extra commit).
  if (answered && !suggestedEntryId) setFreed(null);

  useEffect(() => {
    document.title = t("host.documentTitle", { venue: venue.data?.name ?? "" });
  }, [t, venue.data?.name]);

  const bookingsById = useMemo(
    () => new Map((tonight.data?.items ?? []).map((item) => [item.id, item])),
    [tonight.data],
  );
  const selectedTable = plan.data?.tables.find((table) => table.id === selectedTableId) ?? null;
  const tableNames = useMemo(
    () => new Map((plan.data?.tables ?? []).map((table) => [table.id, table.name])),
    [plan.data],
  );
  const highlightedTableId = suggestedEntryId ? (freed?.tableId ?? null) : null;

  const onSeated = useCallback(
    (tableId: string) => {
      setSelectedTableId(tableId);
      setAnnouncement(t("host.actions.seatedAt", { table: tableNames.get(tableId) ?? "" }));
    },
    [t, tableNames],
  );

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
        <div className={actionStyles.toolbar}>
          <button
            type="button"
            className={actionStyles.primaryButton}
            onClick={() => setDialog({ kind: "walkIn" })}
          >
            {t("host.walkInButton")}
          </button>
          <button
            type="button"
            className={actionStyles.secondaryButton}
            onClick={() => setDialog({ kind: "joinWaitlist" })}
          >
            {t("waitlist.joinButton")}
          </button>
          <LanguageSwitch />
        </div>
      </header>

      <p role="status" aria-live="polite" className={actionStyles.visuallyHidden}>
        {announcement}
      </p>

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
                  highlightedTableId={highlightedTableId}
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
          <WaitlistPanel
            waitlist={waitlist.data}
            pending={waitlist.isPending}
            error={waitlist.error && !signedOut ? messageFor(waitlist.error) : null}
            tableNames={tableNames}
            suggestedEntryId={suggestedEntryId}
            freedTableId={freed?.tableId ?? null}
            onJoin={() => setDialog({ kind: "joinWaitlist" })}
            onDismissSuggestion={() => setFreed(null)}
            onSeated={onSeated}
            onNotified={() => setAnnouncement(t("waitlist.notifiedAnnouncement"))}
          />
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

      {dialog?.kind === "walkIn" && (
        <WalkInDialog
          onClose={() => setDialog(null)}
          onSeated={(tableId) => {
            setDialog(null);
            onSeated(tableId);
          }}
          onAddToWaitlist={(partySize) => setDialog({ kind: "joinWaitlist", partySize })}
        />
      )}
      {dialog?.kind === "joinWaitlist" && (
        <JoinWaitlistDialog
          partySize={dialog.partySize}
          onClose={() => setDialog(null)}
          onJoined={() => {
            setDialog(null);
            setAnnouncement(t("waitlist.joined"));
          }}
        />
      )}
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

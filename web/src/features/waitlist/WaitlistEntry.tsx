"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useSeatWalkIn } from "@/api/endpoints/seating/seating";
import { useEndWaitlistEntry, useNotifyWaitlistEntry } from "@/api/endpoints/waitlist/waitlist";
import type { SeatWalkInRequest, WaitlistItem, WaitlistOutcome } from "@/api/model";
import { TableChoice, useFreeTables } from "@/features/bookings/TableChoice";
import { defaultTableId, freeTableIds } from "@/features/bookings/tableChoice.mjs";
import { invalidateHostQueries } from "@/features/host/hostQueries";
import { useHostActionError, useIdempotencyKey } from "@/features/host/useHostActionError";
import { isApiError } from "@/lib/apiFetch";
import { venueId } from "@/lib/config";
import styles from "@/features/host/actions.module.css";

type Mode = "seat" | "notify" | null;

type Props = Readonly<{
  entry: WaitlistItem;
  tableNames: ReadonlyMap<string, string>;
  /** This entry is the suggestion for a freed table (AC4.2). */
  suggested: boolean;
  freedTableId: string | null;
  onSeated: (tableId: string) => void;
}>;

/**
 * One waiting party (AC3.6, AC3.7, AC4.5, AC4.6): time waited, quote, status, overdue badge, and the host's actions.
 * "Seat" and "Mark notified" choose a table, pre-selecting the notified table, then the freed table if this party is
 * the suggestion, then the API's suggestion. Every action sends the entry's version as shown.
 */
export function WaitlistEntry({ entry, tableNames, suggested, freedTableId, onSeated }: Props) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const messageFor = useHostActionError();
  const idempotency = useIdempotencyKey();
  const [mode, setMode] = useState<Mode>(null);
  const [chosen, setChosen] = useState<string | null>(null);
  const [failure, setFailure] = useState<string | null>(null);

  const suggestion = useFreeTables(mode ? entry.partySize : null);
  const tableId = defaultTableId(
    [chosen, entry.notifiedTableId, suggested ? freedTableId : null],
    freeTableIds(suggestion.data),
  );
  const tableName = tableId ? tableNames.get(tableId) : undefined;

  const seat = useSeatWalkIn();
  const notify = useNotifyWaitlistEntry();
  const end = useEndWaitlistEntry();
  const pending = seat.isPending || notify.isPending || end.isPending;

  const toggle = (next: Mode) => {
    setMode((current) => (current === next ? null : next));
    setChosen(null);
    setFailure(null);
  };

  const run = async (action: () => Promise<unknown>, after?: () => void) => {
    setFailure(null);
    try {
      await action();
      void invalidateHostQueries(queryClient, venueId);
      setMode(null);
      after?.();
    } catch (error) {
      if (isApiError(error) && error.code === "slot_taken") {
        idempotency.reset();
        setChosen(null);
      }
      setFailure(messageFor(error));
    }
  };

  const seatAtTable = (id: string) => {
    const request: SeatWalkInRequest = {
      partySize: entry.partySize,
      tableId: id,
      waitlistEntryId: entry.id,
      waitlistEntryVersion: entry.version,
    };
    return run(
      () =>
        seat.mutateAsync({
          venueId,
          data: request,
          headers: { "Idempotency-Key": idempotency.keyFor(request) },
        }),
      () => {
        idempotency.reset();
        onSeated(id);
      },
    );
  };

  const notifyForTable = (id: string) =>
    run(() =>
      notify.mutateAsync({
        venueId,
        id: entry.id,
        data: { tableId: id, version: entry.version },
      }),
    );

  const endAs = (outcome: WaitlistOutcome) =>
    run(() =>
      end.mutateAsync({ venueId, id: entry.id, data: { outcome, version: entry.version } }),
    );

  const headingId = `waitlist-entry-${entry.id}`;
  const notifiedTable = entry.notifiedTableId ? tableNames.get(entry.notifiedTableId) : null;
  const freedTableName = freedTableId ? tableNames.get(freedTableId) : null;

  return (
    <li
      className={`${styles.entry} ${suggested ? styles.suggestedEntry : ""}`}
      aria-labelledby={headingId}
      data-testid="waitlist-entry"
      data-entry-id={entry.id}
      data-suggested={suggested || undefined}
    >
      <div className={styles.entryHeader}>
        <h3 id={headingId}>{entry.name}</h3>
        <span>{t("host.party", { count: entry.partySize })}</span>
      </div>
      <p className={styles.entryMeta}>
        <span>{t("waitlist.quoted", { minutes: entry.quotedWaitMinutes })}</span>
        <span>{t("waitlist.waited", { minutes: entry.waitedMinutes })}</span>
        {entry.mobileMasked && <span>{entry.mobileMasked}</span>}
      </p>
      <p className={styles.badges}>
        <span className={styles.statusBadge}>
          {entry.status === "notified" && notifiedTable
            ? t("waitlist.notifiedFor", { table: notifiedTable })
            : t(`waitlistStatus.${entry.status}`)}
        </span>
        {entry.overdue && (
          <span className={`${styles.statusBadge} ${styles.overdue}`} data-testid="overdue-badge">
            <span aria-hidden="true">⚠ </span>
            {t("waitlist.overdue")}
          </span>
        )}
        {suggested && freedTableName && (
          <span className={`${styles.statusBadge} ${styles.suggestedBadge}`}>
            <span aria-hidden="true">★ </span>
            {t("waitlist.suggestedFor", { table: freedTableName })}
          </span>
        )}
      </p>

      <div className={styles.buttonRow}>
        <button
          type="button"
          className={styles.primaryButton}
          aria-expanded={mode === "seat"}
          disabled={pending}
          onClick={() => toggle("seat")}
        >
          {t("waitlist.actions.seat")}
        </button>
        <button
          type="button"
          className={styles.secondaryButton}
          aria-expanded={mode === "notify"}
          disabled={pending}
          onClick={() => toggle("notify")}
        >
          {t("waitlist.actions.notify")}
        </button>
        <button
          type="button"
          className={styles.secondaryButton}
          disabled={pending}
          onClick={() => void endAs("walkedAway")}
        >
          {t("waitlist.actions.walkedAway")}
        </button>
        <button
          type="button"
          className={styles.secondaryButton}
          disabled={pending}
          onClick={() => void endAs("removed")}
        >
          {t("waitlist.actions.remove")}
        </button>
      </div>

      {mode && suggestion.isPending && <p>{t("common.loading")}</p>}
      {mode && suggestion.data && !tableId && (
        <p>{t("host.actions.noOtherTable", { count: entry.partySize })}</p>
      )}
      {mode && suggestion.data && tableId && (
        <div className={styles.actions}>
          <TableChoice
            suggestion={suggestion.data}
            value={tableId}
            onChange={setChosen}
            legend={t("host.actions.chooseTable")}
            freedTableId={suggested ? freedTableId : null}
          />
          {mode === "notify" && <p className={styles.hint}>{t("waitlist.notifyHint")}</p>}
          <button
            type="button"
            className={styles.primaryButton}
            disabled={pending}
            onClick={() => void (mode === "seat" ? seatAtTable(tableId) : notifyForTable(tableId))}
          >
            {mode === "seat"
              ? t("host.walkInDialog.seatAt", { table: tableName ?? "" })
              : t("waitlist.actions.notifyFor", { table: tableName ?? "" })}
          </button>
        </div>
      )}

      {failure && (
        <p role="alert" className={styles.error}>
          {failure}
        </p>
      )}
    </li>
  );
}

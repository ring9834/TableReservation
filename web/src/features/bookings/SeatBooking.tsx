"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useSeatBooking } from "@/api/endpoints/seating/seating";
import type { BookingStatus } from "@/api/model";
import { invalidateHostQueries } from "@/features/host/hostQueries";
import { useHostActionError } from "@/features/host/useHostActionError";
import { venueId } from "@/lib/config";
import { TableChoice, useFreeTables } from "./TableChoice";
import { defaultTableId, freeTableIds } from "./tableChoice.mjs";
import styles from "@/features/host/actions.module.css";

/** A booked guest can be seated while the booking is `booked` or `confirmed` (Design §3.1, §14.2 "Seat"). */
export const canSeat = (status: BookingStatus) => status === "booked" || status === "confirmed";

export type SeatableBooking = Readonly<{
  bookingId: string;
  version: number;
  partySize: number;
  guestName: string | null;
}>;

/**
 * `seatBooking` with the version the screen was shown (Design §14.2). Without `tableIds` the booking keeps its own
 * tables. Failures refetch when the screen was behind and come back as a translated message.
 */
const useSeatAction = () => {
  const queryClient = useQueryClient();
  const messageFor = useHostActionError();
  const seat = useSeatBooking();
  const [failure, setFailure] = useState<string | null>(null);

  const run = async (booking: SeatableBooking, tableIds?: string[]) => {
    setFailure(null);
    try {
      await seat.mutateAsync({
        venueId,
        id: booking.bookingId,
        data: { version: booking.version, ...(tableIds ? { tableIds } : {}) },
      });
      void invalidateHostQueries(queryClient, venueId);
      return true;
    } catch (error) {
      setFailure(messageFor(error));
      return false;
    }
  };

  return { run, pending: seat.isPending, failure };
};

/** One-tap "Seat" for a row of tonight's list (AC3.3): the booking's own tables. */
export function SeatBookingButton({
  booking,
  label,
}: Readonly<{ booking: SeatableBooking; label: string }>) {
  const { t } = useTranslation();
  const action = useSeatAction();
  return (
    <span className={styles.rowAction}>
      <button
        type="button"
        className={styles.secondaryButton}
        aria-label={label}
        disabled={action.pending}
        onClick={() => void action.run(booking)}
      >
        {action.pending ? t("host.actions.seating") : t("host.actions.seat")}
      </button>
      {action.failure && (
        <span role="alert" className={styles.error}>
          {action.failure}
        </span>
      )}
    </span>
  );
}

/**
 * The side panel's seat actions for the table's next booking (AC3.3): "Seat" on its own tables in one tap, or
 * "Seat at another table" from the free tables that fit the party now.
 */
export function SeatBookingActions({ booking }: Readonly<{ booking: SeatableBooking }>) {
  const { t } = useTranslation();
  const action = useSeatAction();
  const [elsewhere, setElsewhere] = useState(false);
  const [chosen, setChosen] = useState<string | null>(null);
  const suggestion = useFreeTables(elsewhere ? booking.partySize : null);
  const tableId = defaultTableId([chosen], freeTableIds(suggestion.data));
  const tableName = [suggestion.data?.table, ...(suggestion.data?.alternatives ?? [])].find(
    (table) => table?.id === tableId,
  )?.name;

  const seatElsewhere = async () => {
    if (!tableId) return;
    if (await action.run(booking, [tableId])) {
      setElsewhere(false);
      setChosen(null);
    } else {
      setChosen(null);
    }
  };

  return (
    <div className={styles.actions}>
      <div className={styles.buttonRow}>
        <button
          type="button"
          className={styles.primaryButton}
          disabled={action.pending}
          onClick={() => void action.run(booking)}
        >
          {action.pending ? t("host.actions.seating") : t("host.actions.seat")}
        </button>
        <button
          type="button"
          className={styles.secondaryButton}
          aria-expanded={elsewhere}
          onClick={() => setElsewhere((open) => !open)}
        >
          {t("host.actions.seatElsewhere")}
        </button>
      </div>
      {elsewhere && suggestion.isPending && <p>{t("common.loading")}</p>}
      {elsewhere && suggestion.data && (
        <>
          {tableId ? (
            <>
              <TableChoice
                suggestion={suggestion.data}
                value={tableId}
                onChange={setChosen}
                legend={t("host.actions.chooseTable")}
              />
              <button
                type="button"
                className={styles.primaryButton}
                disabled={action.pending}
                onClick={() => void seatElsewhere()}
              >
                {t("host.walkInDialog.seatAt", { table: tableName ?? "" })}
              </button>
            </>
          ) : (
            <p>{t("host.actions.noOtherTable", { count: booking.partySize })}</p>
          )}
        </>
      )}
      {action.failure && (
        <p role="alert" className={styles.error}>
          {action.failure}
        </p>
      )}
    </div>
  );
}

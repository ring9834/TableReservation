"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { useClearTable } from "@/api/endpoints/seating/seating";
import type { BookingListItem, FloorTableDto, TableBookingSummary } from "@/api/model";
import { CancelBookingButton, canCancel } from "@/features/bookings/CancelBooking";
import { DepositLine } from "@/features/bookings/DepositLine";
import { canSeat, SeatBookingActions } from "@/features/bookings/SeatBooking";
import { invalidateHostQueries } from "@/features/host/hostQueries";
import { useHostActionError } from "@/features/host/useHostActionError";
import { venueId } from "@/lib/config";
import { formatInstantTime } from "@/lib/format";
import { displayState } from "./tableText";
import styles from "./floorPlan.module.css";
import actionStyles from "@/features/host/actions.module.css";

type Props = Readonly<{
  /** The selected table; `null` when a booking without a table was picked from tonight's list. */
  table: FloorTableDto | null;
  /** The booking picked from tonight's list, if the panel was opened from there. */
  selectedBooking: BookingListItem | null;
  timeZone: string;
  /** Tonight's list, for the details the floor plan leaves out (allergy notes, occasion, masked mobile). */
  bookingsById: ReadonlyMap<string, BookingListItem>;
  onClose: () => void;
}>;

/** A row of tonight's list in the shape the floor plan uses for a table's booking. */
const summaryOf = (item: BookingListItem): TableBookingSummary => ({
  bookingId: item.id,
  reference: item.reference,
  status: item.status,
  partySize: item.partySize,
  startsAt: item.startsAt,
  endsAt: item.endsAt,
  guestName: item.guestName,
  hasAllergy: item.hasAllergy,
  version: item.version,
  deposit: item.deposit,
  depositStatus: item.depositStatus,
});

/**
 * The selected table's current and next booking (Design §18.3) with the host's actions (Design §18.6): "Clear table"
 * for the seated party (AC4.1), "Seat" / "Seat at another table" for the next booking (AC3.3) and, at the bottom of
 * a booking's details and apart from Seat, a quiet "Cancel booking" for one not yet seated (M8b, AC5.5). A booking
 * picked from tonight's list that is neither the table's current nor next one (a later booking on it), or one without
 * a table, gets its own "Selected booking" section with the same details and Cancel. "Bill paid" through the POS
 * arrives with M9. Focus moves to the heading when a table or booking is chosen.
 */
export function TableDetailsPanel({
  table,
  selectedBooking,
  timeZone,
  bookingsById,
  onClose,
}: Props) {
  const { t } = useTranslation();
  const heading = useRef<HTMLHeadingElement>(null);
  const focusKey = `${table?.id ?? ""}:${selectedBooking?.id ?? ""}`;

  useEffect(() => {
    heading.current?.focus();
  }, [focusKey]);

  const shownIds = [table?.current?.bookingId, table?.next?.bookingId];
  const extra =
    selectedBooking && !shownIds.includes(selectedBooking.id) ? summaryOf(selectedBooking) : null;

  return (
    <section
      className={styles.panel}
      aria-labelledby="table-panel-heading"
      data-testid={table ? "table-panel" : "booking-panel"}
    >
      <div className={styles.panelHeader}>
        <h2 id="table-panel-heading" ref={heading} tabIndex={-1}>
          {table
            ? t("host.tableLabel", { name: table.name, count: table.seats })
            : t("host.panel.bookingHeading", { reference: selectedBooking?.reference ?? "" })}
        </h2>
        <button type="button" className={styles.close} onClick={onClose}>
          {t("host.panel.close")}
        </button>
      </div>
      {table && (
        <p className={`${styles.stateBadge} ${styles[displayState(table)]}`}>
          {t(`tableState.${displayState(table)}`)}
        </p>
      )}
      {table?.current && (
        <BookingDetails
          title={t("host.panel.current")}
          booking={table.current}
          timeZone={timeZone}
          details={bookingsById.get(table.current.bookingId)}
        />
      )}
      {table?.current?.status === "seated" && (
        <ClearTableButton
          key={table.current.bookingId}
          tableId={table.id}
          booking={table.current}
        />
      )}
      {table?.next && (
        <>
          <BookingDetails
            title={t("host.panel.next")}
            booking={table.next}
            timeZone={timeZone}
            details={bookingsById.get(table.next.bookingId)}
          />
          {canSeat(table.next.status) && (
            <SeatBookingActions key={table.next.bookingId} booking={table.next} />
          )}
          {canCancel(table.next.status) && (
            <CancelBookingButton
              key={`cancel-${table.next.bookingId}`}
              booking={table.next}
              timeZone={timeZone}
            />
          )}
        </>
      )}
      {table && !table.current && !table.next && !extra && <p>{t("host.panel.nothingTonight")}</p>}
      {extra && (
        <div data-testid="selected-booking">
          <BookingDetails
            title={t("host.panel.selected")}
            booking={extra}
            timeZone={timeZone}
            details={selectedBooking ?? undefined}
          />
          {canCancel(extra.status) && (
            <CancelBookingButton
              key={`cancel-${extra.bookingId}`}
              booking={extra}
              timeZone={timeZone}
            />
          )}
        </div>
      )}
    </section>
  );
}

/** Clears the table after the party leaves (AC4.1): completes the booking with the version shown (Design §14.2). */
function ClearTableButton({
  tableId,
  booking,
}: Readonly<{ tableId: string; booking: TableBookingSummary }>) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const messageFor = useHostActionError();
  const clear = useClearTable();
  const [failure, setFailure] = useState<string | null>(null);

  const run = async () => {
    setFailure(null);
    try {
      await clear.mutateAsync({
        venueId,
        tableId,
        data: { bookingId: booking.bookingId, version: booking.version },
      });
      void invalidateHostQueries(queryClient, venueId);
    } catch (error) {
      setFailure(messageFor(error));
    }
  };

  return (
    <div className={actionStyles.actions}>
      <button
        type="button"
        className={actionStyles.primaryButton}
        disabled={clear.isPending}
        onClick={() => void run()}
      >
        {clear.isPending ? t("host.actions.clearing") : t("host.actions.clear")}
      </button>
      {failure && (
        <p role="alert" className={actionStyles.error}>
          {failure}
        </p>
      )}
    </div>
  );
}

function BookingDetails({
  title,
  booking,
  timeZone,
  details,
}: Readonly<{
  title: string;
  booking: TableBookingSummary;
  timeZone: string;
  details: BookingListItem | undefined;
}>) {
  const { t, i18n } = useTranslation();
  const time = (instant: string) => formatInstantTime(instant, timeZone, i18n.language);

  return (
    <div className={styles.booking}>
      <h3>{title}</h3>
      <dl>
        <dt>{t("host.panel.guest")}</dt>
        <dd>{booking.guestName ?? t("host.walkIn")}</dd>
        <dt>{t("host.panel.time")}</dt>
        <dd>
          {time(booking.startsAt)}–{time(booking.endsAt)}
        </dd>
        <dt>{t("host.panel.party")}</dt>
        <dd>{t("host.party", { count: booking.partySize })}</dd>
        <dt>{t("host.panel.status")}</dt>
        <dd>{t(`bookingStatus.${booking.status}`)}</dd>
        <dt>{t("host.panel.reference")}</dt>
        <dd className={styles.reference}>{booking.reference}</dd>
        {booking.deposit && (
          <>
            <dt>{t("host.panel.deposit")}</dt>
            <dd>
              <DepositLine
                deposit={booking.deposit}
                depositStatus={booking.depositStatus}
                bookingStatus={booking.status}
                refundDecision={details?.refundDecision}
              />
            </dd>
          </>
        )}
        {details?.mobileMasked && (
          <>
            <dt>{t("host.panel.mobile")}</dt>
            <dd>{details.mobileMasked}</dd>
          </>
        )}
        {details?.occasion && (
          <>
            <dt>{t("host.panel.occasion")}</dt>
            <dd>{details.occasion}</dd>
          </>
        )}
        {details?.notes && (
          <>
            <dt>{t("host.panel.notes")}</dt>
            <dd>{details.notes}</dd>
          </>
        )}
      </dl>
      {booking.hasAllergy && (
        <p className={styles.allergyNote} role="note">
          <span aria-hidden="true" className={styles.allergyIcon}>
            !
          </span>{" "}
          <strong>{t("host.allergy")}:</strong>{" "}
          {details?.allergyNotes ?? t("host.panel.allergyInList")}
        </p>
      )}
    </div>
  );
}

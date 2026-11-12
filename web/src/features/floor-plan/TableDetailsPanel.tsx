"use client";

import { useEffect, useRef } from "react";
import { useTranslation } from "react-i18next";
import type { BookingListItem, FloorTableDto, TableBookingSummary } from "@/api/model";
import { formatInstantTime } from "@/lib/format";
import styles from "./floorPlan.module.css";

type Props = Readonly<{
  table: FloorTableDto;
  timeZone: string;
  /** Tonight's list, for the details the floor plan leaves out (allergy notes, occasion, masked mobile). */
  bookingsById: ReadonlyMap<string, BookingListItem>;
  onClose: () => void;
}>;

/**
 * The selected table's current and next booking (Design §18.3). Seat, clear, "Bill paid" and notify arrive with
 * M6, M7 and M9. Focus moves to the heading when a table is chosen.
 */
export function TableDetailsPanel({ table, timeZone, bookingsById, onClose }: Props) {
  const { t } = useTranslation();
  const heading = useRef<HTMLHeadingElement>(null);

  useEffect(() => {
    heading.current?.focus();
  }, [table.id]);

  return (
    <section
      className={styles.panel}
      aria-labelledby="table-panel-heading"
      data-testid="table-panel"
    >
      <div className={styles.panelHeader}>
        <h2 id="table-panel-heading" ref={heading} tabIndex={-1}>
          {t("host.tableLabel", { name: table.name, count: table.seats })}
        </h2>
        <button type="button" className={styles.close} onClick={onClose}>
          {t("host.panel.close")}
        </button>
      </div>
      <p className={`${styles.stateBadge} ${styles[table.state]}`}>
        {t(`tableState.${table.state}`)}
      </p>
      {table.current && (
        <BookingDetails
          title={t("host.panel.current")}
          booking={table.current}
          timeZone={timeZone}
          details={bookingsById.get(table.current.bookingId)}
        />
      )}
      {table.next && (
        <BookingDetails
          title={t("host.panel.next")}
          booking={table.next}
          timeZone={timeZone}
          details={bookingsById.get(table.next.bookingId)}
        />
      )}
      {!table.current && !table.next && <p>{t("host.panel.nothingTonight")}</p>}
    </section>
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

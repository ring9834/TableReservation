"use client";

import { useTranslation } from "react-i18next";
import type { BookingListItem } from "@/api/model";
import { formatInstantTime } from "@/lib/format";
import { DepositLine } from "./DepositLine";
import { canSeat, SeatBookingButton } from "./SeatBooking";
import styles from "./tonightList.module.css";

type Props = Readonly<{
  items: BookingListItem[];
  timeZone: string;
  selectedBookingId: string | null;
  /** Opens the side panel for this booking: its table's panel, or a panel of its own without a table. */
  onSelectBooking: (item: BookingListItem) => void;
}>;

/**
 * Tonight's bookings in time order, every status (AC2.1, Design §14.1 "Host list"). A row selects its table; a booked
 * or confirmed booking has a one-tap "Seat" beside it (AC3.3). "Cancel booking" is in the booking's details in the
 * side panel that the row opens, not in the row (M8b). A deposit shows on its own line; cancelled bookings stay,
 * greyed, with theirs (AC5.5).
 */
export function TonightList({ items, timeZone, selectedBookingId, onSelectBooking }: Props) {
  const { t, i18n } = useTranslation();

  if (items.length === 0) {
    return <p data-testid="tonight-list">{t("host.list.empty")}</p>;
  }

  return (
    <ol className={styles.list} data-testid="tonight-list">
      {items.map((item) => {
        const selected = item.id === selectedBookingId;
        const tables = item.tables.map((table) => table.name).join(", ") || "–";
        const time = formatInstantTime(item.startsAt, timeZone, i18n.language);
        const guest = item.guestName ?? t("host.walkIn");
        return (
          <li key={item.id} className={`${styles.item} ${styles[item.status] ?? ""}`}>
            <button
              type="button"
              className={styles.row}
              aria-pressed={selected}
              onClick={() => onSelectBooking(item)}
              data-testid={`booking-${item.reference}`}
            >
              <span className={styles.time}>{time}</span>
              <span className={styles.name}>
                {guest}
                {item.hasAllergy && (
                  <span className={styles.allergy} title={t("host.allergy")}>
                    <span aria-hidden="true">!</span>
                    <span className={styles.visuallyHidden}>{t("host.allergy")}</span>
                  </span>
                )}
              </span>
              <span className={styles.meta}>
                {t("host.party", { count: item.partySize })} · {tables}
              </span>
              <DepositLine
                className={styles.deposit}
                deposit={item.deposit}
                depositStatus={item.depositStatus}
                bookingStatus={item.status}
                refundDecision={item.refundDecision}
              />
              <span
                className={styles.status}
                data-testid="booking-status"
                data-status={item.status}
              >
                {t(`bookingStatus.${item.status}`)}
              </span>
            </button>
            {canSeat(item.status) && (
              <SeatBookingButton
                booking={{
                  bookingId: item.id,
                  version: item.version,
                  partySize: item.partySize,
                  guestName: item.guestName,
                }}
                label={t("host.actions.seatGuest", { guest, time })}
              />
            )}
          </li>
        );
      })}
    </ol>
  );
}

"use client";

import { useTranslation } from "react-i18next";
import type { BookingListItem } from "@/api/model";
import { formatInstantTime } from "@/lib/format";
import styles from "./tonightList.module.css";

type Props = Readonly<{
  items: BookingListItem[];
  timeZone: string;
  selectedTableId: string | null;
  onSelectTable: (tableId: string) => void;
}>;

/** Tonight's bookings in time order, every status (AC2.1, Design §14.1 "Host list"). A row selects its table. */
export function TonightList({ items, timeZone, selectedTableId, onSelectTable }: Props) {
  const { t, i18n } = useTranslation();

  if (items.length === 0) {
    return <p data-testid="tonight-list">{t("host.list.empty")}</p>;
  }

  return (
    <ol className={styles.list} data-testid="tonight-list">
      {items.map((item) => {
        const tableId = item.tables[0]?.id;
        const selected = !!tableId && tableId === selectedTableId;
        const tables = item.tables.map((table) => table.name).join(", ") || "–";
        return (
          <li key={item.id} className={`${styles.item} ${styles[item.status] ?? ""}`}>
            <button
              type="button"
              className={styles.row}
              aria-pressed={selected}
              disabled={!tableId}
              onClick={() => tableId && onSelectTable(tableId)}
              data-testid={`booking-${item.reference}`}
            >
              <span className={styles.time}>
                {formatInstantTime(item.startsAt, timeZone, i18n.language)}
              </span>
              <span className={styles.name}>
                {item.guestName ?? t("host.walkIn")}
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
              <span className={styles.status}>{t(`bookingStatus.${item.status}`)}</span>
            </button>
          </li>
        );
      })}
    </ol>
  );
}

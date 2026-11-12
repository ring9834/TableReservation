"use client";

import type { KeyboardEvent } from "react";
import { useTranslation } from "react-i18next";
import type { FloorTableDto } from "@/api/model";
import { formatInstantTime } from "@/lib/format";
import { shownBooking, truncateName } from "./tableText";
import styles from "./floorPlan.module.css";

type Props = Readonly<{
  table: FloorTableDto;
  timeZone: string;
  selected: boolean;
  onSelect: (tableId: string) => void;
}>;

/**
 * One table on the plan (Design §18.3, ADR-001): a real button with a label, so it can be tapped, read by a screen
 * reader and found by tests. State shows as colour **and** text (QA-7); allergies as an icon (AC2.6).
 */
export function TableShape({ table, timeZone, selected, onSelect }: Props) {
  const { t, i18n } = useTranslation();
  const { x, y, width, height, rotation } = table.position;
  const cx = x + width / 2;
  const cy = y + height / 2;
  const booking = shownBooking(table);
  const time = booking ? formatInstantTime(booking.startsAt, timeZone, i18n.language) : null;
  const state = t(`tableState.${table.state}`);
  const bookingLine =
    booking && time
      ? table.current
        ? t("host.tile.seatedSince", { time, count: booking.partySize })
        : t("host.tile.next", { time, count: booking.partySize })
      : null;
  const guest = booking?.guestName ?? (booking ? t("host.walkIn") : null);

  const label = [
    t("host.tableLabel", { name: table.name, count: table.seats }),
    state,
    bookingLine,
    booking?.guestName,
    booking?.hasAllergy ? t("host.allergy") : null,
  ]
    .filter(Boolean)
    .join(", ");

  const onKeyDown = (event: KeyboardEvent<SVGGElement>) => {
    if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      onSelect(table.id);
    }
  };

  const lines = [
    { text: table.name, className: styles.tileName },
    { text: state, className: styles.tileState },
    bookingLine ? { text: bookingLine, className: styles.tileText } : null,
    guest ? { text: truncateName(guest), className: styles.tileText } : null,
  ].filter((line): line is { text: string; className: string } => line !== null);
  const lineHeight = 13;
  const firstLine = cy - ((lines.length - 1) * lineHeight) / 2;

  return (
    <g
      role="button"
      tabIndex={0}
      aria-label={label}
      aria-pressed={selected}
      data-testid={`table-${table.name}`}
      data-state={table.state}
      className={`${styles.table} ${styles[table.state]} ${selected ? styles.selected : ""}`}
      onClick={() => onSelect(table.id)}
      onKeyDown={onKeyDown}
    >
      {table.shape === "round" ? (
        <ellipse className={styles.shape} cx={cx} cy={cy} rx={width / 2} ry={height / 2} />
      ) : (
        <rect
          className={styles.shape}
          x={x}
          y={y}
          width={width}
          height={height}
          rx={8}
          transform={rotation ? `rotate(${rotation} ${cx} ${cy})` : undefined}
        />
      )}
      <text textAnchor="middle" aria-hidden="true">
        {lines.map((line, index) => (
          <tspan
            key={index}
            x={cx}
            y={firstLine + index * lineHeight}
            dominantBaseline="middle"
            className={line.className}
          >
            {line.text}
          </tspan>
        ))}
      </text>
      {booking?.hasAllergy && (
        <g aria-hidden="true" data-testid="allergy-icon">
          <circle className={styles.allergyBadge} cx={x + width - 4} cy={y + 4} r={10} />
          <text
            x={x + width - 4}
            y={y + 4}
            textAnchor="middle"
            dominantBaseline="central"
            className={styles.allergyMark}
          >
            !
          </text>
        </g>
      )}
    </g>
  );
}

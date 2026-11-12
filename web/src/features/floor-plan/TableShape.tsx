"use client";

import type { KeyboardEvent } from "react";
import { useTranslation } from "react-i18next";
import type { FloorTableDto } from "@/api/model";
import { formatInstantTime } from "@/lib/format";
import { shownBooking } from "./tableText";
import { layoutTile, personIconEm } from "./tileLayout.mjs";
import styles from "./floorPlan.module.css";

type Props = Readonly<{
  table: FloorTableDto;
  timeZone: string;
  selected: boolean;
  /** Freed for the suggested waiting party (AC4.2): outlined and labelled "Suggested". */
  highlighted?: boolean;
  onSelect: (tableId: string) => void;
}>;

const lineClass: Record<string, string | undefined> = {
  name: styles.tileName,
  status: styles.tileState,
  party: styles.tileText,
  guest: styles.tileText,
};

/** A small person, drawn before "2 · 18:25" when "2 guests · 18:25" does not fit. */
function PersonIcon({ x, y, size }: Readonly<{ x: number; y: number; size: number }>) {
  const width = size * 0.7;
  return (
    <g className={styles.tileIcon}>
      <circle cx={x + width / 2} cy={y - size * 0.2} r={size * 0.2} />
      <path d={`M ${x} ${y + size * 0.42} a ${width / 2} ${size * 0.38} 0 0 1 ${width} 0 Z`} />
    </g>
  );
}

/**
 * One table on the plan (Design §18.3, ADR-001): a real button with a label, so it can be tapped, read by a screen
 * reader and found by tests. State shows as colour **and** text (QA-7); allergies as an icon (AC2.6).
 *
 * The text fits inside the outline at every table size, in both languages (Design §18.6): the name; a short status
 * (or "★ Suggested"); the party and time ("2 guests · 18:25", then a person icon with "2 · 18:25", then the time is
 * dropped); and the guest's name on one line, cut with "…". The full details stay in the aria-label and side panel.
 */
export function TableShape({ table, timeZone, selected, highlighted = false, onSelect }: Props) {
  const { t, i18n } = useTranslation();
  const { x, y, width, height, rotation } = table.position;
  const cx = x + width / 2;
  const cy = y + height / 2;
  const booking = shownBooking(table);
  const time = booking ? formatInstantTime(booking.startsAt, timeZone, i18n.language) : null;
  const state = t(`tableState.${table.state}`);
  const guests = booking
    ? booking.partySize === 1
      ? t("host.tile.guestsOne")
      : t("host.tile.guestsMany", { count: booking.partySize })
    : null;
  const guest = booking?.guestName ?? (booking ? t("host.walkIn") : null);

  const label = [
    t("host.tableLabel", { name: table.name, count: table.seats }),
    state,
    booking && time && guests
      ? t(table.current ? "host.tile.seatedSince" : "host.tile.next", { time, party: guests })
      : null,
    booking?.guestName,
    booking?.hasAllergy ? t("host.allergy") : null,
    highlighted ? t("host.tile.suggestedLabel") : null,
  ]
    .filter(Boolean)
    .join(", ");

  const suggested = t("host.tile.suggested");
  const lines = layoutTile({ shape: table.shape, width, height, rotation }, [
    { key: "name", kind: "name", variants: [{ text: table.name }] },
    {
      key: "status",
      kind: "text",
      bold: true,
      variants: highlighted
        ? [
            { text: `★ ${suggested}` },
            { text: suggested },
            { text: suggested, scale: 0.8 },
            { text: "★" },
          ]
        : [
            { text: t(`host.tile.status.${table.state}`) },
            { text: t(`host.tile.status.${table.state}`), scale: 0.85 },
          ],
    },
    ...(booking && guests && time
      ? [
          {
            key: "party",
            kind: "text" as const,
            variants: [
              { text: `${guests} · ${time}` },
              { text: `${booking.partySize} · ${time}`, icon: true },
              { text: guests },
              { text: String(booking.partySize), icon: true },
            ],
          },
        ]
      : []),
    ...(guest ? [{ key: "guest", kind: "text" as const, truncate: guest }] : []),
  ]);

  const onKeyDown = (event: KeyboardEvent<SVGGElement>) => {
    if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      onSelect(table.id);
    }
  };

  return (
    <g
      role="button"
      tabIndex={0}
      aria-label={label}
      aria-pressed={selected}
      data-testid={`table-${table.name}`}
      data-state={table.state}
      data-highlighted={highlighted || undefined}
      className={`${styles.table} ${styles[table.state]} ${selected ? styles.selected : ""} ${highlighted ? styles.highlighted : ""}`}
      onClick={() => onSelect(table.id)}
      onKeyDown={onKeyDown}
    >
      {highlighted && (
        <rect
          className={styles.highlightRing}
          x={x - 7}
          y={y - 7}
          width={width + 14}
          height={height + 14}
          rx={14}
          aria-hidden="true"
        />
      )}
      {table.shape === "round" ? (
        <ellipse
          className={styles.shape}
          cx={cx}
          cy={cy}
          rx={width / 2}
          ry={height / 2}
          data-outline=""
        />
      ) : (
        <rect
          className={styles.shape}
          x={x}
          y={y}
          width={width}
          height={height}
          rx={8}
          transform={rotation ? `rotate(${rotation} ${cx} ${cy})` : undefined}
          data-outline=""
        />
      )}
      <g aria-hidden="true">
        {lines.map((line) => {
          const lineY = cy + line.y;
          const left = cx - line.width / 2;
          const className = `${lineClass[line.key] ?? styles.tileText} ${highlighted && line.key === "status" ? styles.tileSuggested : ""}`;
          return line.icon ? (
            <g key={line.key} data-tile-line={line.key}>
              <PersonIcon x={left} y={lineY} size={line.size} />
              <text
                x={left + personIconEm * line.size}
                y={lineY}
                dominantBaseline="middle"
                fontSize={line.size}
                className={className}
              >
                {line.text}
              </text>
            </g>
          ) : (
            <text
              key={line.key}
              data-tile-line={line.key}
              x={cx}
              y={lineY}
              textAnchor="middle"
              dominantBaseline="middle"
              fontSize={line.size}
              className={className}
            >
              {line.text}
            </text>
          );
        })}
      </g>
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

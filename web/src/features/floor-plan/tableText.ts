import type { FloorTableDto, TableBookingSummary } from "@/api/model";

/** The booking a tile talks about: the party seated there, otherwise the next one tonight. */
export const shownBooking = (table: FloorTableDto): TableBookingSummary | null =>
  table.current ?? table.next;

/** The plan's drawing area: every table's box plus a margin, in floor units. */
export const planBounds = (tables: FloorTableDto[], margin = 40) => {
  if (tables.length === 0) {
    return { x: 0, y: 0, width: 100, height: 100 };
  }
  const left = Math.min(...tables.map((t) => t.position.x));
  const top = Math.min(...tables.map((t) => t.position.y));
  const right = Math.max(...tables.map((t) => t.position.x + t.position.width));
  const bottom = Math.max(...tables.map((t) => t.position.y + t.position.height));
  return {
    x: left - margin,
    y: top - margin,
    width: right - left + 2 * margin,
    height: bottom - top + 2 * margin,
  };
};

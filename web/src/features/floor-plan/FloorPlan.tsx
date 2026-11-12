"use client";

import { useTranslation } from "react-i18next";
import type { FloorAreaDto, FloorPlanResponse } from "@/api/model";
import { TableShape } from "./TableShape";
import { planBounds } from "./tableText";
import styles from "./floorPlan.module.css";

type Props = Readonly<{
  plan: FloorPlanResponse;
  selectedTableId: string | null;
  /** The table freed for a suggested waiting party (AC4.2). */
  highlightedTableId?: string | null;
  onSelect: (tableId: string) => void;
  label: string;
}>;

/**
 * The room as one SVG (ADR-001): one `viewBox` scales it to the screen, each area is a layer with its name, each
 * table a {@link TableShape}. A notice refetches the whole plan; React redraws only the tables that changed.
 * Area names come in English and Chinese; the active language picks one (QA-7).
 */
export function FloorPlan({ plan, selectedTableId, highlightedTableId, onSelect, label }: Props) {
  const bounds = planBounds(plan.tables);
  const areas = [...plan.areas].sort((a, b) => a.sortOrder - b.sortOrder);
  const { i18n } = useTranslation();
  const areaName = (area: FloorAreaDto) =>
    i18n.language.startsWith("zh") ? area.nameZh : area.name;

  return (
    <svg
      className={styles.svg}
      viewBox={`${bounds.x} ${bounds.y} ${bounds.width} ${bounds.height}`}
      role="group"
      aria-label={label}
      data-testid="floor-plan"
    >
      {areas.map((area) => {
        const tables = plan.tables.filter((t) => t.areaId === area.id);
        if (tables.length === 0) return null;
        const box = planBounds(tables, 20);
        return (
          <g key={area.id} role="group" aria-label={areaName(area)}>
            <rect
              className={styles.area}
              x={box.x}
              y={box.y - 14}
              width={box.width}
              height={box.height + 14}
              rx={12}
            />
            <text className={styles.areaName} x={box.x + 10} y={box.y + 2} aria-hidden="true">
              {areaName(area)}
            </text>
            {tables.map((table) => (
              <TableShape
                key={table.id}
                table={table}
                timeZone={plan.timeZone}
                selected={table.id === selectedTableId}
                highlighted={table.id === highlightedTableId}
                onSelect={onSelect}
              />
            ))}
          </g>
        );
      })}
    </svg>
  );
}

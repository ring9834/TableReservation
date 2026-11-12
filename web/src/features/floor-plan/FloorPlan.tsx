"use client";

import type { FloorPlanResponse } from "@/api/model";
import { TableShape } from "./TableShape";
import { planBounds } from "./tableText";
import styles from "./floorPlan.module.css";

type Props = Readonly<{
  plan: FloorPlanResponse;
  selectedTableId: string | null;
  onSelect: (tableId: string) => void;
  label: string;
}>;

/**
 * The room as one SVG (ADR-001): one `viewBox` scales it to the screen, each area is a layer with its name, each
 * table a {@link TableShape}. A notice refetches the whole plan; React redraws only the tables that changed.
 */
export function FloorPlan({ plan, selectedTableId, onSelect, label }: Props) {
  const bounds = planBounds(plan.tables);
  const areas = [...plan.areas].sort((a, b) => a.sortOrder - b.sortOrder);

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
          <g key={area.id} role="group" aria-label={area.name}>
            <rect
              className={styles.area}
              x={box.x}
              y={box.y - 14}
              width={box.width}
              height={box.height + 14}
              rx={12}
            />
            <text className={styles.areaName} x={box.x + 10} y={box.y + 2} aria-hidden="true">
              {area.name}
            </text>
            {tables.map((table) => (
              <TableShape
                key={table.id}
                table={table}
                timeZone={plan.timeZone}
                selected={table.id === selectedTableId}
                onSelect={onSelect}
              />
            ))}
          </g>
        );
      })}
    </svg>
  );
}

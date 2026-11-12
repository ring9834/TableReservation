"use client";

import { useId } from "react";
import { useTranslation } from "react-i18next";
import { useGetTableSuggestion } from "@/api/endpoints/seating/seating";
import type { TableSuggestionResponse } from "@/api/model";
import { venueId } from "@/lib/config";
import styles from "@/features/host/actions.module.css";

/** The free tables that seat `partySize` now (Design §14.2 "Suggestion"); off while `partySize` is null. */
export const useFreeTables = (partySize: number | null) =>
  useGetTableSuggestion(
    venueId,
    { partySize: partySize ?? 0 },
    { query: { enabled: partySize !== null, staleTime: 0 } },
  );

type Props = Readonly<{
  suggestion: TableSuggestionResponse;
  value: string | null;
  onChange: (tableId: string) => void;
  legend: string;
  /** The table freed for a waiting party (AC4.2), marked as such. */
  freedTableId?: string | null;
}>;

/**
 * Free fitting tables as a radio group (AC3.1, AC3.2): the API's suggestion first and marked, then the others.
 * Arrow keys move between them; each option is a 44 px target.
 */
export function TableChoice({ suggestion, value, onChange, legend, freedTableId }: Props) {
  const { t } = useTranslation();
  const name = useId();
  const tables = [suggestion.table, ...suggestion.alternatives].filter(
    (table): table is NonNullable<typeof table> => table !== null,
  );

  return (
    <fieldset className={styles.choice} data-testid="table-choice">
      <legend>{legend}</legend>
      <div className={styles.choiceOptions}>
        {tables.map((table) => (
          <label key={table.id} className={styles.choiceOption}>
            <input
              type="radio"
              name={name}
              value={table.id}
              checked={value === table.id}
              onChange={() => onChange(table.id)}
              data-testid={`choose-${table.name}`}
            />
            <span>
              <strong>{table.name}</strong> · {t("host.seats", { count: table.seats })}
              {table.id === suggestion.table?.id && (
                <span className={styles.tag}>{t("host.walkInDialog.suggested")}</span>
              )}
              {freedTableId === table.id && (
                <span className={styles.tag}>{t("waitlist.freedTable")}</span>
              )}
            </span>
          </label>
        ))}
      </div>
    </fieldset>
  );
}

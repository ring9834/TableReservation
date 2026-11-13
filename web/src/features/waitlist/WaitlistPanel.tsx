"use client";

import { useTranslation } from "react-i18next";
import type { WaitlistResponse } from "@/api/model";
import { WaitlistEntry } from "./WaitlistEntry";
import styles from "@/features/host/actions.module.css";

type Props = Readonly<{
  waitlist: WaitlistResponse | undefined;
  pending: boolean;
  error: string | null;
  tableNames: ReadonlyMap<string, string>;
  suggestedEntryId: string | null;
  freedTableId: string | null;
  onJoin: () => void;
  onDismissSuggestion: () => void;
  onSeated: (tableId: string) => void;
  onNotified: () => void;
}>;

/**
 * The waitlist (Requirements step 3 / step 4, Design §18.6): active parties in join order. When a table frees, the
 * suggested party is marked here and the table on the plan (AC4.2); the host may seat another party instead.
 */
export function WaitlistPanel({
  waitlist,
  pending,
  error,
  tableNames,
  suggestedEntryId,
  freedTableId,
  onJoin,
  onDismissSuggestion,
  onSeated,
  onNotified,
}: Props) {
  const { t } = useTranslation();
  const freedTableName = freedTableId ? tableNames.get(freedTableId) : null;
  const suggestedEntry = waitlist?.items.find((item) => item.id === suggestedEntryId);

  return (
    <section aria-labelledby="waitlist-heading" data-testid="waitlist">
      <div className={styles.sectionHeader}>
        <h2 id="waitlist-heading">
          {t("waitlist.heading")}
          {waitlist && <span className={styles.count}> ({waitlist.items.length})</span>}
        </h2>
        <button type="button" className={styles.secondaryButton} onClick={onJoin}>
          {t("waitlist.joinButton")}
        </button>
      </div>

      <div role="status" aria-live="polite">
        {suggestedEntry && freedTableName && (
          <div className={styles.suggestionBanner} data-testid="suggestion-banner">
            <p>
              {t("waitlist.suggestion", {
                table: freedTableName,
                name: suggestedEntry.name,
                count: suggestedEntry.partySize,
              })}
            </p>
            <button type="button" className={styles.secondaryButton} onClick={onDismissSuggestion}>
              {t("waitlist.dismiss")}
            </button>
          </div>
        )}
      </div>

      {pending && <p>{t("common.loading")}</p>}
      {error && (
        <p role="alert" className={styles.error}>
          {error}
        </p>
      )}
      {waitlist && waitlist.items.length === 0 && <p>{t("waitlist.empty")}</p>}
      {waitlist && waitlist.items.length > 0 && (
        <ol className={styles.entries}>
          {waitlist.items.map((entry) => (
            <WaitlistEntry
              key={entry.id}
              entry={entry}
              tableNames={tableNames}
              suggested={entry.id === suggestedEntryId}
              freedTableId={freedTableId}
              timeZone={waitlist.timeZone}
              onSeated={onSeated}
              onNotified={onNotified}
            />
          ))}
        </ol>
      )}
    </section>
  );
}

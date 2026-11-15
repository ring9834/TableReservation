"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useId, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { useSetMockPosOnline } from "@/api/endpoints/demo/demo";
import {
  getGetPosMessagesQueryKey,
  getGetPosStatusQueryKey,
  useGetPosMessages,
  useGetPosStatus,
} from "@/api/endpoints/pos/pos";
import type { PosLogItem } from "@/api/model";
import { venueId } from "@/lib/config";
import { formatInstantTime, formatMoney } from "@/lib/format";
import { useApiError } from "@/lib/useApiError";
import { keyFields, posFilters, posStatus, unmatchedReasonKey } from "./posLog.mjs";
import { useFreshIds } from "./useFreshIds";
import styles from "./posPanel.module.css";

type Filter = (typeof posFilters)[number];

/**
 * The presenter's POS panel (Requirements steps 6, 11 and 12; AC6.1, AC6.2, AC6.5, QA-5; Design §6.2, §6.7, §18.9):
 * the mock POS's online switch with the number of messages waiting, and the POS log, newest first. Live through
 * `posLogChanged` (every filter refetches); a reconnect refetches everything. A row expands to the full message JSON
 * as the API returns it (phone numbers already masked), one row at a time.
 */
export function PosPanel() {
  const { t } = useTranslation();
  const messageFor = useApiError();
  const [filter, setFilter] = useState<Filter>("all");
  const [expandedId, setExpandedId] = useState<string | null>(null);

  const log = useGetPosMessages(venueId, filter === "all" ? undefined : { status: filter });
  // Also the count next to the switch; the "Waiting" filter shares this query.
  const waiting = useGetPosMessages(venueId, { status: "waiting" });
  const items = log.data?.items;
  const fresh = useFreshIds(items);

  return (
    <section className={styles.panel} aria-labelledby="pos-heading" data-testid="pos-panel">
      <header className={styles.header}>
        <h2 id="pos-heading">{t("pos.heading")}</h2>
        <PosSwitch />
        {waiting.data && (
          <p className={styles.waitingCount} data-testid="pos-waiting-count">
            {t("pos.waitingCount", { count: waiting.data.items.length })}
          </p>
        )}
      </header>

      <fieldset className={styles.filters}>
        <legend>{t("pos.filterLabel")}</legend>
        {posFilters.map((option) => (
          <label key={option} className={styles.filter}>
            <input
              type="radio"
              name="pos-filter"
              value={option}
              checked={filter === option}
              onChange={() => setFilter(option)}
            />
            <span>{t(`pos.filter.${option}`)}</span>
          </label>
        ))}
      </fieldset>

      {log.isPending && <p className={styles.state}>{t("common.loading")}</p>}
      {log.error && (
        <p role="alert" className={`${styles.state} ${styles.error}`}>
          {messageFor(log.error)}
        </p>
      )}

      <ol
        className={styles.log}
        role="log"
        aria-live="polite"
        aria-relevant="additions"
        aria-label={t("pos.logLabel")}
      >
        {items?.map((item) => (
          <PosRow
            key={item.id}
            item={item}
            timeZone={log.data!.timeZone}
            fresh={fresh.has(item.id)}
            expanded={expandedId === item.id}
            onToggle={() => setExpandedId((current) => (current === item.id ? null : item.id))}
          />
        ))}
      </ol>
      {items && items.length === 0 && (
        <p className={styles.state} data-testid="pos-empty">
          {filter === "all" ? t("pos.empty") : t("pos.emptyFiltered")}
        </p>
      )}
    </section>
  );
}

/**
 * The mock POS's online switch (Design §6.7, QA-5): colour, text and icon together, so it never relies on colour
 * alone. Disabled while the request runs; afterwards the status and the log refetch (going online sends the waiting
 * messages at once).
 */
function PosSwitch() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const messageFor = useApiError();
  const status = useGetPosStatus(venueId);
  const setOnline = useSetMockPosOnline();
  const [failure, setFailure] = useState<string | null>(null);
  const online = status.data?.online;

  const toggle = async () => {
    if (online === undefined) return;
    setFailure(null);
    try {
      await setOnline.mutateAsync({ data: { online: !online } });
    } catch (error) {
      setFailure(messageFor(error));
    } finally {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: getGetPosStatusQueryKey(venueId) }),
        queryClient.invalidateQueries({ queryKey: getGetPosMessagesQueryKey(venueId) }),
      ]);
    }
  };

  const busy = setOnline.isPending || status.isFetching;
  return (
    <div className={styles.switchRow}>
      <button
        type="button"
        role="switch"
        aria-checked={online ?? false}
        aria-label={t("pos.switchLabel")}
        aria-busy={busy}
        disabled={online === undefined || setOnline.isPending}
        className={`${styles.switch} ${online === false ? styles.offline : styles.online}`}
        onClick={() => void toggle()}
        data-testid="pos-switch"
      >
        <span aria-hidden="true" className={styles.switchIcon}>
          {online === false ? "✕" : "✓"}
        </span>
        <span>{online === false ? t("pos.offline") : t("pos.online")}</span>
      </button>
      {failure && (
        <p role="alert" className={styles.error}>
          {failure}
        </p>
      )}
    </div>
  );
}

type RowProps = Readonly<{
  item: PosLogItem;
  timeZone: string;
  fresh: boolean;
  expanded: boolean;
  onToggle: () => void;
}>;

/** One POS message: a button with the summary line (AC6.1); expanded, the full JSON and "Copy JSON" (AC6.2). */
function PosRow({ item, timeZone, fresh, expanded, onToggle }: RowProps) {
  const { t, i18n } = useTranslation();
  const detailsId = useId();
  const status = posStatus(item.status);
  const fields = keyFields({
    messageType: item.messageType,
    tableNames: item.tableNames,
    payload: item.payload as unknown,
  });
  const money = (cents: number) => formatMoney(cents, fields.currency, i18n.language);
  const summary = [
    fields.tables,
    fields.partySize === null
      ? null
      : fields.partySize === 1
        ? t("pos.guestsOne")
        : t("pos.guestsMany", { count: fields.partySize }),
    fields.name,
    fields.phone,
    fields.amountCents === null ? null : money(fields.amountCents),
  ].filter((part): part is string => part !== null);
  const statusText =
    status.key === "unmatched"
      ? t("pos.status.unmatched", {
          reason: t(`pos.unmatchedReason.${unmatchedReasonKey(item.unmatchedReason)}`),
        })
      : t(`pos.status.${status.key}`);

  return (
    <li
      className={`${styles.row} ${fresh ? styles.fresh : ""}`}
      data-testid="pos-message"
      data-message-id={item.id}
      data-sequence={item.venueSequence}
      data-type={item.messageType}
      data-status={item.status}
      data-direction={item.direction}
      data-booking-id={item.bookingId ?? undefined}
    >
      <button
        type="button"
        className={styles.summary}
        aria-expanded={expanded}
        aria-controls={detailsId}
        onClick={onToggle}
      >
        <span className={styles.line}>
          <time dateTime={item.createdAt} className={styles.time}>
            {formatInstantTime(item.createdAt, timeZone, i18n.language)}
          </time>
          <span className={styles.direction} data-testid="pos-direction">
            {item.direction === "inbound" ? t("pos.fromPos") : t("pos.toPos")}
          </span>
          <span className={styles.type}>{item.messageType}</span>
          <span className={styles.sequence}>{t("pos.sequence", { n: item.venueSequence })}</span>
        </span>
        {summary.length > 0 && (
          <span className={styles.fields} data-testid="pos-fields">
            {summary.join(" · ")}
          </span>
        )}
        <span className={`${styles.badge} ${styles[status.tone]}`} data-testid="pos-status">
          {statusText}
        </span>
        <span aria-hidden="true" className={styles.chevron}>
          {expanded ? "▾" : "▸"}
        </span>
      </button>
      {expanded && <PosJson id={detailsId} payload={item.payload} />}
    </li>
  );
}

const copiedForMs = 2_000;

/** The full message, pretty-printed and scrollable, with "Copy JSON" (AC6.2). Message JSON is never translated. */
function PosJson({ id, payload }: Readonly<{ id: string; payload: unknown }>) {
  const { t } = useTranslation();
  const [copy, setCopy] = useState<"idle" | "copied" | "failed">("idle");
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const json = JSON.stringify(payload, null, 2);

  useEffect(() => () => clearTimeout(timer.current), []);

  const copyJson = async () => {
    clearTimeout(timer.current);
    try {
      await navigator.clipboard.writeText(json);
      setCopy("copied");
    } catch {
      setCopy("failed");
    }
    timer.current = setTimeout(() => setCopy("idle"), copiedForMs);
  };

  return (
    <div id={id} className={styles.details}>
      <div className={styles.detailsHeader}>
        <span className={styles.jsonLabel}>{t("pos.json")}</span>
        <button
          type="button"
          className={styles.copy}
          onClick={() => void copyJson()}
          data-testid="copy-json"
          data-state={copy}
        >
          {copy === "copied"
            ? `${t("pos.copied")} ✓`
            : copy === "failed"
              ? t("pos.copyFailed")
              : t("pos.copy")}
        </button>
        <span role="status" className={styles.visuallyHidden}>
          {copy === "copied" ? t("pos.copied") : copy === "failed" ? t("pos.copyFailed") : ""}
        </span>
      </div>
      {/* Focusable, so the keyboard can scroll it. */}
      <pre className={styles.json} tabIndex={0} lang="en" data-testid="pos-json">
        {json}
      </pre>
    </div>
  );
}

"use client";

import { useLayoutEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import type { MessageLogItem, MessageLogResponse } from "@/api/model";
import { formatInstantTime } from "@/lib/format";
import { messageStatus } from "./messageStatus.mjs";
import styles from "./phonePanel.module.css";

type Props = Readonly<{
  log: MessageLogResponse | undefined;
  pending: boolean;
  error: string | null;
  /** Shown as the sender at the top of the phone. */
  venueName: string;
}>;

/** How close to the bottom (px) still counts as "at the newest message", so a new one scrolls into view. */
const stickToBottomPx = 40;

const ticks: Record<string, string> = { sent: "✓", delivered: "✓✓" };

/**
 * The presenter's phone panel (Requirements steps 1, 3 and 4; AC1.6, AC4.3; Design §7, §18.7): the texts the demo
 * "sends", shown like an SMS app. Oldest at the top; the newest scrolls into view unless the presenter has scrolled up.
 * Bodies are shown exactly as sent and never translated; labels follow the screen's language.
 */
export function PhonePanel({ log, pending, error, venueName }: Props) {
  const { t } = useTranslation();
  const items = log?.items;
  const list = useRef<HTMLOListElement>(null);
  const atBottom = useRef(true);

  // Messages that arrived after the first load get a short highlight. Worked out while rendering when the items
  // change (React's pattern for state that follows other data); an ID stays marked, so the CSS animation runs once.
  const [seen, setSeen] = useState<{
    items: MessageLogItem[] | undefined;
    ids: Set<string> | null;
  }>({
    items: undefined,
    ids: null,
  });
  const [fresh, setFresh] = useState<ReadonlySet<string>>(new Set());
  if (items && items !== seen.items) {
    const ids = new Set(items.map((item) => item.id));
    if (seen.ids) {
      const arrived = items.filter((item) => !seen.ids!.has(item.id)).map((item) => item.id);
      if (arrived.length > 0) setFresh(new Set([...fresh, ...arrived]));
    }
    setSeen({ items, ids });
  }

  useLayoutEffect(() => {
    const element = list.current;
    if (element && atBottom.current) element.scrollTop = element.scrollHeight;
  }, [items]);

  const onScroll = () => {
    const element = list.current;
    if (!element) return;
    atBottom.current =
      element.scrollHeight - element.scrollTop - element.clientHeight <= stickToBottomPx;
  };

  return (
    <section className={styles.phone} aria-labelledby="phone-heading" data-testid="phone-panel">
      <div className={styles.notch} aria-hidden="true" />
      <header className={styles.top}>
        <h2 id="phone-heading">{t("phone.heading")}</h2>
        <p className={styles.sender}>{venueName}</p>
      </header>

      {pending && <p className={styles.state}>{t("common.loading")}</p>}
      {error && (
        <p role="alert" className={`${styles.state} ${styles.error}`}>
          {error}
        </p>
      )}

      <ol
        ref={list}
        className={styles.messages}
        role="log"
        aria-live="polite"
        aria-relevant="additions"
        aria-labelledby="phone-heading"
        onScroll={onScroll}
        tabIndex={0}
      >
        {items?.map((item) => (
          <Bubble key={item.id} item={item} timeZone={log!.timeZone} fresh={fresh.has(item.id)} />
        ))}
      </ol>
      {items && items.length === 0 && (
        <p className={styles.state} data-testid="phone-empty">
          {t("phone.empty")}
        </p>
      )}
    </section>
  );
}

function Bubble({
  item,
  timeZone,
  fresh,
}: Readonly<{ item: MessageLogItem; timeZone: string; fresh: boolean }>) {
  const { t, i18n } = useTranslation();
  const status = messageStatus(item.status, item.lastError);
  const tick = item.status ? ticks[item.status] : undefined;

  return (
    <li
      className={`${styles.bubble} ${fresh ? styles.fresh : ""}`}
      data-testid="phone-message"
      data-message-id={item.id}
      data-subject-id={item.subjectId}
      data-purpose={item.purpose}
      data-status={item.status ?? undefined}
    >
      <p className={styles.meta}>
        {item.mobileMasked && <span>{t("phone.to", { mobile: item.mobileMasked })}</span>}
        <span className={styles.purpose}>{t(`phone.purpose.${item.purpose}`)}</span>
      </p>
      {/* The text exactly as sent, in the guest's language whatever the screen's. */}
      <p className={styles.body} lang={item.language === "zh" ? "zh-CN" : "en"}>
        {item.body}
      </p>
      <p className={styles.footer}>
        {item.sentAt && (
          <time dateTime={item.sentAt}>
            {formatInstantTime(item.sentAt, timeZone, i18n.language)}
          </time>
        )}
        <span className={styles[status.tone]} data-testid="message-status">
          {t(`phone.status.${status.key}`)}
          {tick && <span aria-hidden="true"> {tick}</span>}
        </span>
      </p>
    </li>
  );
}

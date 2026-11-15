"use client";

import { useLayoutEffect, useRef, useState, type MouseEvent } from "react";
import { useTranslation } from "react-i18next";
import type { MessageLogItem, MessageLogResponse } from "@/api/model";
import { formatInstantTime } from "@/lib/format";
import { messageStatus } from "./messageStatus.mjs";
import { managePath, segments } from "./phoneLinks.mjs";
import { useFreshIds } from "./useFreshIds";
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
 *
 * Since M10b (Design §18.10) a text with links shows them short (still links) and its actions as buttons, labelled in
 * the text's language: Reminder → Confirm / Cancel, Confirmation → Manage booking. A tap opens the guest's manage page
 * inside the phone (a same-origin frame of `/manage/?token=…`), with "‹ Messages" to come back. The frame makes the
 * same requests as a guest's browser: it only reads the booking; a change happens on a tap inside it.
 */
export function PhonePanel({ log, pending, error, venueName }: Props) {
  const { t } = useTranslation();
  const items = log?.items;
  const list = useRef<HTMLOListElement>(null);
  const atBottom = useRef(true);
  const backButton = useRef<HTMLButtonElement>(null);
  const [openPath, setOpenPath] = useState<string | null>(null);

  const open = (path: string) => {
    setOpenPath(path);
    requestAnimationFrame(() => backButton.current?.focus());
  };
  const back = () => {
    setOpenPath(null);
    // Back on the list: show the newest texts (e.g. the cancellation that arrived meanwhile).
    requestAnimationFrame(() => {
      const element = list.current;
      if (!element) return;
      element.scrollTop = element.scrollHeight;
      atBottom.current = true;
      element.focus();
    });
  };

  // Messages that arrived after the first load get a short highlight.
  const fresh = useFreshIds(items);

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

      {openPath && (
        <div className={styles.browser} data-testid="phone-browser">
          <div className={styles.browserBar}>
            <button ref={backButton} type="button" className={styles.back} onClick={back}>
              <span aria-hidden="true">‹ </span>
              {t("phone.back")}
            </button>
            <a href={openPath} target="_blank" rel="noopener noreferrer" className={styles.newTab}>
              {t("phone.newTab")}
            </a>
          </div>
          <iframe
            key={openPath}
            src={openPath}
            title={t("phone.browserTitle")}
            referrerPolicy="no-referrer"
            className={styles.frame}
          />
        </div>
      )}

      <div className={styles.screen} hidden={openPath !== null}>
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
            <Bubble
              key={item.id}
              item={item}
              timeZone={log!.timeZone}
              fresh={fresh.has(item.id)}
              onOpen={open}
            />
          ))}
        </ol>
        {items && items.length === 0 && (
          <p className={styles.state} data-testid="phone-empty">
            {t("phone.empty")}
          </p>
        )}
      </div>
    </section>
  );
}

/** Opens a link in the phone on a plain click; a modified click (new tab, new window) is left to the browser. */
const openInPhone = (
  event: MouseEvent<HTMLAnchorElement>,
  path: string,
  onOpen: (path: string) => void,
) => {
  if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey)
    return;
  event.preventDefault();
  onOpen(path);
};

function Bubble({
  item,
  timeZone,
  fresh,
  onOpen,
}: Readonly<{
  item: MessageLogItem;
  timeZone: string;
  fresh: boolean;
  onOpen: (path: string) => void;
}>) {
  const { t, i18n } = useTranslation();
  const textLanguage = item.language === "zh" ? "zh-CN" : "en";
  const actions = item.actions.flatMap((action) => {
    const path = managePath(action.url);
    return path ? [{ kind: action.kind, path }] : [];
  });
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
      data-language={item.language}
    >
      <p className={styles.meta}>
        {item.mobileMasked && <span>{t("phone.to", { mobile: item.mobileMasked })}</span>}
        <span className={styles.purpose}>{t(`phone.purpose.${item.purpose}`)}</span>
      </p>
      {/* The text exactly as sent, in the guest's language whatever the screen's. */}
      <p className={styles.body} lang={textLanguage}>
        {segments(item.body).map((part, index) => {
          if (part.kind === "text") return part.text;
          const path = managePath(part.url);
          return (
            <a
              key={index}
              href={path ?? part.url}
              target="_blank"
              rel="noopener noreferrer"
              referrerPolicy="no-referrer"
              className={styles.link}
              onClick={path ? (event) => openInPhone(event, path, onOpen) : undefined}
            >
              {part.label}
            </a>
          );
        })}
      </p>
      {actions.length > 0 && (
        <div className={styles.actions} lang={textLanguage}>
          {actions.map((action) => (
            <button
              key={action.kind}
              type="button"
              className={styles.action}
              data-action={action.kind}
              onClick={() => onOpen(action.path)}
            >
              {t(`phone.actions.${action.kind}`, { lng: textLanguage })}
            </button>
          ))}
        </div>
      )}
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

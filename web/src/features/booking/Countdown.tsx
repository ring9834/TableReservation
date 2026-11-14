"use client";

import { useState } from "react";
import { useTranslation } from "react-i18next";
import { announcementFor, formatCountdown } from "./payment.mjs";
import styles from "./booking.module.css";

const announcementKey = {
  300: "pay.announceFiveMinutes",
  60: "pay.announceOneMinute",
  0: "pay.holdEnded",
};

/**
 * "We'll hold your table for 9:41" (M8b). Minutes and seconds left, never a clock time: the demo's story time differs
 * from real time. The visible text ticks every second but is not a live region; a separate polite status line speaks
 * only at 5 minutes, 1 minute and the end, so a screen reader isn't interrupted every second.
 */
export function Countdown({ seconds }: Readonly<{ seconds: number }>) {
  const { t } = useTranslation();
  const [previous, setPrevious] = useState<number | null>(null);
  const [mark, setMark] = useState<keyof typeof announcementKey | null>(null);

  // Derived from the previous render's value (React's "storing information from previous renders" pattern).
  if (previous !== seconds) {
    setPrevious(seconds);
    const crossed = announcementFor(previous, seconds);
    if (crossed !== null) {
      setMark(crossed);
    }
  }

  return (
    <>
      <p className={styles.countdown} data-testid="hold-countdown">
        {t("pay.holdFor")}{" "}
        <span className={styles.countdownTime} data-testid="hold-countdown-time">
          {formatCountdown(seconds)}
        </span>
      </p>
      <p role="status" aria-live="polite" className={styles.visuallyHidden}>
        {mark === null ? "" : t(announcementKey[mark])}
      </p>
    </>
  );
}

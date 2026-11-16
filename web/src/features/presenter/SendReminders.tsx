"use client";

import { useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { useSendRemindersNow } from "@/api/endpoints/demo/demo";
import type { SendRemindersResponse } from "@/api/model";
import { useApiError } from "@/lib/useApiError";
import { reminderSummary } from "./reminders.mjs";
import styles from "./presenter.module.css";

/**
 * "Send reminders now" (M10b, AC5.1, BR-13, Design §7.3, §18.10): stands in for the pilot's 24-hour scheduler. The
 * API queues a Reminder for each eligible booking; the phone shows them through `messageLogChanged`. The status line
 * says how many went and why the others were skipped ("2 reminders sent · 4 skipped: 1 already confirmed, 3 starting
 * within 2 hours"); a second press finds nothing new. Part of the presenter's demo controls since M11a.
 */
export function SendReminders() {
  const { t } = useTranslation();
  const messageFor = useApiError();
  const send = useSendRemindersNow();
  const [answer, setAnswer] = useState<SendRemindersResponse | null>(null);
  const [error, setError] = useState<unknown>(null);
  const inFlight = useRef(false);

  const press = async () => {
    if (inFlight.current) return;
    inFlight.current = true;
    setError(null);
    try {
      setAnswer(await send.mutateAsync());
    } catch (failure) {
      setAnswer(null);
      setError(failure);
    } finally {
      inFlight.current = false;
    }
  };

  return (
    <div className={styles.reminders}>
      <button
        type="button"
        className={styles.demoButton}
        onClick={() => void press()}
        disabled={send.isPending}
        data-testid="send-reminders"
      >
        {send.isPending ? t("presenter.reminders.sending") : t("presenter.reminders.button")}
      </button>
      <p
        role="status"
        aria-live="polite"
        className={styles.remindersStatus}
        data-testid="reminders-status"
      >
        {answer && <SummaryText answer={answer} />}
        {error ? <span className={styles.remindersError}>{messageFor(error)}</span> : null}
      </p>
    </div>
  );
}

function SummaryText({ answer }: Readonly<{ answer: SendRemindersResponse }>) {
  const { t } = useTranslation();
  const summary = reminderSummary(answer);
  const sent =
    summary.sent === "none"
      ? t("presenter.reminders.none")
      : summary.sent === "one"
        ? t("presenter.reminders.sentOne")
        : t("presenter.reminders.sentMany", { n: summary.count });
  if (summary.skippedTotal === 0) return <>{sent}</>;
  const reasons = summary.skipped
    .map((s) => t(`presenter.reminders.reason.${s.reason}`, { n: s.count }))
    .join(t("presenter.reminders.separator"));
  return (
    <>
      {sent}
      {" · "}
      {t("presenter.reminders.skipped", { n: summary.skippedTotal, reasons })}
    </>
  );
}

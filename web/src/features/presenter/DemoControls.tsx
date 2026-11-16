"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useId, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import {
  useAdvanceDemoClock,
  useGetDemoClock,
  useResetDemo,
  useSetDemoClock,
} from "@/api/endpoints/demo/demo";
import { formatInstantTime } from "@/lib/format";
import { useApiError } from "@/lib/useApiError";
import { advanceSteps, demoNow, toggledMode } from "./demoClock.mjs";
import { SendReminders } from "./SendReminders";
import styles from "./presenter.module.css";

/**
 * The presenter's demo controls (M11a, Design §17.2), one "Demo" group in the header:
 * - **Reset** with an inline confirm step ("Reset the demo to 18:25 with the sample bookings?"), "Resetting…" while it
 *   runs, and `503 demo_reset_busy` as "Busy, try again in a moment". The API pushes `demoReset`, so every screen
 *   refetches.
 * - **The demo time** ("6:25 pm · fixed" / "6:40 pm · running"); while running it ticks locally between refetches.
 *   "+5 min" / "+15 min" move business time forward; "Run" / "Freeze" switch the mode. Only business time moves
 *   (rule 14); the API pushes `demoClockChanged` and every screen refetches.
 * - **Send reminders now** (M10b), unchanged.
 * The POS switch stays in the POS panel.
 */
export function DemoControls() {
  const { t, i18n } = useTranslation();
  const labelId = useId();
  const messageFor = useApiError();
  const queryClient = useQueryClient();

  const clock = useGetDemoClock();
  const advance = useAdvanceDemoClock();
  const setClock = useSetDemoClock();
  const reset = useResetDemo();

  const [confirming, setConfirming] = useState(false);
  const [status, setStatus] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const inFlight = useRef(false);

  // A running clock ticks on the screen every second; a fixed one needs no timer.
  const [nowMs, setNowMs] = useState(() => Date.now());
  const running = clock.data?.mode === "running";
  useEffect(() => {
    if (!running) return;
    const timer = setInterval(() => setNowMs(Date.now()), 1_000);
    return () => clearInterval(timer);
  }, [running]);

  /** Runs one control at a time; afterwards every query refetches (business time moved, or the data was reset). */
  const run = async (action: () => Promise<unknown>, done?: string) => {
    if (inFlight.current) return;
    inFlight.current = true;
    setError(null);
    setStatus(null);
    try {
      await action();
      if (done) setStatus(done);
      void queryClient.invalidateQueries();
    } catch (failure) {
      setError(failure);
    } finally {
      inFlight.current = false;
    }
  };

  const busy = advance.isPending || setClock.isPending || reset.isPending;
  const data = clock.data;
  const label = data
    ? t("presenter.demo.clock", {
        time: formatInstantTime(
          demoNow(data, clock.dataUpdatedAt, nowMs),
          data.timeZone,
          i18n.language,
        ),
        mode: t(`presenter.demo.mode.${data.mode}`),
      })
    : "…";

  return (
    <div role="group" aria-labelledby={labelId} className={styles.demo} data-testid="demo-controls">
      <span id={labelId} className={styles.demoLabel}>
        {t("presenter.demo.label")}
      </span>

      {confirming ? (
        <div className={styles.demoConfirm} data-testid="reset-confirm">
          <span>{t("presenter.demo.resetConfirm")}</span>
          <button
            type="button"
            className={`${styles.demoButton} ${styles.demoDanger}`}
            disabled={busy}
            onClick={() =>
              void run(async () => {
                await reset.mutateAsync();
                setConfirming(false);
              }, t("presenter.demo.resetDone"))
            }
            data-testid="reset-yes"
          >
            {reset.isPending ? t("presenter.demo.resetting") : t("presenter.demo.resetYes")}
          </button>
          <button
            type="button"
            className={styles.demoButton}
            disabled={reset.isPending}
            onClick={() => setConfirming(false)}
          >
            {t("presenter.demo.resetNo")}
          </button>
        </div>
      ) : (
        <button
          type="button"
          className={styles.demoButton}
          disabled={busy}
          onClick={() => {
            setStatus(null);
            setError(null);
            setConfirming(true);
          }}
          data-testid="reset-demo"
        >
          {t("presenter.demo.reset")}
        </button>
      )}

      <span className={styles.demoClock}>
        <span className={styles.visuallyHidden}>{t("presenter.demo.clockLabel")}: </span>
        <span data-testid="demo-clock" data-mode={data?.mode} data-local-time={data?.localTime}>
          {label}
        </span>
      </span>
      {advanceSteps.map((minutes) => (
        <button
          key={minutes}
          type="button"
          className={styles.demoButton}
          disabled={!data || busy}
          aria-label={t(`presenter.demo.plus${minutes}Label`)}
          onClick={() => void run(() => advance.mutateAsync({ data: { minutes } }))}
          data-testid={`advance-${minutes}`}
        >
          {t(`presenter.demo.plus${minutes}`)}
        </button>
      ))}
      <button
        type="button"
        className={styles.demoButton}
        disabled={!data || busy}
        aria-label={running ? t("presenter.demo.freezeLabel") : t("presenter.demo.runLabel")}
        onClick={() =>
          data && void run(() => setClock.mutateAsync({ data: { mode: toggledMode(data.mode) } }))
        }
        data-testid="clock-toggle"
      >
        {running ? t("presenter.demo.freeze") : t("presenter.demo.run")}
      </button>

      <SendReminders />

      <p
        role="status"
        aria-live="polite"
        className={styles.remindersStatus}
        data-testid="demo-status"
      >
        {status}
        {error ? <span className={styles.remindersError}>{messageFor(error)}</span> : null}
      </p>
    </div>
  );
}

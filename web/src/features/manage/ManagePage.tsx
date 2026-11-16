"use client";

import { useEffect, useId, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import {
  useCancelGuestBooking,
  useConfirmGuestBooking,
  useGetGuestBooking,
} from "@/api/endpoints/guest-bookings/guest-bookings";
import type { GuestActionResponse, GuestBookingResponse } from "@/api/model";
import { partyLabel } from "@/features/booking/party";
import { DepositLine } from "@/features/bookings/DepositLine";
import { applyLanguage } from "@/i18n/i18n";
import { LanguageSwitch } from "@/i18n/LanguageSwitch";
import { isApiError } from "@/lib/apiFetch";
import { formatInstantTime, formatLocalDate, formatLocalTime, formatMoney } from "@/lib/format";
import { useApiError } from "@/lib/useApiError";
import {
  cancelConsequence,
  errorKind,
  pageLanguage,
  resultKeys,
  showsLateWarning,
  showsTerms,
  visibleActions,
} from "./manage.mjs";
import styles from "./manage.module.css";

type Action = "confirm" | "cancel";

/**
 * The guest's requests carry no cookie: inside the presenter's phone (same origin) the API sees exactly what a guest's
 * own browser would send (Design §18.10).
 */
const guestRequest = { credentials: "omit" } as const satisfies RequestInit;

/** The token from `?token=…`, read once after hydration (static export); `undefined` until then. */
const readToken = () => new URLSearchParams(window.location.search).get("token");

/**
 * `/manage/?token=…` (M10b, Requirements step 5, AC5.3–AC5.5, BR-07, BR-13; Design §7, §7.3, §18.10): what a guest
 * sees after tapping a link in a reminder or confirmation text. Opening the link only reads the booking (GET); a change
 * happens only on the guest's tap (POST), which the API checks against the token again. The token stays in the address
 * bar (a reload works), is never logged and goes only to the guest endpoints; the page sends no referrer.
 */
export function ManagePage() {
  const { t, i18n } = useTranslation();
  const messageFor = useApiError();
  const id = useId();
  const [token, setToken] = useState<string | null | undefined>(undefined);
  /** Shown inside the presenter's phone (a frame): a little room under its "‹ Messages" bar. */
  const [framed, setFramed] = useState(false);
  const [cancelStep, setCancelStep] = useState(false);
  const [result, setResult] = useState<{ action: Action; response: GuestActionResponse } | null>(
    null,
  );
  const [changed, setChanged] = useState(false);
  const [actionError, setActionError] = useState<unknown>(null);
  const inFlight = useRef(false);
  const languageApplied = useRef(false);

  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- read once from the URL after hydration
    setToken(readToken());
    setFramed(window.self !== window.top);
  }, []);

  const booking = useGetGuestBooking(token ? { token } : undefined, {
    query: { enabled: !!token },
    request: guestRequest,
  });
  const confirm = useConfirmGuestBooking({ request: guestRequest });
  const cancel = useCancelGuestBooking({ request: guestRequest });
  const busy = confirm.isPending || cancel.isPending;

  // Open in the booking's language once; the switch works afterwards, and nothing is stored.
  useEffect(() => {
    if (booking.data && !languageApplied.current) {
      languageApplied.current = true;
      applyLanguage(pageLanguage(booking.data.language));
    }
  }, [booking.data]);

  const act = async (action: Action) => {
    if (!token || inFlight.current) return; // a second tap in the same frame
    inFlight.current = true;
    setActionError(null);
    try {
      const response = await (action === "confirm" ? confirm : cancel).mutateAsync({
        data: { token },
      });
      setResult({ action, response });
      setCancelStep(false);
    } catch (error) {
      if (isApiError(error) && errorKind(error.code) === "changed") {
        setChanged(true);
        setCancelStep(false);
      } else {
        setActionError(error);
      }
    } finally {
      inFlight.current = false;
      void booking.refetch();
    }
  };

  const language = i18n.resolvedLanguage ?? "en";
  const linkError =
    (isApiError(booking.error) && errorKind(booking.error.code) === "link" && booking.error.code) ||
    (isApiError(actionError) && errorKind(actionError.code) === "link" && actionError.code) ||
    (token === null && "invalid_link") ||
    null;

  return (
    <div className={styles.page} data-testid="manage-page" data-framed={framed || undefined}>
      <header className={styles.header}>
        <LanguageSwitch remember={false} />
      </header>
      <main className={styles.main} aria-labelledby={`${id}-heading`}>
        <h1 id={`${id}-heading`}>
          {booking.data
            ? t("manage.heading", { venue: booking.data.venueName })
            : t("manage.headingPlain")}
        </h1>

        {linkError ? (
          <p role="alert" className={styles.alert} data-testid="manage-error">
            {t(`errors.${linkError}`)}
          </p>
        ) : booking.data ? (
          <BookingView
            booking={booking.data}
            language={language}
            changed={changed}
            result={result}
            cancelStep={cancelStep}
            busy={busy}
            pending={confirm.isPending ? "confirm" : cancel.isPending ? "cancel" : null}
            error={actionError ? messageFor(actionError) : null}
            onConfirm={() => void act("confirm")}
            onCancel={() => setCancelStep(true)}
            onKeep={() => setCancelStep(false)}
            onCancelConfirmed={() => void act("cancel")}
          />
        ) : booking.error ? (
          <p role="alert" className={styles.alert} data-testid="manage-error">
            {messageFor(booking.error)}
          </p>
        ) : (
          <p className={styles.muted}>{t("common.loading")}</p>
        )}
      </main>
    </div>
  );
}

type ViewProps = Readonly<{
  booking: GuestBookingResponse;
  language: string;
  changed: boolean;
  result: { action: Action; response: GuestActionResponse } | null;
  cancelStep: boolean;
  busy: boolean;
  pending: Action | null;
  error: string | null;
  onConfirm: () => void;
  onCancel: () => void;
  onKeep: () => void;
  onCancelConfirmed: () => void;
}>;

function BookingView({
  booking,
  language,
  changed,
  result,
  cancelStep,
  busy,
  pending,
  error,
  onConfirm,
  onCancel,
  onKeep,
  onCancelConfirmed,
}: ViewProps) {
  const { t } = useTranslation();
  const actions = changed ? [] : visibleActions(booking);
  const consequence = cancelConsequence(booking);
  const money = (cents: number, currency: string) => formatMoney(cents, currency, language);
  const deposit = booking.deposit;

  return (
    <>
      <dl className={styles.details}>
        <div>
          <dt>{t("manage.reference")}</dt>
          <dd className={styles.reference}>{booking.reference}</dd>
        </div>
        <div>
          <dt>{t("manage.when")}</dt>
          <dd>
            {t("manage.dateTime", {
              date: formatLocalDate(booking.localDate, language),
              time: formatLocalTime(booking.localTime, language),
            })}
          </dd>
        </div>
        <div>
          <dt>{t("manage.party")}</dt>
          <dd>{partyLabel(t, booking.partySize)}</dd>
        </div>
        <div>
          <dt>{t("manage.statusLabel")}</dt>
          <dd data-testid="manage-status" data-status={booking.status}>
            {t(`manage.status.${booking.status}`)}
          </dd>
        </div>
        {deposit && (
          <div>
            <dt>{t("manage.deposit")}</dt>
            <dd>
              <DepositLine
                deposit={deposit.amount}
                depositStatus={deposit.status}
                bookingStatus={booking.status}
                refundDecision={booking.cancellationRefund}
              />
            </dd>
          </div>
        )}
      </dl>

      {showsTerms(booking) && (
        <p className={styles.terms} data-testid="manage-terms">
          {termsText(t, booking, language)}
        </p>
      )}

      {showsLateWarning(booking) && !result && (
        <p className={styles.warning} data-testid="manage-late-warning">
          {t("manage.lateWarning", {
            amount: money(deposit!.amount.amountCents, deposit!.amount.currency),
          })}
        </p>
      )}

      <div role="status" aria-live="polite">
        {result && (
          <div className={styles.result} data-testid="manage-result" data-action={result.action}>
            {resultKeys(result.action, result.response).map((key) => (
              <p key={key}>{t(`manage.result.${key}`)}</p>
            ))}
          </div>
        )}
        {changed && (
          <p className={styles.notice} data-testid="manage-changed">
            {t("manage.changed", { status: t(`manage.status.${booking.status}`) })}
          </p>
        )}
      </div>

      {error && (
        <p role="alert" className={styles.alert}>
          {error}
        </p>
      )}

      {cancelStep && actions.includes("cancel") ? (
        <section className={styles.confirmStep} aria-labelledby="cancel-heading">
          <h2 id="cancel-heading">{t("manage.cancelHeading")}</h2>
          <p data-testid="manage-consequence" data-consequence={consequence.key}>
            {t(`manage.consequence.${consequence.key}`, {
              amount:
                consequence.amountCents !== undefined
                  ? money(consequence.amountCents, consequence.currency!)
                  : "",
            })}
          </p>
          <div className={styles.buttons}>
            <button
              type="button"
              className={styles.danger}
              onClick={onCancelConfirmed}
              disabled={busy}
              data-testid="manage-cancel-confirm"
            >
              {pending === "cancel" ? t("manage.cancelling") : t("manage.cancelConfirm")}
            </button>
            <button type="button" className={styles.secondary} onClick={onKeep} disabled={busy}>
              {t("manage.keep")}
            </button>
          </div>
        </section>
      ) : (
        actions.length > 0 && (
          <div className={styles.buttons}>
            {actions.includes("confirm") && (
              <button
                type="button"
                className={styles.primary}
                onClick={onConfirm}
                disabled={busy}
                data-testid="manage-confirm"
              >
                {pending === "confirm" ? t("manage.confirming") : t("manage.confirm")}
              </button>
            )}
            {actions.includes("cancel") && (
              <button
                type="button"
                className={styles.secondary}
                onClick={onCancel}
                disabled={busy}
                data-testid="manage-cancel"
              >
                {t("manage.cancel")}
              </button>
            )}
          </div>
        )
      )}
    </>
  );
}

/** The booking's own cancellation terms in plain words (Design §2.6), with the time in the venue's zone. */
function termsText(
  t: ReturnType<typeof useTranslation>["t"],
  booking: GuestBookingResponse,
  language: string,
) {
  const until = formatInstantTime(booking.freeCancellationUntil, booking.timeZone, language);
  const deposit = booking.deposit;
  if (!deposit) return t("manage.terms.noDeposit", { time: until });
  const amount = formatMoney(deposit.amount.amountCents, deposit.amount.currency, language);
  return booking.terms.refundWhenInTime === "full"
    ? t("manage.terms.deposit", { time: until, amount })
    : t("manage.terms.depositKept", { amount });
}

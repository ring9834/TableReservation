"use client";

import { Elements, PaymentElement, useElements, useStripe } from "@stripe/react-stripe-js";
import type { Appearance, Stripe } from "@stripe/stripe-js";
import { loadStripe } from "@stripe/stripe-js/pure";
import { useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { apiLanguage } from "@/i18n/i18n";
import { payControls } from "./payment.mjs";
import styles from "./booking.module.css";

// `/pure` loads Stripe.js from js.stripe.com only when the payment step asks for it, never on other pages.
const stripeByKey = new Map<string, Promise<Stripe | null>>();
const stripeFor = (publishableKey: string) => {
  let stripe = stripeByKey.get(publishableKey);
  if (!stripe) {
    stripe = loadStripe(publishableKey);
    stripeByKey.set(publishableKey, stripe);
  }
  return stripe;
};

/** Stripe's frame in our colours: the values of the page's colour tokens (Stripe can't read CSS variables). */
function appearanceFromTokens(): Appearance {
  const css = getComputedStyle(document.documentElement);
  const token = (name: string) => css.getPropertyValue(name).trim();
  const dark = window.matchMedia("(prefers-color-scheme: dark)").matches;
  return {
    theme: dark ? "night" : "stripe",
    variables: {
      colorPrimary: token("--accent"),
      colorText: token("--text"),
      colorTextSecondary: token("--muted"),
      colorBackground: token("--surface"),
      colorDanger: token("--error"),
      fontFamily: 'system-ui, "PingFang SC", "Microsoft YaHei", sans-serif',
      borderRadius: "8px",
      focusOutline: `2px solid ${token("--focus")}`,
    },
    rules: { ".Input": { borderColor: token("--border") } },
  };
}

type Props = Readonly<{
  publishableKey: string;
  clientSecret: string;
  bookingId: string;
  amount: string;
  secondsLeft: number | null;
  initialError: string | null;
  onBeforePay: () => void;
  onPaid: () => void;
}>;

/**
 * Stripe Elements (ADR-004 CC2): card details are typed into Stripe's own frame and never reach our servers. The
 * frame follows the page language (en / zh) and our colour tokens. "Pay" confirms with `redirect: "if_required"`; a
 * card that needs 3-D Secure leaves for the bank and comes back to /book/?bookingId=… (see `paymentReturn.ts`).
 */
export function StripePayment(props: Props) {
  const { i18n } = useTranslation();
  const locale = apiLanguage(i18n.resolvedLanguage ?? "en"); // Stripe's "zh" is Simplified Chinese
  const [appearance] = useState(appearanceFromTokens);

  return (
    <Elements
      key={locale}
      stripe={stripeFor(props.publishableKey)}
      options={{ clientSecret: props.clientSecret, appearance, locale }}
    >
      <StripeForm {...props} />
    </Elements>
  );
}

function StripeForm({ bookingId, amount, secondsLeft, initialError, onBeforePay, onPaid }: Props) {
  const { t } = useTranslation();
  const stripe = useStripe();
  const elements = useElements();
  const [paying, setPaying] = useState(false);
  const [error, setError] = useState<string | null>(initialError);
  // Set before React re-renders, so a second tap in the same frame can't confirm twice.
  const inFlight = useRef(false);
  const controls = payControls({ secondsLeft, paying, paid: false });

  const pay = async () => {
    if (inFlight.current || !stripe || !elements || controls.buttonDisabled) {
      return;
    }
    inFlight.current = true;
    setPaying(true);
    setError(null);
    onBeforePay();

    const returnUrl = new URL(
      `/book/?bookingId=${encodeURIComponent(bookingId)}`,
      window.location.origin,
    );
    const result = await stripe.confirmPayment({
      elements,
      redirect: "if_required",
      confirmParams: { return_url: returnUrl.toString() },
    });
    if (result.error) {
      // Stripe's own message, already in the frame's language (a declined card, a wrong expiry date…).
      setError(result.error.message ?? t("pay.cardFailed"));
      inFlight.current = false;
      setPaying(false);
      return;
    }
    onPaid();
  };

  if (!controls.showForm) {
    return null;
  }

  return (
    <form
      className={styles.fields}
      onSubmit={(event) => {
        event.preventDefault();
        void pay();
      }}
    >
      <div data-testid="payment-element">
        <PaymentElement options={{ layout: "tabs" }} />
      </div>
      {error && (
        <div role="alert" className={styles.alert}>
          <p>{error}</p>
        </div>
      )}
      <button
        type="submit"
        className={styles.primary}
        disabled={controls.buttonDisabled || !stripe || !elements}
        data-testid="pay-button"
      >
        {controls.buttonLabel === "paying" ? t("pay.paying") : t("pay.pay", { amount })}
      </button>
    </form>
  );
}

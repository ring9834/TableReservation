"use client";

import { useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { useSimulatePayment } from "@/api/endpoints/demo/demo";
import { useApiError } from "@/lib/useApiError";
import { payControls } from "./payment.mjs";
import styles from "./booking.module.css";

type Props = Readonly<{
  bookingId: string;
  secondsLeft: number | null;
  onPaid: () => void;
}>;

/**
 * The Fake provider's payment (M8b, Design §18.8): no card form. "Simulate payment (test mode)" asks the API to send
 * itself the signed `payment_intent.succeeded` Stripe would send, through the real webhook path; then the page polls
 * the status as it would after a real payment.
 */
export function FakePayment({ bookingId, secondsLeft, onPaid }: Props) {
  const { t } = useTranslation();
  const messageFor = useApiError();
  const simulate = useSimulatePayment();
  const [paying, setPaying] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Set before React re-renders, so a second tap in the same frame can't send twice.
  const inFlight = useRef(false);
  const controls = payControls({ secondsLeft, paying, paid: false });

  const pay = async () => {
    if (inFlight.current || controls.buttonDisabled) {
      return;
    }
    inFlight.current = true;
    setPaying(true);
    setError(null);
    try {
      await simulate.mutateAsync({ bookingId });
      onPaid();
    } catch (failure) {
      setError(messageFor(failure));
      inFlight.current = false;
      setPaying(false);
    }
  };

  if (!controls.showForm) {
    return null;
  }

  return (
    <div className={styles.fields}>
      <div className={styles.notice} data-testid="test-mode-note">
        <p>{t("pay.testModeNote")}</p>
      </div>
      {error && (
        <div role="alert" className={styles.alert}>
          <p>{error}</p>
        </div>
      )}
      <button
        type="button"
        className={styles.primary}
        disabled={controls.buttonDisabled}
        onClick={() => void pay()}
        data-testid="simulate-payment"
      >
        {controls.buttonLabel === "paying" ? t("pay.paying") : t("pay.simulate")}
      </button>
    </div>
  );
}

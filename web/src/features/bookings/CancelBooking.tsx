"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useCancelBooking } from "@/api/endpoints/bookings/bookings";
import type { BookingStatus, DepositStatus, MoneyDto } from "@/api/model";
import { Dialog } from "@/features/host/Dialog";
import { invalidateHostQueries } from "@/features/host/hostQueries";
import { useHostActionError } from "@/features/host/useHostActionError";
import { venueId } from "@/lib/config";
import { formatInstantTime, formatMoney } from "@/lib/format";
import styles from "@/features/host/actions.module.css";

/** The host may cancel a booking that hasn't been seated yet (Design §3.1, §14.2 "Cancel"; M8b). */
export const canCancel = (status: BookingStatus) =>
  status === "booked" || status === "confirmed" || status === "pendingPayment";

export type CancellableBooking = Readonly<{
  bookingId: string;
  reference: string;
  version: number;
  partySize: number;
  startsAt: string;
  deposit: MoneyDto | null;
  depositStatus: DepositStatus | null;
}>;

type Props = Readonly<{
  booking: CancellableBooking;
  timeZone: string;
}>;

/**
 * "Cancel booking" (M8b, AC5.5). Destructive, so it asks first: "Cancel 6 guests at 7:00 pm? The deposit of
 * NZ$120.00 will be refunded in full." (a host cancel always refunds a paid deposit in full, BR-07). Then
 * `cancelBooking` with the version shown; a stale screen refetches through `useHostActionError`. It sits at the bottom
 * of one booking's details in the side panel, apart from "Seat", as a quiet text button: never in tonight's list rows.
 */
export function CancelBookingButton({ booking, timeZone }: Props) {
  const { t, i18n } = useTranslation();
  const queryClient = useQueryClient();
  const messageFor = useHostActionError();
  const cancel = useCancelBooking();
  const [open, setOpen] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);

  const time = formatInstantTime(booking.startsAt, timeZone, i18n.language);
  const party =
    booking.partySize === 1
      ? t("host.tile.guestsOne")
      : t("host.tile.guestsMany", { count: booking.partySize });
  const depositText =
    booking.deposit && booking.depositStatus === "captured"
      ? t("host.actions.cancelRefund", {
          amount: formatMoney(booking.deposit.amountCents, booking.deposit.currency, i18n.language),
        })
      : booking.deposit
        ? t("host.actions.cancelNoDeposit")
        : null;

  const run = async () => {
    setFailure(null);
    try {
      await cancel.mutateAsync({
        venueId,
        id: booking.bookingId,
        data: { version: booking.version },
      });
      setOpen(false);
      void invalidateHostQueries(queryClient, venueId);
    } catch (error) {
      setFailure(messageFor(error));
    }
  };

  return (
    <div className={styles.cancelRow}>
      <button
        type="button"
        className={styles.quietDangerButton}
        onClick={() => {
          setFailure(null);
          setOpen(true);
        }}
        data-testid={`cancel-${booking.reference}`}
      >
        {t("host.actions.cancel")}
      </button>
      {open && (
        <Dialog
          title={t("host.actions.cancelTitle")}
          onClose={() => setOpen(false)}
          testId="cancel-dialog"
        >
          <p>{t("host.actions.cancelQuestion", { party, time })}</p>
          {depositText && <p>{depositText}</p>}
          {failure && (
            <p role="alert" className={styles.error}>
              {failure}
            </p>
          )}
          <div className={styles.buttonRow}>
            <button
              type="button"
              className={styles.secondaryButton}
              autoFocus
              onClick={() => setOpen(false)}
            >
              {t("host.actions.keepBooking")}
            </button>
            <button
              type="button"
              className={styles.dangerButton}
              disabled={cancel.isPending}
              onClick={() => void run()}
              data-testid="confirm-cancel"
            >
              {cancel.isPending ? t("host.actions.cancelling") : t("host.actions.confirmCancel")}
            </button>
          </div>
        </Dialog>
      )}
    </div>
  );
}

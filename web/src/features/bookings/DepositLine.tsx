"use client";

import { useTranslation } from "react-i18next";
import type { BookingStatus, DepositStatus, MoneyDto, RefundDecision } from "@/api/model";
import { depositLabelKey } from "@/features/booking/payment.mjs";
import { formatMoney } from "@/lib/format";

type Props = Readonly<{
  deposit: MoneyDto | null;
  depositStatus: DepositStatus | null;
  bookingStatus: BookingStatus;
  refundDecision?: RefundDecision | null;
  className?: string;
}>;

/**
 * "Deposit NZ$120.00 · Paid" / "· Refund pending" / "· Refunded" / "· Kept (cancelled late)" / "· Released" (M8b,
 * AC5.5). Refund pending turns into Refunded live: `charge.refunded` raises `DepositRefunded`, which pushes
 * `floorPlanChanged`, which refetches the host queries. Nothing for a booking without a deposit.
 */
export function DepositLine({
  deposit,
  depositStatus,
  bookingStatus,
  refundDecision,
  className,
}: Props) {
  const { t, i18n } = useTranslation();
  const key = depositLabelKey(depositStatus, bookingStatus, refundDecision);
  if (!deposit || !key) {
    return null;
  }

  return (
    <span className={className} data-testid="deposit-line" data-deposit={key}>
      {t("deposit.line", {
        amount: formatMoney(deposit.amountCents, deposit.currency, i18n.language),
        status: t(`deposit.status.${key}`),
      })}
    </span>
  );
}

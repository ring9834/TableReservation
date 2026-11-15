"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useId, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { useSimulateBillPaid } from "@/api/endpoints/demo/demo";
import { Dialog } from "@/features/host/Dialog";
import { invalidateHostQueries } from "@/features/host/hostQueries";
import { useHostActionError } from "@/features/host/useHostActionError";
import { unmatchedReasonKey } from "@/features/presenter/posLog.mjs";
import { venueId } from "@/lib/config";
import { centsToField, parseBillTotal, suggestedBillCents } from "./billTotal.mjs";
import styles from "@/features/host/actions.module.css";

type Props = Readonly<{
  tableId: string;
  tableName: string;
  partySize: number;
}>;

/**
 * "Bill paid" (AC6.6, Design §6.7, §18.9), shown only when the demo controls exist (`demoEnabled` on the floor plan).
 * The mock POS sends the signed `BillClosed` its POS would send for the table, through the same webhook processing
 * (`POST /demo/pos/bill-paid`). The total is pre-filled at NZ$45.00 a head and the host can change it. Matched: the
 * table frees through `floorPlanChanged`, as after "Clear table". Unmatched: the reason in plain words.
 */
export function BillPaidButton({ tableId, tableName, partySize }: Props) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);

  return (
    <>
      <button
        type="button"
        className={styles.secondaryButton}
        onClick={() => setOpen(true)}
        data-testid="bill-paid"
      >
        {t("host.actions.billPaid")}
      </button>
      {open && (
        <BillPaidDialog
          tableId={tableId}
          tableName={tableName}
          partySize={partySize}
          onClose={() => setOpen(false)}
        />
      )}
    </>
  );
}

function BillPaidDialog({
  tableId,
  tableName,
  partySize,
  onClose,
}: Props & { onClose: () => void }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const messageFor = useHostActionError();
  const billPaid = useSimulateBillPaid();
  const inputId = useId();
  const [value, setValue] = useState(() => centsToField(suggestedBillCents(partySize)));
  const [fieldError, setFieldError] = useState<string | null>(null);
  const [failure, setFailure] = useState<string | null>(null);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setFailure(null);
    const parsed = parseBillTotal(value);
    if ("error" in parsed) {
      setFieldError(t(`host.actions.billErrors.${parsed.error}`));
      return;
    }
    setFieldError(null);
    try {
      const result = await billPaid.mutateAsync({ data: { tableId, totalCents: parsed.cents } });
      void invalidateHostQueries(queryClient, venueId);
      if (result.status === "matched") {
        onClose();
        return;
      }
      setFailure(
        t("host.actions.billUnmatched", {
          reason: t(`pos.unmatchedReason.${unmatchedReasonKey(result.unmatchedReason)}`),
        }),
      );
    } catch (error) {
      setFailure(messageFor(error));
    }
  };

  return (
    <Dialog
      title={t("host.actions.billTitle", { table: tableName })}
      onClose={onClose}
      testId="bill-paid-dialog"
    >
      <form noValidate className={styles.form} onSubmit={(event) => void submit(event)}>
        <p className={styles.hint}>{t("host.actions.billHint")}</p>
        <div className={styles.field}>
          <label htmlFor={inputId}>{t("host.actions.billTotal")}</label>
          <input
            id={inputId}
            type="text"
            inputMode="decimal"
            autoComplete="off"
            aria-required
            aria-invalid={fieldError ? true : undefined}
            aria-describedby={fieldError ? `${inputId}-error` : undefined}
            value={value}
            onChange={(event) => setValue(event.target.value)}
            autoFocus
          />
          {fieldError && (
            <p id={`${inputId}-error`} className={styles.error}>
              {fieldError}
            </p>
          )}
        </div>
        {failure && (
          <p role="alert" className={styles.error} data-testid="bill-paid-failure">
            {failure}
          </p>
        )}
        <div className={styles.buttonRow}>
          <button type="button" className={styles.secondaryButton} onClick={onClose}>
            {t("host.actions.closeDialog")}
          </button>
          <button type="submit" className={styles.primaryButton} disabled={billPaid.isPending}>
            {billPaid.isPending ? t("host.actions.sendingBill") : t("host.actions.sendBill")}
          </button>
        </div>
      </form>
    </Dialog>
  );
}

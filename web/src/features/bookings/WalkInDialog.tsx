"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useId, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { useSeatWalkIn } from "@/api/endpoints/seating/seating";
import type { SeatWalkInRequest } from "@/api/model";
import { seatWalkInBodyNameMax, seatWalkInBodyPartySizeMax } from "@/api/zod/seating/seating.zod";
import { Dialog } from "@/features/host/Dialog";
import { invalidateHostQueries } from "@/features/host/hostQueries";
import { useHostActionError, useIdempotencyKey } from "@/features/host/useHostActionError";
import { isApiError } from "@/lib/apiFetch";
import { venueId } from "@/lib/config";
import { TableChoice, useFreeTables } from "./TableChoice";
import { defaultTableId, freeTableIds } from "./tableChoice.mjs";
import styles from "@/features/host/actions.module.css";

/** Party sizes offered as one-tap buttons; larger parties type the number. */
const quickPartySizes = [1, 2, 3, 4, 5, 6, 7, 8];

type Props = Readonly<{
  onClose: () => void;
  onSeated: (tableId: string, tableName: string) => void;
  onAddToWaitlist: (partySize: number) => void;
}>;

/**
 * Seat a walk-in (Requirements step 3, AC3.1, AC3.2): party size → the suggested table is already chosen → Seat.
 * Three taps from the "Walk-in" button. When nothing fits, offers the waitlist with the party size (AC3.4).
 */
export function WalkInDialog({ onClose, onSeated, onAddToWaitlist }: Props) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const messageFor = useHostActionError();
  const idempotency = useIdempotencyKey();
  const baseId = useId();
  const [partySize, setPartySize] = useState<number | null>(null);
  const [largeParty, setLargeParty] = useState("");
  const [chosen, setChosen] = useState<string | null>(null);
  const [name, setName] = useState("");
  const [failure, setFailure] = useState<string | null>(null);

  const suggestion = useFreeTables(partySize);
  const seat = useSeatWalkIn();
  const free = freeTableIds(suggestion.data);
  const tableId = defaultTableId([chosen], free);
  const tableName = [suggestion.data?.table, ...(suggestion.data?.alternatives ?? [])].find(
    (table) => table?.id === tableId,
  )?.name;

  const choosePartySize = (size: number) => {
    setPartySize(size);
    setChosen(null);
    setFailure(null);
  };

  const submitLargeParty = (event: FormEvent) => {
    event.preventDefault();
    const size = Number(largeParty);
    if (Number.isInteger(size) && size >= 1 && size <= seatWalkInBodyPartySizeMax) {
      choosePartySize(size);
    }
  };

  const seatNow = async () => {
    if (partySize === null || !tableId || !tableName) return;
    setFailure(null);
    const request: SeatWalkInRequest = {
      partySize,
      tableId,
      name: name.trim() === "" ? null : name.trim(),
    };
    try {
      await seat.mutateAsync({
        venueId,
        data: request,
        headers: { "Idempotency-Key": idempotency.keyFor(request) },
      });
      idempotency.reset();
      void invalidateHostQueries(queryClient, venueId);
      onSeated(tableId, tableName);
    } catch (error) {
      if (isApiError(error) && error.code === "slot_taken") {
        idempotency.reset();
        setChosen(null);
      }
      setFailure(messageFor(error));
    }
  };

  const nothingFits = suggestion.data && suggestion.data.table === null;

  return (
    <Dialog title={t("host.walkInDialog.title")} onClose={onClose} testId="walk-in-dialog">
      <fieldset className={styles.choice}>
        <legend>{t("host.walkInDialog.partySize")}</legend>
        <div className={styles.partyButtons}>
          {quickPartySizes.map((size) => (
            <button
              key={size}
              type="button"
              className={styles.partyButton}
              aria-pressed={partySize === size}
              aria-label={t("host.party", { count: size })}
              onClick={() => choosePartySize(size)}
            >
              {size}
            </button>
          ))}
        </div>
        <form className={styles.inlineForm} onSubmit={submitLargeParty}>
          <label htmlFor={`${baseId}-large`}>{t("host.walkInDialog.largerParty")}</label>
          <input
            id={`${baseId}-large`}
            type="number"
            inputMode="numeric"
            min={quickPartySizes.length + 1}
            max={seatWalkInBodyPartySizeMax}
            value={largeParty}
            onChange={(event) => setLargeParty(event.target.value)}
          />
          <button type="submit" className={styles.secondaryButton}>
            {t("host.walkInDialog.findTable")}
          </button>
        </form>
      </fieldset>

      <div aria-live="polite">
        {partySize !== null && suggestion.isPending && <p>{t("common.loading")}</p>}
        {suggestion.error && <p className={styles.error}>{messageFor(suggestion.error)}</p>}
      </div>

      {nothingFits && partySize !== null && (
        <div className={styles.notice} data-testid="walk-in-no-table">
          <p>{t("host.walkInDialog.noTable", { count: partySize })}</p>
          <button
            type="button"
            className={styles.primaryButton}
            onClick={() => onAddToWaitlist(partySize)}
          >
            {t("host.walkInDialog.addToWaitlist")}
          </button>
        </div>
      )}

      {suggestion.data && suggestion.data.table !== null && partySize !== null && (
        <>
          <TableChoice
            suggestion={suggestion.data}
            value={tableId}
            onChange={setChosen}
            legend={t("host.walkInDialog.table")}
          />
          <div className={styles.field}>
            <label htmlFor={`${baseId}-name`}>{t("host.walkInDialog.name")}</label>
            <input
              id={`${baseId}-name`}
              type="text"
              autoComplete="off"
              maxLength={seatWalkInBodyNameMax}
              value={name}
              onChange={(event) => setName(event.target.value)}
            />
          </div>
          <button
            type="button"
            className={styles.primaryButton}
            disabled={!tableName || seat.isPending}
            onClick={() => void seatNow()}
          >
            {seat.isPending
              ? t("host.actions.seating")
              : t("host.walkInDialog.seatAt", { table: tableName ?? "" })}
          </button>
        </>
      )}

      {failure && (
        <p role="alert" className={styles.error}>
          {failure}
        </p>
      )}
    </Dialog>
  );
}

"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useId, useState } from "react";
import { useForm, useWatch, type FieldError } from "react-hook-form";
import { useTranslation } from "react-i18next";
import type { z } from "zod";
import { useGetQuotedWait, useJoinWaitlist } from "@/api/endpoints/waitlist/waitlist";
import type { JoinWaitlistRequest } from "@/api/model";
import {
  JoinWaitlistBody,
  joinWaitlistBodyNameMax,
  joinWaitlistBodyPartySizeMax,
  joinWaitlistBodyQuotedWaitMinutesMax,
  joinWaitlistBodyQuotedWaitMinutesMin,
} from "@/api/zod/waitlist/waitlist.zod";
import { Dialog } from "@/features/host/Dialog";
import { invalidateHostQueries } from "@/features/host/hostQueries";
import { useHostActionError, useIdempotencyKey } from "@/features/host/useHostActionError";
import { apiLanguage } from "@/i18n/i18n";
import { isApiError } from "@/lib/apiFetch";
import { venueId } from "@/lib/config";
import styles from "@/features/host/actions.module.css";

/** The generated rules (Design §18.4), with the guest's language required so the radios always have a value. */
const formSchema = JoinWaitlistBody.required({ language: true });
type FormValues = z.input<typeof formSchema>;
type FieldName = keyof FormValues;
const fields: FieldName[] = ["name", "mobile", "partySize", "quotedWaitMinutes", "language"];
const isFieldName = (name: string): name is FieldName => (fields as string[]).includes(name);

/** The Design §14.1 / §14.2 rule code for a client-side failure, so client and server errors read the same. */
const clientCode = (field: FieldName) => {
  switch (field) {
    case "name":
      return "name_length";
    case "mobile":
      return "mobile_invalid";
    case "partySize":
      return "party_size_out_of_range";
    case "quotedWaitMinutes":
      return "quoted_wait_out_of_range";
    case "language":
      return "language_invalid";
  }
};

const validPartySize = (size: unknown): size is number =>
  typeof size === "number" &&
  Number.isInteger(size) &&
  size >= 1 &&
  size <= joinWaitlistBodyPartySizeMax;

type Props = Readonly<{
  /** Carried over from the walk-in dialog when no table fitted (AC3.4). */
  partySize?: number;
  onClose: () => void;
  onJoined: () => void;
}>;

/**
 * Join the waitlist (AC3.4, AC3.5): name, mobile, party size, the guest's language (default the screen's) and the
 * quoted wait, pre-filled from `GET …/waitlist/quote` and following the party size until the host changes it.
 */
export function JoinWaitlistDialog({ partySize: initialPartySize, onClose, onJoined }: Props) {
  const { t, i18n } = useTranslation();
  const queryClient = useQueryClient();
  const messageFor = useHostActionError();
  const idempotency = useIdempotencyKey();
  const baseId = useId();
  const [failure, setFailure] = useState<string | null>(null);

  const {
    register,
    handleSubmit,
    setError,
    setValue,
    control,
    formState: { errors, dirtyFields },
  } = useForm<FormValues>({
    resolver: zodResolver(formSchema),
    defaultValues: {
      name: "",
      mobile: "",
      partySize: initialPartySize,
      language: apiLanguage(i18n.resolvedLanguage ?? "en"),
    },
    mode: "onTouched",
  });

  const partySize = useWatch({ control, name: "partySize" });
  const quote = useGetQuotedWait(
    venueId,
    { partySize: validPartySize(partySize) ? partySize : 0 },
    { query: { enabled: validPartySize(partySize), staleTime: 0 } },
  );
  const quoteEdited = !!dirtyFields.quotedWaitMinutes;

  useEffect(() => {
    if (quote.data && !quoteEdited) {
      setValue("quotedWaitMinutes", quote.data.quotedWaitMinutes, { shouldValidate: true });
    }
  }, [quote.data, quoteEdited, setValue]);

  const join = useJoinWaitlist();

  const onSubmit = async (values: FormValues) => {
    setFailure(null);
    const request: JoinWaitlistRequest = {
      name: values.name.trim(),
      mobile: values.mobile.trim(),
      partySize: values.partySize,
      quotedWaitMinutes: values.quotedWaitMinutes,
      language: values.language,
    };
    try {
      await join.mutateAsync({
        venueId,
        data: request,
        headers: { "Idempotency-Key": idempotency.keyFor(request) },
      });
      idempotency.reset();
      void invalidateHostQueries(queryClient, venueId);
      onJoined();
    } catch (error) {
      const fieldErrors = isApiError(error) ? Object.entries(error.fieldErrors ?? {}) : [];
      const formFields = fieldErrors.filter(([name]) => isFieldName(name));
      formFields.forEach(([name, codes], index) =>
        setError(
          name as FieldName,
          { type: "server", message: codes[0] },
          { shouldFocus: index === 0 },
        ),
      );
      if (formFields.length === 0 || formFields.length < fieldErrors.length) {
        setFailure(messageFor(error));
      }
    }
  };

  const errorText = (field: FieldName, error: FieldError | undefined) =>
    error &&
    t(
      `fieldErrors.${error.type === "server" && error.message ? error.message : clientCode(field)}`,
    );

  const describe = (field: FieldName, hint?: boolean) => {
    const id = `${baseId}-${field}`;
    const message = errorText(field, errors[field]);
    return {
      id,
      message,
      errorId: `${id}-error`,
      props: {
        id,
        "aria-invalid": message ? true : undefined,
        "aria-describedby":
          [hint ? `${id}-hint` : undefined, message ? `${id}-error` : undefined]
            .filter(Boolean)
            .join(" ") || undefined,
      },
    };
  };

  const name = describe("name");
  const mobile = describe("mobile", true);
  const party = describe("partySize");
  const quoted = describe("quotedWaitMinutes", true);
  const fieldError = (d: { message: string | undefined; errorId: string }) =>
    d.message && (
      <p id={d.errorId} className={styles.error}>
        {d.message}
      </p>
    );

  return (
    <Dialog title={t("waitlist.join.title")} onClose={onClose} testId="join-waitlist-dialog">
      <form noValidate className={styles.form} onSubmit={handleSubmit(onSubmit)}>
        <div className={styles.field}>
          <label htmlFor={name.id}>{t("waitlist.join.name")}</label>
          <input
            type="text"
            autoComplete="off"
            maxLength={joinWaitlistBodyNameMax}
            aria-required
            {...name.props}
            {...register("name")}
          />
          {fieldError(name)}
        </div>
        <div className={styles.field}>
          <label htmlFor={mobile.id}>{t("waitlist.join.mobile")}</label>
          <p id={`${mobile.id}-hint`} className={styles.hint}>
            {t("details.mobileHint")}
          </p>
          <input
            type="tel"
            inputMode="tel"
            autoComplete="off"
            aria-required
            {...mobile.props}
            {...register("mobile")}
          />
          {fieldError(mobile)}
        </div>
        <div className={styles.field}>
          <label htmlFor={party.id}>{t("waitlist.join.partySize")}</label>
          <input
            type="number"
            inputMode="numeric"
            min={1}
            max={joinWaitlistBodyPartySizeMax}
            aria-required
            {...party.props}
            {...register("partySize", { valueAsNumber: true })}
          />
          {fieldError(party)}
        </div>
        <fieldset className={styles.choice}>
          <legend>{t("waitlist.join.language")}</legend>
          <div className={styles.choiceOptions}>
            <label className={styles.choiceOption}>
              <input type="radio" value="en" {...register("language")} />
              <span lang="en">{t("language.en")}</span>
            </label>
            <label className={styles.choiceOption}>
              <input type="radio" value="zh" {...register("language")} />
              <span lang="zh-CN">{t("language.zh")}</span>
            </label>
          </div>
        </fieldset>
        <div className={styles.field}>
          <label htmlFor={quoted.id}>{t("waitlist.join.quote")}</label>
          <p id={`${quoted.id}-hint`} className={styles.hint}>
            {quote.error
              ? messageFor(quote.error)
              : quote.data
                ? t("waitlist.join.quoteHint", { minutes: quote.data.quotedWaitMinutes })
                : t("waitlist.join.quoteHintEmpty")}
          </p>
          <input
            type="number"
            inputMode="numeric"
            min={joinWaitlistBodyQuotedWaitMinutesMin}
            max={joinWaitlistBodyQuotedWaitMinutesMax}
            step={5}
            aria-required
            data-testid="quoted-wait"
            {...quoted.props}
            {...register("quotedWaitMinutes", { valueAsNumber: true })}
          />
          {fieldError(quoted)}
        </div>

        {failure && (
          <p role="alert" className={styles.error}>
            {failure}
          </p>
        )}
        <button type="submit" className={styles.primaryButton} disabled={join.isPending}>
          {join.isPending ? t("waitlist.join.submitting") : t("waitlist.join.submit")}
        </button>
      </form>
    </Dialog>
  );
}

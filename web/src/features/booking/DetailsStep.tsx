"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useQueryClient } from "@tanstack/react-query";
import { useId, useRef, useState } from "react";
import { useForm, type FieldError } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { z } from "zod";
import {
  getGetAvailabilityQueryKey,
  useCreateOnlineBooking,
} from "@/api/endpoints/bookings/bookings";
import type { CreateBookingRequest, CreateBookingResponse } from "@/api/model";
import {
  CreateOnlineBookingBody,
  createOnlineBookingBodyAllergyNotesMax,
  createOnlineBookingBodyEmailMax,
  createOnlineBookingBodyNameMax,
  createOnlineBookingBodyNotesMax,
  createOnlineBookingBodyOccasionMax,
} from "@/api/zod/bookings/bookings.zod";
import { apiLanguage } from "@/i18n/i18n";
import { isApiError } from "@/lib/apiFetch";
import { venueId } from "@/lib/config";
import { formatLocalDate, formatLocalTime } from "@/lib/format";
import { partyLabel } from "./party";
import { useApiError } from "./useApiError";
import { useFocusHeading } from "./useFocusHeading";
import type { ChosenSlot } from "./WhenStep";
import styles from "./booking.module.css";

/**
 * The form's rules are the generated ones (FluentValidation → OpenAPI → orval zod, Design §18.4). The only change:
 * an optional email may be left blank, which the form sends as `null`.
 */
const formSchema = CreateOnlineBookingBody.pick({
  name: true,
  mobile: true,
  email: true,
  occasion: true,
  allergyNotes: true,
  notes: true,
}).extend({ email: CreateOnlineBookingBody.shape.email.or(z.literal("")) });

type FormValues = z.input<typeof formSchema>;
type FieldName = keyof FormValues;

const fields: FieldName[] = ["name", "mobile", "email", "occasion", "allergyNotes", "notes"];
const isFieldName = (name: string): name is FieldName => (fields as string[]).includes(name);

/** The Design §14.1 rule code for a client-side zod failure, so client and server errors read the same. */
const clientCode = (field: FieldName, type: string) => {
  switch (field) {
    case "name":
      return "name_length";
    case "mobile":
      return "mobile_invalid";
    case "email":
      return type === "too_big" ? "email_too_long" : "email_invalid";
    case "occasion":
      return "occasion_too_long";
    case "allergyNotes":
      return "allergy_notes_too_long";
    case "notes":
      return "notes_too_long";
  }
};

const blankToNull = (value: string | null | undefined) =>
  value === undefined || value === null || value.trim() === "" ? null : value.trim();

type Props = Readonly<{
  slot: ChosenSlot;
  onBack: () => void;
  onSlotTaken: () => void;
  onBooked: (booking: CreateBookingResponse) => void;
}>;

/** Name, NZ mobile, optional email, occasion, allergies and notes (AC1.4), then the create call. */
export function DetailsStep({ slot, onBack, onSlotTaken, onBooked }: Props) {
  const { t, i18n } = useTranslation();
  const messageFor = useApiError();
  const queryClient = useQueryClient();
  const headingRef = useFocusHeading<HTMLHeadingElement>(true);
  const baseId = useId();
  const [failure, setFailure] = useState<string | null>(null);
  const language = i18n.resolvedLanguage ?? "en";

  // One Idempotency-Key per submission: reused when the same request is sent again (a retry after a lost answer),
  // replaced as soon as anything in the request changes (ADR-004, Design §14.1).
  const idempotency = useRef<{ key: string; request: string } | null>(null);
  const keyFor = (request: CreateBookingRequest) => {
    const fingerprint = JSON.stringify(request);
    if (idempotency.current?.request !== fingerprint) {
      idempotency.current = { key: crypto.randomUUID(), request: fingerprint };
    }
    return idempotency.current.key;
  };

  const {
    register,
    handleSubmit,
    setError,
    formState: { errors, isSubmitted },
  } = useForm<FormValues>({
    resolver: zodResolver(formSchema),
    defaultValues: { name: "", mobile: "", email: "", occasion: "", allergyNotes: "", notes: "" },
    mode: "onTouched",
  });

  const create = useCreateOnlineBooking();

  const onSubmit = async (values: FormValues) => {
    setFailure(null);
    const request: CreateBookingRequest = {
      startsAt: slot.startsAt,
      partySize: slot.partySize,
      name: values.name.trim(),
      mobile: values.mobile.trim(),
      language: apiLanguage(language),
      email: blankToNull(values.email),
      occasion: blankToNull(values.occasion),
      allergyNotes: blankToNull(values.allergyNotes),
      notes: blankToNull(values.notes),
    };

    try {
      const booking = await create.mutateAsync({
        venueId,
        data: request,
        headers: { "Idempotency-Key": keyFor(request) },
      });
      idempotency.current = null;
      onBooked(booking);
    } catch (error) {
      if (isApiError(error) && error.code === "slot_taken") {
        idempotency.current = null;
        await queryClient.invalidateQueries({ queryKey: getGetAvailabilityQueryKey(venueId) });
        onSlotTaken();
        return;
      }

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
      `fieldErrors.${error.type === "server" && error.message ? error.message : clientCode(field, error.type)}`,
    );

  const input = (
    field: FieldName,
    label: string,
    options: {
      type?: string;
      autoComplete?: string;
      inputMode?: "tel" | "email" | "text";
      maxLength?: number;
      hint?: string;
      required?: boolean;
      multiline?: boolean;
    },
  ) => {
    const id = `${baseId}-${field}`;
    const hintId = options.hint ? `${id}-hint` : undefined;
    const errorId = `${id}-error`;
    const message = errorText(field, errors[field]);
    const describedBy =
      [hintId, message ? errorId : undefined].filter(Boolean).join(" ") || undefined;
    const common = {
      id,
      "aria-invalid": message ? true : undefined,
      "aria-describedby": describedBy,
      "aria-required": options.required || undefined,
      maxLength: options.maxLength,
      ...register(field),
    };
    return (
      <div className={styles.field}>
        <label htmlFor={id}>{label}</label>
        {options.hint && (
          <p id={hintId} className={styles.hint}>
            {options.hint}
          </p>
        )}
        {options.multiline ? (
          <textarea rows={2} {...common} />
        ) : (
          <input
            type={options.type ?? "text"}
            autoComplete={options.autoComplete}
            inputMode={options.inputMode}
            {...common}
          />
        )}
        {message && (
          <p id={errorId} className={styles.fieldError}>
            {message}
          </p>
        )}
      </div>
    );
  };

  const hasFieldErrors = Object.keys(errors).length > 0;

  return (
    <section aria-labelledby={`${baseId}-heading`} className={styles.step}>
      <h2 id={`${baseId}-heading`} ref={headingRef} tabIndex={-1}>
        {t("details.heading")}
      </h2>
      <div className={styles.summary}>
        <p>
          {t("details.summary", {
            date: formatLocalDate(slot.date, language),
            time: formatLocalTime(slot.localTime, language),
            party: partyLabel(t, slot.partySize),
          })}
        </p>
        <button type="button" className={styles.link} onClick={onBack}>
          {t("details.change")}
        </button>
      </div>

      <form noValidate onSubmit={handleSubmit(onSubmit)} className={styles.fields}>
        {input("name", t("details.name"), {
          autoComplete: "name",
          maxLength: createOnlineBookingBodyNameMax,
          required: true,
        })}
        {input("mobile", t("details.mobile"), {
          type: "tel",
          autoComplete: "tel-national",
          inputMode: "tel",
          hint: t("details.mobileHint"),
          required: true,
        })}
        {input("email", t("details.email"), {
          type: "email",
          autoComplete: "email",
          inputMode: "email",
          maxLength: createOnlineBookingBodyEmailMax,
        })}
        {input("occasion", t("details.occasion"), {
          hint: t("details.occasionHint"),
          maxLength: createOnlineBookingBodyOccasionMax,
        })}
        {input("allergyNotes", t("details.allergies"), {
          maxLength: createOnlineBookingBodyAllergyNotesMax,
          multiline: true,
        })}
        {input("notes", t("details.notes"), {
          maxLength: createOnlineBookingBodyNotesMax,
          multiline: true,
        })}

        {isSubmitted && hasFieldErrors && !failure && (
          <p role="alert" className={styles.fieldError}>
            {t("details.fixErrors")}
          </p>
        )}
        {failure && (
          <div role="alert" className={styles.alert}>
            <p>{failure}</p>
          </div>
        )}

        <button type="submit" className={styles.primary} disabled={create.isPending}>
          {create.isPending ? t("details.submitting") : t("details.submit")}
        </button>
      </form>
    </section>
  );
}

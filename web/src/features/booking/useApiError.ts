import { useCallback } from "react";
import { useTranslation } from "react-i18next";
import { isApiError } from "@/lib/apiFetch";

/** The Problem Details codes the guest page has its own message for; any other code reads as `unexpected`. */
export const knownErrorCodes = [
  "slot_taken",
  "party_too_large",
  "outside_service_hours",
  "outside_booking_window",
  "validation_failed",
  "too_many_requests",
  "unexpected",
] as const;

export type KnownErrorCode = (typeof knownErrorCodes)[number];

/** The code to show for an error: the API's own code when the page knows it, otherwise `unexpected`. */
export const errorCodeOf = (error: unknown): KnownErrorCode => {
  const code = isApiError(error) ? error.code : "unexpected";
  return (knownErrorCodes as readonly string[]).includes(code)
    ? (code as KnownErrorCode)
    : "unexpected";
};

/** One place that turns an API error into a translated message (ADR-004, Design §18.2). */
export function useApiError() {
  const { t } = useTranslation();
  return useCallback((error: unknown) => t(`errors.${errorCodeOf(error)}`), [t]);
}

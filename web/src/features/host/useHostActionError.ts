"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useCallback, useRef } from "react";
import { useTranslation } from "react-i18next";
import { isApiError } from "@/lib/apiFetch";
import { venueId } from "@/lib/config";
import { errorCodeOf } from "@/lib/useApiError";
import { invalidateHostQueries } from "./hostQueries";

/** Codes a host action can meet that have host wording under `host.errors.*` (Design §14.2, §18.6). */
export const hostErrorCodes = [
  "slot_taken",
  "version_conflict",
  "invalid_transition",
  "table_too_small",
  "party_too_large",
  "not_found",
  "not_signed_in",
] as const;

/** The screen is behind: refetch everything, then the host can look again and retry. */
const refreshCodes: readonly string[] = [
  "slot_taken",
  "version_conflict",
  "invalid_transition",
  "not_found",
  "not_signed_in", // the queries answer 401 too and send the screen to sign-in
];

/**
 * Turns a failed host action into a translated message and, when the data on screen was out of date
 * (`slot_taken`, `version_conflict`, `invalid_transition`), refetches it so the host sees what changed.
 */
export function useHostActionError() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  return useCallback(
    (error: unknown): string => {
      const code = isApiError(error) ? error.code : "unexpected";
      if (refreshCodes.includes(code)) void invalidateHostQueries(queryClient, venueId);
      return (hostErrorCodes as readonly string[]).includes(code)
        ? t(`host.errors.${code}`)
        : t(`errors.${errorCodeOf(error)}`);
    },
    [queryClient, t],
  );
}

/**
 * One Idempotency-Key per request (Design §14.1, §18.4): reused while the same request is sent again (a retry after
 * a lost answer), replaced when anything in it changes, dropped with `reset()` after success or `slot_taken`.
 */
export function useIdempotencyKey() {
  const current = useRef<{ key: string; request: string } | null>(null);
  const keyFor = useCallback((request: unknown) => {
    const fingerprint = JSON.stringify(request);
    if (current.current?.request !== fingerprint) {
      current.current = { key: crypto.randomUUID(), request: fingerprint };
    }
    return current.current.key;
  }, []);
  const reset = useCallback(() => {
    current.current = null;
  }, []);
  return { keyFor, reset };
}

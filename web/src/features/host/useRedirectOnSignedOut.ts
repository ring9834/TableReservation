"use client";

import { useRouter } from "next/navigation";
import { useEffect } from "react";
import { isApiError } from "@/lib/apiFetch";

/** True when the API answered 401: the demo session is missing or has ended (Design §18.2). */
export const isSignedOut = (error: unknown) => isApiError(error) && error.status === 401;

/** Host and presenter screens go to `/demo-login` as soon as any of their requests answers 401 (Design §18.2). */
export function useRedirectOnSignedOut(...errors: unknown[]) {
  const router = useRouter();
  const signedOut = errors.some(isSignedOut);
  useEffect(() => {
    if (signedOut) {
      router.replace("/demo-login/");
    }
  }, [router, signedOut]);
  return signedOut;
}

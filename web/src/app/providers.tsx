"use client";

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { I18nextProvider } from "react-i18next";
import { isApiError } from "@/lib/apiFetch";
import i18n, { changeLanguage, storedLanguage } from "@/i18n/i18n";

/** Retry a failed read twice, but never a 4xx: the answer would not change (Design §18.2). */
const shouldRetry = (failureCount: number, error: unknown) =>
  failureCount < 2 && !(isApiError(error) && error.status >= 400 && error.status < 500);

export function Providers({ children }: Readonly<{ children: React.ReactNode }>) {
  const [queryClient] = useState(
    () =>
      new QueryClient({
        defaultOptions: {
          queries: { retry: shouldRetry, refetchOnWindowFocus: false },
          mutations: { retry: false },
        },
      }),
  );

  // The static HTML is English; switch to the guest's stored language once the page is live.
  useEffect(() => {
    const language = storedLanguage();
    if (language && language !== i18n.resolvedLanguage) {
      changeLanguage(language);
    }
  }, []);

  return (
    <I18nextProvider i18n={i18n}>
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    </I18nextProvider>
  );
}

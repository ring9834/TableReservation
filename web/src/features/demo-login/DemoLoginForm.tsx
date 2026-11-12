"use client";

import { useRouter } from "next/navigation";
import { useEffect, useRef, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { useDemoLogin } from "@/api/endpoints/demo/demo";
import { demoLoginBodyPasscodeMax } from "@/api/zod/demo/demo.zod";
import { LanguageSwitch } from "@/i18n/LanguageSwitch";
import { isApiError } from "@/lib/apiFetch";
import { useApiError } from "@/lib/useApiError";
import styles from "./demoLogin.module.css";

/**
 * `/demo-login` (Design §17, §18.1): a passcode gives a demo session cookie; the role decides where to go.
 * The passcode never leaves this form except in the request body, and is cleared after a failure.
 */
export function DemoLoginForm() {
  const { t } = useTranslation();
  const router = useRouter();
  const messageFor = useApiError();
  const login = useDemoLogin();
  const [passcode, setPasscode] = useState("");
  const [fieldError, setFieldError] = useState<string | null>(null);
  const input = useRef<HTMLInputElement>(null);

  useEffect(() => {
    document.title = t("login.documentTitle");
  }, [t]);

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    if (passcode.trim() === "") {
      setFieldError("passcode_required");
      input.current?.focus();
      return;
    }
    setFieldError(null);
    login.mutate(
      { data: { passcode } },
      {
        onSuccess: ({ role }) => router.replace(role === "presenter" ? "/presenter/" : "/host/"),
        onError: (error) => {
          const code = isApiError(error) ? error.fieldErrors?.passcode?.[0] : undefined;
          setFieldError(code ?? null);
          setPasscode("");
          input.current?.focus();
        },
      },
    );
  };

  const errorText = fieldError
    ? t(`fieldErrors.${fieldError}`, { defaultValue: t("errors.validation_failed") })
    : login.isError
      ? messageFor(login.error)
      : null;

  return (
    <div className={styles.page}>
      <header className={styles.header}>
        <LanguageSwitch />
      </header>
      <main className={styles.main}>
        <h1>{t("login.title")}</h1>
        <p className={styles.intro}>{t("login.intro")}</p>
        <form onSubmit={onSubmit} noValidate className={styles.form}>
          <label htmlFor="passcode">{t("login.passcode")}</label>
          <input
            id="passcode"
            ref={input}
            type="password"
            autoComplete="current-password"
            maxLength={demoLoginBodyPasscodeMax}
            value={passcode}
            onChange={(event) => setPasscode(event.target.value)}
            aria-invalid={errorText ? true : undefined}
            aria-describedby={errorText ? "passcode-error" : undefined}
          />
          {errorText && (
            <p id="passcode-error" role="alert" className={styles.error}>
              {errorText}
            </p>
          )}
          <button type="submit" disabled={login.isPending} className={styles.submit}>
            {login.isPending ? t("login.signingIn") : t("login.submit")}
          </button>
        </form>
      </main>
    </div>
  );
}

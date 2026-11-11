"use client";

import { useTranslation } from "react-i18next";
import { changeLanguage, languages, type AppLanguage } from "./i18n";

const labelKey = {
  en: "language.en",
  "zh-CN": "language.zh",
} as const satisfies Record<AppLanguage, string>;

/** English / 中文 switch (AC1.8, QA-7). Each label is written in its own language, with a matching `lang`. */
export function LanguageSwitch() {
  const { t, i18n } = useTranslation();
  return (
    <div role="group" aria-label={t("language.label")} className="language-switch">
      {languages.map((language) => (
        <button
          key={language}
          type="button"
          lang={language}
          aria-pressed={i18n.resolvedLanguage === language}
          onClick={() => changeLanguage(language)}
        >
          {t(labelKey[language])}
        </button>
      ))}
    </div>
  );
}

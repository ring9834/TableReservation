import i18next from "i18next";
import { initReactI18next } from "react-i18next";
import en from "./en.json";
import zhCN from "./zh-CN.json";

// react-i18next runs fully in the browser, so it works with the static export (Design §18.2, ADR-001).

export const languages = ["en", "zh-CN"] as const;
export type AppLanguage = (typeof languages)[number];

const storageKey = "reservation.language";

/** The API's `Language` for the active language: messages go out in it (Design §20). */
export const apiLanguage = (language: string): "en" | "zh" =>
  language.startsWith("zh") ? "zh" : "en";

if (!i18next.isInitialized) {
  void i18next.use(initReactI18next).init({
    resources: { en: { translation: en }, "zh-CN": { translation: zhCN } },
    lng: "en", // the static HTML is English; a stored choice is applied after hydration
    fallbackLng: "en",
    supportedLngs: [...languages],
    interpolation: { escapeValue: false }, // React escapes
  });

  i18next.on("languageChanged", (language) => {
    if (typeof document !== "undefined") {
      document.documentElement.lang = language;
    }
  });
}

/** The language the guest chose last time, if the browser lets us read it. */
export const storedLanguage = (): AppLanguage | null => {
  try {
    const value = window.localStorage.getItem(storageKey);
    return languages.find((l) => l === value) ?? null;
  } catch {
    return null;
  }
};

/** Switches language instantly (no reload, AC1.8) and remembers it in this browser. */
export const changeLanguage = (language: AppLanguage) => {
  void i18next.changeLanguage(language);
  try {
    window.localStorage.setItem(storageKey, language);
  } catch {
    // private mode or blocked storage: the switch still works for this visit
  }
};

export default i18next;

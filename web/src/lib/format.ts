// Dates, times and money for display (BR-11): always in the venue's time zone and NZD, whatever the browser's zone.

/** The `Intl` locale for an i18n language: `en` → `en-NZ`, `zh-CN` → `zh-CN`. */
export const intlLocale = (language: string) => (language.startsWith("zh") ? "zh-CN" : "en-NZ");

/** A venue-local time from the API (`"19:30:00"`), e.g. "7:30 pm" or "19:30". */
export const formatLocalTime = (time: string, language: string) => {
  const [hours, minutes] = time.split(":").map(Number);
  return new Intl.DateTimeFormat(intlLocale(language), {
    hour: "numeric",
    minute: "2-digit",
    timeZone: "UTC",
  }).format(new Date(Date.UTC(2000, 0, 1, hours, minutes)));
};

/** A venue-local date (`"2026-11-12"`), e.g. "Thursday 12 November". */
export const formatLocalDate = (date: string, language: string) =>
  new Intl.DateTimeFormat(intlLocale(language), {
    weekday: "long",
    day: "numeric",
    month: "long",
    timeZone: "UTC",
  }).format(dateOnlyToUtc(date));

/** An instant (UTC from the API) as the date and time in the venue's time zone. */
export const formatInstant = (instant: string, timeZone: string, language: string) =>
  new Intl.DateTimeFormat(intlLocale(language), {
    weekday: "long",
    day: "numeric",
    month: "long",
    hour: "numeric",
    minute: "2-digit",
    timeZone,
  }).format(new Date(instant));

/** An instant as a time only, in the venue's time zone. */
export const formatInstantTime = (instant: string, timeZone: string, language: string) =>
  new Intl.DateTimeFormat(intlLocale(language), {
    hour: "numeric",
    minute: "2-digit",
    timeZone,
  }).format(new Date(instant));

/** Money in whole cents (BR-11). */
export const formatMoney = (amountCents: number, currency: string, language: string) =>
  new Intl.NumberFormat(intlLocale(language), { style: "currency", currency }).format(
    amountCents / 100,
  );

/** `"2026-11-12"` plus `days`, as a date string. Calendar arithmetic only: no time zone involved. */
export const addDays = (date: string, days: number) => {
  const d = dateOnlyToUtc(date);
  d.setUTCDate(d.getUTCDate() + days);
  return d.toISOString().slice(0, 10);
};

/** A `tel:` link for a display number such as "+64 9 000 0000". */
export const telHref = (phone: string) => `tel:${phone.replace(/[^\d+]/g, "")}`;

const dateOnlyToUtc = (date: string) => {
  const [year, month, day] = date.split("-").map(Number);
  return new Date(Date.UTC(year, month - 1, day));
};

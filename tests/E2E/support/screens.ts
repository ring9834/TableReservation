import { devices, type Browser, type BrowserContextOptions } from "@playwright/test";
import { hostSessionPath } from "./session";

const baseOptions = (baseURL: string | undefined): BrowserContextOptions => ({
  baseURL,
  ignoreHTTPSErrors: true, // local dev certificates
});

/** A host tablet, signed in with the shared session (global-setup.ts). */
export const hostContext = (browser: Browser, baseURL: string | undefined) =>
  browser.newContext({ ...devices["Galaxy Tab S4 landscape"], ...baseOptions(baseURL), storageState: hostSessionPath });

/** A guest phone. */
export const guestContext = (browser: Browser, baseURL: string | undefined) =>
  browser.newContext({ ...devices["Pixel 7"], ...baseOptions(baseURL) });

/** A fictional guest name; the "E2E " prefix lets a later run find what an aborted run left behind. */
export const uniqueGuestName = () => `E2E ${Math.random().toString(36).slice(2, 7).toUpperCase()}`;

/** A fictional mobile (021 0xx xxxx) per guest: a known mobile reuses the stored guest and its name (Design §14.1). */
export const uniqueMobile = () => {
  const digits = Math.floor(Math.random() * 1_000_000)
    .toString()
    .padStart(6, "0");
  return `021 0${digits.slice(0, 2)} ${digits.slice(2)}`;
};

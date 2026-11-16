import { expect, type Page } from "@playwright/test";

/** The sign-in page's answer to too many attempts (en.json errors.too_many_requests; /demo-login allows 5 a minute). */
const rateLimited = "Too many attempts. Please wait a minute and try again.";

/**
 * Types `passcode` into /demo-login's form and taps "Sign in" (Design §17). Waits first until React has hydrated the
 * passcode field: typed earlier, the server-rendered field takes the text and hydration then puts React's empty state
 * back, so the form says "Please enter the passcode." without asking the API (seen on the iPad in WebKit, M11b).
 * If the API answers 429 (5 attempts a minute per IP), fails saying so instead of with a missing message.
 */
export const submitPasscode = async (page: Page, passcode: string) => {
  const field = page.getByLabel("Passcode", { exact: true });
  await expect(field).toBeVisible();
  await page.waitForFunction(() => {
    const input = document.getElementById("passcode");
    return !!input && Object.keys(input).some((key) => key.startsWith("__reactProps"));
  });
  await field.fill(passcode);
  await expect(field).toHaveValue(passcode);
  await page.getByRole("button", { name: "Sign in" }).click();
};

/** Fails with a clear message when the sign-in page shows the rate-limit answer. */
export const expectNotRateLimited = async (page: Page) => {
  const limited = await page.getByText(rateLimited).isVisible();
  expect(
    limited,
    "/demo-login answered 429: more than 5 sign-in attempts this minute from this machine. Wait a minute and rerun.",
  ).toBe(false);
};

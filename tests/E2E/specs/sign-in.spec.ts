import { expect, test } from "@playwright/test";
import { expectNotRateLimited, submitPasscode } from "../support/signIn";

// The staff sign-in (Design §17, §18.5): a wrong passcode, and a host screen without a session. Moved out of host.spec
// in M11b so they run once per suite in the default project, not again on the iPad project: /demo-login allows 5
// attempts a minute per IP, and global-setup.ts already signs in twice. The passcode is typed only once React has
// hydrated the field (support/signIn.ts).

test("a wrong passcode shows an error", async ({ page }) => {
  await page.goto("/demo-login/");
  await submitPasscode(page, "not-the-passcode");

  const wrong = page.getByText("That passcode isn't right. Please try again.");
  await expect(wrong.or(page.getByText(/Too many attempts/))).toBeVisible();
  await expectNotRateLimited(page);
  await expect(wrong).toBeVisible();
  await expect(page.getByLabel("Passcode", { exact: true })).toHaveValue("");
  await expect(page).toHaveURL(/\/demo-login\/$/);
});

test("/host without a session goes to /demo-login", async ({ page }) => {
  await page.goto("/host/");

  await expect(page).toHaveURL(/\/demo-login\/$/);
  await expect(page.getByRole("heading", { level: 1, name: "Staff sign-in (demo)" })).toBeVisible();
});

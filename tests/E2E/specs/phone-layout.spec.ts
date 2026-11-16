import { expect, test, type Browser, type Page } from "@playwright/test";
import { expectNoHorizontalScroll } from "../support/layout";
import { loadPresenterScreen, phoneDevice } from "../support/screens";
import { hostSessionPath, presenterSessionPath } from "../support/session";

// QA-8 (M11b, Requirements §8; Design §17.3): every page at the project's phone width (an Android phone in Edge /
// Chromium, an iPhone in WebKit), in English and 中文, has no horizontal scrolling: the guest's booking steps, the
// staff sign-in, and the host and presenter screens signed in. The manage page is checked in manage.spec, the pay step
// and confirmations in deposit.spec and booking.spec. Nothing is booked or changed.

/** Checks `page` as it is, then again after switching to 中文 with its own switch. */
const inBothLanguages = async (page: Page, where: string, after: () => Promise<void> = async () => {}) => {
  await expectNoHorizontalScroll(page, `${where} (en)`);
  await page.getByRole("button", { name: "中文", exact: true }).click();
  await expect(page.locator("html")).toHaveAttribute("lang", "zh-CN");
  await after();
  await expectNoHorizontalScroll(page, `${where} (zh)`);
};

/** A phone-sized context, signed in with a shared staff session when given one. */
const phone = (browser: Browser, baseURL: string | undefined, storageState?: string) =>
  browser.newContext({ ...phoneDevice(), baseURL, ignoreHTTPSErrors: true, storageState });

test("the booking page's steps fit the phone (QA-8)", async ({ page }) => {
  await page.goto("/book/");
  const date = page.getByLabel("Date", { exact: true });
  await expect(date).toHaveAttribute("min", /^\d{4}-\d{2}-\d{2}$/);
  await expectNoHorizontalScroll(page, "book, first step (en)");

  // Tonight at 19:00 for 4 is full on the seed: the nearest times are offered.
  await page.getByLabel("Party size", { exact: true }).selectOption("4");
  await date.fill((await date.getAttribute("min"))!);
  await page.getByLabel("Time", { exact: true }).selectOption("19:00:00");
  await expect(page.getByText(/is full|is available|no free tables/)).toBeVisible();
  await inBothLanguages(page, "book, time step");

  // On to the details step (the first time offered, or Continue); nothing is booked.
  await page
    .getByRole("listitem")
    .getByRole("button")
    .or(page.getByRole("button", { name: "继续", exact: true }))
    .first()
    .click();
  await expect(page.getByLabel("手机号码", { exact: true })).toBeVisible();
  await expectNoHorizontalScroll(page, "book, details step (zh)");
  await page.getByRole("button", { name: "English", exact: true }).click();
  await expectNoHorizontalScroll(page, "book, details step (en)");
});

test("the staff sign-in fits the phone (QA-8)", async ({ page }) => {
  await page.goto("/demo-login/");
  await expect(page.getByRole("button", { name: "Sign in" })).toBeVisible();
  await inBothLanguages(page, "demo-login");
});

test("the host screen fits the phone (QA-8)", async ({ browser, baseURL }) => {
  const context = await phone(browser, baseURL, hostSessionPath);
  try {
    const page = await context.newPage();
    await page.goto("/host/");
    await expect(page.getByTestId("floor-plan")).toBeVisible();
    await expect(page.getByTestId("tonight-list")).toBeVisible();
    await inBothLanguages(page, "host", async () => {
      await page.getByTestId("table-T4").click();
      await expect(page.getByTestId("table-panel")).toBeVisible();
    });
  } finally {
    await context.close();
  }
});

test("the presenter screen fits the phone (QA-8)", async ({ browser, baseURL }) => {
  const context = await phone(browser, baseURL, presenterSessionPath);
  try {
    const page = await loadPresenterScreen(await context.newPage());
    await inBothLanguages(page, "presenter");
  } finally {
    await context.close();
  }
});

import { expect, test, type Page } from "@playwright/test";
import { describeOffenders, wrongLanguageText } from "../support/language";
import {
  guestContext,
  hostContext,
  openPresenterScreen,
  phoneMessages,
  posRows,
  presenterContext,
} from "../support/screens";
import { bookingByReference, guestNames, resetDemo, seatWalkIns } from "../support/session";

// QA-7 (M11b, Requirements §8; Design §17.3): every screen (book, host, presenter, manage, demo-login), switched to one
// language with its own switch, shows no UI text in the other: no untranslated English words in 中文, no Chinese in
// English. Guest names, text messages in the guest's language and the POS JSON are data, not UI, and are skipped
// (support/language.ts says how). Each screen is a soft check, so one run lists every offender with its element.
// The evening is reset before and after. Before the checks: one walk-in seated (a POS message to open) and "Send
// reminders now" (#10's and #11's manage links, opened on a guest phone).

const confirmsRef = "GDB3K7"; // #10, English
const cancelsRef = "GDC4L8"; // #11, Chinese

test.describe("QA-7: each screen in one language", () => {
  test.describe.configure({ mode: "serial" });

  const managePaths: string[] = [];

  test.beforeAll(async ({ browser, baseURL }) => {
    await resetDemo();
    await seatWalkIns(1, 2);
    const context = await presenterContext(browser, baseURL);
    try {
      const presenter = await openPresenterScreen(context);
      await presenter.getByTestId("send-reminders").click();
      await expect(presenter.getByTestId("reminders-status")).toContainText("2 reminders sent");
      // Each reminder's link, as the phone inside the presenter screen opens it.
      for (const [reference, action] of [
        [confirmsRef, "confirm"],
        [cancelsRef, "cancel"],
      ] as const) {
        const { id } = await bookingByReference(reference);
        const reminder = presenter.locator(
          `[data-testid="phone-message"][data-subject-id="${id}"][data-purpose="reminder"]`,
        );
        await expect(reminder).toHaveCount(1, { timeout: 2_000 });
        await reminder.locator(`[data-action="${action}"]`).click();
        const frame = presenter.getByTestId("phone-browser").locator("iframe");
        managePaths.push((await frame.getAttribute("src"))!);
        await presenter.getByRole("button", { name: "Messages" }).click();
        await expect(presenter.getByTestId("phone-browser")).toHaveCount(0);
      }
      expect(managePaths.every((p) => p.startsWith("/manage/"))).toBe(true);
    } finally {
      await context.close();
    }
  });

  test.afterAll(async () => {
    await resetDemo();
  });

  for (const language of ["zh", "en"] as const) {
    const name = language === "zh" ? "中文" : "English";

    test(`${name}: no UI text in the other language on any screen (QA-7)`, async ({ browser, baseURL }) => {
      test.setTimeout(120_000);
      const names = await guestNames();

      /** Switches with the screen's own English / 中文 switch, whatever language the page opened in. */
      const switchLanguage = async (page: Page) => {
        await page.getByRole("button", { name, exact: true }).click();
        await expect(page.locator("html")).toHaveAttribute("lang", language === "zh" ? "zh-CN" : "en");
      };
      const check = async (page: Page, screen: string) => {
        const offenders = await wrongLanguageText(page, language, names);
        expect.soft(describeOffenders(offenders), `${screen}, in ${name}: UI text in the other language`).toEqual([]);
      };

      await test.step("demo-login", async () => {
        const context = await browser.newContext({ baseURL, ignoreHTTPSErrors: true });
        try {
          const page = await context.newPage();
          await page.goto("/demo-login/");
          await expect(page.getByRole("button", { name: "Sign in" })).toBeVisible();
          await switchLanguage(page);
          await check(page, "demo-login");
        } finally {
          await context.close();
        }
      });

      await test.step("book: the nearest times, then the details step", async () => {
        const context = await guestContext(browser, baseURL);
        try {
          const page = await context.newPage();
          await page.goto("/book/");
          const date = page.getByLabel("Date", { exact: true });
          await expect(date).toHaveAttribute("min", /^\d{4}-\d{2}-\d{2}$/);
          await page.getByLabel("Party size", { exact: true }).selectOption("4");
          await date.fill((await date.getAttribute("min"))!);
          await page.getByLabel("Time", { exact: true }).selectOption("19:00:00");
          await expect(page.getByText(/is full|is available|no free tables/)).toBeVisible();
          await switchLanguage(page);
          await check(page, "book, time step");

          // On to the details step (the first time offered, or Continue); nothing is booked.
          await page
            .getByRole("listitem")
            .getByRole("button")
            .or(page.getByRole("button", { name: /^(Continue|继续)$/ }))
            .first()
            .click();
          await expect(page.getByLabel(language === "zh" ? "手机号码" : "Mobile", { exact: true })).toBeVisible();
          await check(page, "book, details step");
        } finally {
          await context.close();
        }
      });

      await test.step("host: floor plan, tonight's list, waitlist, a table's panel, the walk-in dialog", async () => {
        const context = await hostContext(browser, baseURL);
        try {
          const page = await context.newPage();
          await page.goto("/host/");
          await expect(page.getByTestId("floor-plan")).toBeVisible();
          await expect(page.getByTestId("tonight-list")).toBeVisible();
          await expect(page.getByTestId("waitlist")).toBeVisible();
          await expect(page.getByTestId("table-T4")).toHaveAttribute("data-state", "seated");
          await switchLanguage(page);
          await check(page, "host");

          await page.getByTestId("table-T4").click();
          await expect(page.getByTestId("table-panel")).toBeVisible();
          await check(page, "host, T4's panel");

          await page.getByTestId("walk-in-button").click();
          await expect(page.getByTestId("walk-in-dialog")).toBeVisible();
          await check(page, "host, walk-in dialog");
        } finally {
          await context.close();
        }
      });

      await test.step("presenter: phone panel, POS log with an open message, demo controls, Scan to book", async () => {
        const context = await presenterContext(browser, baseURL);
        try {
          const page = await openPresenterScreen(context);
          await expect(phoneMessages(page).first()).toBeVisible();
          await expect(posRows(page).first()).toBeVisible();
          await switchLanguage(page);
          await posRows(page).first().getByRole("button").first().click();
          await expect(page.getByTestId("pos-json")).toBeVisible();
          await check(page, "presenter");

          // M12b: the "Scan to book" code full size (Design §17.5).
          await page.getByTestId("scan-to-book").getByRole("button").click();
          await expect(page.getByTestId("scan-overlay")).toBeVisible();
          await check(page, "presenter, Scan to book overlay");
          await page.keyboard.press("Escape");
          await expect(page.getByTestId("scan-overlay")).toHaveCount(0);
        } finally {
          await context.close();
        }
      });

      await test.step("manage: #10's and #11's links on a guest phone", async () => {
        const context = await guestContext(browser, baseURL);
        try {
          const page = await context.newPage();
          for (const [index, managePath] of managePaths.entries()) {
            await page.goto(managePath);
            await expect(page.getByTestId("manage-page")).toContainText(index === 0 ? confirmsRef : cancelsRef);
            await switchLanguage(page);
            await check(page, `manage (${index === 0 ? "#10, Confirm link" : "#11, Cancel link"})`);
          }
        } finally {
          await context.close();
        }
      });
    });
  }
});

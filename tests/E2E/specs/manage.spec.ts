import { expect, test } from "@playwright/test";
import { expectNoHorizontalScroll } from "../support/layout";
import { openPresenterScreen, presenterContext } from "../support/screens";
import { bookingByReference, resetDemo } from "../support/session";

// QA-8 (M11b, Design §17.3): the guest's manage page on the guest's own phone (the project's phone: an Android phone in
// Edge / Chromium, an iPhone in WebKit), not inside the presenter's phone panel as in reminders.spec. #10 (GDB3K7)
// gets a reminder at 18:25, opens its Confirm link, checks the page fits the phone in both languages, and confirms.
// The demo is reset before (a fresh #10) and after (so the specs that follow start from the seeded evening).

const confirmsRef = "GDB3K7"; // #10

test.describe("the manage page on a phone (QA-8)", () => {
  test.describe.configure({ mode: "serial" });

  test.beforeAll(async () => {
    await resetDemo();
  });

  test.afterAll(async () => {
    await resetDemo();
  });

  test("#10 opens its Confirm link on the phone, in English and 中文, and confirms (AC5.3)", async ({
    page,
    browser,
    baseURL,
  }) => {
    // The reminder, and its Confirm link as the presenter's phone panel opens it.
    const presenterScreen = await presenterContext(browser, baseURL);
    let confirmPath: string;
    try {
      const presenter = await openPresenterScreen(presenterScreen);
      await presenter.getByTestId("send-reminders").click();
      await expect(presenter.getByTestId("reminders-status")).toContainText("2 reminders sent");
      const { id } = await bookingByReference(confirmsRef);
      const reminder = presenter.locator(
        `[data-testid="phone-message"][data-subject-id="${id}"][data-purpose="reminder"]`,
      );
      await expect(reminder).toHaveCount(1, { timeout: 2_000 });
      await reminder.locator('[data-action="confirm"]').click();
      confirmPath = (await presenter.getByTestId("phone-browser").locator("iframe").getAttribute("src"))!;
      expect(confirmPath).toMatch(/^\/manage\//);
    } finally {
      await presenterScreen.close();
    }

    await page.goto(confirmPath);
    await expect(page.getByTestId("manage-page")).toContainText(confirmsRef);
    await expect(page.getByTestId("manage-status")).toHaveAttribute("data-status", "booked");
    await expectNoHorizontalScroll(page, "manage page (en)");

    await page.getByRole("button", { name: "中文", exact: true }).click();
    await expect(page.locator("html")).toHaveAttribute("lang", "zh-CN");
    await expectNoHorizontalScroll(page, "manage page (zh)");
    await page.getByRole("button", { name: "English", exact: true }).click();

    await page.getByTestId("manage-confirm").click();
    await expect(page.getByTestId("manage-result")).toHaveText("Thanks, you're confirmed.");
    await expectNoHorizontalScroll(page, "manage page, confirmed");
  });
});

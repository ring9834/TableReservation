import { expect, test, type BrowserContext, type Page } from "@playwright/test";
import { guestContext, hostContext, openPresenterScreen, presenterContext } from "../support/screens";
import { bookingByReference, resetDemo } from "../support/session";

// M10b (Requirements step 5, AC5.1-AC5.6, BR-07, BR-13; Design §7.3, §10.4, §18.10): reminders, the guest's manage
// page inside the presenter's phone, and the host screen, against the real API, delivery worker, SignalR and database,
// with the business clock fixed at 18:25 in Development (Design §17).
// At 18:25 only two seeded bookings get a reminder: #10 (GDB3K7, party of 2, English, no deposit) confirms; #11
// (GDC4L8, party of 6, Chinese, NZ$120.00 deposit paid) cancels in time and is refunded in full (Fake in CI; locally in
// Stripe mode the seed router refunds the seeded intent, Design §7.3).
// The demo is reset before (so #10 and #11 are fresh) and after (so walk-in.spec, which runs next, starts from the
// seeded evening). The late-cancellation wording is unit-tested (manage.test.mjs), not here: it needs another time.

const confirmsRef = "GDB3K7"; // #10
const cancelsRef = "GDC4L8"; // #11

const openHostScreen = async (context: BrowserContext) => {
  const page = await context.newPage();
  await page.goto("/host/");
  await expect(page.getByTestId("floor-plan")).toBeVisible();
  return page;
};

test.describe("Reminders and the guest's manage page (step 5)", () => {
  test.describe.configure({ mode: "serial" });

  let hostScreen: BrowserContext;
  let presenterScreen: BrowserContext;
  let host: Page;
  let presenter: Page;
  let confirmsId: string;
  let cancelsId: string;

  test.beforeAll(async ({ browser, baseURL }) => {
    await resetDemo();
    confirmsId = (await bookingByReference(confirmsRef)).id;
    cancelsId = (await bookingByReference(cancelsRef)).id;
    hostScreen = await hostContext(browser, baseURL);
    presenterScreen = await presenterContext(browser, baseURL);
    [host, presenter] = await Promise.all([openHostScreen(hostScreen), openPresenterScreen(presenterScreen)]);
  });

  test.afterAll(async () => {
    await Promise.all([hostScreen?.close(), presenterScreen?.close()]);
    await resetDemo();
  });

  const texts = (bookingId: string, purpose: string) =>
    presenter.locator(
      `[data-testid="phone-message"][data-subject-id="${bookingId}"][data-purpose="${purpose}"]`,
    );
  const listRow = (reference: string) =>
    host.getByTestId("tonight-list").locator("li").filter({ has: host.getByTestId(`booking-${reference}`) });
  const phoneFrame = () => presenter.getByTestId("phone-browser").frameLocator("iframe");

  /** Opens the booking's details on the host screen and returns its reminder line. */
  const reminderLine = async (reference: string, bookingId: string) => {
    await listRow(reference).getByTestId(`booking-${reference}`).click();
    const panel = host.getByTestId("table-panel").or(host.getByTestId("booking-panel"));
    return panel.locator(`[data-booking-id="${bookingId}"]`).getByTestId("reminder-line");
  };

  test("Send reminders now: two reminders, #10 in English and #11 in Chinese, each with Confirm and Cancel (AC5.1)", async () => {
    await presenter.getByTestId("send-reminders").click();
    await expect(presenter.getByTestId("reminders-status")).toContainText("2 reminders sent");
    await expect(presenter.getByTestId("reminders-status")).toContainText("4 skipped");

    for (const [bookingId, language] of [
      [confirmsId, "en"],
      [cancelsId, "zh"],
    ] as const) {
      const reminder = texts(bookingId, "reminder");
      await expect(reminder).toHaveCount(1, { timeout: 2_000 });
      await expect(reminder).toHaveAttribute("data-language", language);
      await expect(reminder.locator("[data-action]")).toHaveCount(2);
      await expect(reminder.locator('[data-action="confirm"]')).toBeVisible();
      await expect(reminder.locator('[data-action="cancel"]')).toBeVisible();
      // The links in the text are shown short; the token never appears whole.
      await expect(reminder.locator("p a").first()).toContainText("/manage/?token=");
      await expect(reminder.locator("p a").first()).toContainText("…");
    }
    await expect(texts(confirmsId, "reminder").locator('[data-action="confirm"]')).toHaveText("Confirm");
    await expect(texts(cancelsId, "reminder").locator('[data-action="cancel"]')).toHaveText("取消");

    // The host's details show the reminder with no reply yet.
    await expect(await reminderLine(confirmsRef, confirmsId)).toHaveAttribute("data-response", "none", {
      timeout: 2_000,
    });
  });

  test("#10 confirms from the phone; the host sees Confirmed within 2 s (AC5.3, AC5.6)", async () => {
    await texts(confirmsId, "reminder").locator('[data-action="confirm"]').click();
    const page = phoneFrame();
    await expect(page.getByTestId("manage-page")).toContainText(confirmsRef);
    await expect(page.getByTestId("manage-status")).toHaveAttribute("data-status", "booked");
    await expect(page.getByTestId("manage-cancel")).toHaveCount(0); // a Confirm link only confirms

    await page.getByTestId("manage-confirm").click();
    await expect(page.getByTestId("manage-result")).toHaveText("Thanks, you're confirmed.");
    await expect(listRow(confirmsRef).getByTestId("booking-status")).toHaveText("Confirmed", { timeout: 2_000 });

    const line = await reminderLine(confirmsRef, confirmsId);
    await expect(line).toHaveAttribute("data-response", "confirmed", { timeout: 2_000 });
    await expect(line).toContainText("Confirmed");

    await presenter.getByRole("button", { name: "Messages" }).click();
    await expect(presenter.getByTestId("phone-browser")).toHaveCount(0);
  });

  test("#11 cancels in Chinese in time: the table frees within 2 s and the deposit is refunded (AC5.4, AC5.5, AC5.6)", async () => {
    // T9 holds #11 next (#2 is seated there now).
    await host.getByTestId("table-T9").click();
    const t9 = host.getByTestId("table-panel");
    await expect(t9.locator(`[data-booking-id="${cancelsId}"]`)).toHaveCount(1);

    await texts(cancelsId, "reminder").locator('[data-action="cancel"]').click();
    const page = phoneFrame();
    await expect(page.locator("html")).toHaveAttribute("lang", "zh-CN");
    await expect(page.getByTestId("manage-page")).toContainText(cancelsRef);
    await expect(page.getByTestId("manage-confirm")).toHaveCount(0); // a Cancel link only cancels

    await page.getByTestId("manage-cancel").click();
    const consequence = page.getByTestId("manage-consequence");
    await expect(consequence).toHaveAttribute("data-consequence", "refundFull");
    await expect(consequence).toContainText("NZ$120.00");
    await expect(consequence).toContainText("全额退还");
    await page.getByTestId("manage-cancel-confirm").click();
    await expect(page.getByTestId("manage-result")).toContainText("您的预订已取消");
    await expect(page.getByTestId("manage-result")).toContainText("退款");

    // AC5.4: the host frees T9's slot within 2 s.
    await expect(t9.locator(`[data-booking-id="${cancelsId}"]`)).toHaveCount(0, { timeout: 2_000 });
    await expect(listRow(cancelsRef).getByTestId("booking-status")).toHaveText("Cancelled", { timeout: 2_000 });

    // AC5.5: the deposit is refunded; the guest gets the Cancellation and Refund texts in Chinese.
    await expect(listRow(cancelsRef).getByTestId("deposit-line")).toHaveAttribute("data-deposit", "refunded", {
      timeout: 15_000,
    });
    for (const purpose of ["cancellation", "refund"]) {
      await expect(texts(cancelsId, purpose)).toHaveCount(1, { timeout: 10_000 });
      await expect(texts(cancelsId, purpose)).toHaveAttribute("data-language", "zh");
    }

    // AC5.6: the reminder line shows the guest's answer.
    const line = await reminderLine(cancelsRef, cancelsId);
    await expect(line).toHaveAttribute("data-response", "cancelled");
    await expect(line).toContainText("Cancelled by guest");

    await presenter.getByRole("button", { name: "Messages" }).click();
  });

  test("a second press sends nothing new", async () => {
    await presenter.getByTestId("send-reminders").click();
    await expect(presenter.getByTestId("reminders-status")).toContainText("No new reminders");
  });

  test("a garbage link says it isn't valid, and the page sends no referrer", async ({ browser, baseURL }) => {
    const guest = await guestContext(browser, baseURL);
    try {
      const page = await guest.newPage();
      await page.goto("/manage/?token=garbage");
      await expect(page.getByTestId("manage-error")).toHaveText("This link isn't valid.");
      await expect(page.locator('meta[name="referrer"]')).toHaveAttribute("content", "no-referrer");
    } finally {
      await guest.close();
    }
  });
});

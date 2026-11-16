import { expect, test, type BrowserContext, type Page } from "@playwright/test";
import {
  hostContext,
  openPresenterScreen,
  presenterContext,
  uniqueGuestName,
  uniqueMobile,
} from "../support/screens";
import { advanceDemoClock, notifyWaitingParty, resetDemo } from "../support/session";

// M11a (Requirements §10, AC4.6, QA-6; Design §17.2): the presenter's demo controls move business time, and both host
// screens follow at once: a party notified at 18:25 is flagged overdue once the clock passes 10 minutes. Reset brings
// back 18:25 and the seeded evening. Only business time moves; the infrastructure clock is untouched (rule 14).
// The demo is reset before and after, so the specs that follow start from the seeded evening at 18:25.

const openHostScreen = async (context: BrowserContext) => {
  const page = await context.newPage();
  await page.goto("/host/");
  await expect(page.getByTestId("floor-plan")).toBeVisible();
  await expect(page.getByTestId("waitlist")).toBeVisible();
  return page;
};

const waitlistEntry = (page: Page, name: string) =>
  page.getByTestId("waitlist-entry").filter({ has: page.getByRole("heading", { name, exact: true }) });

test.describe("the demo clock and Reset (presenter controls)", () => {
  test.describe.configure({ mode: "serial" });

  const waitingName = uniqueGuestName();
  let hostA: BrowserContext;
  let hostB: BrowserContext;
  let presenterScreen: BrowserContext;
  let screenA: Page;
  let screenB: Page;
  let presenter: Page;

  test.beforeAll(async ({ browser, baseURL }) => {
    await resetDemo();
    hostA = await hostContext(browser, baseURL);
    hostB = await hostContext(browser, baseURL);
    presenterScreen = await presenterContext(browser, baseURL);
    [screenA, screenB, presenter] = await Promise.all([
      openHostScreen(hostA),
      openHostScreen(hostB),
      openPresenterScreen(presenterScreen),
    ]);
  });

  test.afterAll(async () => {
    await Promise.all([hostA?.close(), hostB?.close(), presenterScreen?.close()]);
    await resetDemo();
  });

  test("the presenter's Demo group shows the fixed demo time", async () => {
    const controls = presenter.getByTestId("demo-controls");
    await expect(controls).toBeVisible();
    await expect(controls.getByTestId("demo-clock")).toHaveText("6:25 pm · fixed");
    await expect(controls.getByTestId("send-reminders")).toBeVisible();
  });

  test("advancing 11 minutes flags a notified party overdue on both host screens within 2 s (AC4.6)", async () => {
    await notifyWaitingParty(waitingName, uniqueMobile(), 2, "T1");
    for (const screen of [screenA, screenB]) {
      await expect(waitlistEntry(screen, waitingName)).toBeVisible({ timeout: 2_000 });
      await expect(waitlistEntry(screen, waitingName).getByTestId("overdue-badge")).toHaveCount(0);
    }

    // +5 twice on the presenter screen: 18:35, exactly 10 minutes, not overdue yet (BR-10).
    const clock = presenter.getByTestId("demo-clock");
    await presenter.getByTestId("advance-5").click();
    await expect(clock).toHaveText("6:30 pm · fixed");
    await presenter.getByTestId("advance-5").click();
    await expect(clock).toHaveText("6:35 pm · fixed");
    for (const screen of [screenA, screenB]) {
      await expect(waitlistEntry(screen, waitingName).getByTestId("overdue-badge")).toHaveCount(0);
    }

    // One more minute: 11 minutes since the text, and both screens flag it at once.
    await advanceDemoClock(1);
    for (const screen of [screenA, screenB]) {
      const badge = waitlistEntry(screen, waitingName).getByTestId("overdue-badge");
      await expect(badge).toBeVisible({ timeout: 2_000 });
      // What is overdue, counted from the Notify on the business clock (M11b).
      await expect(badge).toHaveText("⚠ Not back 11 min after notify");
    }
    await expect(clock).toHaveText("6:36 pm · fixed", { timeout: 2_000 });
  });

  test("Reset asks first, then brings back 18:25 and the seeded evening on every screen", async () => {
    await presenter.getByTestId("reset-demo").click();
    const confirm = presenter.getByTestId("reset-confirm");
    await expect(confirm).toContainText("Reset the demo to 18:25 with the sample bookings?");
    await confirm.getByTestId("reset-yes").click();

    await expect(presenter.getByTestId("demo-clock")).toHaveText("6:25 pm · fixed", { timeout: 5_000 });
    await expect(presenter.getByTestId("demo-status")).toContainText("Back at 6:25 pm");
    await expect(confirm).toBeHidden();
    for (const screen of [screenA, screenB]) {
      await expect(waitlistEntry(screen, waitingName)).toHaveCount(0, { timeout: 2_000 });
      await expect(screen.getByTestId("table-T4")).toHaveAttribute("data-state", "seated");
      await expect(screen.getByTestId("table-T1")).toHaveAttribute("data-state", "free");
    }
  });
});

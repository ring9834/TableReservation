import {
  devices,
  expect,
  test,
  type Browser,
  type BrowserContext,
  type BrowserContextOptions,
  type Page,
} from "@playwright/test";
import { cancelBookings, hostSessionPath } from "../support/session";

// Requirements step 2 (AC2.1, AC2.4, AC2.5; QA-1, QA-2) with two host screens and a guest phone against the real
// API, SignalR and database. The API's business clock is fixed at 18:25 in Development (Design §17), so "tonight"
// at 20:00 / 20:15 is always still to come. The bookings a run makes are cancelled afterwards through the host
// cancel endpoint, so reruns against one database don't fill the evening (cancelled bookings don't count for pacing).
// The host session comes from global-setup.ts (one sign-in per run, shared with the other spec files).

const baseOptions = (baseURL: string | undefined): BrowserContextOptions => ({
  baseURL,
  ignoreHTTPSErrors: true, // local dev certificates
});

/** A host tablet, signed in with the shared session. */
const hostContext = (browser: Browser, baseURL: string | undefined) =>
  browser.newContext({ ...devices["Galaxy Tab S4 landscape"], ...baseOptions(baseURL), storageState: hostSessionPath });

/** A guest phone. */
const guestContext = (browser: Browser, baseURL: string | undefined) =>
  browser.newContext({ ...devices["Pixel 7"], ...baseOptions(baseURL) });

const uniqueGuestName = () =>
  `E2E ${Math.random().toString(36).slice(2, 7).toUpperCase()}`;

/** A fictional mobile (021 0xx xxxx) per booking: a known mobile reuses the stored guest and its name (Design §14.1). */
const uniqueMobile = () => {
  const digits = Math.floor(Math.random() * 1_000_000)
    .toString()
    .padStart(6, "0");
  return `021 0${digits.slice(0, 2)} ${digits.slice(2)}`;
};

/**
 * Books tonight (the venue's "today", the date picker's `min`) for 2 at `time` on /book/, waits for the confirmation
 * and returns the booking reference.
 */
const bookTonight = async (page: Page, time: string, name: string) => {
  await page.goto("/book/");
  const date = page.getByLabel("Date", { exact: true });
  await expect(date).toHaveAttribute("min", /^\d{4}-\d{2}-\d{2}$/);
  const today = (await date.getAttribute("min"))!;

  await page.getByLabel("Party size", { exact: true }).selectOption("2");
  await date.fill(today);
  await page.getByLabel("Time", { exact: true }).selectOption(time);
  await expect(page.getByText(/is available for 2 guests/)).toBeVisible();
  await page.getByRole("button", { name: "Continue" }).click();

  await page.getByLabel("Name", { exact: true }).fill(name);
  await page.getByLabel("Mobile", { exact: true }).fill(uniqueMobile());
  await page.getByRole("button", { name: "Book now" }).click();
  await expect(page.getByRole("heading", { name: "You're booked!" })).toBeVisible();
  return (await page.getByTestId("booking-reference").textContent())!.trim();
};

/**
 * The booking is in tonight's list and on the floor plan. On the plan a tile names the party seated there or the next
 * one, so the check taps the booking's table and reads the side panel, which works whichever table it got.
 */
const expectBookingShown = async (page: Page, name: string, timeout: number) => {
  const deadline = Date.now() + timeout;
  const left = () => Math.max(1, deadline - Date.now());
  const row = page.getByTestId("tonight-list").getByRole("button", { name: new RegExp(name) });
  await expect(row).toBeVisible({ timeout: left() });
  const tableName = (await row.textContent())?.match(/· ([A-Z]+\d+)/)?.[1];
  expect(tableName, "the booking has a table").toBeTruthy();

  await page.getByTestId(`table-${tableName}`).click();
  await expect(page.getByTestId("table-panel")).toContainText(name, { timeout: left() });
};

/**
 * SignalR errors a host page wrote to the console. Under `next dev` React Strict Mode mounts the connection twice, and
 * the first start, stopped by us, must not surface as an error (Design §18.5).
 */
const signalRConsoleErrors: string[] = [];

const openHostScreen = async (context: BrowserContext) => {
  const page = await context.newPage();
  page.on("console", (message) => {
    const text = message.text();
    if (message.type() === "error" && /SignalR|Failed to start the connection|stopped during negotiation/.test(text)) {
      signalRConsoleErrors.push(text);
    }
  });
  await page.goto("/host/");
  await expect(page.getByTestId("floor-plan")).toBeVisible();
  await expect(page.getByTestId("tonight-list")).toBeVisible();
  return page;
};

test.describe("host screens", () => {
  test.describe.configure({ mode: "serial" });

  const created: string[] = [];

  test.afterAll(async () => {
    await cancelBookings(created);
  });

  test("two host screens show a guest's booking within 2 seconds (AC2.1, AC2.4)", async ({
    browser,
    baseURL,
  }) => {
    const hostA = await hostContext(browser, baseURL);
    const hostB = await hostContext(browser, baseURL);
    const guest = await guestContext(browser, baseURL);
    const [screenA, screenB] = await Promise.all([openHostScreen(hostA), openHostScreen(hostB)]);
    const name = uniqueGuestName();

    created.push(await bookTonight(await guest.newPage(), "20:00:00", name));

    await Promise.all([
      expectBookingShown(screenA, name, 2_000),
      expectBookingShown(screenB, name, 2_000),
    ]);
    expect(signalRConsoleErrors, "SignalR console errors on the host screens").toEqual([]);
    await Promise.all([hostA.close(), hostB.close(), guest.close()]);
  });

  test("a host screen that was offline shows 'reconnecting' and catches up within 5 seconds (AC2.5)", async ({
    browser,
    baseURL,
  }) => {
    test.setTimeout(60_000);
    const host = await hostContext(browser, baseURL);
    const guest = await guestContext(browser, baseURL);
    const screen = await openHostScreen(host);
    const banner = screen.getByTestId("connection-banner");
    await expect(banner).toBeHidden();
    const name = uniqueGuestName();

    await host.setOffline(true);
    const offlineAt = Date.now();
    await expect(banner).toBeVisible();
    await expect(banner).toHaveText("Connection lost. Reconnecting…");

    created.push(await bookTonight(await guest.newPage(), "20:15:00", name));
    await screen.waitForTimeout(Math.max(0, 10_000 - (Date.now() - offlineAt)));
    await expect(screen.getByTestId("tonight-list").getByText(name)).toHaveCount(0);

    await host.setOffline(false);
    await expectBookingShown(screen, name, 5_000);
    await expect(banner).toBeHidden();
    expect(signalRConsoleErrors, "SignalR console errors while offline and reconnecting").toEqual([]);
    await Promise.all([host.close(), guest.close()]);
  });
});

test("a wrong passcode shows an error", async ({ page }) => {
  await page.goto("/demo-login/");
  await page.getByLabel("Passcode", { exact: true }).fill("not-the-passcode");
  await page.getByRole("button", { name: "Sign in" }).click();

  await expect(page.getByText("That passcode isn't right. Please try again.")).toBeVisible();
  await expect(page.getByLabel("Passcode", { exact: true })).toHaveValue("");
  await expect(page).toHaveURL(/\/demo-login\/$/);
});

test("/host without a session goes to /demo-login", async ({ page }) => {
  await page.goto("/host/");

  await expect(page).toHaveURL(/\/demo-login\/$/);
  await expect(page.getByRole("heading", { level: 1, name: "Staff sign-in (demo)" })).toBeVisible();
});

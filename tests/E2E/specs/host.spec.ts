import {
  devices,
  expect,
  test,
  type Browser,
  type BrowserContext,
  type BrowserContextOptions,
  type Page,
  request,
} from "@playwright/test";

// Requirements step 2 (AC2.1, AC2.4, AC2.5; QA-1, QA-2) with two host screens and a guest phone against the real
// API, SignalR and database. The API's business clock is fixed at 18:25 in Development (Design §17), so "tonight"
// at 20:00 / 20:15 is always still to come. The bookings a run makes are cancelled afterwards through the host
// cancel endpoint, so reruns against one database don't fill the evening (cancelled bookings don't count for pacing).

type BrowserStorageState = Awaited<ReturnType<BrowserContext["storageState"]>>;

const hostPasscode = process.env.E2E_HOST_PASSCODE ?? "";

/** The API the web app calls (CI: http://localhost:5000), for the clean-up, and Golden Dragon's seeded ID. */
const apiBaseUrl =
  process.env.E2E_API_BASE_URL ?? process.env.NEXT_PUBLIC_API_BASE_URL ?? "https://localhost:5001";
const venueId = process.env.E2E_VENUE_ID ?? "0193a7d0-0000-7000-8000-000000000001";

type HostListItem = { id: string; reference: string; status: string; version: number };

/** Cancels the given bookings with the host session (POST …/bookings/{id}/cancel {version}, Design §14.1). */
const cancelBookings = async (session: BrowserStorageState, references: string[]) => {
  if (references.length === 0) return;
  const api = await request.newContext({
    baseURL: apiBaseUrl,
    storageState: session,
    ignoreHTTPSErrors: true,
  });
  try {
    const list = await api.get(`/api/v1/venues/${venueId}/bookings`);
    expect(list.ok(), `host list answered ${list.status()}`).toBe(true);
    const { items } = (await list.json()) as { items: HostListItem[] };
    for (const item of items.filter((i) => references.includes(i.reference) && i.status !== "cancelled")) {
      const cancel = await api.post(`/api/v1/venues/${venueId}/bookings/${item.id}/cancel`, {
        data: { version: item.version },
      });
      expect(cancel.ok(), `cancel ${item.reference} answered ${cancel.status()}`).toBe(true);
    }
  } finally {
    await api.dispose();
  }
};

const baseOptions = (baseURL: string | undefined): BrowserContextOptions => ({
  baseURL,
  ignoreHTTPSErrors: true, // local dev certificates
});

/** A host tablet. */
const hostContext = (browser: Browser, baseURL: string | undefined, storageState?: BrowserStorageState) =>
  browser.newContext({ ...devices["Galaxy Tab S4 landscape"], ...baseOptions(baseURL), storageState });

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

  let hostSession: BrowserStorageState | undefined;
  const created: string[] = [];

  // One sign-in through the form for the whole file: /demo-login allows 5 attempts a minute per IP.
  test.beforeAll(async ({ browser, baseURL }) => {
    expect(hostPasscode, "Set E2E_HOST_PASSCODE to the API's Demo:HostPasscode").not.toBe("");
    const context = await hostContext(browser, baseURL);
    const page = await context.newPage();
    await page.goto("/demo-login/");
    await page.getByLabel("Passcode", { exact: true }).fill(hostPasscode);
    await page.getByRole("button", { name: "Sign in" }).click();
    await expect(page).toHaveURL(/\/host\/$/);
    await expect(page.getByTestId("floor-plan")).toBeVisible();
    hostSession = await context.storageState();
    await context.close();
  });

  test.afterAll(async () => {
    if (hostSession) await cancelBookings(hostSession, created);
  });

  test("two host screens show a guest's booking within 2 seconds (AC2.1, AC2.4)", async ({
    browser,
    baseURL,
  }) => {
    const hostA = await hostContext(browser, baseURL, hostSession);
    const hostB = await hostContext(browser, baseURL, hostSession);
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
    const host = await hostContext(browser, baseURL, hostSession);
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

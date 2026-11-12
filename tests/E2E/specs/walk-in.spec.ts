import { expect, test, type BrowserContext, type Page } from "@playwright/test";
import { hostContext, uniqueGuestName, uniqueMobile } from "../support/screens";
import { clearBookings, endWaitlistEntries, ensureSeated, quotedWait } from "../support/session";

// Requirements step 3 and the step 4 waitlist (AC3.1, AC3.2, AC3.4, AC4.1, AC4.2) on two host tablets against the
// real API, SignalR and database, with the business clock fixed at 18:25 in Development (Design §17, §18.6).
// The seed has every table of 4 or more seats taken at 18:25, T4 by a party seated at 17:30.
//
// Reruns against one database: the spec clears the tables it seated and ends the waitlist entries it added. Clearing
// T4 completes the seated party for good, so `beforeAll` seats a stand-in walk-in of 4 on T4 when it is free, and
// ends "E2E …" entries an aborted run left behind. The quote therefore depends on what is seated (35 min on a fresh
// seed, 50 after a stand-in), so the spec compares with the API's own quote; the exact 35 is checked by the
// integration tests on a fresh database.

const openHostScreen = async (context: BrowserContext) => {
  const page = await context.newPage();
  await page.goto("/host/");
  await expect(page.getByTestId("floor-plan")).toBeVisible();
  await expect(page.getByTestId("waitlist")).toBeVisible();
  return page;
};

/** Every `/walk-ins` answer on a page, for the clean-up. */
const recordWalkIns = (page: Page, references: string[]) =>
  page.on("response", async (response) => {
    if (response.request().method() === "POST" && /\/walk-ins$/.test(response.url()) && response.ok()) {
      references.push(((await response.json()) as { reference: string }).reference);
    }
  });

const waitlistEntry = (page: Page, name: string) =>
  page.getByTestId("waitlist-entry").filter({ has: page.getByRole("heading", { name, exact: true }) });

test.describe("walk-ins and the waitlist (step 3)", () => {
  test.describe.configure({ mode: "serial" });

  const seated: string[] = [];
  const entries: string[] = [];
  let hostA: BrowserContext;
  let hostB: BrowserContext;
  let screenA: Page;
  let screenB: Page;
  const waitingName = uniqueGuestName();

  test.beforeAll(async ({ browser, baseURL }) => {
    await endWaitlistEntries((entry) => entry.name.startsWith("E2E "));
    await ensureSeated("T4", 4);
    hostA = await hostContext(browser, baseURL);
    hostB = await hostContext(browser, baseURL);
    [screenA, screenB] = await Promise.all([openHostScreen(hostA), openHostScreen(hostB)]);
    recordWalkIns(screenA, seated);
    recordWalkIns(screenB, seated);
  });

  test.afterAll(async () => {
    await Promise.all([hostA?.close(), hostB?.close()]);
    await clearBookings(seated);
    await endWaitlistEntries(entries);
  });

  test("a walk-in for 2 is seated on the suggested table in three taps and shows on the other screen (AC3.1, AC3.2)", async () => {
    // Tap 1: Walk-in. Tap 2: party size. Tap 3: Seat (the suggested table is already chosen).
    await screenA.getByRole("button", { name: "Walk-in", exact: true }).click();
    const dialog = screenA.getByTestId("walk-in-dialog");
    await dialog.getByRole("button", { name: "Party of 2", exact: true }).click();
    const seatButton = dialog.getByRole("button", { name: /^Seat at / });
    await expect(seatButton).toBeEnabled();
    const tableName = (await seatButton.textContent())!.replace("Seat at ", "").trim();
    await expect(dialog.getByTestId(`choose-${tableName}`)).toBeChecked();

    const answer = screenA.waitForResponse((r) => /\/walk-ins$/.test(r.url()) && r.request().method() === "POST");
    await seatButton.click();
    expect((await answer).status()).toBe(201);
    const { reference } = (await (await answer).json()) as { reference: string };
    await expect(dialog).toBeHidden();

    for (const screen of [screenA, screenB]) {
      await expect(screen.getByTestId(`table-${tableName}`)).toHaveAttribute("data-state", "seated", {
        timeout: 2_000,
      });
      await expect(screen.getByTestId(`booking-${reference}`)).toBeVisible({ timeout: 2_000 });
    }
  });

  test("a walk-in for 4 finds no table and joins the waitlist with the API's quote (AC3.4, AC3.5)", async () => {
    const quote = await quotedWait(4);

    await screenA.getByRole("button", { name: "Walk-in", exact: true }).click();
    const walkIn = screenA.getByTestId("walk-in-dialog");
    await walkIn.getByRole("button", { name: "Party of 4", exact: true }).click();
    await expect(walkIn.getByTestId("walk-in-no-table")).toBeVisible();
    await walkIn.getByRole("button", { name: "Add to waitlist" }).click();

    const join = screenA.getByTestId("join-waitlist-dialog");
    await expect(join.getByLabel("Party size", { exact: true })).toHaveValue("4");
    await expect(join.getByTestId("quoted-wait")).toHaveValue(String(quote));
    await join.getByLabel("Name", { exact: true }).fill(waitingName);
    await join.getByLabel("Mobile", { exact: true }).fill(uniqueMobile());

    const answer = screenA.waitForResponse((r) => /\/waitlist$/.test(r.url()) && r.request().method() === "POST");
    await join.getByRole("button", { name: "Add to waitlist" }).click();
    expect((await answer).status()).toBe(201);
    entries.push(((await (await answer).json()) as { id: string }).id);
    await expect(join).toBeHidden();

    for (const screen of [screenA, screenB]) {
      await expect(waitlistEntry(screen, waitingName)).toContainText(`Quoted ${quote} min`, { timeout: 2_000 });
    }
  });

  test("clearing T4 highlights the waiting party of 4 on both screens; seating them there ends the entry (AC4.1, AC4.2)", async () => {
    await screenA.getByTestId("table-T4").click();
    await screenA.getByTestId("table-panel").getByRole("button", { name: "Clear table" }).click();

    for (const screen of [screenA, screenB]) {
      await expect(waitlistEntry(screen, waitingName)).toHaveAttribute("data-suggested", "true", { timeout: 2_000 });
      await expect(screen.getByTestId("table-T4")).toHaveAttribute("data-highlighted", "true");
      await expect(screen.getByTestId("suggestion-banner")).toContainText("T4");
    }

    const entry = waitlistEntry(screenB, waitingName);
    await entry.getByRole("button", { name: "Seat", exact: true }).click();
    await expect(entry.getByTestId("choose-T4")).toBeChecked();
    await entry.getByRole("button", { name: "Seat at T4" }).click();

    for (const screen of [screenA, screenB]) {
      await expect(waitlistEntry(screen, waitingName)).toHaveCount(0, { timeout: 2_000 });
      await expect(screen.getByTestId("table-T4")).toHaveAttribute("data-state", "seated");
      await expect(screen.getByTestId("table-T4")).not.toHaveAttribute("data-highlighted", "true");
    }
  });
});

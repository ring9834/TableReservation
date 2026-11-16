import { expect, test, type BrowserContext, type Page } from "@playwright/test";
import {
  hostContext,
  openPresenterScreen,
  posRows,
  presenterContext,
  uniqueGuestName,
  uniqueMobile,
} from "../support/screens";
import {
  cancelBookings,
  clearBookings,
  createPhoneBooking,
  expectSentInOrder,
  guestHistory,
  seatGuestFromWaitlist,
  seatWalkIns,
  setPosOnline,
  type SeatedWalkIn,
} from "../support/session";

// M9b (Requirements steps 11-12, AC6.1, AC6.2, AC6.5, AC6.6, QA-5; Design §6.7, §18.9): the presenter's POS panel and
// the host's "Bill paid" against the real API, delivery worker, mock POS, SignalR and database, with the business clock
// fixed at 18:25 in Development (Design §17).
// - QA-5: the presenter switches the mock POS offline, three walk-ins are seated, their OpenTable messages wait in
//   order and go out once each, in order, when it is switched back on.
// - Step 12: a message expands to its full JSON, which can be copied.
// - AC6.6: "Bill paid" on a seated guest's table closes the bill through the mock POS; the table frees and the guest's
//   next booking shows the visit and the bill.
// Tables: the walk-ins are parties of 2 on the tables the API suggests (at 18:25 the seed leaves only two-seat tables
// free: T1-T3, W1, W2), so the 4-seat stand-ins walk-in.spec relies on are untouched. Clean-up: afterAll switches the
// POS back on whatever happened, clears every walk-in still seated and cancels the phone booking.

const openHostScreen = async (context: BrowserContext) => {
  const page = await context.newPage();
  await page.goto("/host/");
  await expect(page.getByTestId("floor-plan")).toBeVisible();
  return page;
};

test.describe("POS log, switch and Bill paid (steps 11-12)", () => {
  test.describe.configure({ mode: "serial" });

  const walkIns: SeatedWalkIn[] = [];
  const phoneBookings: string[] = [];
  let hostScreen: BrowserContext;
  let presenterScreen: BrowserContext;
  let host: Page;
  let presenter: Page;

  test.beforeAll(async ({ browser, baseURL }) => {
    await setPosOnline(true); // an aborted run may have left it off
    hostScreen = await hostContext(browser, baseURL);
    presenterScreen = await presenterContext(browser, baseURL);
    await presenterScreen.grantPermissions(["clipboard-read", "clipboard-write"]);
    [host, presenter] = await Promise.all([
      openHostScreen(hostScreen),
      openPresenterScreen(presenterScreen),
    ]);
  });

  test.afterAll(async () => {
    await setPosOnline(true);
    await Promise.all([hostScreen?.close(), presenterScreen?.close()]);
    await clearBookings(walkIns.map((w) => w.reference));
    await cancelBookings(phoneBookings);
  });

  test("offline, three seated parties wait in order; online, each is sent once, in order (QA-5, AC6.5)", async () => {
    const posSwitch = presenter.getByRole("switch", { name: "Mock POS" });
    await expect(posSwitch).toHaveAttribute("aria-checked", "true");
    await expect(posSwitch).toHaveText(/POS online/);
    await posSwitch.click();
    await expect(posSwitch).toHaveAttribute("aria-checked", "false");
    await expect(posSwitch).toHaveText(/POS offline/);

    walkIns.push(...(await seatWalkIns(3, 2)));

    // Within 2 s: one OpenTable each, Waiting (never shown as a failure), consecutive sequence numbers (AC6.1, AC6.5).
    const sequences: number[] = [];
    for (const walkIn of walkIns) {
      const row = posRows(presenter, { type: "OpenTable", bookingId: walkIn.bookingId });
      await expect(row).toHaveCount(1, { timeout: 2_000 });
      await expect(row).toHaveAttribute("data-status", "waiting");
      await expect(row.getByTestId("pos-status")).toHaveText("Waiting for the POS");
      await expect(row.getByTestId("pos-direction")).toHaveText("To POS →");
      await expect(row.getByTestId("pos-fields")).toHaveText(`${walkIn.tableName} · 2 guests`);
      sequences.push(Number(await row.getAttribute("data-sequence")));
    }
    expect(sequences).toEqual([sequences[0], sequences[0] + 1, sequences[0] + 2]);
    await expect(presenter.getByTestId("pos-waiting-count")).toHaveText("3 waiting");

    await posSwitch.click();
    await expect(posSwitch).toHaveAttribute("aria-checked", "true");

    // In order: whenever the API is asked, no message is Sent while an earlier one still waits; all Sent within 10 s.
    await expectSentInOrder(walkIns.map((w) => w.bookingId));

    for (const walkIn of walkIns) {
      const row = posRows(presenter, { type: "OpenTable", bookingId: walkIn.bookingId });
      await expect(row).toHaveAttribute("data-status", "sent", { timeout: 2_000 });
      await expect(row).toHaveCount(1);
    }
    // No message appears twice.
    const ids = await posRows(presenter).evaluateAll((rows) => rows.map((r) => r.getAttribute("data-message-id")));
    expect(new Set(ids).size).toBe(ids.length);
    await expect(presenter.getByTestId("pos-waiting-count")).toHaveText("0 waiting");
  });

  test("a message expands to its full JSON, one at a time, and Copy JSON copies it (step 12, AC6.2)", async () => {
    const first = posRows(presenter, { type: "OpenTable", bookingId: walkIns[0].bookingId });
    const second = posRows(presenter, { type: "OpenTable", bookingId: walkIns[1].bookingId });
    // The summary button is the row's first button; "Copy JSON" follows it when expanded.
    const toggle = (row: typeof first) => row.getByRole("button").first();

    await toggle(first).click();
    await expect(toggle(first)).toHaveAttribute("aria-expanded", "true");
    const json = first.getByTestId("pos-json");
    await expect(json).toBeVisible();
    const message = JSON.parse((await json.textContent()) ?? "{}") as Record<string, unknown>;
    expect(message.messageId).toBe(await first.getAttribute("data-message-id"));
    expect(message.messageType).toBe("OpenTable");
    expect(message.venueId).toEqual(expect.any(String));
    expect(message.bookingId).toBe(walkIns[0].bookingId);
    expect(message.tableIds).toEqual([expect.any(String)]);
    expect(message.version).toBe(1);

    await first.getByTestId("copy-json").click();
    await expect(first.getByTestId("copy-json")).toHaveAttribute("data-state", "copied");
    await expect(first.getByTestId("copy-json")).toHaveText(/Copied/);
    const copied = await presenter.evaluate(() => navigator.clipboard.readText());
    expect((JSON.parse(copied) as { messageId: string }).messageId).toBe(message.messageId);

    // Only one row is open at a time.
    await toggle(second).click();
    await expect(toggle(second)).toHaveAttribute("aria-expanded", "true");
    await expect(toggle(first)).toHaveAttribute("aria-expanded", "false");
    await expect(json).toBeHidden();
  });

  test("Bill paid closes the guest's bill: matched in the log, table free, visit and bill on the next booking (AC6.6)", async () => {
    const name = uniqueGuestName();
    const mobile = uniqueMobile();
    const guest = await seatGuestFromWaitlist(name, mobile, 2);
    walkIns.push(guest);
    await expect(posRows(presenter, { type: "OpenTable", bookingId: guest.bookingId })).toHaveAttribute(
      "data-status",
      "sent",
      { timeout: 10_000 },
    );

    const tile = host.getByTestId(`table-${guest.tableName}`);
    await expect(tile).toHaveAttribute("data-state", "seated", { timeout: 2_000 });
    await tile.click();
    const panel = host.getByTestId("table-panel");
    await panel.getByRole("button", { name: "Bill paid" }).click();
    const dialog = host.getByTestId("bill-paid-dialog");
    const total = dialog.getByLabel("Bill total (NZ$)");
    await expect(total).toHaveValue("90.00"); // 2 × NZ$45.00
    await total.fill("123.45");
    await dialog.getByRole("button", { name: "Send bill" }).click();
    await expect(dialog).toBeHidden();

    const bill = posRows(presenter, { type: "BillClosed", bookingId: guest.bookingId });
    await expect(bill).toHaveCount(1, { timeout: 2_000 });
    await expect(bill).toHaveAttribute("data-status", "matched");
    await expect(bill).toHaveAttribute("data-direction", "inbound");
    await expect(bill.getByTestId("pos-direction")).toHaveText("← From POS");
    await expect(bill.getByTestId("pos-status")).toHaveText("Matched");
    await expect(bill.getByTestId("pos-fields")).toHaveText(`${guest.tableName} · NZ$123.45`);
    await expect(tile).not.toHaveAttribute("data-state", "seated", { timeout: 2_000 });

    // The guest's profile is updated by its own event handler just after; then their next booking shows it.
    await expect
      .poll(async () => guestHistory(guest.reference), { timeout: 5_000 })
      .toEqual({ visitCount: 1, lastBillCents: 12_345 });
    const next = await createPhoneBooking("20:00", 2, name, mobile);
    phoneBookings.push(next.reference);

    const row = host.getByTestId(`booking-${next.reference}`);
    await expect(row).toBeVisible({ timeout: 2_000 });
    await row.click();
    await expect(
      host.locator(`[data-booking-id="${next.bookingId}"]`).getByTestId("guest-history"),
    ).toHaveText("2nd visit · last bill NZ$123.45");
  });
});

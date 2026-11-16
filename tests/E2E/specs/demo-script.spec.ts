import { devices, expect, test, type BrowserContext, type BrowserContextOptions, type Page } from "@playwright/test";
import { mkdir } from "node:fs/promises";
import path from "node:path";
import { payDeposit } from "../support/payment";
import { recording } from "../support/recording";
import {
  guestContext,
  hostContext,
  loadPresenterScreen,
  phoneDevice,
  phoneMessages,
  posRows,
  presenterContext,
  presenterViewport,
  screenDevices,
  uniqueGuestName,
  uniqueMobile,
} from "../support/screens";
import {
  bookingByReference,
  expectSentInOrder,
  paymentConfig,
  quotedWait,
  resetDemo,
  setPosOnline,
} from "../support/session";
import { TimingReport } from "../support/timing";
import { ScreenWatch } from "../support/watch";

// M11b (Requirements §10 demo script rows 2–12; QA-3, QA-4, QA-6; Design §17.3): the demo as one continuous run on
// the three screens of the stage, each row seeing what the rows before it left:
// - the guest's phone (the project's phone), the host's tablet (landscape) and the presenter's laptop;
// - the business clock fixed at 18:25 after the reset (Design §17.2), the seed as in Design §17 (#10 GDB3K7 confirms,
//   #11 GDC4L8 cancels, #6 on T5 is the table seated while the POS is offline).
// One test, one step per row, so `--repeat-each=3` runs the whole script three times (QA-6), each from a reset.
// QA-6 "no errors or reloads": every screen fails on a console error, an uncaught error or a response of 500 or more,
// and the host and presenter screens on any reload or navigation after they loaded (support/watch.ts). Checked at the
// end of every row.
// QA-3 (a guest books a table for 2 in 60 s or less) and QA-4 (each host action responds within 1 s, measured from the
// tap to the screen change) are printed as a table at the end and fail only above their limits (support/timing.ts).
// DEMO_RECORD=1 records the three screens at a watchable pace into demo-recordings/ (support/recording.ts); the
// timings are then reported, not enforced.
// Clean-up: the demo is reset at the end (and the POS switched on), so the specs after this one start from the seed.

const confirmsRef = "GDB3K7"; // #10
const cancelsRef = "GDC4L8"; // #11

const recordingsDir = path.join(__dirname, "..", "demo-recordings", new Date().toISOString().replace(/[:.]/g, "-"));

const waitlistEntry = (page: Page, name: string) =>
  page.getByTestId("waitlist-entry").filter({ has: page.getByRole("heading", { name, exact: true }) });

// The QA-4 checks below run in the browser (page.waitForFunction), so each one is self-contained: it can use only the
// DOM and its argument, never a helper from this file.

test("the demo script, rows 2–12, as one continuous run (Requirements §10; QA-3, QA-4, QA-6)", async ({
  browser,
  baseURL,
}, testInfo) => {
  test.setTimeout(recording ? 20 * 60_000 : 4 * 60_000);
  const run = `run ${testInfo.repeatEachIndex + 1}`;
  const report = new TimingReport(!recording);

  await resetDemo();
  const { provider } = await paymentConfig();
  const [confirmsId, cancelsId] = await Promise.all([
    bookingByReference(confirmsRef).then((b) => b.id),
    bookingByReference(cancelsRef).then((b) => b.id),
  ]);

  if (recording) await mkdir(recordingsDir, { recursive: true });
  const video = (size: { width: number; height: number }): BrowserContextOptions =>
    recording ? { recordVideo: { dir: recordingsDir, size } } : {};
  const contexts: [string, BrowserContext][] = [
    ["guest", await guestContext(browser, baseURL, video(phoneDevice().viewport))],
    ["host", await hostContext(browser, baseURL, video(devices[screenDevices().tablet].viewport))],
    ["presenter", await presenterContext(browser, baseURL, video(presenterViewport))],
  ];
  const [guestScreen, hostScreen, presenterScreen] = contexts.map(([, context]) => context);

  const pages: [string, Page][] = [];
  try {
    const guest = await guestScreen.newPage();
    const host = await hostScreen.newPage();
    const presenter = await presenterScreen.newPage();
    pages.push(["guest", guest], ["host", host], ["presenter", presenter]);
    const watches = [
      new ScreenWatch(guest, "guest", false),
      new ScreenWatch(host, "host", true),
      new ScreenWatch(presenter, "presenter", true),
    ];

    // Before starting: the presenter screen with the phone panel, POS log and demo time; the host app.
    await loadPresenterScreen(presenter);
    await expect(presenter.getByTestId("demo-clock")).toHaveText("6:25 pm · fixed");
    await host.goto("/host/");
    await expect(host.getByTestId("floor-plan")).toBeVisible();
    await expect(host.getByTestId("tonight-list")).toBeVisible();
    await expect(host.getByTestId("waitlist")).toBeVisible();
    await expect(host.getByTestId("table-T4")).toHaveAttribute("data-state", "seated");
    await Promise.all(watches.map((w) => w.arm()));

    /** One row of the script: its own step, then the QA-6 checks on all three screens. */
    const row = (name: string, body: () => Promise<void>) =>
      test.step(name, async () => {
        await body();
        for (const watch of watches) await watch.check(name);
      });

    let booked2 = "";
    await row("Row 2 — on the phone, book a table for 2 at 19:30, switching to 中文 halfway", async () => {
      await expect(host.getByTestId("table-T1")).toHaveAttribute("data-state", "free");
      const started = Date.now();
      await guest.goto("/book/");
      const date = guest.getByLabel("Date", { exact: true });
      await expect(date).toHaveAttribute("min", /^\d{4}-\d{2}-\d{2}$/);
      await guest.getByLabel("Party size", { exact: true }).selectOption("2");
      await date.fill((await date.getAttribute("min"))!);
      await guest.getByLabel("Time", { exact: true }).selectOption("19:30:00");
      await expect(guest.getByText(/is available for 2 guests/)).toBeVisible();
      await guest.getByRole("button", { name: "Continue" }).click();
      await expect(guest.getByRole("heading", { name: "Your details" })).toBeVisible();

      // Halfway: the guest switches to Chinese; the form keeps what was chosen.
      await guest.getByRole("button", { name: "中文" }).click();
      await expect(guest.locator("html")).toHaveAttribute("lang", "zh-CN");
      await guest.getByLabel("姓名", { exact: true }).fill(uniqueGuestName());
      await guest.getByLabel("手机号码", { exact: true }).fill(uniqueMobile());
      await guest.getByRole("button", { name: "立即预订" }).click();
      await expect(guest.getByRole("heading", { name: "预订成功！" })).toBeVisible();
      report.add("QA-3 row 2: a guest books a table for 2", Date.now() - started, 60_000);

      await expect(guest.getByTestId("booking-details")).toContainText("2 位");
      booked2 = (await guest.getByTestId("booking-reference").textContent())!.trim();
    });

    await row("Row 3 — the host screen: T1 turns booked; the confirmation in the phone panel", async () => {
      await expect(host.getByTestId("table-T1")).toHaveAttribute("data-state", "booked", { timeout: 2_000 });
      await expect(host.getByTestId(`booking-${booked2}`)).toContainText("T1", { timeout: 2_000 });
      const text = phoneMessages(presenter, booked2);
      await expect(text).toBeVisible({ timeout: 2_000 });
      await expect(text).toHaveAttribute("data-purpose", "confirmation");
      await expect(text).toHaveAttribute("data-language", "zh");
    });

    let booked6 = "";
    await row("Row 4 — on the phone, book for 6 at 20:00 and pay the deposit; UpsertMember in the POS log", async () => {
      // The presenter switches the phone back to English for the payment (Design §17.3).
      await guest.getByRole("button", { name: "English" }).click();
      await guest.goto("/book/");
      const date = guest.getByLabel("Date", { exact: true });
      await expect(date).toHaveAttribute("min", /^\d{4}-\d{2}-\d{2}$/);
      await guest.getByLabel("Party size", { exact: true }).selectOption("6");
      await date.fill((await date.getAttribute("min"))!);
      await guest.getByLabel("Time", { exact: true }).selectOption("20:00:00");
      await expect(guest.getByText(/is available for 6 guests/)).toBeVisible();
      await guest.getByRole("button", { name: "Continue" }).click();
      await guest.getByLabel("Name", { exact: true }).fill(uniqueGuestName());
      await guest.getByLabel("Mobile", { exact: true }).fill(uniqueMobile());
      await guest.getByRole("button", { name: "Book now" }).click();

      await expect(guest.getByRole("heading", { name: "Pay your deposit" })).toBeVisible();
      await expect(guest.getByTestId("deposit-summary")).toHaveText("Deposit NZ$120.00 for 6 guests");
      await payDeposit(guest, provider, "NZ$120.00");
      await expect(guest.getByRole("heading", { name: "You're booked!" })).toBeVisible();
      booked6 = (await guest.getByTestId("booking-reference").textContent())!.trim();

      await expect(host.getByTestId(`booking-${booked6}`)).toContainText("T10", { timeout: 2_000 });
      const { id } = await bookingByReference(booked6);
      const upsert = posRows(presenter, { type: "UpsertMember", bookingId: id });
      await expect(upsert).toHaveCount(1, { timeout: 5_000 });
      await expect(upsert).toHaveAttribute("data-status", "sent", { timeout: 10_000 });
    });

    await row("Row 5 — choose a full time, 19:00 for 4: the 3 nearest available times are offered", async () => {
      await guest.goto("/book/");
      const date = guest.getByLabel("Date", { exact: true });
      await expect(date).toHaveAttribute("min", /^\d{4}-\d{2}-\d{2}$/);
      await guest.getByLabel("Party size", { exact: true }).selectOption("4");
      await date.fill((await date.getAttribute("min"))!);
      await guest.getByLabel("Time", { exact: true }).selectOption("19:00:00");
      await expect(guest.getByText(/7:00\s?pm is full/i)).toBeVisible();
      await expect(guest.getByRole("button", { name: /^Book \d/ })).toHaveText([
        /^Book 7:15\s?pm$/i,
        /^Book 7:30\s?pm$/i,
        /^Book 7:45\s?pm$/i,
      ]);
    });

    await row("Row 6 — on the host app, seat a walk-in for 2 in three taps; OpenTable in the POS log", async () => {
      let taps = 0;
      const tap = async (target: ReturnType<Page["getByRole"]>) => {
        await target.click();
        taps++;
      };
      await tap(host.getByRole("button", { name: "Walk-in", exact: true }));
      const dialog = host.getByTestId("walk-in-dialog");
      await tap(dialog.getByRole("button", { name: "Party of 2", exact: true }));
      const seat = dialog.getByRole("button", { name: "Seat at T2" });
      await expect(seat).toBeEnabled();
      await expect(dialog.getByTestId("choose-T2")).toBeChecked();

      const answer = host.waitForResponse((r) => /\/walk-ins$/.test(r.url()) && r.request().method() === "POST");
      await report.hostAction(
        host,
        "row 6 seat a walk-in for 2 (T2)",
        () => tap(seat),
        (t) => document.querySelector(`[data-testid="table-${t}"]`)?.getAttribute("data-state") === "seated",
        "T2",
      );
      expect(taps, "taps to seat a walk-in (AC3.1, QA-4)").toBe(3);
      expect((await answer).status()).toBe(201);
      const { bookingId } = (await (await answer).json()) as { bookingId: string };
      await expect(dialog).toBeHidden();

      const openTable = posRows(presenter, { type: "OpenTable", bookingId });
      await expect(openTable).toHaveCount(1, { timeout: 2_000 });
      await expect(openTable).toHaveAttribute("data-status", "sent", { timeout: 10_000 });
    });

    const waitingName = uniqueGuestName();
    let entryId = "";
    let quote = 0;
    await row("Row 7 — a walk-in for 4 when nothing fits: quoted wait, then on the waitlist", async () => {
      quote = await quotedWait(4);
      await host.getByRole("button", { name: "Walk-in", exact: true }).click();
      const walkIn = host.getByTestId("walk-in-dialog");
      await walkIn.getByRole("button", { name: "Party of 4", exact: true }).click();
      await expect(walkIn.getByTestId("walk-in-no-table")).toBeVisible();
      await walkIn.getByRole("button", { name: "Add to waitlist" }).click();

      const join = host.getByTestId("join-waitlist-dialog");
      await expect(join.getByLabel("Party size", { exact: true })).toHaveValue("4");
      await expect(join.getByTestId("quoted-wait")).toHaveValue(String(quote));
      await join.getByLabel("Name", { exact: true }).fill(waitingName);
      await join.getByLabel("Mobile", { exact: true }).fill(uniqueMobile());

      const answer = host.waitForResponse((r) => /\/waitlist$/.test(r.url()) && r.request().method() === "POST");
      await report.hostAction(
        host,
        "row 7 add the party of 4 to the waitlist",
        () => join.getByRole("button", { name: "Add to waitlist" }).click(),
        (name) =>
          [...document.querySelectorAll('[data-testid="waitlist-entry"] h3')].some((h) => h.textContent === name),
        waitingName,
      );
      expect((await answer).status()).toBe(201);
      entryId = ((await (await answer).json()) as { id: string }).id;
      await expect(join).toBeHidden();
      await expect(waitlistEntry(host, waitingName).getByTestId("quoted-line")).toHaveText(`Quoted ${quote} min`);
    });

    await row("Row 8 — Bill paid on T4: BillClosed from the POS, T4 free, the waiting party highlighted", async () => {
      await host.getByTestId("table-T4").click();
      await host.getByTestId("table-panel").getByRole("button", { name: "Bill paid" }).click();
      const dialog = host.getByTestId("bill-paid-dialog");
      await report.hostAction(
        host,
        "row 8 Bill paid on T4 (T4 free)",
        () => dialog.getByRole("button", { name: "Send bill" }).click(),
        (t) => {
          const state = document.querySelector(`[data-testid="table-${t}"]`)?.getAttribute("data-state");
          return !!state && state !== "seated";
        },
        "T4",
      );
      await expect(dialog).toBeHidden();

      // The suggestion follows the table freeing (waitlistChanged), as in walk-in.spec: within 2 s.
      await expect(host.getByTestId("table-T4")).toHaveAttribute("data-highlighted", "true", { timeout: 2_000 });
      await expect(waitlistEntry(host, waitingName)).toHaveAttribute("data-suggested", "true", { timeout: 2_000 });
      await expect(host.getByTestId("suggestion-banner")).toContainText("T4");

      const bill = posRows(presenter, { type: "BillClosed" }).filter({
        has: presenter.getByTestId("pos-fields").filter({ hasText: /^T4 · / }),
      });
      await expect(bill).toHaveCount(1, { timeout: 2_000 });
      await expect(bill).toHaveAttribute("data-status", "matched");
      await expect(bill.getByTestId("pos-status")).toHaveText("Matched");
    });

    await row("Row 9 — Notify: “table ready” in the phone panel; then seat them on T4 in one tap", async () => {
      const entry = waitlistEntry(host, waitingName);
      await entry.getByRole("button", { name: "Notify", exact: true }).click();
      await expect(entry.getByTestId("choose-T4")).toBeChecked();
      await report.hostAction(
        host,
        "row 9 Notify for T4",
        () => entry.getByRole("button", { name: "Notify for T4" }).click(),
        (id) => document.querySelector(`[data-entry-id="${id}"]`)?.textContent?.includes("Notified for T4") ?? false,
        entryId,
      );

      const text = presenter.locator(
        `[data-testid="phone-message"][data-subject-id="${entryId}"][data-purpose="tableReady"]`,
      );
      await expect(text).toBeVisible({ timeout: 2_000 });
      await expect(text).toContainText("Golden Dragon");
      // The quote no longer applies once they are notified (M11b).
      await expect(entry.getByTestId("quoted-line")).toHaveText(`was quoted ${quote} min`);
      await expect(entry.getByTestId("quoted-line")).toHaveAttribute("data-stale", "true");

      await report.hostAction(
        host,
        "row 9 seat the notified party at T4 (one tap)",
        () => entry.getByRole("button", { name: "Seat at T4" }).click(),
        (id) =>
          !document.querySelector(`[data-entry-id="${id}"]`) &&
          document.querySelector('[data-testid="table-T4"]')?.getAttribute("data-state") === "seated",
        entryId,
      );
      await expect(host.getByTestId("table-T4")).not.toHaveAttribute("data-highlighted", "true");
    });

    await row("Row 10 — Send reminders now; #10 confirms, #11 cancels: it leaves T9's 20:30 slot, refunded", async () => {
      const sixAt2030 = async () => {
        await guest.goto("/book/");
        const date = guest.getByLabel("Date", { exact: true });
        await expect(date).toHaveAttribute("min", /^\d{4}-\d{2}-\d{2}$/);
        await guest.getByLabel("Party size", { exact: true }).selectOption("6");
        await date.fill((await date.getAttribute("min"))!);
        await guest.getByLabel("Time", { exact: true }).selectOption("20:30:00");
      };
      // Before: no table for 6 at 20:30 (T7 and T8 until 21:00, T9 has #11, T10 has row 4's party).
      await sixAt2030();
      await expect(guest.getByText(/is full|no free tables/)).toBeVisible();

      const texts = (bookingId: string, purpose: string) =>
        presenter.locator(`[data-testid="phone-message"][data-subject-id="${bookingId}"][data-purpose="${purpose}"]`);
      const listRow = (reference: string) =>
        host.getByTestId("tonight-list").locator("li").filter({ has: host.getByTestId(`booking-${reference}`) });
      const phone = () => presenter.getByTestId("phone-browser").frameLocator("iframe");

      await presenter.getByTestId("send-reminders").click();
      await expect(presenter.getByTestId("reminders-status")).toContainText("2 reminders sent");

      // #10 confirms from the phone.
      await expect(texts(confirmsId, "reminder")).toHaveCount(1, { timeout: 2_000 });
      await texts(confirmsId, "reminder").locator('[data-action="confirm"]').click();
      await phone().getByTestId("manage-confirm").click();
      await expect(phone().getByTestId("manage-result")).toHaveText("Thanks, you're confirmed.");
      await expect(listRow(confirmsRef).getByTestId("booking-status")).toHaveText("Confirmed", { timeout: 2_000 });
      await presenter.getByRole("button", { name: "Messages" }).click();
      await expect(presenter.getByTestId("phone-browser")).toHaveCount(0);

      // #11 cancels in Chinese, in time: it leaves T9's 20:30 slot (T9 stays seated by #2) and is refunded.
      await host.getByTestId("table-T9").click();
      const t9 = host.getByTestId("table-panel");
      await expect(t9.locator(`[data-booking-id="${cancelsId}"]`)).toHaveCount(1);
      await expect(texts(cancelsId, "reminder")).toHaveCount(1, { timeout: 2_000 });
      await texts(cancelsId, "reminder").locator('[data-action="cancel"]').click();
      await expect(phone().locator("html")).toHaveAttribute("lang", "zh-CN");
      await phone().getByTestId("manage-cancel").click();
      await expect(phone().getByTestId("manage-consequence")).toHaveAttribute("data-consequence", "refundFull");
      await phone().getByTestId("manage-cancel-confirm").click();
      await expect(phone().getByTestId("manage-result")).toContainText("您的预订已取消");

      await expect(t9.locator(`[data-booking-id="${cancelsId}"]`)).toHaveCount(0, { timeout: 2_000 });
      await expect(host.getByTestId("table-T9")).toHaveAttribute("data-state", "seated");
      await expect(listRow(cancelsRef).getByTestId("booking-status")).toHaveText("Cancelled", { timeout: 2_000 });
      await expect(listRow(cancelsRef).getByTestId("deposit-line")).toHaveAttribute("data-deposit", "refunded", {
        timeout: 15_000,
      });
      await expect(texts(cancelsId, "refund")).toHaveCount(1, { timeout: 10_000 });
      await presenter.getByRole("button", { name: "Messages" }).click();

      // After: 6 at 20:30 can be booked again on the guest page (nothing is booked here).
      await sixAt2030();
      await expect(guest.getByText(/is available for 6 guests/)).toBeVisible();
    });

    let seatedId = "";
    await row("Row 11 — mock POS offline, seat a table, back online: sent once each, in order", async () => {
      const posSwitch = presenter.getByRole("switch", { name: "Mock POS" });
      await expect(posSwitch).toHaveAttribute("aria-checked", "true");
      await posSwitch.click();
      await expect(posSwitch).toHaveAttribute("aria-checked", "false");

      // #6 (18:45, party of 4) is next on T5: seated early, while the POS is offline.
      await host.getByTestId("table-T5").click();
      const panel = host.getByTestId("table-panel");
      seatedId = (await panel.locator("[data-booking-id]").first().getAttribute("data-booking-id"))!;
      expect(seatedId).toBeTruthy();
      await report.hostAction(
        host,
        "row 11 seat the 18:45 booking on T5",
        () => panel.getByRole("button", { name: "Seat", exact: true }).click(),
        (t) => document.querySelector(`[data-testid="table-${t}"]`)?.getAttribute("data-state") === "seated",
        "T5",
      );

      const openTable = posRows(presenter, { type: "OpenTable", bookingId: seatedId });
      await expect(openTable).toHaveCount(1, { timeout: 2_000 });
      await expect(openTable).toHaveAttribute("data-status", "waiting");
      await expect(presenter.getByTestId("pos-waiting-count")).toHaveText("1 waiting");

      await posSwitch.click();
      await expect(posSwitch).toHaveAttribute("aria-checked", "true");
      await expectSentInOrder([seatedId]);
      await expect(openTable).toHaveAttribute("data-status", "sent", { timeout: 2_000 });
      await expect(openTable).toHaveCount(1);
      const ids = await posRows(presenter).evaluateAll((rows) => rows.map((r) => r.getAttribute("data-message-id")));
      expect(new Set(ids).size, "no POS message appears twice").toBe(ids.length);
      await expect(presenter.getByTestId("pos-waiting-count")).toHaveText("0 waiting");
    });

    await row("Row 12 — open one POS message and show its JSON", async () => {
      const message = posRows(presenter, { type: "OpenTable", bookingId: seatedId });
      const toggle = message.getByRole("button").first();
      await toggle.click();
      await expect(toggle).toHaveAttribute("aria-expanded", "true");
      const json = JSON.parse((await message.getByTestId("pos-json").textContent()) ?? "{}") as Record<string, unknown>;
      // The envelope (docs/api/pos-messages.md) and the OpenTable fields.
      expect(json).toMatchObject({
        version: 1,
        messageId: await message.getAttribute("data-message-id"),
        messageType: "OpenTable",
        venueId: expect.any(String),
        bookingId: seatedId,
        tableIds: [expect.any(String)],
        occurredAt: expect.stringMatching(/Z$/),
      });
    });

    report.finish(run);
  } finally {
    for (const [, context] of contexts) await context.close();
    // The videos are complete once their contexts are closed: guest-run1.webm, host-run1.webm, presenter-run1.webm.
    for (const [screen, page] of pages) {
      const video = page.video();
      if (!video) continue;
      await video.saveAs(path.join(recordingsDir, `${screen}-run${testInfo.repeatEachIndex + 1}.webm`));
      await video.delete();
    }
    await setPosOnline(true);
    await resetDemo();
  }
});

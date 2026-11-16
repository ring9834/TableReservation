import { expect, test, type Page } from "@playwright/test";
import { expectNoHorizontalScroll } from "../support/layout";
import { payDeposit } from "../support/payment";
import {
  hostContext,
  openPresenterScreen,
  presenterContext,
  uniqueGuestName,
  uniqueMobile,
} from "../support/screens";
import { bookingByReference, cancelBookings, paymentConfig } from "../support/session";

// M8b (AC1.5, AC5.5, Design §18.8): a party of 6 pays its deposit on the guest page, the host sees it paid, cancels it
// and watches the deposit go from "Refund pending" to "Refunded"; the phone panel shows the Confirmation, Cancellation
// and Refund texts. The booking is for tonight (the business clock stands at 18:25 in Development), so it is on the
// host's list and floor plan.
// - Fake provider (CI, and locally unless Stripe test mode is on): "Simulate payment (test mode)" sends a signed
//   payment_intent.succeeded through the API's webhook path; the Fake's refund comes back as a signed charge.refunded.
// - Stripe test mode (Payments:Provider=Stripe locally): the card 4242 4242 4242 4242 in the PaymentElement's frame.
//   The webhook needs `stripe listen` running; without one, the booking never becomes Booked and the test is skipped
//   with a message saying so.
// Clean-up: the booking is registered as soon as the API creates it, and afterAll cancels it if it is still active.

const created: string[] = [];

test.afterAll(async () => {
  await cancelBookings(created);
});


const referencePattern = /^[A-HJ-NP-Z2-9]{6}$/;
const clockTime = /\d{1,2}:\d{2}\s?(am|pm)/i;

/**
 * Party of 6 tonight at the first time from 20:00 that has a table (an earlier one if none later), up to the pay step.
 * The reference comes from the create response and is registered for the clean-up at once, before the payment step,
 * so a failure at any later point still cancels the booking (an unpaid hold keeps tonight's 6-seat table).
 */
const bookSixTonight = async (page: Page) => {
  await page.goto("/book/");
  await page.getByLabel("Party size", { exact: true }).selectOption("6");
  const time = page.getByLabel("Time", { exact: true });
  await expect(time.locator("option").nth(1)).toBeAttached();
  const times = await time
    .locator("option")
    .evaluateAll((options) => options.map((o) => (o as HTMLOptionElement).value).filter(Boolean));
  const order = [...times.filter((t) => t >= "20:00:00"), ...times.filter((t) => t < "20:00:00").reverse()];

  const available = page.getByText(/is available for 6 guests/);
  const notFree = page.getByText(/is full|no free tables/);
  let found = false;
  for (const value of order) {
    await time.selectOption(value);
    await expect(available.or(notFree)).toBeVisible();
    if (await available.isVisible()) {
      found = true;
      break;
    }
  }
  expect(
    found,
    "No table for 6 tonight: earlier bookings hold the 6-seat tables. Reset the demo data (docker compose down -v) or cancel those bookings, then rerun.",
  ).toBe(true);
  await page.getByRole("button", { name: "Continue" }).click();

  await page.getByLabel("Name", { exact: true }).fill(uniqueGuestName());
  await page.getByLabel("Mobile", { exact: true }).fill(uniqueMobile());
  const createResponse = page.waitForResponse(
    (response) =>
      response.request().method() === "POST" &&
      /\/api\/v1\/venues\/[^/]+\/bookings$/.test(new URL(response.url()).pathname),
  );
  await page.getByRole("button", { name: "Book now" }).click();
  const response = await createResponse;
  expect(response.status(), "create booking answered").toBe(201);
  const { reference } = (await response.json()) as { reference: string };
  expect(reference).toMatch(referencePattern);
  created.push(reference);

  await expect(page.getByRole("heading", { name: "Pay your deposit" })).toBeVisible();
  return reference;
};

test("a party of 6 pays its deposit, the host cancels and the deposit is refunded (AC1.5, AC5.5)", async ({
  page,
  browser,
  baseURL,
}) => {
  test.setTimeout(90_000);
  const { provider } = await paymentConfig();
  const presenterScreen = await presenterContext(browser, baseURL);
  const presenter = await openPresenterScreen(presenterScreen);

  const reference = await bookSixTonight(page);
  const payStep = page.getByTestId("pay-step");
  await expect(page.getByTestId("deposit-summary")).toHaveText("Deposit NZ$120.00 for 6 guests");
  // The hold is a countdown from the server, never a clock time (the demo's story time differs from real time).
  await expect(page.getByTestId("hold-countdown-time")).toHaveText(/^\d{1,2}:\d{2}$/);
  await expect(page.getByTestId("hold-countdown")).toContainText("We'll hold your table for");
  await expect(payStep).not.toContainText(clockTime);

  await expectNoHorizontalScroll(page, "pay step");

  await payDeposit(page, provider, "NZ$120.00");

  await expect(page.getByRole("heading", { name: "You're booked!" })).toBeVisible();
  await expect(page.getByTestId("booking-reference")).toHaveText(reference);
  await expectNoHorizontalScroll(page, "confirmation after the deposit");
  const booking = await bookingByReference(reference);

  // The phone panel shows the Confirmation once the deposit is paid.
  const textsAbout = (purpose: string) =>
    presenter.locator(
      `[data-testid="phone-message"][data-subject-id="${booking.id}"][data-purpose="${purpose}"]`,
    );
  await expect(textsAbout("confirmation")).toHaveCount(1, { timeout: 5_000 });

  // The host sees it booked and paid, and cancels it.
  const hostScreen = await hostContext(browser, baseURL);
  const host = await hostScreen.newPage();
  await host.goto("/host/");
  const row = host.getByTestId("tonight-list").locator("li").filter({ has: host.getByTestId(`booking-${reference}`) });
  await expect(row.getByTestId("booking-status")).toHaveText("Booked");
  await expect(row.getByTestId("deposit-line")).toHaveText("Deposit NZ$120.00 · Paid");

  // Cancel lives in the booking's details, not in the list row: tapping the row opens the side panel for it.
  await expect(row.getByTestId(`cancel-${reference}`)).toHaveCount(0);
  await row.getByTestId(`booking-${reference}`).click();
  const panel = host.getByTestId("table-panel").or(host.getByTestId("booking-panel"));
  await panel.getByTestId(`cancel-${reference}`).click();
  const dialog = host.getByTestId("cancel-dialog");
  await expect(dialog).toContainText(/Cancel 6 guests at \d{1,2}:\d{2}\s?(am|pm)\?/i);
  await expect(dialog).toContainText("The deposit of NZ$120.00 will be refunded in full.");
  await dialog.getByTestId("confirm-cancel").click();
  await expect(dialog).toBeHidden();

  // Refund pending (until Stripe's charge.refunded), then Refunded, live through floorPlanChanged, no reload.
  const depositLine = row.getByTestId("deposit-line");
  await expect(row.getByTestId("booking-status")).toHaveText("Cancelled");
  await expect(depositLine).toHaveAttribute("data-deposit", /refundPending|refunded/);
  await expect(depositLine).toHaveText("Deposit NZ$120.00 · Refunded", { timeout: 15_000 });

  await expect(textsAbout("cancellation")).toHaveCount(1, { timeout: 5_000 });
  await expect(textsAbout("refund")).toHaveCount(1, { timeout: 10_000 });
  await expect(textsAbout("refund")).toContainText("NZ$120.00");

  await hostScreen.close();
  await presenterScreen.close();
});

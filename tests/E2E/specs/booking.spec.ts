import { expect, test, type Browser, type Page } from "@playwright/test";
import { openPresenterScreen, phoneMessages, presenterContext } from "../support/screens";
import { cancelBookings } from "../support/session";

// Requirements step 1 (AC1.1–AC1.4, AC1.6, AC1.8) and BR-04 through the real page, API and database.
// Bookings are made for tomorrow so the result does not depend on the time of day. The date picker's `min` is the
// venue's "today" (from getVenue, on the API's business clock), so the browser's own clock and zone do not matter.
// The bookings a run makes are cancelled afterwards with the shared host session (global-setup.ts), like
// host.spec.ts, so reruns against one database never fill tomorrow's slots.
// With the presenter screen open, each confirmation text reaches the phone panel within 2 s (AC1.6, Design §18.7),
// in the language the guest booked in. Bodies are not hard-coded: the reference, the venue name and a few template
// words are enough.

/** References this file booked, and the venue-local date they are on. */
const created: string[] = [];
let bookedDay: string | undefined;

test.afterAll(async () => {
  await cancelBookings(created, bookedDay);
});

/** Reads the confirmed booking's reference and remembers it for the clean-up. */
const rememberBooking = async (page: Page, day: string) => {
  const reference = page.getByTestId("booking-reference");
  await expect(reference).toHaveText(referencePattern);
  const text = (await reference.textContent())!.trim();
  created.push(text);
  bookedDay = day;
  return text;
};

/** The presenter's screen with the phone panel, opened before the guest books. */
const openPhonePanel = async (browser: Browser, baseURL: string | undefined) => {
  const context = await presenterContext(browser, baseURL);
  return { context, presenter: await openPresenterScreen(context) };
};

const referencePattern = /^[A-HJ-NP-Z2-9]{6}$/; // BookingReference: 6 characters, no 0/O/1/I

const tomorrowFrom = (today: string) => {
  const [year, month, day] = today.split("-").map(Number);
  return new Date(Date.UTC(year, month - 1, day + 1))
    .toISOString()
    .slice(0, 10);
};

/** Opens /book and returns tomorrow's date as the venue sees it. */
const openBookingPage = async (page: Page, dateLabel: string) => {
  await page.goto("/book/");
  const date = page.getByLabel(dateLabel, { exact: true });
  await expect(date).toHaveAttribute("min", /^\d{4}-\d{2}-\d{2}$/);
  return tomorrowFrom((await date.getAttribute("min"))!);
};

test("a guest books a table for 2 tomorrow at 19:30 in English", async ({
  page,
  browser,
  baseURL,
}) => {
  const { context, presenter } = await openPhonePanel(browser, baseURL);
  const tomorrow = await openBookingPage(page, "Date");
  await expect(
    page.getByRole("heading", { level: 1, name: "Book a table" }),
  ).toBeVisible();

  await page.getByLabel("Party size", { exact: true }).selectOption("2");
  await page.getByLabel("Date", { exact: true }).fill(tomorrow);
  await page.getByLabel("Time", { exact: true }).selectOption("19:30:00");
  await expect(page.getByText(/is available for 2 guests/)).toBeVisible();
  await page.getByRole("button", { name: "Continue" }).click();

  await expect(
    page.getByRole("heading", { name: "Your details" }),
  ).toBeFocused();
  await page.getByLabel("Name", { exact: true }).fill("Playwright Guest");
  await page.getByLabel("Mobile", { exact: true }).fill("021 011 0198");
  await page.getByRole("button", { name: "Book now" }).click();

  await expect(
    page.getByRole("heading", { name: "You're booked!" }),
  ).toBeVisible();
  const reference = await rememberBooking(page, tomorrow);
  await expect(page.getByTestId("booking-details")).toContainText("2 guests");

  // AC1.6: the confirmation text is on the phone within 2 s, in English, then delivered.
  const text = phoneMessages(presenter, reference);
  await expect(text).toBeVisible({ timeout: 2_000 });
  await expect(text).toHaveAttribute("data-purpose", "confirmation");
  await expect(text).toContainText("Golden Dragon");
  await expect(text).toContainText("booked");
  await expect(text.getByTestId("message-status")).toHaveText(/Delivered/, { timeout: 5_000 });
  await context.close();
});

test("a guest books in Chinese and the confirmation is in Chinese", async ({
  page,
  browser,
  baseURL,
}) => {
  const { context, presenter } = await openPhonePanel(browser, baseURL);
  const tomorrow = await openBookingPage(page, "Date");

  await page.getByRole("button", { name: "中文" }).click();
  await expect(page.locator("html")).toHaveAttribute("lang", "zh-CN");
  await expect(
    page.getByRole("heading", { level: 1, name: "预订餐桌" }),
  ).toBeVisible();

  await page.getByLabel("用餐人数", { exact: true }).selectOption("2");
  await page.getByLabel("日期", { exact: true }).fill(tomorrow);
  await page.getByLabel("时间", { exact: true }).selectOption("19:45:00");
  await page.getByRole("button", { name: "继续" }).click();

  await page.getByLabel("姓名", { exact: true }).fill("王小明");
  await page.getByLabel("手机号码", { exact: true }).fill("021 011 0197");
  await page.getByRole("button", { name: "立即预订" }).click();

  await expect(page.getByRole("heading", { name: "预订成功！" })).toBeVisible();
  await expect(page.getByText("预订编号")).toBeVisible();
  await expect(page.getByTestId("booking-details")).toContainText("2 位");
  const reference = await rememberBooking(page, tomorrow);
  await expect(page.getByText("You're booked!")).toHaveCount(0);

  // The text goes out in the language the guest booked in, whatever the presenter screen shows.
  const text = phoneMessages(presenter, reference);
  await expect(text).toBeVisible({ timeout: 2_000 });
  await expect(text).toHaveAttribute("data-purpose", "confirmation");
  await expect(text).toContainText("Golden Dragon");
  await expect(text).toContainText("预订号");
  await context.close();
});

test("a party of 9 is asked to call (BR-04)", async ({ page }) => {
  await openBookingPage(page, "Date");

  await page
    .getByLabel("Party size", { exact: true })
    .selectOption({ label: "9 or more" });

  await expect(
    page.getByText("please call us", { exact: false }),
  ).toBeVisible();
  await expect(page.getByRole("link", { name: /^Call \+64/ })).toHaveAttribute(
    "href",
    "tel:+6490000000",
  );
  await expect(page.getByLabel("Time", { exact: true })).toBeDisabled();
});

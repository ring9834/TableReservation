import { expect, test, type Page } from "@playwright/test";
import { decodeQr } from "../support/qr";
import { openPresenterScreen, presenterContext } from "../support/screens";

// M12b (Design §17.5): the presenter screen's "Scan to book" card. The audience scans it with their own phone and books
// on the guest page, so the code must decode to this site's /book/ (whatever address the screen was opened on) and
// never to a staff screen. The card must not push the POS panel (rows 11–12 of the demo script) below the fold on a
// laptop. Nothing is booked and nothing is changed.

const expectedUrl = (baseURL: string | undefined) => new URL("/book/", baseURL).toString();

/** The POS panel's heading and the top of its log are inside the window, without scrolling. */
const expectPosPanelAboveTheFold = async (page: Page, where: string) => {
  const viewport = page.viewportSize()!;
  const heading = await page.locator("#pos-heading").boundingBox();
  expect(heading, `${where}: the POS heading is laid out`).not.toBeNull();
  expect(heading!.y, `${where}: the POS heading starts inside the window`).toBeGreaterThanOrEqual(0);
  expect(heading!.y + heading!.height, `${where}: the POS heading ends inside the window`).toBeLessThanOrEqual(
    viewport.height,
  );
  // The first messages: at least 120 px of the log is visible under the switch and the filters.
  const log = await page.getByTestId("pos-panel").getByRole("log").boundingBox();
  expect(log, `${where}: the POS log is laid out`).not.toBeNull();
  expect(log!.y + 120, `${where}: the top of the POS log is inside the window`).toBeLessThanOrEqual(viewport.height);
};

test.describe("presenter: Scan to book (M12b)", () => {
  test("shows a QR code that decodes to this site's booking page", async ({ browser, baseURL }) => {
    // A 2× screen, as on most laptops: the small code's modules are a few CSS pixels wide.
    const context = await presenterContext(browser, baseURL, { deviceScaleFactor: 2 });
    try {
      const page = await openPresenterScreen(context);
      const card = page.getByTestId("scan-to-book");
      await expect(card).toBeVisible();
      await expect(card.getByRole("heading", { name: "Scan to book" })).toBeVisible();
      await expect(page.getByTestId("scan-url")).toHaveText(expectedUrl(baseURL));

      const decoded = await decodeQr(page.getByTestId("scan-qr"));
      expect(decoded, "the card's code").toBe(expectedUrl(baseURL));
      // The path, not the whole URL ("localhost" contains "host"): only the guest page, never a staff screen.
      expect(new URL(decoded!).pathname, "the card's code is the guest page").toBe("/book/");
    } finally {
      await context.close();
    }
  });

  test("Enlarge opens the code full size; Escape, Close and a tap outside close it", async ({ browser, baseURL }) => {
    const context = await presenterContext(browser, baseURL);
    try {
      const page = await openPresenterScreen(context);
      const enlarge = page.getByRole("button", { name: "Enlarge" });
      const overlay = page.getByTestId("scan-overlay");

      await enlarge.click();
      await expect(overlay).toBeVisible();
      const decodedLarge = await decodeQr(page.getByTestId("scan-qr-large"));
      expect(decodedLarge, "the large code").toBe(expectedUrl(baseURL));
      expect(new URL(decodedLarge!).pathname, "the large code is the guest page").toBe("/book/");
      await page.keyboard.press("Escape");
      await expect(overlay).toHaveCount(0);
      await expect(enlarge, "focus goes back to Enlarge").toBeFocused();

      await enlarge.click();
      await expect(overlay).toBeVisible();
      await overlay.getByRole("button", { name: "Close" }).click();
      await expect(overlay).toHaveCount(0);

      await enlarge.click();
      await expect(overlay).toBeVisible();
      await page.getByTestId("scan-qr-large").click(); // a tap on the code keeps it open
      await expect(overlay).toBeVisible();
      await page.mouse.click(5, 5); // the backdrop, outside the card
      await expect(overlay).toHaveCount(0);

      // 中文: the same buttons, in Chinese.
      await page.getByRole("button", { name: "中文", exact: true }).click();
      await expect(page.locator("html")).toHaveAttribute("lang", "zh-CN");
      await expect(page.getByTestId("scan-to-book").getByRole("heading", { name: "扫码预订" })).toBeVisible();
      await page.getByRole("button", { name: "放大" }).click();
      await expect(overlay).toBeVisible();
      await overlay.getByRole("button", { name: "关闭" }).click();
      await expect(overlay).toHaveCount(0);
      await page.getByRole("button", { name: "English", exact: true }).click();
    } finally {
      await context.close();
    }
  });

  for (const viewport of [
    { width: 1366, height: 768 },
    { width: 1440, height: 900 },
  ]) {
    test(`keeps the POS panel above the fold at ${viewport.width}×${viewport.height}`, async ({ browser, baseURL }) => {
      const context = await presenterContext(browser, baseURL, { viewport });
      try {
        const page = await openPresenterScreen(context);
        await expect(page.getByTestId("scan-to-book")).toBeVisible();
        await expectPosPanelAboveTheFold(page, `${viewport.width}×${viewport.height}`);
      } finally {
        await context.close();
      }
    });
  }
});

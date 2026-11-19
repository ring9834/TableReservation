import { expect, test } from "@playwright/test";
import { hostContext } from "../support/screens";

// M12b (Design §17.5): the host screen's "Full screen" button in a tablet browser (Chromium / Edge; not in the WebKit
// projects, whose desktop WebKit doesn't behave like iPad Safari here). Hidden on iPhone Safari and in the installed
// app: covered by fullscreen.test.mjs, since neither can be emulated here. Nothing is booked.

test("the host screen offers Full screen, in both languages, and follows the browser leaving it", async ({
  browser,
  baseURL,
}) => {
  const context = await hostContext(browser, baseURL);
  try {
    const page = await context.newPage();
    await page.goto("/host/");
    await expect(page.getByTestId("floor-plan")).toBeVisible();
    const button = page.getByTestId("fullscreen-button");
    await expect(button).toHaveText("Full screen");
    await expect(button).toHaveAttribute("aria-pressed", "false");

    await button.click();
    await expect.poll(() => page.evaluate(() => !!document.fullscreenElement)).toBe(true);
    await expect(button).toHaveText("Exit full screen");
    await expect(button).toHaveAttribute("aria-pressed", "true");

    // Leaving without the button (Escape, the browser's own control) turns the label back.
    await page.evaluate(() => document.exitFullscreen());
    await expect(button).toHaveText("Full screen");

    await button.click();
    await expect(button).toHaveText("Exit full screen");
    await button.click();
    await expect.poll(() => page.evaluate(() => !!document.fullscreenElement)).toBe(false);
    await expect(button).toHaveText("Full screen");

    await page.getByRole("button", { name: "中文", exact: true }).click();
    await expect(page.locator("html")).toHaveAttribute("lang", "zh-CN");
    await expect(button).toHaveText("全屏");
    await page.getByRole("button", { name: "English", exact: true }).click();
  } finally {
    await context.close();
  }
});

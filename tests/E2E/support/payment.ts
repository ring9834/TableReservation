import { expect, test, type FrameLocator, type Page } from "@playwright/test";

/**
 * The Stripe frame inside our PaymentElement container that holds the card fields, chosen by what it contains, never
 * by its order, title or file name. Stripe renders several frames there that share the title "Secure payment input
 * frame" (the form and an accessory frame, plus a loader while it starts), and its file names change between
 * Stripe.js versions (the form is `elements-inner-easel-….html` today, not `elements-inner-payment-…`). When Stripe
 * lists the payment methods (accordion, or Card not selected), the Card option is tapped first.
 */
export const stripeCardFrame = async (page: Page): Promise<FrameLocator> => {
  const frames = page.getByTestId("payment-element").locator("iframe");
  let found: FrameLocator | undefined;
  await expect
    .poll(
      async () => {
        for (let i = 0; i < (await frames.count()); i++) {
          const frame = frames.nth(i).contentFrame();
          if ((await frame.getByLabel("Card number").count()) > 0) {
            found = frame;
            return true;
          }
          const cardOption = frame
            .getByRole("tab", { name: /^Card$/ })
            .or(frame.getByRole("button", { name: /^Card$/ }));
          if ((await cardOption.count()) > 0) {
            await cardOption.first().click();
          }
        }
        return false;
      },
      { message: "a Stripe frame in payment-element with a 'Card number' field", timeout: 30_000 },
    )
    .toBe(true);
  return found!;
};

/**
 * Pays the deposit on the guest's pay step (English) and waits for "Deposit paid NZ$…" (Design §18.8):
 * - Fake provider (CI, and locally unless Stripe test mode is on): "Simulate payment (test mode)", tapped twice; the
 *   button turns into "Paying…" and is disabled, so only one payment is sent (payment.test.mjs);
 * - Stripe test mode: the card 4242 4242 4242 4242 in the PaymentElement's frame. The webhook needs `stripe listen`;
 *   without one the booking never becomes Booked and the test is skipped with a message saying so.
 */
export const payDeposit = async (page: Page, provider: "fake" | "stripe", amount: string) => {
  const paid = page.getByTestId("deposit-paid");
  if (provider === "fake") {
    await expect(page.getByTestId("test-mode-note")).toContainText("no real card");
    await page.getByTestId("simulate-payment").dblclick();
    await expect(paid).toHaveText(`Deposit paid ${amount}`, { timeout: 5_000 });
    return;
  }
  const card = await stripeCardFrame(page);
  await card.getByLabel("Card number").fill("4242 4242 4242 4242");
  await card.getByLabel("Expiration date").fill("12 / 34");
  await card.getByLabel("Security code").fill("123");
  await page.getByTestId("pay-button").click();
  try {
    await expect(paid).toBeVisible({ timeout: 15_000 });
  } catch {
    test.skip(true, "No Stripe webhook arrived within 15 s: is `stripe listen` running (Design §8.4)?");
  }
  await expect(paid).toHaveText(`Deposit paid ${amount}`);
};

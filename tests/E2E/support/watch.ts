import { expect, type Page } from "@playwright/test";

/**
 * QA-6 "no errors or reloads" (M11b, Design §17.3) for one screen of the demo script. Collects, from the moment it is
 * attached: console errors, uncaught page errors and responses with status 500 or more (the page's own requests and
 * its frames'). With `noReload` (host, presenter), `arm()` marks the loaded page; any later main-frame navigation, a
 * missing mark (the page was reloaded) or a changed URL counts as a reload. `check()` fails with everything collected,
 * so each row of the script names its own problems.
 */
export class ScreenWatch {
  private readonly problems: string[] = [];
  private armedUrl: string | null = null;

  constructor(
    private readonly page: Page,
    private readonly screen: string,
    private readonly noReload: boolean,
    /** Console errors that are known and harmless; empty unless a run shows one, with the reason next to it. */
    private readonly allowedConsoleErrors: RegExp[] = [],
  ) {
    page.on("console", (message) => {
      if (message.type() !== "error") return;
      const text = message.text();
      if (this.allowedConsoleErrors.some((allowed) => allowed.test(text))) return;
      this.problems.push(`console error: ${text.slice(0, 300)}`);
    });
    page.on("pageerror", (error) => this.problems.push(`uncaught error: ${error.message.slice(0, 300)}`));
    page.on("response", (response) => {
      if (response.status() >= 500) {
        const url = new URL(response.url());
        this.problems.push(`${response.status()} from ${response.request().method()} ${url.pathname}`);
      }
    });
    page.on("framenavigated", (frame) => {
      if (this.noReload && this.armedUrl !== null && frame === page.mainFrame()) {
        this.problems.push(`navigated to ${frame.url()}`);
      }
    });
  }

  /** After the screen's first load: from now on a navigation or a reload is a failure. */
  async arm() {
    if (!this.noReload) return;
    await this.page.evaluate(() => {
      (window as unknown as { __demoScript?: boolean }).__demoScript = true;
    });
    this.armedUrl = this.page.url();
  }

  /** Fails if anything went wrong on this screen so far. */
  async check(step: string) {
    if (this.noReload && this.armedUrl !== null) {
      const marked = await this.page.evaluate(
        () => (window as unknown as { __demoScript?: boolean }).__demoScript === true,
      );
      if (!marked) this.problems.push("the page was reloaded (the start-up mark is gone)");
      if (this.page.url() !== this.armedUrl) this.problems.push(`the URL changed to ${this.page.url()}`);
    }
    expect(this.problems, `${this.screen} screen, ${step}: errors or reloads (QA-6)`).toEqual([]);
  }
}

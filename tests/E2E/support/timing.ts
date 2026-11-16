import { expect, test, type Page } from "@playwright/test";

/** One measured action of the demo script (QA-3, QA-4). */
export type Timing = { label: string; ms: number; limitMs: number };

/**
 * Measures the time from a tap to the screen change it causes (QA-4, M11b, Design §17.3): notes the time, runs `tap`,
 * then waits in the page until `changed(arg)` is true, polling every 16 ms (about a frame), so the figure isn't padded
 * by `expect`'s retry back-off. Includes the tap's own dispatch, so it errs on the slow side.
 */
export const measure = async (
  page: Page,
  tap: () => Promise<unknown>,
  changed: (arg: string) => boolean,
  arg: string,
  timeout = 10_000,
) => {
  const start = Date.now();
  await tap();
  await page.waitForFunction(changed, arg, { polling: 16, timeout });
  return Date.now() - start;
};

/**
 * The timings of one run of the demo script: printed as a table at the end (the list reporter shows it), added to the
 * test's annotations (the HTML report), then checked against their limits unless `enforce` is false (the slowMo
 * recording).
 */
export class TimingReport {
  private readonly timings: Timing[] = [];

  constructor(private readonly enforce: boolean) {}

  add(label: string, ms: number, limitMs: number) {
    this.timings.push({ label, ms, limitMs });
  }

  /** Times a host action (QA-4: within 1 s) and records it. */
  async hostAction(
    page: Page,
    label: string,
    tap: () => Promise<unknown>,
    changed: (arg: string) => boolean,
    arg: string,
  ) {
    this.add(`QA-4 ${label}`, await measure(page, tap, changed, arg), 1_000);
  }

  /** Prints and annotates the report, then fails on any timing over its limit (when enforced). */
  finish(run: string) {
    const hostActions = this.timings.filter((t) => t.label.startsWith("QA-4"));
    const slowest = hostActions.reduce<Timing | undefined>((a, b) => (a && a.ms >= b.ms ? a : b), undefined);
    const lines = this.timings.map((t) => {
      const value = t.ms >= 10_000 ? `${(t.ms / 1000).toFixed(1)} s` : `${t.ms} ms`;
      const mark = t === slowest ? "  ← slowest host action" : "";
      const over = t.ms > t.limitMs ? "  OVER LIMIT" : "";
      return `  ${t.label.padEnd(44)} ${value.padStart(9)}  (limit ${t.limitMs / 1000} s)${mark}${over}`;
    });
    const summary = [`Demo script timings, ${run}${this.enforce ? "" : " (recording: not enforced)"}`, ...lines].join(
      "\n",
    );
    console.log(summary);
    test.info().annotations.push({ type: "timings", description: summary });
    if (slowest) {
      test.info().annotations.push({ type: "QA-4 slowest", description: `${slowest.label}: ${slowest.ms} ms` });
    }
    if (this.enforce) {
      const over = this.timings.filter((t) => t.ms > t.limitMs).map((t) => `${t.label} ${t.ms} ms > ${t.limitMs} ms`);
      expect(over, "timings over their limit (QA-3, QA-4)").toEqual([]);
    }
  }
}

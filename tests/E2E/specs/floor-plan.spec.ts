import { devices, expect, test, type Page } from "@playwright/test";
import { hostSessionPath } from "../support/session";

// The text on every table stays inside the table's outline (Design §18.6), in English and Chinese, on a tablet and a
// phone. The floor plan the API returns is rewritten in the browser so every table has a party with a long name
// ("Alexandra Johnson" / "欧阳娜娜"): seated on half of them, arriving soon on the rest. Nothing is booked. It also
// checks that the zoom controls cover no table.

type Booking = Record<string, unknown>;
type Table = { name: string; seats: number; state: string; current: Booking | null; next: Booking | null };

const longNames = ["Alexandra Johnson", "欧阳娜娜"];

const crowdedPlan = (plan: { tables: Table[] }) => ({
  ...plan,
  tables: plan.tables.map((table, index) => {
    const booking: Booking = {
      bookingId: `00000000-0000-7000-8000-${String(index).padStart(12, "0")}`,
      reference: `GD-TEST${index}`,
      status: index % 2 === 0 ? "seated" : "confirmed",
      partySize: table.seats,
      startsAt: "2026-11-13T05:25:00Z",
      endsAt: "2026-11-13T07:25:00Z",
      guestName: longNames[index % longNames.length],
      hasAllergy: index % 3 === 0,
      version: 1,
    };
    return index % 2 === 0
      ? { ...table, state: "seated", current: booking, next: null }
      : { ...table, state: "arrivingSoon", current: null, next: booking };
  }),
});

/** Every tile line's box that is not inside its table's outline (inner edge of the stroke). */
const linesOutside = (page: Page) =>
  page.getByTestId("floor-plan").evaluate((svg) => {
    const problems: string[] = [];
    for (const tile of svg.querySelectorAll<SVGGElement>('[data-testid^="table-"]')) {
      const outline = tile.querySelector<SVGGraphicsElement>("[data-outline]")!;
      const o = outline.getBBox();
      const stroke = 1.25; // half the normal outline stroke
      const round = outline.tagName === "ellipse";
      for (const line of tile.querySelectorAll<SVGGraphicsElement>("[data-tile-line]")) {
        // The glyphs' box: getBBox of text is the font's full line box, which is taller than the glyphs. Use the
        // horizontal extent as is and the vertical extent of the em box (0.5 em either side of the middle).
        const b = line.getBBox();
        const text = line.tagName === "text" ? line : line.querySelector("text")!;
        const size = parseFloat(text.getAttribute("font-size")!);
        const middle = parseFloat(text.getAttribute("y")!);
        const box = { left: b.x, right: b.x + b.width, top: middle - size / 2, bottom: middle + size / 2 };
        const corners = [
          [box.left, box.top],
          [box.right, box.top],
          [box.left, box.bottom],
          [box.right, box.bottom],
        ];
        const inside = ([x, y]: number[]) => {
          if (round) {
            const a = o.width / 2 - stroke;
            const c = o.height / 2 - stroke;
            const dx = x - (o.x + o.width / 2);
            const dy = y - (o.y + o.height / 2);
            return (dx / a) ** 2 + (dy / c) ** 2 <= 1;
          }
          return x >= o.x + stroke && x <= o.x + o.width - stroke && y >= o.y + stroke && y <= o.y + o.height - stroke;
        };
        if (!corners.every(inside)) {
          problems.push(`${tile.dataset.testid} ${line.dataset.tileLine} "${line.textContent}"`);
        }
      }
    }
    return problems;
  });

const screens = [
  { name: "tablet", device: devices["Galaxy Tab S4 landscape"] },
  { name: "phone", device: devices["Pixel 7"] },
];

for (const screen of screens) {
  test(`tile text stays inside every table on a ${screen.name}, in English and Chinese`, async ({ browser, baseURL }) => {
    const context = await browser.newContext({
      ...screen.device,
      baseURL,
      ignoreHTTPSErrors: true,
      storageState: hostSessionPath,
    });
    const page = await context.newPage();
    await page.route(/\/floor-plan$/, async (route) => {
      const response = await route.fetch();
      await route.fulfill({ response, json: crowdedPlan(await response.json()) });
    });
    await page.goto("/host/");
    const plan = page.getByTestId("floor-plan");
    await expect(plan.locator('[data-tile-line="guest"]').first()).toBeVisible();

    for (const language of ["English", "中文"]) {
      await page.getByRole("button", { name: language, exact: true }).click();
      await expect(page.locator("html")).toHaveAttribute("lang", language === "中文" ? "zh-CN" : "en");
      await expect(plan.locator('[data-tile-line="party"]')).toHaveCount(14);
      expect(await linesOutside(page), `${language}: lines crossing a table outline`).toEqual([]);
    }

    // The zoom buttons cover no table, so every table can be tapped (W1 sat under them on a phone).
    const controls = (await page.getByTestId("zoom-controls").boundingBox())!;
    const covered: string[] = [];
    for (const tile of await plan.locator('[data-testid^="table-"]').all()) {
      const box = (await tile.boundingBox())!;
      const overlaps =
        box.x < controls.x + controls.width &&
        controls.x < box.x + box.width &&
        box.y < controls.y + controls.height &&
        controls.y < box.y + box.height;
      if (overlaps) covered.push((await tile.getAttribute("data-testid"))!);
    }
    expect(covered, "tables under the zoom controls").toEqual([]);

    // The full details stay in the label even where the tile cuts them.
    await expect(page.getByTestId("table-T1")).toHaveAttribute("aria-label", /欧阳娜娜|Alexandra Johnson/);
    await context.close();
  });
}

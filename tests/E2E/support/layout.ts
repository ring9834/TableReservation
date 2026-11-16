import { expect, type Page } from "@playwright/test";

/**
 * QA-8 (M11b, Design §17.3): the page doesn't scroll sideways (`scrollWidth ≤ clientWidth` on the document and the
 * body). On failure, names the widest elements that stick out past the viewport, so the CSS fix is easy to find.
 */
export const expectNoHorizontalScroll = async (page: Page, where: string) => {
  const result = await page.evaluate(() => {
    const root = document.documentElement;
    const width = root.clientWidth;
    const scroll = Math.max(root.scrollWidth, document.body.scrollWidth);
    const describe = (element: Element) => {
      const testId = element.getAttribute("data-testid");
      const id = element.id ? `#${element.id}` : "";
      const name = typeof element.className === "string" ? element.className.split(" ")[0] : "";
      return `${element.tagName.toLowerCase()}${testId ? `[data-testid=${testId}]` : id || (name ? `.${name}` : "")}`;
    };
    const wide =
      scroll > width
        ? [...document.body.querySelectorAll("*")]
            .map((element) => ({ element, right: element.getBoundingClientRect().right }))
            .filter(({ right }) => right > width + 0.5)
            .sort((a, b) => b.right - a.right)
            .slice(0, 5)
            .map(({ element, right }) => `${describe(element)} (right edge ${Math.round(right)} px)`)
        : [];
    return { width, scroll, wide };
  });
  expect(
    result.scroll,
    `${where}: scrolls sideways (${result.scroll} px of content in ${result.width} px). Widest: ${result.wide.join(", ")}`,
  ).toBeLessThanOrEqual(result.width);
};

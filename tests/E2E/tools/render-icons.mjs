// Renders the host app icons (M11c, Design §18.11) from web/icons/gd-mark.svg with the installed Playwright, once:
//   cd tests/E2E && node tools/render-icons.mjs
// Writes web/public/icons/: host-192.png and host-512.png (purpose "any": rounded square, transparent corners),
// host-maskable-512.png (full bleed, the monogram inside the central 80 % safe circle) and apple-touch-icon.png
// (180, full bleed; iOS rounds the corners). Commit the PNGs after changing the SVG. Locally Playwright drives the
// installed Microsoft Edge (as the E2E projects do); PLAYWRIGHT_CHANNEL="" uses Playwright's own Chromium instead.
import { chromium } from "@playwright/test";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..", "..");
const source = path.join(root, "web", "icons", "gd-mark.svg");
const outDir = path.join(root, "web", "public", "icons");

/** `fullBleed`: the background fills the square (no rounded corners). `scale`: the monogram's size around the centre. */
const icons = [
  { file: "host-192.png", size: 192, fullBleed: false, scale: 1 },
  { file: "host-512.png", size: 512, fullBleed: false, scale: 1 },
  { file: "host-maskable-512.png", size: 512, fullBleed: true, scale: 0.8 },
  { file: "apple-touch-icon.png", size: 180, fullBleed: true, scale: 0.9 },
];

const svg = await readFile(source, "utf8");
const channel = process.env.PLAYWRIGHT_CHANNEL ?? "msedge";
const browser = await chromium.launch(channel ? { channel } : {});
try {
  for (const icon of icons) {
    const page = await browser.newPage({
      viewport: { width: icon.size, height: icon.size },
      deviceScaleFactor: 1,
    });
    await page.setContent(`<!doctype html><html><body style="margin:0;background:transparent">${svg}</body></html>`);
    await page.evaluate(({ size, fullBleed, scale }) => {
      const element = document.querySelector("svg");
      element.setAttribute("width", String(size));
      element.setAttribute("height", String(size));
      element.style.display = "block";
      if (fullBleed) document.getElementById("bg").setAttribute("rx", "0");
      document
        .getElementById("mark")
        .setAttribute("transform", `translate(256 256) scale(${scale}) translate(-256 -256)`);
    }, icon);
    await page.screenshot({ path: path.join(outDir, icon.file), omitBackground: true });
    await page.close();
    console.log(`${icon.file} ${icon.size}×${icon.size}`);
  }
} finally {
  await browser.close();
}

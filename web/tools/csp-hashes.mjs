// Content-Security-Policy hashes for the static export (M12a, Design §17.4). Runs after `next build` (npm run build).
//
// Next writes inline <script> tags into every page (the RSC payload, self.__next_f.push(…)), so `script-src 'self'`
// alone would block the app. This step hashes every inline script (sha256 of its exact text) so the policy can allow
// exactly those, and nothing else inline.
//
//   CSP_MODE=enforce (default): each page gets <meta http-equiv="Content-Security-Policy" content="script-src …">
//     with its own hashes. CloudFront's header carries every other directive.
//   CSP_MODE=report: no meta (a report-only policy in a <meta> is ignored by browsers); the union of every page's
//     hashes goes to out/csp-script-hashes.json, and CDK puts script-src into the Report-Only header instead.
//
// Usage: node tools/csp-hashes.mjs [outDir]

import { createHash } from "node:crypto";
import { readdirSync, readFileSync, statSync, writeFileSync, rmSync, existsSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

/** Sources allowed for scripts besides the hashes: our own files and Stripe.js. */
export const SCRIPT_SOURCES = ["'self'", "https://js.stripe.com"];

export const HASHES_FILE = "csp-script-hashes.json";

const SCRIPT = /<script\b([^>]*)>([\s\S]*?)<\/script>/gi;
const OUR_META = /<meta http-equiv="Content-Security-Policy" content="[^"]*" data-csp="hashes"\/>/g;

/** The texts of the inline scripts in an HTML page (a script with a src attribute isn't inline). */
export function inlineScripts(html) {
  const scripts = [];
  for (const match of html.matchAll(SCRIPT)) {
    const attributes = match[1];
    const body = match[2];
    if (/\ssrc\s*=/i.test(attributes) || body.length === 0) {
      continue;
    }
    scripts.push(body);
  }
  return scripts;
}

/** A CSP source for a script text: 'sha256-<base64>' of its UTF-8 bytes. */
export function hashSource(script) {
  return `'sha256-${createHash("sha256").update(script, "utf8").digest("base64")}'`;
}

/** The sorted, distinct hash sources for a page. */
export function pageHashes(html) {
  return [...new Set(inlineScripts(withoutOurMeta(html)).map(hashSource))].sort();
}

export function scriptSrc(hashes) {
  return ["script-src", ...SCRIPT_SOURCES, ...hashes].join(" ");
}

export function withoutOurMeta(html) {
  return html.replace(OUR_META, "");
}

/** The page with its own script-src meta first in <head>; running it twice gives the same page. */
export function withMeta(html) {
  const clean = withoutOurMeta(html);
  const meta = `<meta http-equiv="Content-Security-Policy" content="${scriptSrc(pageHashes(clean))}" data-csp="hashes"/>`;
  const head = /<head\b[^>]*>/i.exec(clean);
  if (!head) {
    throw new Error("No <head> in the page; cannot add the CSP meta.");
  }
  const at = head.index + head[0].length;
  return clean.slice(0, at) + meta + clean.slice(at);
}

export function htmlFiles(dir) {
  const files = [];
  for (const name of readdirSync(dir)) {
    const path = join(dir, name);
    if (statSync(path).isDirectory()) {
      files.push(...htmlFiles(path));
    } else if (name.endsWith(".html")) {
      files.push(path);
    }
  }
  return files.sort();
}

/** Applies the mode to every page under outDir. Returns the union of hashes. */
export function run(outDir, mode) {
  if (mode !== "enforce" && mode !== "report") {
    throw new Error(`CSP_MODE must be "enforce" or "report", not "${mode}".`);
  }
  const all = new Set();
  for (const file of htmlFiles(outDir)) {
    const html = readFileSync(file, "utf8");
    pageHashes(html).forEach((h) => all.add(h));
    writeFileSync(file, mode === "enforce" ? withMeta(html) : withoutOurMeta(html));
  }
  const hashes = [...all].sort();
  const hashesFile = join(outDir, HASHES_FILE);
  if (mode === "report") {
    writeFileSync(
      hashesFile,
      JSON.stringify({ scriptSrc: [...SCRIPT_SOURCES, ...hashes] }, null, 2) + "\n",
    );
  } else if (existsSync(hashesFile)) {
    rmSync(hashesFile);
  }
  return hashes;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const outDir = process.argv[2] ?? "out";
  const mode = process.env.CSP_MODE ?? "enforce";
  const hashes = run(outDir, mode);
  console.log(
    `CSP (${mode}): ${hashes.length} inline script hashes across ${htmlFiles(outDir).length} pages.`,
  );
}

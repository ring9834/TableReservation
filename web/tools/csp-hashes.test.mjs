import { test } from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
  hashSource,
  inlineScripts,
  pageHashes,
  run,
  withMeta,
  HASHES_FILE,
} from "./csp-hashes.mjs";

const page = (body = "") =>
  `<!DOCTYPE html><html><head><meta charSet="utf-8"/><script src="/_next/a.js" async=""></script></head>` +
  `<body>${body}<script>(self.__next_f=self.__next_f||[]).push([0])</script>` +
  `<script id="_R_">self.__next_f.push([1,"x\\u003c/script"])</script></body></html>`;

const sha = (text) => `'sha256-${createHash("sha256").update(text, "utf8").digest("base64")}'`;

test("inline scripts are found and scripts with src are not", () => {
  assert.deepEqual(inlineScripts(page()), [
    "(self.__next_f=self.__next_f||[]).push([0])",
    'self.__next_f.push([1,"x\\u003c/script"])',
  ]);
});

test("a hash is sha256 of the exact script text, base64", () => {
  assert.equal(hashSource("alert(1)"), sha("alert(1)"));
  assert.equal(hashSource("中文"), sha("中文"));
});

test("the meta goes first in <head> and lists every inline script of the page", () => {
  const html = withMeta(page());
  assert.match(
    html,
    /<head><meta http-equiv="Content-Security-Policy" content="script-src 'self' https:\/\/js\.stripe\.com 'sha256-/,
  );
  for (const script of inlineScripts(page())) {
    assert.ok(html.includes(sha(script)), `missing hash for ${script}`);
  }
});

test("adding the meta twice gives the same page", () => {
  const once = withMeta(page());
  assert.equal(withMeta(once), once);
  assert.deepEqual(pageHashes(once), pageHashes(page()));
});

test("enforce writes a meta into every page; report writes none and lists the union of hashes", () => {
  const out = mkdtempSync(join(tmpdir(), "csp-"));
  mkdirSync(join(out, "host"));
  writeFileSync(join(out, "index.html"), page());
  writeFileSync(join(out, "host", "index.html"), page("<script>window.hostOnly=1</script>"));

  const enforced = run(out, "enforce");
  assert.equal(enforced.length, 3);
  assert.match(readFileSync(join(out, "host", "index.html"), "utf8"), /data-csp="hashes"/);
  assert.ok(
    readFileSync(join(out, "host", "index.html"), "utf8").includes(sha("window.hostOnly=1")),
  );
  assert.ok(!readFileSync(join(out, "index.html"), "utf8").includes(sha("window.hostOnly=1")));
  assert.equal(existsSync(join(out, HASHES_FILE)), false);

  const reported = run(out, "report");
  assert.deepEqual(reported, enforced);
  assert.doesNotMatch(
    readFileSync(join(out, "host", "index.html"), "utf8"),
    /Content-Security-Policy/,
  );
  const file = JSON.parse(readFileSync(join(out, HASHES_FILE), "utf8"));
  assert.deepEqual(file.scriptSrc, ["'self'", "https://js.stripe.com", ...enforced]);
});

test("an unknown mode is refused", () => {
  assert.throws(() => run(tmpdir(), "off"), /CSP_MODE/);
});

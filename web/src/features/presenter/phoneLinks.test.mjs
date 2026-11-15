// Run with `npm test` (node --test). Links in the phone panel's texts (M10b, Design §18.10).
import assert from "node:assert/strict";
import { test } from "node:test";
import { managePath, segments, shortLink } from "./phoneLinks.mjs";

const token = "AQExAZOn0AAAcAAAgAAAAAAAAAoC.Zm9vYmFyYmF6cXV4";
const confirmUrl = `https://localhost:3000/manage/?token=${token}`;

test("a reminder body splits into text and links, in order, nothing lost", () => {
  const body = `Golden Dragon: reminder. Confirm: ${confirmUrl} Cancel: ${confirmUrl}x`;
  const parts = segments(body);
  assert.deepEqual(
    parts.map((p) => p.kind),
    ["text", "link", "text", "link"],
  );
  assert.equal(parts.map((p) => (p.kind === "text" ? p.text : p.url)).join(""), body);
  assert.equal(parts[1].kind === "link" && parts[1].url, confirmUrl);
});

test("a body without links is one text", () => {
  assert.deepEqual(segments("Your table is ready."), [
    { kind: "text", text: "Your table is ready." },
  ]);
  assert.deepEqual(segments(""), []);
});

test("links are shown short, with only the start of the token", () => {
  assert.equal(shortLink(confirmUrl), "localhost:3000/manage/?token=AQEx…");
  assert.equal(shortLink("https://example.org/a"), "example.org/a");
  assert.equal(
    shortLink("https://example.org/" + "x".repeat(60)),
    "example.org/" + "x".repeat(28) + "…",
  );
});

test("the phone opens the manage page on its own origin, token encoded", () => {
  assert.equal(managePath(confirmUrl), `/manage/?token=${encodeURIComponent(token)}`);
  assert.equal(managePath("http://localhost:3000/manage?token=a+b"), "/manage/?token=a%20b");
  assert.equal(managePath("https://localhost:3000/book/?token=x"), null);
  assert.equal(managePath("https://localhost:3000/manage/"), null);
  assert.equal(managePath("not a url"), null);
});

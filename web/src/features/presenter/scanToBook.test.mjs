// Run with `npm test` (node --test). The "Scan to book" code (M12b, Design §17.5).
import assert from "node:assert/strict";
import { test } from "node:test";
import { create } from "qrcode/lib/core/qrcode.js";
import { bookingUrl, qrPath, quietZone, viewBoxFor } from "./scanToBook.mjs";

test("the code points at the guest booking page on the page's own origin", () => {
  assert.equal(bookingUrl("https://localhost:3000"), "https://localhost:3000/book/");
  assert.equal(
    bookingUrl("https://d1234abcd.cloudfront.net"),
    "https://d1234abcd.cloudfront.net/book/",
  );
});

test("a path or query on the origin is dropped: only ever /book/", () => {
  assert.equal(
    bookingUrl("https://d1234abcd.cloudfront.net/"),
    "https://d1234abcd.cloudfront.net/book/",
  );
  assert.equal(
    bookingUrl("https://d1234abcd.cloudfront.net/presenter/?x=1"),
    "https://d1234abcd.cloudfront.net/book/",
  );
});

test("never a staff screen: the path is always /book/", () => {
  // The path, not the whole URL: "localhost" contains "host".
  for (const origin of [
    "https://localhost:3000",
    "https://d1234abcd.cloudfront.net/host/",
    "https://d1234abcd.cloudfront.net/presenter/",
    "https://d1234abcd.cloudfront.net/demo-login/",
  ]) {
    assert.equal(new URL(bookingUrl(origin)).pathname, "/book/");
  }
});

test("one 1 × 1 square per dark module", () => {
  const dark = new Set(["0,0", "1,1"]);
  assert.equal(
    qrPath(2, (row, col) => dark.has(`${row},${col}`)),
    "M0 0h1v1h-1zM1 1h1v1h-1z",
  );
  assert.equal(
    qrPath(2, () => false),
    "",
  );
});

test("the viewBox keeps the quiet zone around the code", () => {
  assert.equal(quietZone, 4);
  assert.equal(viewBoxFor(25), "-4 -4 33 33");
});

test("a real code for the booking URL draws its finder pattern", () => {
  const { modules } = create(bookingUrl("https://d1234abcd.cloudfront.net"), {
    errorCorrectionLevel: "M",
  });
  const path = qrPath(modules.size, (row, col) => !!modules.get(row, col));
  // Version 3 (29 × 29) or 4 (33 × 33) for an address this long; the top-left finder starts with a dark module.
  assert.ok([29, 33].includes(modules.size), `size ${modules.size}`);
  assert.ok(path.startsWith("M0 0h1v1h-1z"));
  assert.equal(path.split("M").length - 1, [...modules.data].filter(Boolean).length);
});

// Run with `npm test` (node --test). The waitlist entry's overdue minutes and quote line (M11b, Design §17.3).
import assert from "node:assert/strict";
import { test } from "node:test";
import { minutesSinceNotify, quoteIsStale } from "./waitlist.mjs";

test("minutes since notify are whole minutes on the business clock", () => {
  assert.equal(minutesSinceNotify("2026-11-16T05:25:00Z", "2026-11-16T05:36:00Z"), 11);
  assert.equal(minutesSinceNotify("2026-11-16T05:25:00Z", "2026-11-16T05:36:59Z"), 11);
  assert.equal(minutesSinceNotify("2026-11-16T05:25:30Z", "2026-11-16T05:36:00Z"), 10);
});

test("not notified, or a business time before the notify, never gives a negative count", () => {
  assert.equal(minutesSinceNotify(null, "2026-11-16T05:36:00Z"), null);
  assert.equal(minutesSinceNotify("2026-11-16T05:25:00Z", "2026-11-16T05:20:00Z"), 0);
  assert.equal(minutesSinceNotify("not a time", "2026-11-16T05:20:00Z"), null);
});

test("the quote is stale only once the party is notified", () => {
  assert.equal(quoteIsStale("notified"), true);
  assert.equal(quoteIsStale("waiting"), false);
});

// Run with `npm test` (node --test). The guest's history on a booking's details (AC6.6, Design §18.9).
import assert from "node:assert/strict";
import { test } from "node:test";
import { visitLine } from "./guestHistory.mjs";

test("no guest, no line", () => {
  assert.equal(visitLine(null, "booked", "en-NZ"), null);
  assert.equal(visitLine(undefined, "seated", "en-NZ"), null);
});

test("a booking still to come or seated is the next visit", () => {
  assert.deepEqual(visitLine(0, "booked", "en-NZ"), { kind: "first" });
  assert.deepEqual(visitLine(0, "seated", "en-NZ"), { kind: "first" });
  assert.deepEqual(visitLine(1, "booked", "en-NZ"), { kind: "nth", n: 2, form: "two" });
  assert.deepEqual(visitLine(2, "confirmed", "en-NZ"), { kind: "nth", n: 3, form: "few" });
  assert.deepEqual(visitLine(3, "seated", "en-NZ"), { kind: "nth", n: 4, form: "other" });
  assert.deepEqual(visitLine(10, "booked", "en-NZ"), { kind: "nth", n: 11, form: "other" });
  assert.deepEqual(visitLine(20, "booked", "en-NZ"), { kind: "nth", n: 21, form: "one" });
});

test("Chinese has one ordinal form", () => {
  assert.deepEqual(visitLine(2, "booked", "zh-CN"), { kind: "nth", n: 3, form: "other" });
});

test("a completed booking was already counted, so it shows the count", () => {
  assert.deepEqual(visitLine(1, "completed", "en-NZ"), { kind: "count", n: 1 });
  assert.deepEqual(visitLine(3, "completed", "en-NZ"), { kind: "count", n: 3 });
});

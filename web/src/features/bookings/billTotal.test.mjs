// Run with `npm test` (node --test). The host's "Bill paid" total (AC6.6, Design §18.9).
import assert from "node:assert/strict";
import { test } from "node:test";
import { centsToField, maxBillCents, parseBillTotal, suggestedBillCents } from "./billTotal.mjs";

test("whole dollars and up to two decimals", () => {
  assert.deepEqual(parseBillTotal("186"), { cents: 18600 });
  assert.deepEqual(parseBillTotal("186.5"), { cents: 18650 });
  assert.deepEqual(parseBillTotal(" 123.45 "), { cents: 12345 });
  assert.deepEqual(parseBillTotal("0.01"), { cents: 1 });
});

test("a dollar sign and thousands separators are fine", () => {
  assert.deepEqual(parseBillTotal("NZ$1,234.50"), { cents: 123450 });
  assert.deepEqual(parseBillTotal("$90"), { cents: 9000 });
});

test("required, more than 0, two decimals at most, within the API's limit", () => {
  assert.deepEqual(parseBillTotal(""), { error: "required" });
  assert.deepEqual(parseBillTotal("   "), { error: "required" });
  assert.deepEqual(parseBillTotal("0"), { error: "tooSmall" });
  assert.deepEqual(parseBillTotal("0.00"), { error: "tooSmall" });
  assert.deepEqual(parseBillTotal("-1"), { error: "invalid" });
  assert.deepEqual(parseBillTotal("1.234"), { error: "invalid" });
  assert.deepEqual(parseBillTotal("abc"), { error: "invalid" });
  assert.deepEqual(parseBillTotal("12.3.4"), { error: "invalid" });
  assert.deepEqual(parseBillTotal("100000.00"), { cents: maxBillCents });
  assert.deepEqual(parseBillTotal("100000.01"), { error: "tooLarge" });
});

test("the suggestion is NZ$45.00 a head, as the field shows it", () => {
  assert.equal(centsToField(suggestedBillCents(2)), "90.00");
  assert.equal(centsToField(suggestedBillCents(4)), "180.00");
  assert.equal(centsToField(suggestedBillCents(0)), "45.00");
});

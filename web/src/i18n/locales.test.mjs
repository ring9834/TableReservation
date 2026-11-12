// Run with `npm test` (node --test, no extra packages). Fails when the two languages drift apart (AC1.8, QA-7).
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";

const load = (name) => JSON.parse(readFileSync(new URL(`./${name}`, import.meta.url), "utf8"));
const en = load("en.json");
const zh = load("zh-CN.json");

const keysOf = (value, prefix = "") =>
  Object.entries(value).flatMap(([key, child]) =>
    typeof child === "object" && child !== null
      ? keysOf(child, `${prefix}${key}.`)
      : [`${prefix}${key}`],
  );

const valueAt = (resources, key) => key.split(".").reduce((node, part) => node?.[part], resources);
const placeholders = (text) => [...text.matchAll(/{{\s*(\w+)\s*}}/g)].map((m) => m[1]).sort();

test("every key in English exists in Chinese", () => {
  const missing = keysOf(en).filter((key) => valueAt(zh, key) === undefined);
  assert.deepEqual(missing, [], `Missing in zh-CN.json: ${missing.join(", ")}`);
});

test("every key in Chinese exists in English", () => {
  const missing = keysOf(zh).filter((key) => valueAt(en, key) === undefined);
  assert.deepEqual(missing, [], `Missing in en.json: ${missing.join(", ")}`);
});

test("no message is empty and both languages use the same placeholders", () => {
  for (const key of keysOf(en)) {
    const english = valueAt(en, key);
    const chinese = valueAt(zh, key);
    assert.ok(typeof english === "string" && english.trim() !== "", `en ${key} is empty`);
    assert.ok(typeof chinese === "string" && chinese.trim() !== "", `zh-CN ${key} is empty`);
    assert.deepEqual(placeholders(chinese), placeholders(english), `Placeholders differ: ${key}`);
  }
});

test("every API error code the pages can meet has a message", () => {
  const codes = [
    "slot_taken",
    "party_too_large",
    "outside_service_hours",
    "outside_booking_window",
    "validation_failed",
    "too_many_requests",
    "not_signed_in",
    "unexpected",
  ];
  for (const code of codes) {
    assert.ok(valueAt(en, `errors.${code}`), `No message for error code ${code}`);
  }
});

test("every field rule code the booking form can meet has a message", () => {
  // Design §14.1 field rule codes of CreateBookingRequest that a guest can cause from the form.
  const codes = [
    "name_length",
    "mobile_invalid",
    "mobile_not_mobile",
    "email_invalid",
    "email_too_long",
    "occasion_too_long",
    "allergy_notes_too_long",
    "notes_too_long",
  ];
  for (const code of codes) {
    assert.ok(valueAt(en, `fieldErrors.${code}`), `No message for field code ${code}`);
  }
});

test("every field rule code the sign-in form can meet has a message", () => {
  for (const code of ["passcode_required", "passcode_too_long"]) {
    assert.ok(valueAt(en, `fieldErrors.${code}`), `No message for field code ${code}`);
  }
});

test("every table state and booking status has a label", () => {
  for (const state of ["free", "booked", "arrivingSoon", "seated"]) {
    assert.ok(valueAt(en, `tableState.${state}`), `No label for table state ${state}`);
  }
  const statuses = ["pendingPayment", "booked", "confirmed", "seated", "completed", "cancelled"];
  for (const status of statuses) {
    assert.ok(valueAt(en, `bookingStatus.${status}`), `No label for booking status ${status}`);
  }
});

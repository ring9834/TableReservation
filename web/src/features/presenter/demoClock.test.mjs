// Run with `npm test` (node --test). The presenter's demo clock helpers (M11a, Design §17.2).
import assert from "node:assert/strict";
import { test } from "node:test";
import { demoNow, toggledMode } from "./demoClock.mjs";

const at = "2026-11-24T05:25:00.000Z"; // 6:25 pm in Auckland

test("a fixed clock shows the API's time whatever the browser's clock does", () => {
  assert.equal(demoNow({ mode: "fixed", at }, 1_000, 61_000), at);
});

test("a running clock ticks on from the API's time", () => {
  assert.equal(demoNow({ mode: "running", at }, 1_000, 61_000), "2026-11-24T05:26:00.000Z");
});

test("a running clock never shows a time before the API's answer", () => {
  assert.equal(demoNow({ mode: "running", at }, 5_000, 1_000), at);
});

test("Run and Freeze switch the mode", () => {
  assert.equal(toggledMode("fixed"), "running");
  assert.equal(toggledMode("running"), "fixed");
});

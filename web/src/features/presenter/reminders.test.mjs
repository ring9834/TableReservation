// Run with `npm test` (node --test). "Send reminders now" (M10b, Design §18.10).
import assert from "node:assert/strict";
import { test } from "node:test";
import { reminderSummary } from "./reminders.mjs";

test("the seeded evening at 18:25: two sent, four skipped", () => {
  assert.deepEqual(
    reminderSummary({
      sent: 2,
      skipped: [
        { reason: "alreadyConfirmed", count: 1 },
        { reason: "startsWithin2h", count: 3 },
      ],
    }),
    {
      sent: "many",
      count: 2,
      skippedTotal: 4,
      skipped: [
        { reason: "alreadyConfirmed", count: 1 },
        { reason: "startsWithin2h", count: 3 },
      ],
    },
  );
});

test("a second press sends nothing new", () => {
  const summary = reminderSummary({
    sent: 0,
    skipped: [{ reason: "alreadyReminded", count: 2 }],
  });
  assert.equal(summary.sent, "none");
  assert.equal(summary.skippedTotal, 2);
});

test("one reminder; unknown reasons and empty counts are left out", () => {
  const summary = reminderSummary({
    sent: 1,
    skipped: [
      { reason: "somethingNew", count: 5 },
      { reason: "noGuest", count: 0 },
    ],
  });
  assert.deepEqual(summary, { sent: "one", count: 1, skippedTotal: 0, skipped: [] });
});

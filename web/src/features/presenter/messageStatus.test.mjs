// Run with `npm test` (node --test). How the phone panel words a message's status (Design §18.7).
import assert from "node:assert/strict";
import { test } from "node:test";
import { messageStatus, statusKeys } from "./messageStatus.mjs";

test("queued, sent and delivered", () => {
  assert.deepEqual(messageStatus("queued", null), { key: "sending", tone: "muted" });
  assert.deepEqual(messageStatus("sent", null), { key: "sent", tone: "ok" });
  assert.deepEqual(messageStatus("delivered", null), { key: "delivered", tone: "ok" });
});

test("a superseded text reads as replaced, in grey, not as an error (Design §7.2)", () => {
  assert.deepEqual(messageStatus("failed", "superseded"), { key: "replaced", tone: "muted" });
});

test("failures are in plain words", () => {
  assert.deepEqual(messageStatus("failed", "expired"), { key: "expired", tone: "error" });
  assert.deepEqual(messageStatus("failed", "sender_exception:HttpRequestException"), {
    key: "senderFailed",
    tone: "error",
  });
  assert.deepEqual(messageStatus("failed", "invalid_number"), { key: "notSent", tone: "error" });
  assert.deepEqual(messageStatus("failed", null), { key: "notSent", tone: "error" });
});

test("every key it returns is listed", () => {
  const cases = [
    ["queued", null],
    ["sent", null],
    ["delivered", null],
    ["failed", "superseded"],
    ["failed", "expired"],
    ["failed", "sender_exception:X"],
    ["failed", "other"],
  ];
  for (const [status, error] of cases) {
    assert.ok(statusKeys.includes(messageStatus(status, error).key));
  }
});

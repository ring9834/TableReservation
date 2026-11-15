// Run with `npm test` (node --test). How the presenter's POS log words a message (AC6.1, Design §18.9).
import assert from "node:assert/strict";
import { test } from "node:test";
import { keyFields, posStatus, posStatusKeys, unmatchedReasonKey } from "./posLog.mjs";

const none = {
  tables: null,
  partySize: null,
  name: null,
  phone: null,
  amountCents: null,
  currency: "NZD",
};

test("OpenTable shows its tables, party and name", () => {
  const item = {
    messageType: "OpenTable",
    tableNames: ["T4"],
    payload: { partySize: 4, guestName: "Aroha Ngata", tableIds: ["x"] },
  };
  assert.deepEqual(keyFields(item), { ...none, tables: "T4", partySize: 4, name: "Aroha Ngata" });
});

test("a walk-in without a name and joined tables", () => {
  const item = { messageType: "OpenTable", tableNames: ["T7", "T8"], payload: { partySize: 12 } };
  assert.deepEqual(keyFields(item), { ...none, tables: "T7+T8", partySize: 12 });
});

test("ApplyDeposit shows the amount", () => {
  const item = {
    messageType: "ApplyDeposit",
    tableNames: ["T7"],
    payload: { amountCents: 12000, currency: "NZD" },
  };
  assert.deepEqual(keyFields(item), { ...none, amountCents: 12000 });
});

test("UpsertMember shows the name and the masked phone as sent", () => {
  const item = {
    messageType: "UpsertMember",
    tableNames: [],
    payload: { name: "Mere", memberPhone: "+64******291" },
  };
  assert.deepEqual(keyFields(item), { ...none, name: "Mere", phone: "+64******291" });
});

test("BillClosed shows the table and the bill total", () => {
  const item = {
    messageType: "BillClosed",
    tableNames: ["T4"],
    payload: { billTotalCents: 18600 },
  };
  assert.deepEqual(keyFields(item), { ...none, tables: "T4", amountCents: 18600 });
});

test("an unknown type or an odd payload shows nothing", () => {
  assert.deepEqual(keyFields({ messageType: "Something", tableNames: ["T1"], payload: {} }), none);
  assert.deepEqual(keyFields({ messageType: "OpenTable", tableNames: [], payload: null }), none);
  assert.deepEqual(
    keyFields({ messageType: "ApplyDeposit", tableNames: [], payload: { amountCents: "12000" } }),
    none,
  );
});

test("waiting is never a failure; unmatched is a warning", () => {
  assert.deepEqual(posStatus("waiting"), { key: "waiting", tone: "muted" });
  assert.deepEqual(posStatus("sent"), { key: "sent", tone: "ok" });
  assert.deepEqual(posStatus("matched"), { key: "matched", tone: "ok" });
  assert.deepEqual(posStatus("unmatched"), { key: "unmatched", tone: "warning" });
  assert.deepEqual(posStatus("failed"), { key: "failed", tone: "error" });
});

test("every key it returns is listed", () => {
  for (const status of [...posStatusKeys, "something-new"]) {
    assert.ok(posStatusKeys.includes(posStatus(status).key));
  }
});

test("unmatched reasons in plain words, an unknown one as other", () => {
  assert.equal(unmatchedReasonKey("no_match"), "no_match");
  assert.equal(unmatchedReasonKey("second_bill"), "second_bill");
  assert.equal(unmatchedReasonKey("tab_closed"), "other");
  assert.equal(unmatchedReasonKey(null), "other");
});

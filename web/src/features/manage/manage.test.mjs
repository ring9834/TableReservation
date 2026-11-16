// Run with `npm test` (node --test). The guest's manage page rules (M10b, BR-07, BR-13, Design §18.10).
import assert from "node:assert/strict";
import { test } from "node:test";
import {
  cancelConsequence,
  errorKind,
  pageLanguage,
  resultKeys,
  showsLateWarning,
  showsTerms,
  visibleActions,
} from "./manage.mjs";

const paid = { amount: { amountCents: 12_000, currency: "NZD" }, status: "captured" };

/** #11 at 18:25: party of 6, deposit paid, 2 h 05 min before the start. */
const inTime = {
  status: "booked",
  deposit: paid,
  cancellingNowKeepsDeposit: false,
  refundIfCancelledNow: "full",
  allowedActions: ["cancel"],
};

test("only the actions the API allows, Confirm first", () => {
  assert.deepEqual(visibleActions({ ...inTime, allowedActions: ["cancel", "confirm"] }), [
    "confirm",
    "cancel",
  ]);
  assert.deepEqual(visibleActions({ ...inTime, allowedActions: ["cancel"] }), ["cancel"]);
  assert.deepEqual(visibleActions({ ...inTime, allowedActions: [] }), []);
  assert.deepEqual(visibleActions({ ...inTime, allowedActions: ["manage"] }), []);
});

test("in time, a paid deposit is refunded in full", () => {
  assert.deepEqual(cancelConsequence(inTime), {
    key: "refundFull",
    amountCents: 12_000,
    currency: "NZD",
  });
  assert.equal(showsLateWarning(inTime), false);
});

test("inside the free-cancellation window the deposit is kept, and the page warns before any tap (BR-13)", () => {
  const late = { ...inTime, cancellingNowKeepsDeposit: true, refundIfCancelledNow: "none" };
  assert.deepEqual(cancelConsequence(late), { key: "kept", amountCents: 12_000, currency: "NZD" });
  assert.equal(showsLateWarning(late), true);
  // No warning once cancelling isn't possible any more (e.g. a Confirm link, or already cancelled).
  assert.equal(showsLateWarning({ ...late, allowedActions: ["confirm"] }), false);
});

test("terms that never refund keep the deposit too", () => {
  assert.equal(cancelConsequence({ ...inTime, refundIfCancelledNow: "none" }).key, "kept");
});

test("nothing paid: no deposit, or still awaiting payment", () => {
  assert.deepEqual(
    cancelConsequence({ ...inTime, deposit: null, refundIfCancelledNow: "notApplicable" }),
    {
      key: "noDeposit",
    },
  );
  assert.deepEqual(
    cancelConsequence({
      ...inTime,
      deposit: { ...paid, status: "pending" },
      refundIfCancelledNow: "notApplicable",
    }),
    { key: "noDeposit" },
  );
});

test("after the tap", () => {
  assert.deepEqual(resultKeys("confirm", { status: "confirmed", refundDecision: null }), [
    "confirmed",
  ]);
  assert.deepEqual(resultKeys("cancel", { status: "cancelled", refundDecision: "full" }), [
    "cancelled",
    "refundOnItsWay",
  ]);
  assert.deepEqual(resultKeys("cancel", { status: "cancelled", refundDecision: "none" }), [
    "cancelled",
  ]);
  assert.deepEqual(resultKeys("cancel", { status: "cancelled", refundDecision: "notApplicable" }), [
    "cancelled",
  ]);
});

test("errors: a bad link is the whole page; a changed booking shows its status", () => {
  assert.equal(errorKind("invalid_link"), "link");
  assert.equal(errorKind("link_expired"), "link");
  assert.equal(errorKind("invalid_transition"), "changed");
  assert.equal(errorKind("version_conflict"), "changed");
  assert.equal(errorKind("too_many_requests"), "other");
  assert.equal(errorKind("unexpected"), "other");
});

test("the page opens in the booking's language", () => {
  assert.equal(pageLanguage("zh"), "zh-CN");
  assert.equal(pageLanguage("en"), "en");
  assert.equal(pageLanguage(null), "en");
});

test("the cancellation terms show only while the booking can still be cancelled (M11a)", () => {
  for (const status of ["pendingPayment", "booked", "confirmed"]) {
    assert.equal(showsTerms({ status }), true, status);
  }
  for (const status of ["cancelled", "seated", "completed"]) {
    assert.equal(showsTerms({ status }), false, status);
  }
  // A Confirm link allows no cancel, but the guest still sees the terms they booked under.
  assert.equal(showsTerms({ ...inTime, allowedActions: ["confirm"] }), true);
});

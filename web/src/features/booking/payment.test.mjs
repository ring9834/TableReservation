// Run with `npm test` (node --test). The guest payment step's rules and the host's deposit wording (Design §18.8).
import assert from "node:assert/strict";
import { test } from "node:test";
import {
  announcementFor,
  depositLabelKey,
  formatCountdown,
  payControls,
  pollOutcome,
  secondsLeft,
  stillConfirmingAfterMs,
} from "./payment.mjs";

test("the countdown is minutes and seconds left, never a clock time", () => {
  assert.equal(formatCountdown(581), "9:41");
  assert.equal(formatCountdown(600), "10:00");
  assert.equal(formatCountdown(5), "0:05");
  assert.equal(formatCountdown(0), "0:00");
  assert.equal(formatCountdown(-3), "0:00");
  assert.equal(formatCountdown(59.2), "1:00"); // rounded up, like the server
});

test("between polls the countdown ticks from the server's answer, not the guest's clock", () => {
  assert.equal(secondsLeft(600, 0), 600);
  assert.equal(secondsLeft(600, 1500), 599);
  assert.equal(secondsLeft(600, 2000), 598);
  assert.equal(secondsLeft(3, 10_000), 0);
  assert.equal(secondsLeft(600, -5000), 600); // a clock that went backwards adds nothing
});

test("screen readers hear the countdown only at 5 minutes, 1 minute and the end", () => {
  assert.equal(announcementFor(null, 600), null); // opening the page
  assert.equal(announcementFor(600, 599), null);
  assert.equal(announcementFor(301, 300), 300);
  assert.equal(announcementFor(300, 299), null);
  assert.equal(announcementFor(61, 60), 60);
  assert.equal(announcementFor(60, 59), null);
  assert.equal(announcementFor(1, 0), 0);
  assert.equal(announcementFor(0, 0), null);
  assert.equal(announcementFor(320, 290), 300); // a late poll jumping past a mark still announces it
  assert.equal(announcementFor(70, 0), 0); // and jumping to the end announces the end
  assert.equal(announcementFor(null, 0), 0); // a hold already over when the page opens
});

test("a double tap can't pay twice: the button is disabled and reads Paying… while a payment is in flight", () => {
  assert.deepEqual(payControls({ secondsLeft: 500, paying: false, paid: false }), {
    showForm: true,
    buttonDisabled: false,
    buttonLabel: "pay",
  });
  assert.deepEqual(payControls({ secondsLeft: 500, paying: true, paid: false }), {
    showForm: true,
    buttonDisabled: true,
    buttonLabel: "paying",
  });
  assert.equal(payControls({ secondsLeft: null, paying: false, paid: false }).showForm, true); // before the first poll
});

test("the form and buttons go as soon as the countdown reaches 0, even mid-payment", () => {
  assert.deepEqual(payControls({ secondsLeft: 0, paying: false, paid: false }), {
    showForm: false,
    buttonDisabled: true,
    buttonLabel: "pay",
  });
  assert.equal(payControls({ secondsLeft: 0, paying: true, paid: false }).showForm, false);
  assert.equal(payControls({ secondsLeft: 1, paying: false, paid: false }).showForm, true);
  assert.equal(payControls({ secondsLeft: 300, paying: false, paid: true }).showForm, false);
});

test("the poll outcome", () => {
  assert.equal(pollOutcome(undefined, null), "waiting");
  assert.equal(pollOutcome({ status: "pendingPayment" }, null), "waiting");
  assert.equal(pollOutcome({ status: "booked" }, 2000), "booked");
  assert.equal(pollOutcome({ status: "confirmed" }, 2000), "booked");
  assert.equal(
    pollOutcome({ status: "cancelled", cancelReason: "holdExpired" }, null),
    "holdEnded",
  );
  assert.equal(
    pollOutcome({ status: "cancelled", cancelReason: "holdExpired" }, 5000),
    "holdEnded",
  );
  assert.equal(
    pollOutcome({ status: "cancelled", cancelReason: "hostRequest" }, null),
    "cancelled",
  );
  assert.equal(pollOutcome({ status: "pendingPayment" }, stillConfirmingAfterMs - 1), "waiting");
  assert.equal(
    pollOutcome({ status: "pendingPayment" }, stillConfirmingAfterMs),
    "stillConfirming",
  );
});

test("the host's deposit wording", () => {
  assert.equal(depositLabelKey(null, "booked", null), null);
  assert.equal(depositLabelKey("pending", "pendingPayment", null), "awaiting");
  assert.equal(depositLabelKey("captured", "booked", null), "paid");
  assert.equal(depositLabelKey("captured", "seated", null), "paid");
  assert.equal(depositLabelKey("captured", "cancelled", "full"), "refundPending");
  assert.equal(depositLabelKey("refunded", "cancelled", "full"), "refunded");
  assert.equal(depositLabelKey("captured", "cancelled", "none"), "kept");
  assert.equal(depositLabelKey("released", "cancelled", "notApplicable"), "released");
});

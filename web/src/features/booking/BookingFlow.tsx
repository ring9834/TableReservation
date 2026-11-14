"use client";

import { useEffect, useState } from "react";
import type { CreateBookingResponse, VenueResponse } from "@/api/model";
import { Confirmation } from "./Confirmation";
import { DetailsStep } from "./DetailsStep";
import { PayStep } from "./PayStep";
import { readPaymentReturn } from "./paymentReturn";
import { WhenStep, type ChosenSlot, type WhenChoice } from "./WhenStep";

type Step =
  | { kind: "when"; slotTaken: boolean }
  | { kind: "details"; slot: ChosenSlot }
  | { kind: "pay"; booking: CreateBookingResponse; returned: boolean; returnFailed: boolean }
  | { kind: "done"; booking: CreateBookingResponse };

/**
 * The guest flow (Design §18.1): WhenStep → DetailsStep → PayStep (a party of 6+ with a deposit, M8b) → Confirmation.
 * Coming back from Stripe's 3-D Secure page (`?bookingId=…&payment_intent=…`) opens PayStep straight away. Each step
 * moves focus to its heading, except on first load.
 */
export function BookingFlow({ venue }: Readonly<{ venue: VenueResponse }>) {
  const [choice, setChoice] = useState<WhenChoice>({ party: 2, date: venue.today, time: "" });
  const [step, setStep] = useState<Step>({ kind: "when", slotTaken: false });
  const [moved, setMoved] = useState(false);

  const go = (next: Step) => {
    setMoved(true);
    setStep(next);
  };

  // The address bar is only readable in the browser, after hydration (static export).
  useEffect(() => {
    const returned = readPaymentReturn();
    if (returned) {
      // eslint-disable-next-line react-hooks/set-state-in-effect -- read once from the URL after hydration
      setStep({
        kind: "pay",
        booking: returned.booking,
        returned: true,
        returnFailed: returned.failed,
      });
    }
  }, []);

  const startAgain = () => {
    setChoice({ party: 2, date: venue.today, time: "" });
    go({ kind: "when", slotTaken: false });
  };

  switch (step.kind) {
    case "when":
      return (
        <WhenStep
          venue={venue}
          choice={choice}
          onChange={setChoice}
          slotTaken={step.slotTaken}
          focusHeading={moved}
          onChoose={(slot) => go({ kind: "details", slot })}
        />
      );
    case "details":
      return (
        <DetailsStep
          slot={step.slot}
          onBack={() => go({ kind: "when", slotTaken: false })}
          onSlotTaken={() => {
            // Ask again for the time that was just taken: availability now offers the nearest free times (AC1.3).
            setChoice((c) => ({ ...c, date: step.slot.date, time: step.slot.localTime }));
            go({ kind: "when", slotTaken: true });
          }}
          onBooked={(booking) =>
            go(
              booking.deposit
                ? { kind: "pay", booking, returned: false, returnFailed: false }
                : { kind: "done", booking },
            )
          }
        />
      );
    case "pay":
      return (
        <PayStep
          venue={venue}
          booking={step.booking}
          returned={step.returned}
          returnFailed={step.returnFailed}
          onStartAgain={startAgain}
        />
      );
    case "done":
      return <Confirmation venue={venue} booking={step.booking} onBookAnother={startAgain} />;
  }
}

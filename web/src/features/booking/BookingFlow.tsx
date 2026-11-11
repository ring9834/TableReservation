"use client";

import { useState } from "react";
import type { CreateBookingResponse, VenueResponse } from "@/api/model";
import { Confirmation } from "./Confirmation";
import { DetailsStep } from "./DetailsStep";
import { WhenStep, type ChosenSlot, type WhenChoice } from "./WhenStep";

type Step =
  | { kind: "when"; slotTaken: boolean }
  | { kind: "details"; slot: ChosenSlot }
  | { kind: "done"; booking: CreateBookingResponse };

/**
 * The guest flow (Design §18.1): WhenStep → DetailsStep → Confirmation. PayStep joins in M8; until then a party of 6+
 * ends on the confirmation with a "deposit needed" notice. Each step moves focus to its heading, except on first load.
 */
export function BookingFlow({ venue }: Readonly<{ venue: VenueResponse }>) {
  const [choice, setChoice] = useState<WhenChoice>({ party: 2, date: venue.today, time: "" });
  const [step, setStep] = useState<Step>({ kind: "when", slotTaken: false });
  const [moved, setMoved] = useState(false);

  const go = (next: Step) => {
    setMoved(true);
    setStep(next);
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
          onBooked={(booking) => go({ kind: "done", booking })}
        />
      );
    case "done":
      return (
        <Confirmation
          venue={venue}
          booking={step.booking}
          onBookAnother={() => {
            setChoice({ party: 2, date: venue.today, time: "" });
            go({ kind: "when", slotTaken: false });
          }}
        />
      );
  }
}

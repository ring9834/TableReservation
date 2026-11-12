"use client";

import { useId } from "react";
import { useTranslation } from "react-i18next";
import { useGetAvailability } from "@/api/endpoints/bookings/bookings";
import type { AvailableSlot, VenueResponse } from "@/api/model";
import { venueId } from "@/lib/config";
import { addDays, formatLocalTime, telHref } from "@/lib/format";
import { partyLabel } from "./party";
import { useApiError } from "@/lib/useApiError";
import { useFocusHeading } from "./useFocusHeading";
import styles from "./booking.module.css";

/** Party size (or "too large to book online"), venue-local date and slot time (`"19:30:00"`, or empty). */
export type WhenChoice = { party: number | "large"; date: string; time: string };

/** A free slot the guest picked, with what the details step needs to show and send. */
export type ChosenSlot = { startsAt: string; localTime: string; date: string; partySize: number };

type Props = Readonly<{
  venue: VenueResponse;
  choice: WhenChoice;
  onChange: (choice: WhenChoice) => void;
  /** The last create was 409 `slot_taken`: say so above the nearest times. */
  slotTaken: boolean;
  focusHeading: boolean;
  onChoose: (slot: ChosenSlot) => void;
}>;

/**
 * Party size, date and time → availability (AC1.1–AC1.3). Every slot of the venue's grid can be chosen (`getVenue`);
 * when the chosen one is full, the API returns the 3 nearest free times instead. Parties above the online limit are
 * asked to call (BR-04) and nothing is looked up.
 */
export function WhenStep({ venue, choice, onChange, slotTaken, focusHeading, onChoose }: Props) {
  const { t, i18n } = useTranslation();
  const messageFor = useApiError();
  const headingRef = useFocusHeading<HTMLHeadingElement>(focusHeading);
  const ids = { party: useId(), date: useId(), time: useId(), callUs: useId() };
  const language = i18n.resolvedLanguage ?? "en";

  const partySize = typeof choice.party === "number" ? choice.party : null;
  const params =
    partySize !== null && choice.date && choice.time
      ? { date: choice.date, partySize, time: choice.time.slice(0, 5) }
      : undefined;
  const availability = useGetAvailability(venueId, params, { query: { enabled: !!params } });
  const data = params ? availability.data : undefined;

  const choose = (slot: AvailableSlot) =>
    onChoose({
      startsAt: slot.startsAt,
      localTime: slot.localTime,
      date: choice.date,
      partySize: partySize!,
    });

  const time = (value: string) => formatLocalTime(value, language);
  const requested =
    data && params ? data.slots.find((s) => s.localTime === choice.time) : undefined;
  const partySizes = Array.from({ length: venue.maxOnlinePartySize }, (_, i) => i + 1);

  return (
    <section aria-labelledby={`${ids.party}-heading`} className={styles.step}>
      <h2 id={`${ids.party}-heading`} ref={headingRef} tabIndex={-1}>
        {t("when.heading")}
      </h2>

      <div className={styles.fields}>
        <div className={styles.field}>
          <label htmlFor={ids.party}>{t("when.partySize")}</label>
          <select
            id={ids.party}
            value={String(choice.party)}
            aria-describedby={choice.party === "large" ? ids.callUs : undefined}
            onChange={(e) =>
              onChange({
                ...choice,
                party: e.target.value === "large" ? "large" : Number(e.target.value),
              })
            }
          >
            {partySizes.map((size) => (
              <option key={size} value={size}>
                {partyLabel(t, size)}
              </option>
            ))}
            <option value="large">
              {t("when.partyLarge", { count: venue.maxOnlinePartySize + 1 })}
            </option>
          </select>
        </div>

        <div className={styles.field}>
          <label htmlFor={ids.date}>{t("when.date")}</label>
          <input
            id={ids.date}
            type="date"
            required
            min={venue.today}
            max={addDays(venue.today, venue.bookingWindowDays)}
            value={choice.date}
            onChange={(e) => onChange({ ...choice, date: e.target.value })}
          />
        </div>

        <div className={styles.field}>
          <label htmlFor={ids.time}>{t("when.time")}</label>
          <select
            id={ids.time}
            value={choice.time}
            disabled={choice.party === "large"}
            onChange={(e) => onChange({ ...choice, time: e.target.value })}
          >
            <option value="">{t("when.chooseTime")}</option>
            {venue.slotTimes.map((slot) => (
              <option key={slot} value={slot}>
                {time(slot)}
              </option>
            ))}
          </select>
        </div>
      </div>

      {choice.party === "large" && (
        <div id={ids.callUs} className={styles.notice}>
          <p>{t("when.callUs", { count: venue.maxOnlinePartySize + 1 })}</p>
          {venue.contactPhone ? (
            <a className={styles.primary} href={telHref(venue.contactPhone)}>
              {t("when.callUsPhone", { phone: venue.contactPhone })}
            </a>
          ) : (
            <p>{t("when.callUsNoPhone")}</p>
          )}
        </div>
      )}

      <div aria-live="polite" className={styles.results}>
        {params && availability.isFetching && <p>{t("when.checking")}</p>}
        {params && availability.isError && !availability.isFetching && (
          <div role="alert" className={styles.alert}>
            <p>{messageFor(availability.error)}</p>
          </div>
        )}
        {data && !availability.isFetching && requested && data.requestedTimeAvailable && (
          <div className={styles.available}>
            <p>
              {t("when.available", {
                time: time(requested.localTime),
                party: partyLabel(t, data.partySize),
              })}
            </p>
            <button type="button" className={styles.primary} onClick={() => choose(requested)}>
              {t("when.continue")}
            </button>
          </div>
        )}
        {data &&
          !availability.isFetching &&
          !data.requestedTimeAvailable &&
          data.slots.length > 0 && (
            <div>
              <p>{slotTaken ? t("when.slotTaken") : t("when.full", { time: time(choice.time) })}</p>
              <ul className={styles.slots}>
                {data.slots.map((slot) => (
                  <li key={slot.startsAt}>
                    <button type="button" className={styles.slot} onClick={() => choose(slot)}>
                      {t("when.pickTime", { time: time(slot.localTime) })}
                    </button>
                  </li>
                ))}
              </ul>
            </div>
          )}
        {data &&
          !availability.isFetching &&
          !data.requestedTimeAvailable &&
          data.slots.length === 0 && (
            <p>{t("when.noneThatDay", { party: partyLabel(t, data.partySize) })}</p>
          )}
      </div>
    </section>
  );
}

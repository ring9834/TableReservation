// How the presenter's POS log words a message (AC6.1, Design §6.7, §18.9). Plain JavaScript with JSDoc types and no
// imports, so `npm test` (node --test) can test it directly; TypeScript reads the types through `allowJs`.

/** Every key under `pos.status` the log can show; `locales.test.mjs` checks each has a label. */
export const posStatusKeys = /** @type {const} */ ([
  "waiting",
  "sent",
  "acknowledged",
  "failed",
  "received",
  "matched",
  "unmatched",
  "dismissed",
]);

/** The reasons an inbound bill can stay unmatched (§6.5); each has plain words under `pos.unmatchedReason`. */
export const unmatchedReasons = /** @type {const} */ (["no_match", "ambiguous", "second_bill"]);

/** The POS log filters: all messages, or one status (the API's `status` parameter). */
export const posFilters = /** @type {const} */ (["all", "waiting", "unmatched"]);

/**
 * The label key under `pos.status` and its tone. Waiting is "waiting for the POS", never a failure, however many
 * times the worker has tried (AC6.5); Unmatched is a warning, not an error: a manager looks at it later (§6.5).
 * @param {string} status the API's `PosMessageStatus`
 * @returns {{ key: (typeof posStatusKeys)[number], tone: "muted" | "ok" | "warning" | "error" }}
 */
export const posStatus = (status) => {
  switch (status) {
    case "sent":
    case "acknowledged":
    case "matched":
      return { key: status, tone: "ok" };
    case "failed":
      return { key: "failed", tone: "error" };
    case "unmatched":
      return { key: "unmatched", tone: "warning" };
    case "received":
    case "dismissed":
      return { key: status, tone: "muted" };
    default:
      return { key: "waiting", tone: "muted" };
  }
};

/** The unmatched reason's key under `pos.unmatchedReason`; a code the screen doesn't know reads as `other`. */
export const unmatchedReasonKey = (/** @type {string | null | undefined} */ reason) =>
  reason && /** @type {readonly string[]} */ (unmatchedReasons).includes(reason) ? reason : "other";

/**
 * @typedef {{
 *   tables: string | null,
 *   partySize: number | null,
 *   name: string | null,
 *   phone: string | null,
 *   amountCents: number | null,
 *   currency: string,
 * }} KeyFields
 */

const text = (/** @type {unknown} */ value) =>
  typeof value === "string" && value.trim() !== "" ? value : null;
const count = (/** @type {unknown} */ value) =>
  typeof value === "number" && Number.isFinite(value) ? value : null;

/**
 * The fields a log row shows on its one line (AC6.1), read from the payload as the API returns it (phone masked):
 * OpenTable `T4 · 4 guests · name`; ApplyDeposit `NZ$120.00`; UpsertMember `name · +64******291`;
 * BillClosed `T4 · NZ$186.00`. The screen formats the money and the guest count in its language. An unknown type
 * shows nothing.
 * @param {{ messageType: string, tableNames: readonly string[], payload: unknown }} item
 * @returns {KeyFields}
 */
export const keyFields = (item) => {
  const payload = /** @type {Record<string, unknown>} */ (
    item.payload && typeof item.payload === "object" ? item.payload : {}
  );
  const tables = item.tableNames.length > 0 ? item.tableNames.join("+") : null;
  const currency = text(payload.currency) ?? "NZD";
  /** @type {KeyFields} */
  const none = {
    tables: null,
    partySize: null,
    name: null,
    phone: null,
    amountCents: null,
    currency,
  };
  switch (item.messageType) {
    case "OpenTable":
      return {
        ...none,
        tables,
        partySize: count(payload.partySize),
        name: text(payload.guestName),
      };
    case "ApplyDeposit":
      return { ...none, amountCents: count(payload.amountCents) };
    case "UpsertMember":
      return { ...none, name: text(payload.name), phone: text(payload.memberPhone) };
    case "BillClosed":
      return { ...none, tables, amountCents: count(payload.billTotalCents) };
    default:
      return none;
  }
};

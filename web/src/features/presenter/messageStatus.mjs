// How the phone panel words a message's status (Design §7.2, §18.7). Plain JavaScript with JSDoc types and no
// imports, so `npm test` (node --test) can test it directly; TypeScript reads the types through `allowJs`.

/** Every key under `phone.status` the panel can show; `locales.test.mjs` checks each has a label. */
export const statusKeys = /** @type {const} */ ([
  "sending",
  "sent",
  "delivered",
  "replaced",
  "expired",
  "senderFailed",
  "notSent",
]);

/** Every message purpose the API can send; `locales.test.mjs` checks each has a label under `phone.purpose`. */
export const purposes = /** @type {const} */ ([
  "confirmation",
  "reminder",
  "tableReady",
  "cancellation",
  "refund",
]);

/**
 * The label key under `phone.status` and its tone. `superseded` (an earlier "table ready" text replaced by a newer
 * Notify before it went out) is not an error, so it is grey (Design §7.2). Other failures are in plain words.
 * @param {string | null} status `queued`, `sent`, `delivered` or `failed`
 * @param {string | null} lastError the API's reason for a failure, e.g. `expired`, `superseded`, `sender_exception:…`
 * @returns {{ key: (typeof statusKeys)[number], tone: "muted" | "ok" | "error" }}
 */
export const messageStatus = (status, lastError) => {
  switch (status) {
    case "sent":
      return { key: "sent", tone: "ok" };
    case "delivered":
      return { key: "delivered", tone: "ok" };
    case "failed":
      if (lastError === "superseded") return { key: "replaced", tone: "muted" };
      if (lastError === "expired") return { key: "expired", tone: "error" };
      if (lastError?.startsWith("sender_exception")) return { key: "senderFailed", tone: "error" };
      return { key: "notSent", tone: "error" };
    default:
      return { key: "sending", tone: "muted" };
  }
};

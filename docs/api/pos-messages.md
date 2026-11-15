# POS message contract (v1)

This page is for the team that builds the POS side of the link. It describes every message the reservation system
exchanges with the POS: the purpose and direction of each message, its fields, an example, and the guarantees on
delivery order, duplicates and signatures.

The demo sends these messages to an in-process mock POS. Their shape and rules are the ones a real POS would get.
Design background: `docs/Design.md` §6 and §6.7. The JSON schemas are in `docs/api/openapi.json` under
`components.schemas`: `PosUpsertMember`, `PosOpenTable`, `PosApplyDeposit` and `PosBillClosed`.

## Messages at a glance

| Message | Direction | Sent when | Purpose |
| --- | --- | --- | --- |
| `UpsertMember` | to the POS | A booking is made by a guest who gave a mobile number | Create or update the member, matched by phone, before they arrive |
| `OpenTable` | to the POS | A party is seated (booking, walk-in or waitlist) | Open the table or tables for this booking |
| `ApplyDeposit` | to the POS | Right after `OpenTable`, when the booking has a paid deposit | Credit the deposit to the table's bill |
| `BillClosed` | from the POS | A bill is paid and closed | Complete the booking and record the bill total on the guest's profile |

## Envelope

Every message is one flat JSON object, encoded as UTF-8, with property names in camelCase. Times are ISO 8601 in UTC,
ending in `Z`. Money is an integer number of cents, GST inclusive. All IDs are UUIDs.

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `version` | integer | yes | Envelope version, currently `1`. New optional fields don't change it; a breaking change does. |
| `messageId` | UUID | yes | Unique per message. The receiver ignores a `messageId` it has already processed. |
| `messageType` | string | yes | `UpsertMember`, `OpenTable`, `ApplyDeposit` or `BillClosed`. |
| `venueId` | UUID | yes | The venue. |
| `bookingId` | UUID | to the POS: yes; `BillClosed`: optional | The booking the message is about. In `BillClosed`, the booking ID the POS received in `OpenTable` for that table, if the POS can echo it (see the open question below). |
| `tableIds` | UUID[] | yes | The tables concerned. Empty for `UpsertMember`. |
| `occurredAt` | date-time | yes | When the change happened (UTC). |

Fields that have no value are left out rather than sent as `null`.

## To the POS

### UpsertMember

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `memberPhone` | string | yes | Mobile number in E.164 format (`+6421…`): the member's key. |
| `name` | string | yes | The guest's name as stored (1–80 characters). |
| `language` | string | yes | `en` or `zh`. |

```json
{
  "version": 1,
  "messageId": "0193a7d2-5b1e-7c4a-9e10-2f7a3c1d8b40",
  "messageType": "UpsertMember",
  "venueId": "0193a7d0-0000-7000-8000-000000000001",
  "bookingId": "0193a7d2-5a90-7f02-b3c1-6d4e2a9f1c07",
  "tableIds": [],
  "occurredAt": "2026-11-24T05:25:00Z",
  "memberPhone": "+64210110291",
  "name": "Aroha Ngata",
  "language": "en"
}
```

### OpenTable

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `partySize` | integer | yes | Number of guests (1–20). |
| `guestName` | string | no | The name given with the booking. Absent for a walk-in who gave none. |
| `seatedAt` | date-time | yes | When the party was seated (UTC). |

`tableIds` lists every table the party sits at. `bookingId` is the ID the POS should keep with the open table, so that
it can echo it in `BillClosed`.

```json
{
  "version": 1,
  "messageId": "0193a7d3-0c44-7a8e-8d2b-41f0b7e6a913",
  "messageType": "OpenTable",
  "venueId": "0193a7d0-0000-7000-8000-000000000001",
  "bookingId": "0193a7d2-5a90-7f02-b3c1-6d4e2a9f1c07",
  "tableIds": ["0193a7d0-0000-7000-8000-000000000007"],
  "occurredAt": "2026-11-24T05:25:00Z",
  "partySize": 6,
  "guestName": "Aroha Ngata",
  "seatedAt": "2026-11-24T05:25:00Z"
}
```

### ApplyDeposit

Sent only for a booking whose deposit was paid online. It always comes straight after the `OpenTable` for the same
seating, with the next sequence number.

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `amountCents` | integer | yes | The deposit in cents (GST inclusive). |
| `currency` | string | yes | ISO 4217 code; `NZD`. |
| `paymentRef` | string | yes | The card payment's reference at the payment provider, for reconciliation. |

```json
{
  "version": 1,
  "messageId": "0193a7d3-0c45-7b10-a6f3-9e28d4c05b72",
  "messageType": "ApplyDeposit",
  "venueId": "0193a7d0-0000-7000-8000-000000000001",
  "bookingId": "0193a7d2-5a90-7f02-b3c1-6d4e2a9f1c07",
  "tableIds": ["0193a7d0-0000-7000-8000-000000000007"],
  "occurredAt": "2026-11-24T05:25:00Z",
  "amountCents": 12000,
  "currency": "NZD",
  "paymentRef": "pi_3QkT2bExample0001"
}
```

## From the POS

### BillClosed

The POS sends this when a bill is paid and closed: `POST {api}/api/v1/webhooks/pos`, with a signed JSON body (see
"Signature" below).

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `posBillId` | string | yes | The POS's own bill ID (1–100 characters), unique per venue. |
| `tableId` | UUID | yes | The table the bill belongs to. `tableIds` repeats it. |
| `billTotalCents` | integer | yes | The bill total in cents (GST inclusive). This is the bill's total, not the sum of its payments. |
| `tipCents` | integer | no | Tip in cents, if any. |
| `closedAt` | date-time | yes | When the bill was closed (UTC). |
| `bookingId` | UUID | no | The `bookingId` from this table's `OpenTable`, if the POS can echo it. |

```json
{
  "version": 1,
  "messageId": "6f1c2a0e-8b7d-4c3e-9a51-0d2e7f4b8c19",
  "messageType": "BillClosed",
  "venueId": "0193a7d0-0000-7000-8000-000000000001",
  "bookingId": "0193a7d2-5a90-7f02-b3c1-6d4e2a9f1c07",
  "tableIds": ["0193a7d0-0000-7000-8000-000000000007"],
  "occurredAt": "2026-11-24T07:10:00Z",
  "posBillId": "B-20261124-0042",
  "tableId": "0193a7d0-0000-7000-8000-000000000007",
  "billTotalCents": 31850,
  "tipCents": 2000,
  "closedAt": "2026-11-24T07:10:00Z"
}
```

**Responses**

| Status | Meaning | Should the POS retry? |
| --- | --- | --- |
| `200` | Accepted, including a repeat of a message already processed | No |
| `400` | Signed, but not a readable `BillClosed`, or for an unknown venue | No: fix the message |
| `401` | Signature missing, wrong or older than 5 minutes | No: fix the signature or clock; nothing was saved |
| `413` | Body larger than 64 KB | No |
| `5xx` | Our side failed; nothing was saved | Yes, with the same `messageId` |

A `200` does not mean the bill matched a booking. The reservation system matches it as follows (Design §6.5):

1. If the message carries `bookingId`, the bill goes to that booking.
2. Otherwise, to the booking seated at the table.
3. Otherwise, to the one booking at that table that a host cleared within 30 minutes of `closedAt`.
4. Otherwise, the bill is kept as unmatched for a manager to review.

## Guarantees

### Duplicates

| Direction | Rule |
| --- | --- |
| To the POS | Every send carries the header `Idempotency-Key: {eventId}:{messageType}`. A resend of the same message has the same key (and the same `messageId`). **The POS must ignore a key it has already accepted** and answer as if it had accepted it again. Resends happen after timeouts, and when our side stops after the POS accepted but before it recorded the send. |
| From the POS | A `messageId` already processed is acknowledged with `200` and changes nothing. A different message with a `posBillId` already recorded at the venue also changes nothing. A **different** bill for a booking that already has one is kept as unmatched, reason `second_bill`: one bill per booking for now. |

### Order

- Messages to the POS are numbered per venue (`venueSequence`, shown in the reservation system's POS log). The POS
  receives them strictly in that order: message *n + 1* is never sent before *n* has been accepted. Only one sender
  per venue is active at a time.
- For one seating, `OpenTable` always comes before `ApplyDeposit`.
- If the POS refuses a message for good (a permanent error), that venue's queue stops behind it until the message is
  replayed or removed, so later messages never get ahead of it.

### When the POS is unreachable

- Messages wait in the queue and are retried every 5 seconds. Waiting doesn't count as a failed attempt.
- They are delivered in order, once each, when the POS answers again.
- A message that still can't be delivered after 24 hours is marked failed.
- Temporary errors from a reachable POS are retried with backoff: 5 s, 30 s, 2 min, 10 min, then every 30 min, for
  up to 24 hours.

## Signature (BillClosed)

Each request carries a header:

```
Pos-Signature: t=1795930200,v1=5f2b1c…e9
```

- `t` is the time of signing in Unix seconds.
- `v1` is the lowercase hex HMAC-SHA256 of the string `{t}.{body}`, keyed with the shared webhook secret. `body` is the
  exact bytes sent.
- The request is refused unless `t` is within 5 minutes of our clock and a `v1` value matches. Comparison is in
  constant time. Several `v1` values may be sent, so a secret can be rotated without downtime.

The secret is exchanged out of band and stored in AWS Secrets Manager on our side. It is never sent in a message.

## Open question for the POS team

**Can the POS return the `bookingId` it received in `OpenTable` when it sends `BillClosed`?** If it can, rule 1 above
matches almost every bill exactly, and the time-based rules 3 and 4 become rare. The demo's mock POS does echo it.

Other points to settle for the pilot:

- How the POS authenticates our calls to it.
- Whether it confirms receipt (`Acknowledged`).
- Whether it supports split bills per table (`docs/Design.md` §6.6).

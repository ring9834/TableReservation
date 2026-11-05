# ADR-002: Event-driven Architecture

Nov 5, 2026 · @what's Parse Server

**Decision:** each state change in the reservation system writes a domain event to an outbox table, in the same PostgreSQL transaction as the change. A background worker then delivers the event to the live screens, guest messages, the POS adapter and the audit log. A message broker is not needed for the demo. It can be added later without changing the event contracts.

## Status and context

| Item | Value |
| --- | --- |
| Status | Proposed |
| Scope | Reservation demo, then pilot |
| Builds on | Demo requirements (steps 1–6, QA-1–QA-8) |
| Related | ADR-001 (technology selection), ADR-003 (data architecture: outbox, inbox and POS message tables), ADR-004 (cross-cutting concerns), ADR-005 (AWS hosting, to follow) |

The demo has six steps, and each step triggers work in other parts of the system. A paid booking must appear on the host's floor plan, send a confirmation text and, after seating, open a table in the POS. If the API made these calls directly, a slow text provider or an unreachable POS would block or break the booking. The requirements already name the events (`BookingCreated`, `TableSeated`, `WaitlistNotified` and others). This ADR sets out how they are defined, stored, delivered and changed over time.

## Decision drivers

| ID | Driver | Source |
| --- | --- | --- |
| ED1 | Floor plan and waitlist update on every host screen within 2 seconds | QA-1 |
| ED2 | No POS message is lost or applied twice, even if the POS is offline for hours | QA-5 |
| ED3 | A slow or failed side task (text, POS, email) never blocks or rolls back a booking | Demo step 1 |
| ED4 | New consumers (loyalty, reports, analytics) can be added without changing booking code | Evolution |
| ED5 | Every change can be traced: who did what, when, and what it caused | Audit, support |
| ED6 | A host screen that reconnects after a network drop catches up without errors | ADR-003 offline options |
| ED7 | One developer can build, test and run it in 2–3 weeks on AWS at low cost | ADR-001 D6, D7 |

## Options considered

Option C is chosen. It gives reliable, decoupled delivery using only PostgreSQL and the API process, and it keeps the move to an AWS broker open.

| Option | How it works | Real-time (ED1) | Reliable POS (ED2) | Decoupled (ED3, ED4) | Effort and cost (ED7) | Verdict |
| --- | --- | --- | --- | --- | --- | --- |
| A. Direct calls | The API calls SignalR, the text provider and the POS inside the request | Yes | No: a failed call is lost or rolls back the booking | No | Lowest | Rejected |
| B. In-process events only | MediatR-style handlers run in memory after the save | Yes | No: events are lost if the process restarts | Partly | Low | Rejected |
| **C. Domain events, outbox and worker** | Events saved in the same transaction; a hosted worker delivers them | Yes | Yes: stored, retried, idempotent | Yes | Low: no extra service | **Chosen** |
| D. AWS broker from day one | Outbox relays to SNS/SQS or EventBridge; consumers read queues | Yes | Yes | Yes, across services | Medium: more AWS resources, IAM and local emulation | Later, at pilot scale |
| E. Kafka (Amazon MSK) or event sourcing | Event log is the source of truth; state is rebuilt from events | Yes | Yes | Yes | High: cluster cost, projections and rebuilds | Rejected for now |

Option C still needs an outbox, but option D does too: without one, a crash between the database save and the broker publish loses the event. So the outbox is built once and kept whichever transport is used later.

## Decision and event flow

We will use option C. A command handler changes the booking, table or waitlist and adds the event to the outbox, both in one transaction. Either both are saved or neither is. A hosted `BackgroundService` in the same .NET process reads committed outbox rows in order and passes each one to its consumers.

&#91;embedded content: event flow · one transaction, one worker, four consumers, one inbox\]

The API only writes to the database, so a down text provider or POS cannot slow or undo a booking. Inbound POS messages are saved to the inbox first, so a repeated `BillClosed` is applied once.

- Code raises events through `IEventPublisher`. In the demo, its implementation writes to the outbox. Later, a relay can forward the same rows to SNS/SQS without changing handlers.
- Consumers are separate handler classes (`IEventHandler<T>`). Each has its own record of which events it has processed (ADR-003 inbox table).
- The POS is reached only through `IPosAdapter`. The demo uses a mock that writes to the on-screen POS message log.

## Event types and catalogue

The system uses three kinds of message, and only domain events are stored in the outbox. POS messages and screen updates are produced from them.

| Kind | What it is | Named as | Example | Stored in |
| --- | --- | --- | --- | --- |
| Domain event | A fact inside the reservation system | Past tense, `<Thing><Verb>ed` | `BookingSeated` | Outbox |
| Integration message | A command to or a fact from the POS | POS terms, present or past tense | `OpenTable`, `BillClosed` | POS message table (out), inbox (in) |
| UI notification | A small push telling host screens to refresh | camelCase | `floorPlanChanged` | Not stored; SignalR only |

### Domain event catalogue (v1)

| Event | Raised when | Key payload fields | Consumers |
| --- | --- | --- | --- |
| `BookingCreated` | A guest or host creates a booking (payment hold starts if a deposit is needed) | bookingId, partySize, startsAt, endsAt, channel, depositCents | Floor plan, audit |
| `DepositCaptured` | Stripe confirms the deposit | bookingId, paymentIntentId, amountCents | Booking status, notifications, audit |
| `BookingConfirmed` | The guest confirms from the reminder link | bookingId, confirmedBy | Floor plan, audit |
| `BookingCancelled` | The guest or host cancels, or the payment hold expires | bookingId, reason, cancelledBy | Floor plan, refund rule, notifications, audit |
| `DepositRefunded` | A refund succeeds under the cancellation rule | bookingId, refundId, amountCents | Notifications, POS (if deposit was applied), audit |
| `BookingSeated` | A party sits at a table, booked or walk-in | bookingId, tableIds, source (`booking` or `walkIn`), seatedAt | Floor plan, POS (`OpenTable`, `ApplyDeposit`), audit |
| `BookingCompleted` | The POS reports the bill closed | bookingId, billTotalCents, closedAt | Guest history, audit |
| `TableCleared` | A host marks a table free | tableId, clearedAt | Floor plan, waitlist, audit |
| `WaitlistJoined` | A walk-in party joins the waitlist | waitlistEntryId, partySize, quotedMinutes | Floor plan, audit |
| `WaitlistNotified` | The host sends “table ready” | waitlistEntryId, tableId | Notifications, audit |
| `WaitlistEnded` | The entry is seated, leaves or is removed | waitlistEntryId, outcome | Floor plan, audit |
| `MessageSent` | A text or email is accepted by the provider | messageId, purpose (`confirmation`, `reminder`, `tableReady`), channel, to (masked) | Message log, audit |

### Names changed from the demo requirements

| Demo requirements | ADR-002 | Why |
| --- | --- | --- |
| `TableSeated`, `WalkInSeated`, `BookingSeated` | `BookingSeated` with `source` | One event for one fact; a walk-in is a booking created at the door |
| `ConfirmationSent`, `ReminderSent`, `MessageSent` | `MessageSent` with `purpose` | One message log and one handler for all channels |
| (none) | `BookingCompleted` | The status diagram ends in Completed, so the change needs its own event |

The demo requirements doc should be updated to these names once this ADR is accepted.

## Event envelope

Every domain event uses the same envelope, so the worker, the audit log and any future broker can handle it without knowing the event type.

| Field | Type | Rule |
| --- | --- | --- |
| `eventId` | UUID v7 | Unique; time-ordered; used as the idempotency key |
| `eventType` | string | Name from the catalogue, e.g. `BookingSeated` |
| `eventVersion` | integer | Starts at 1; raised only for a breaking change |
| `occurredAt` | timestamp (UTC) | When the change was committed |
| `venueId` | UUID | Every event belongs to one venue (ADR-003 DM rules) |
| `aggregateType`, `aggregateId` | string, UUID | The record that changed, e.g. `Booking` and its ID; sets the delivery order |
| `sequence` | integer | Increases by 1 per aggregate; consumers can spot gaps |
| `correlationId` | UUID | Shared by everything caused by one guest or staff action; set from the request's trace context (ADR-004) |
| `causationId` | UUID or null | The event or command that caused this one |
| `actor` | object | `{ type: guest \| staff \| system \| pos, id, role }`; no names or phone numbers |
| `payload` | JSON object | The event's own fields; money in cents, times in UTC |

Example: a walk-in is seated at table 12.

```json
{
  "eventId": "01930f6e-8c2a-7b41-9d3e-5a2f1c0b7e11",
  "eventType": "BookingSeated",
  "eventVersion": 1,
  "occurredAt": "2026-11-20T06:42:10Z",
  "venueId": "0192ff00-1a2b-7c3d-8e4f-000000000001",
  "aggregateType": "Booking",
  "aggregateId": "01930f6e-7f10-7a22-8b33-1c2d3e4f5a6b",
  "sequence": 2,
  "correlationId": "01930f6e-7f0e-7d55-a111-9f8e7d6c5b4a",
  "causationId": null,
  "actor": { "type": "staff", "role": "Host", "id": "0192ff00-aaaa-7bbb-8ccc-000000000007" },
  "payload": {
    "tableIds": ["0192ff00-2b3c-7d4e-9f50-000000000012"],
    "partySize": 4,
    "source": "walkIn",
    "seatedAt": "2026-11-20T06:42:10Z"
  }
}
```

Payloads carry IDs, not personal details. A consumer that needs a guest's name or phone number reads it from the database, so deleting a guest under the retention rules (ADR-003) does not leave copies in old events.

## Delivery guarantees

Delivery is at least once, and every consumer is idempotent, so the result is effectively once. An exactly-once transport is not needed.

| Rule | How it works |
| --- | --- |
| At least once | The worker marks an outbox row as delivered only after every consumer succeeds. A crash before that means the row is sent again. |
| Idempotent consumers | Each consumer records `(consumer, eventId)` in the inbox table in the same transaction as its own change. A repeat is skipped. |
| Order | Events for one aggregate are delivered in `sequence` order. The worker claims rows with `FOR UPDATE SKIP LOCKED`, grouped by `aggregateId`, so two workers never handle the same booking at once. |
| No ordering across aggregates | A booking and a table may update in either order. Consumers never depend on that order. |
| Retries | A failed consumer is retried with backoff: 5 s, 30 s, 2 min, 10 min, then every 30 min for 24 hours. |
| Failed status | After the last retry the row is marked `failed`, shown on an admin page for the PlatformAdmin role (ADR-004) and logged as an error. Later events for that aggregate wait, so they are not applied out of order. |
| Replay | A platform admin can resend a failed row, or replay every event for a venue from a point in time, to rebuild a consumer. |
| Polling and wake-up | The worker polls every 1 s. After a commit, the API also signals the worker in process (a channel), so most events go out within 100–300 ms. PostgreSQL `LISTEN/NOTIFY` is an option if the worker moves to its own process. |
| Housekeeping | Delivered rows are kept 30 days, then deleted by a daily job. The audit log keeps its own copy under the ADR-003 retention rules. |

### POS messages

- **Outbound:** the POS consumer turns `BookingSeated` into `OpenTable` and `ApplyDeposit`, and a guest change into `UpsertMember`. Each message carries an idempotency key, `eventId` plus the message type, so the POS can ignore repeats. Messages wait in the POS message table while the POS is offline and are sent in order when it returns (QA-5).
- **Inbound:** `BillClosed` from the POS has its signature checked (ADR-004) and is saved to the inbox with the POS's own message ID before anything else happens. A handler then completes the booking and raises `BookingCompleted`. A repeated or late `BillClosed` is skipped.
- **Mock in the demo:** the mock adapter accepts messages, can be set to fail so retries can be shown, and writes every message to the on-screen POS log (demo step 6).

## Versioning

Events can gain fields freely, but they never lose or change one within a version.

1. **Adding an optional field** keeps the same `eventVersion`. Consumers ignore fields they do not know.
2. **Renaming, removing or changing the meaning of a field** is a breaking change. The event gets a new version, e.g. `BookingSeated` v2.
3. **During a change**, the publisher writes the new version and consumers accept both for one release. Old outbox rows are upcast to the new version when read.
4. **Event contracts** live in one shared C# project, with a JSON schema test per event. A test fails if a v1 field is removed.
5. **POS message formats** follow the POS's own versioning. The adapter maps domain events to them, so a POS change does not change domain events.

## Real-time host screens

SignalR pushes a small notice and the screen fetches the new state. The push never carries the full floor plan.

- **Groups:** each host screen joins the group `venue:{venueId}` after sign-in. The hub uses the same Cognito token as the API and checks the venue claim (ADR-004).
- **Messages:** `floorPlanChanged`, `waitlistChanged` and `messageLogChanged`, each with the IDs that changed and the event's `sequence`.
- **Refetch:** the screen calls the API for the changed tables or entries. The API is the only source of truth, so a lost push costs one refresh, not wrong data.
- **Reconnect:** after a network drop, the screen reconnects automatically and fetches the whole floor plan once. This is the demo's offline behaviour (ADR-003 option A).
- **Scale:** one API instance needs no backplane. With two or more, an ElastiCache (Redis) backplane is added (ADR-005).
- **Guest pages** do not use SignalR. The booking page polls the payment status every 2 s while it waits for Stripe.

## Path on AWS

Each stage keeps the outbox, the envelope and the catalogue; only the transport behind `IEventPublisher` changes. All of it stays in AWS.

| Stage | Transport | Move on when |
| --- | --- | --- |
| Demo (Lightsail containers) | Outbox and in-process worker in the API container | — |
| Pilot (ECS on Fargate) | Same, with the worker as its own ECS service so it scales and restarts apart from the API | More than one API instance, or worker load slows requests |
| Several venues | Outbox relay publishes to an SNS topic; one SQS queue (with a dead-letter queue) per consumer | Separate teams or services need the events, or one consumer's failures must not hold up others |
| Platform scale | Amazon EventBridge for routing to other products, or Amazon MSK if long event replay and streaming analytics are needed | Many event producers across the company's products |

## Consequences

**Good**

- A booking succeeds even when the text provider or POS is down; side tasks catch up on their own.
- No POS message is lost or applied twice, which the demo can show by failing the mock POS and restoring it.
- New features (loyalty, reports, analytics) subscribe to existing events without changes to booking code.
- The envelope's correlation and causation IDs give a full trace of each guest action for support and audit.
- No broker to run or pay for in the demo, and the whole flow runs in Testcontainers tests.

**Costs and risks**

- Host screens are eventually consistent: a change shows within 2 s, not instantly. Screens refetch to stay correct.
- Every consumer must be idempotent and must record processed events. This is extra code, covered by tests that send each event twice.
- Polling adds a small, constant database load. At demo scale it is negligible; it is checked again at the pilot.
- Failed rows need someone to look at them. The admin page and an alert on any `failed` row cover this.
- The team must keep the catalogue and schemas current. A pull request that adds an event updates this ADR's catalogue.

## Revisit when

- The pilot runs more than one API or worker instance (check locking and the SignalR backplane).
- Outbox delivery takes longer than 2 s at the 95th percentile, or the outbox grows past 1 million rows.
- A second product or team needs to consume reservation events (move to SNS/SQS or EventBridge).
- The POS provides its own event stream or webhook format that changes the inbox design.
- Audit or reporting needs to rebuild past state from events (consider event sourcing for that part only).

## Open questions

- How does the POS deliver `BillClosed`: webhook, polling its API, or a file export?
- Does the POS accept an idempotency key on `OpenTable` and `ApplyDeposit`, or must the adapter check first?
- Who watches failed events in the pilot: the venue, the support team, or the developer?

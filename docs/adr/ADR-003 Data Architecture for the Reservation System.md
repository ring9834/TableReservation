# ADR-003: Data Architecture for the Reservation System

Nov 5, 2026 · @what's Parse Server

Decision: store all data in one PostgreSQL database on Amazon RDS, shared by all venues and separated by venue ID; prevent double bookings in the database itself; and give the host app a small offline cache in the browser rather than a full local database.

## Status and context

| Item | Detail |
| --- | --- |
| Status | Proposed |
| Decision owner | Antonio Huang |
| Applies to | The demo and the pilot; production unless a revisit trigger fires |
| Builds on | ADR-001 (technology selection); ADR-002 (event-driven architecture) |
| Related | ADR-004 (cross-cutting concerns); ADR-005 (AWS hosting and deployment), to follow |
| Inputs | Demo requirements analysis (sections 7–9); business requirements N-02, N-04, N-10–N-12, F-74–F-76 |

ADR-001 chose PostgreSQL as part of the overall stack. This record explains that choice against the main alternatives and sets the rules for how data is modelled, protected, kept and made available when a host's connection drops. These rules shape the database schema, which is the hardest part of the system to change later.

## Data decision drivers

| ID | Driver | Source |
| --- | --- | --- |
| DD1 | No double bookings, even when two guests choose the last table at the same moment | N-04, QA-1 |
| DD2 | A booking, its payment and its events are saved together or not at all | QA-5, step 6 |
| DD3 | Many venues in one system, each seeing only its own data | F-52, D-07 |
| DD4 | Flexible settings and message payloads without frequent schema changes | Section 7 rules, step 6 |
| DD5 | Host app keeps working through short internet drops | N-02, F-64, AC2.5 |
| DD6 | Guest privacy: sensitive allergy data, deletion on request, limited retention | N-10, N-12, F-61, F-76 |
| DD7 | Low cost for the demo and pilot, with room to scale | Constraint: cost |
| DD8 | All data stays within AWS | ADR-001 D10 |
| DD9 | Reports on covers, no-shows and walk-aways from the same data | F-49, F-75 |

## Database choice

Bookings, tables, guests and payments are strongly related and must change together, so a relational database is the natural fit. The real contest is PostgreSQL against SQL Server; MySQL, DynamoDB and MongoDB are included for completeness.

| Criterion | PostgreSQL | SQL Server | MySQL | DynamoDB | MongoDB / Amazon DocumentDB |
| --- | --- | --- | --- | --- | --- |
| Preventing overlapping bookings (DD1) | Built in: an exclusion constraint on table and time range | Needs locking or checks in code | Needs locking or checks in code | Needs careful key design and conditional writes | Needs checks in code |
| Transactions across records (DD2) | Full | Full | Full | Limited multi-item transactions | Supported, less natural |
| Flexible JSON data (DD4) | Strong: indexed JSONB | Good: JSON functions | Good: JSON type | Native documents | Native documents |
| Per-venue data separation (DD3) | Venue ID plus optional row-level security | Venue ID plus row-level security | Venue ID only | Venue ID in keys | Venue ID in documents |
| Reporting with ad hoc queries (DD9) | Strong SQL | Strong SQL | Good SQL | Weak; needs exports | Moderate |
| Cost on AWS (DD7) | No licence fee; small Amazon RDS instances are low cost | Licence included in RDS pricing; the free Express edition has size and feature limits | No licence fee | Pay per request; very cheap at low volume | DocumentDB has a higher starting cost |
| Managed on AWS (DD8) | Amazon RDS and Aurora PostgreSQL | Amazon RDS for SQL Server | Amazon RDS and Aurora MySQL | Fully managed | Amazon DocumentDB |
| Support in .NET (Entity Framework Core) | Mature open-source provider (Npgsql) | First-party provider | Community provider | No EF Core provider; separate SDK | Official provider, document-focused |
| Developer experience | Used recently (MyTodos, BrewQueue) | Many years of use | Limited | Limited | Limited |

**Outcome:** choose **PostgreSQL on Amazon RDS**.

- **Why not SQL Server:** it is a strong database and the developer knows it well, but it costs more on AWS once past the limited Express edition. It also has no built-in way to stop two bookings overlapping on the same table, which PostgreSQL handles with one constraint.
- **Why not DynamoDB or MongoDB:** booking data is relational, and both make transactions, overlap checks and reporting harder.
- **Why not MySQL:** it offers no clear advantage over PostgreSQL here, and its .NET support is weaker.

## Data model and modelling rules

&#91;embedded content: core records · 10 entities\]

The record list and key fields are in the demo requirements analysis (section 8). The rules below apply to every table.

| ID | Rule | Reason |
| --- | --- | --- |
| DM-01 | One shared database for all venues; every record carries a venue ID, and every query filters by it | Simple and low-cost; PostgreSQL row-level security can enforce it later (DD3) |
| DM-02 | Identifiers are time-ordered UUIDs (version 7), created by the application | Safe to create offline or in parallel; sort by creation time; no guessable numbers |
| DM-03 | Money is stored as whole cents in NZD, GST-inclusive | No rounding errors when reconciling with POS bills |
| DM-04 | Times are stored in UTC and shown in the venue's time zone | Correct across daylight saving changes |
| DM-05 | Phone numbers are stored in international format (+64…) and used as the guest match key | Matching guests with POS VIP members (G-06) |
| DM-06 | Statuses (booking, waitlist, payment, message) are fixed lists, stored as text | Readable in reports and safe to extend |
| DM-07 | Every record has a row version for optimistic concurrency | Two hosts editing the same booking cannot overwrite each other silently |
| DM-08 | Venue settings and message payloads use JSONB columns | Rules and payloads change without schema changes (DD4) |
| DM-09 | Every change to bookings, deposits and refunds is written to an audit log with user and time | Disputes and chargebacks (N-20) |
| DM-10 | The schema changes only through EF Core migrations kept in the repository; a seed script loads the demo data and resets it | Repeatable setup and the demo reset button |

## Preventing double bookings

The database, not only the application code, guarantees that a table is never booked twice for overlapping times. Checks in code alone can fail when two requests arrive at the same moment.

| Layer | How it works | What it prevents |
| --- | --- | --- |
| 1. Overlap constraint | Each booking stores its table and a time range (start to start + turn time). A PostgreSQL exclusion constraint rejects any two active bookings for the same table whose ranges overlap. Walk-ins are stored as bookings too, so they are covered | Two guests getting the same table at overlapping times, even at the same millisecond |
| 2. Pacing check | The covers-per-slot limit (BR-03) is checked inside the same transaction, while holding a short database lock for that venue and time slot | Too many guests arriving in one 15-minute slot |
| 3. Payment hold | A booking that needs a deposit is saved as "pending payment" and holds its table for 10 minutes. If payment does not succeed in time, a background job releases it | Losing the table while the guest pays, or a table held forever by an abandoned payment |
| 4. Friendly failure | If the constraint rejects a booking, the API returns "this time was just taken" and offers the nearest available times (AC1.3) | A confusing error for the guest |

**Active statuses** for the overlap constraint are pending payment, booked, confirmed and seated. Cancelled and completed bookings no longer hold the table.

**Test:** an automated test sends two booking requests for the last table at the same moment, against a real PostgreSQL database in Testcontainers; exactly one must succeed (N-04).

## Events, outbox and POS message data

Every change that others must hear about (a booking, a seat, a deposit) writes an outbox event in the same database transaction as the change itself. Either both are saved or neither is, so no event is ever lost or invented (DD2).

| Record | Purpose | Key fields |
| --- | --- | --- |
| Outbox event | Events waiting to be delivered | event ID, venue ID, event type, payload (JSONB), created at, processed at, attempts, last error |
| Inbox message | Messages received from the POS, e.g. "bill closed" | message ID (unique), venue ID, type, payload, received at |
| POS message | Messages sent to or received from the POS, for the log and for audit | message ID, event ID, direction, type, payload, status, delivered at |

**Delivery rules**

- A background worker reads unprocessed events in creation order, locking each batch so two workers never handle the same event.
- Each outgoing message carries a unique message ID, so the POS can ignore duplicates; incoming messages are checked against the inbox the same way.
- Failed deliveries are retried with increasing delays. After a set number of attempts, an event is marked "failed" and shown to staff rather than retried forever.
- Processed outbox events are deleted after 30 days; POS messages are kept as long as the bookings they relate to (see privacy and retention).

This design matches ADR-002 (event-driven architecture) and ADR-001: the transport can later move to a message broker, or Kafka, without changing the tables that business logic writes to.

## Offline support: whether to use a local database

The cloud database stays the single source of truth. A full local database is not used, because online guest bookings keep arriving in the cloud while a host is offline, and merging two copies safely is complex and error-prone. Instead, the host app gets a small cache in the browser for the pilot.

| Option | How it works | Benefit | Cost and risk | Decision |
| --- | --- | --- | --- | --- |
| A. Reconnect and refresh | When the connection returns, the app reloads current state and SignalR resumes | Simplest; no data stored on the device | Host screen is unusable during the outage | **Demo** |
| B. Browser cache with action queue | A read-only copy of today's bookings is kept in the browser (IndexedDB). Host actions made offline are queued and sent when back online | Host can keep seating and clearing tables through short drops | Some conflicts to handle; guest data held on the device | **Pilot** |
| C. Full local database with two-way sync | A complete database on each device syncs with the cloud | Works offline for long periods | Complex conflict resolution; more guest data on devices; much longer build | Not chosen |
| D. Local server at the venue | A small server on site holds the data and syncs to the cloud | Whole venue keeps working when the internet is down | Hardware, installation and support at every venue | Not chosen; revisit if the partner's POS integration needs it |

**Rules for the pilot cache (option B)**

- **Works offline:** viewing tonight's bookings, seating and clearing tables, adding walk-ins and waitlist entries. Offline changes are marked "pending sync".
- **Needs a connection:** new online bookings, payments and refunds, text messages.
- **On reconnect:** queued actions are sent in order, each with a unique ID so none is applied twice. The server's data wins any conflict; the host sees what could not be applied (e.g. "table 7 was booked online") and chooses what to do.
- **Data on the device:** only today's bookings for that venue, with the minimum guest details needed; cleared at the end of the day and on logout.
- **Last resort:** the printable day sheet (F-64) covers longer outages.

## Privacy, retention and security of data

Guest data is kept only as long as it is useful, and allergy details are treated as sensitive. The periods below are proposals to confirm with the partner and a privacy adviser before the pilot; they are stored as settings, not fixed in code.

| Data | Proposed retention | Reason |
| --- | --- | --- |
| Waitlist contact details | 30 days, then anonymised; counts kept for reports | Used only to tell the guest their table is ready (F-76) |
| Guest profiles and booking history | Until the guest asks for deletion, or 3 years after their last visit | Recognising regulars; NZ Privacy Act principle of keeping data no longer than needed |
| Allergy and dietary notes | With the guest profile; never used for marketing; visible only to staff roles that need it (ADR-004) | Safety at service; sensitive information (N-12) |
| Payments, refunds and invoices | 7 years | NZ tax record-keeping |
| Text and email messages | 12 months | Support and disputes |
| Audit log | 7 years for payment-related entries; 2 years for others | Disputes and chargebacks (N-20) |
| Processed outbox events | 30 days | Troubleshooting only |

Application logs and traces hold no personal data (ADR-004), so they fall outside these rules. Their retention period is set in ADR-005.

**Deletion on request:** the guest's name, contact details and notes are removed, while payment records keep an anonymised reference, so financial records stay complete (F-61).

**Security controls**

- Encryption at rest (Amazon RDS encryption with AWS KMS) and in transit (TLS).
- No card data stored: only Stripe payment references (N-09).
- Separate database users: the application cannot change the schema; migrations use their own user.
- Database passwords and keys held in AWS Secrets Manager; details in ADR-005.
- Automatic backups with point-in-time recovery; details in ADR-005.
- When a booker enters other people's details (e.g. a guest list), the privacy notice shown is recorded with the booking (IPP3A).
- The demo uses only fictional data.

## Decision summary

| Topic | Decision |
| --- | --- |
| Database | PostgreSQL on Amazon RDS, accessed through EF Core (Npgsql) |
| Multi-venue | One shared database; venue ID on every record; row-level security considered for the pilot |
| Double bookings | Exclusion constraint on table and time range; pacing checked under a short lock; 10-minute payment holds |
| Flexible data | JSONB for venue settings and message payloads |
| Events | Outbox and inbox tables in the same database; ordered, once-only delivery by a background worker |
| Offline | Demo: reconnect and refresh. Pilot: read-only browser cache plus queued host actions. No full local database |
| Privacy | Retention periods as settings; anonymise on deletion; allergy notes restricted; encryption at rest and in transit |
| Schema changes | EF Core migrations in the repository; seed and reset script for the demo |

## Consequences

**Positive**

- Double bookings are prevented by the database itself, which is the strongest guarantee available.
- No database licence cost, and the database runs on the partner's cloud platform.
- Bookings, payments and events stay consistent because they are saved in one transaction.
- The host app stays useful through short internet drops in the pilot without the cost of full offline sync.

**Negative or to manage**

- The overlap constraint uses PostgreSQL-specific features, which makes a later move to another database harder.
- One shared database means every query must filter by venue; a missed filter could expose another venue's data. Mitigate with a global query filter in EF Core and tests, then row-level security.
- The browser cache stores some guest data on host devices; keep it minimal and clear it daily.
- Retention periods need confirmation before real guest data is stored.

**Follow-ups**

- [ ] Confirm retention periods with the partner and a privacy adviser.
- [ ] Ask whether the partner's POS uses a local server at venues, and whether integration should go through it.
- [ ] Define the event catalogue and payload formats in ADR-002 (event-driven architecture).
- [ ] Choose instance sizes, backups and the AWS region in ADR-005 (hosting and deployment).

## When to revisit this decision

| Trigger | What to reconsider |
| --- | --- |
| The partner requires SQL Server or another database for all its systems | The database choice; replace the overlap constraint with locking in the application |
| Venues need to keep working through long internet outages | Option C or D in the offline section |
| The partner's POS integration runs through a local server at each venue | A local relay at the venue for POS messages (option D) |
| Hundreds of venues, or reporting starts to slow down bookings | A read replica for reports, Aurora PostgreSQL, or a separate reporting store |
| A large venue group asks for its data to be kept separate | A separate database or schema per group |
| Event volume or the number of event consumers grows sharply | Move event delivery to a message broker or Kafka, keeping the outbox |

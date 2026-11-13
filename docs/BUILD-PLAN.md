# Build plan: Reservation Demo

Build in this order. Each milestone ends with green build and tests, a commit (or PR), and a short check against the listed acceptance criteria. Target: 3–4 weeks (about 17 working days).

If we slip, trim in this order:

1. The PWA install prompt.
2. Chinese text on the presenter screen.
3. The M12 rehearsal, reduced to two clean runs.

The six demo steps are never cut.

| # | Milestone | Design sections | Proves | Est. |
| --- | --- | --- | --- | --- |
| M0 | Repository skeleton and CI | §1, §9 | Everything builds and tests run in CI, with dependency checks | 0.5 d |
| M1 | Domain model and unit tests | §2, §3, §4 | Rules and state machines are right before any I/O | 1.5 d |
| M2 | Database, migrations and seed | §12, §13 | Schema, exclusion constraint, Golden Dragon data | 1 d |
| M3 | Outbox, dispatcher, inbox | §5 | Events are saved with the change and delivered once | 1.5 d |
| M4a | Booking and availability API | §9, §11, §14 | Step 1 through the API without payment; `slot_taken`; idempotency; demo sign-in; OpenAPI document and `oasdiff` in CI | 1 d |
| M4b | Guest booking page | §18 | Step 1 on the phone in English and Chinese, without payment; orval client; first Playwright test | 1 d |
| M5a | Real-time API | §15, §18 (server side) | SignalR hub, ID-only notices, floor-plan read; a booking reaches a connected client within 2 s | 1 d |
| M5b | Host screens | §18 | Step 2: host sign-in page, floor plan, tonight's list, reconnect; multi-screen Playwright (QA-1) | 1 d |
| M6a | Walk-in and waitlist API | §3.1, §3.2, §11.3, §11.4, §14, §20 | Step 3 and the step 4 waitlist through the API; booking contact name and bilingual area names; one Playwright sign-in per run | 1 d |
| M6b | Host screens: walk-ins, waitlist, seat, clear | §18 | Step 3 on the host tablet; Playwright for step 3 | 1 d |
| M7 | Messaging and phone panel | §7, §7.1 | Step 4: "table ready" text | 1 d |
| M8 | Stripe deposits and refunds | §8 | Step 1 with deposit; step 5 refunds | 1.5 d |
| M9 | Mock POS and POS log | §6 | Step 6, incl. POS offline (QA-5) | 1.5 d |
| M10 | Reminders and guest links | §7 (`GuestLinkSigner`), §9.2 | Step 5, BR-13 | 1 d |
| M11 | Demo controls, reset, i18n polish | §17 | Demo script runs end to end | 1 d |
| M12 | Deploy to AWS and rehearse | ADR-005 | Public URL; container image scan in CI; QA-6 three clean runs | 1.5 d |

## Prompts to paste into Claude Code

Start each milestone in **plan mode** (Shift+Tab until it shows "plan mode"), read the plan, then let it build.

### M0 Repository skeleton and CI
```
Read CLAUDE.md, docs/Architecture.md §5 and docs/Design.md §1 and §9.
Create the solution skeleton exactly as in the CLAUDE.md layout:
- .NET 10 solution with src/Api, src/Domain, src/Contracts, src/Infrastructure,
  tests/Unit, tests/Integration (xUnit; Testcontainers for PostgreSQL).
- Directory.Build.props: nullable enabled, warnings as errors, central package management.
- web/: Next.js + TypeScript with output 'export', ESLint, Prettier. Empty pages for /book, /demo-login, /host, /presenter.
- docker-compose.yml with PostgreSQL 16 only (the mock POS runs inside the API).
- GitHub Actions workflow: dotnet build + test, web lint + type-check + build,
  `dotnet list package --vulnerable` and `npm audit --audit-level=high` (a high finding fails the build).
- Dependabot for NuGet, npm and GitHub Actions.
- No tests/E2E yet (it starts in M4).
- .gitignore for .NET, Node and secrets; .editorconfig.
- /health/live and /health/ready endpoints with one integration test.
Do not add any feature code yet. Show me the plan first.
```

### M1 Domain model
```
Implement docs/Design.md §2, §3 and §4 in src/Domain only (no EF Core, no ASP.NET):
AggregateRoot with Raise() and LastEventSequence, the value objects, Booking (incl. BookingTerms
snapshot, BookingBills, AttachLateBill), Table, WaitlistEntry, Guest, the policies
(DepositRule, RefundPolicy, late-match rule) and all domain events. Use TimeProvider for time.

Also:
- Result<T>/Error live in src/Domain, with Error kinds that map to the ADR-004 codes. Invalid
  transitions return Error.Conflict("invalid_transition"). A transition to the current state is a
  no-op success that raises nothing (Design §3.1).
- Include the D7 rule: CaptureDeposit on a Cancelled(HoldExpired) booking returns `hold_expired`.
  On a PendingPayment booking past expiry, it captures if the table is free, and otherwise cancels
  with a full refund decision.

Tests (tests/Unit):
- One test class per aggregate. Name tests after the rule, e.g. Seat_WhenCancelled_ReturnsInvalidTransition.
- Cover every row of the §3 tables, including each rejected transition, and BR-02, BR-04, BR-06,
  BR-07 and BR-13.
- Check that each event is raised once, with LastEventSequence going up by exactly 1.
```

### M2 Database and seed
```
Start milestone M2 (database, migrations and seed). Implement docs/Design.md §12 and §13 in
src/Infrastructure with EF Core + Npgsql. Domain stays free of EF Core.

Schema and mapping:
- Every table in §13, including outbox, inbox, pos_messages, messages and booking_bills (M3 adds
  their behaviour; create the tables now). Include cancel_reason, refund_decision and
  last_event_sequence.
- snake_case names, timestamptz in UTC, money as bigint cents, enums stored as text (DM-06),
  BookingTerms and venue settings as jsonb, strongly typed IDs and value objects through
  converters or owned types.
- xmin as the concurrency token on every aggregate (maps to Version).
- The exclusion constraint on table_assignments (needs the btree_gist extension, created in the
  migration), the unique index on booking_bills(booking_id), the unique index on
  booking_bills(venue_id, pos_bill_id), and the global venue query filter.
- Add private parameterless constructors to the aggregates for EF Core; no public setters.

Migrations and start-up (D8):
- Add a dotnet-tools.json with dotnet-ef pinned. One initial migration.
- In Development only, the API applies migrations and the seed at start-up when
  Database:MigrateOnStartup=true. Add `dotnet ef database update` to the CLAUDE.md commands.
- CI: add a step that fails if the model has changes with no migration
  (`dotnet ef migrations has-pending-model-changes`).

Seed (Golden Dragon, Requirements §10):
- One venue, its areas and 14 tables with floor positions, the BR-01 to BR-07 settings, and 11
  bookings for "tonight" placed relative to the current time (TimeProvider), so the seed works on
  any day. Bilingual guest names are fine; phone numbers must be fictional (+64 21 0xx xxxx style).
- The seeder drives the domain with its own TimeProvider stepped through the evening: bookings
  made the day before at 12:00 Auckland, deposits captured a minute later, seated bookings seated
  at their start time. The seed must fit the six demo steps at 18:25 (Design §17 seed table).
- A DemoReset service that truncates venue data and reseeds in one transaction (§17); no endpoint yet.

Integration tests (Testcontainers):
- Migrations apply to an empty database and the seed loads (14 tables, 11 bookings).
- Two concurrent bookings for the last table at the same time: exactly one succeeds; the other
  fails with 23P01.
- A stale update fails with a concurrency error (xmin).
- Round-trip: save and reload a Booking with terms, deposit, assignments, reminder and a bill;
  everything (including LastEventSequence) comes back equal.
- The venue filter hides another venue's bookings.
- DemoReset leaves exactly the seed data.

Ask before adding any package not already listed (e.g. EFCore.NamingConventions).
Record any schema detail §13 left open in Design.md in the same change set.
```

### M3 Event infrastructure
```
Start milestone M3 (event infrastructure). Implement docs/Design.md §5 in src/Infrastructure,
using the outbox, inbox and audit tables M2 created. Domain stays unchanged.

Writing side:
- OutboxSaveChangesInterceptor collects RaisedEvents from every tracked AggregateRoot, wraps each
  in the src/Contracts envelope (ADR-002) using the sequence stamped by Raise(), takes
  correlationId, causationId and actor from IRequestContext, adds the outbox rows to the same
  transaction, then clears the aggregates' events.
- IRequestContext: a simple implementation for now (new correlationId per scope; actor = system or
  the fixed demo host); M4 fills it from the HTTP request and trace context.
- After a successful commit, signal the dispatcher through an in-process channel so most events
  go out within 100–300 ms.

Two clocks:
- Business time: the default TimeProvider (replaced by DemoClock in M11). Used for occurredAt and
  anything domain-related.
- Infrastructure time: a keyed TimeProvider ("infrastructure"), registered as TimeProvider.System
  and never replaced by DemoClock. Used by OutboxDispatcher (poll timeout, next_attempt_at,
  dispatched_at, claim query "now"), OutboxRetryPolicy, OutboxSignal.WaitAsync, OutboxHousekeeping,
  OutboxHealthCheck, outbox.created_at and inbox.processed_at.

Dispatching side:
- OutboxDispatcher as a BackgroundService: polls every 1 s or when signalled; claims a batch with
  FOR UPDATE SKIP LOCKED; per aggregate, only the lowest undelivered sequence is eligible, so a
  failing event holds back later events for that aggregate only.
- For each event, every registered IEventHandler<T> runs inside its own savepoint in the
  dispatcher's transaction, wrapped by IdempotentHandlerDecorator (inbox row (consumer, eventId)
  written in the same savepoint). A failing handler rolls back its savepoint only.
- The outbox row is marked delivered only when every handler has succeeded; otherwise attempts+1
  and next_attempt_at by the ADR-002 backoff (5 s, 30 s, 2 min, 10 min, then every 30 min for 24 h),
  then status failed, logged as an error. Use the infrastructure clock for all of this timing.
- Housekeeping: delete delivered outbox rows older than 30 days (daily).
- Add a test-only recording handler and an AuditHandler that writes one audit_log row per
  event (unique on event_id), so M3 has a real consumer. Other consumers come in later milestones.
- Add the outbox lag check to /health/ready (ADR-004).

Integration tests (Testcontainers, TestClock for each clock):
- Event saved in the same transaction as the state change; a rolled-back save leaves no outbox row.
- Sequences in the outbox match LastEventSequence; a second save continues from it.
- Delivering the same event twice has one effect per handler (inbox).
- A failing handler doesn't undo the other handlers' work; its retry follows the backoff; after the
  last retry the row is failed and later events for that aggregate stay pending, while other
  aggregates still flow.
- Business clock frozen, infrastructure clock advancing: a failing handler is retried on schedule
  and polling still runs.
- Two dispatchers running at once never deliver the same row twice and keep per-aggregate order.
- Seed data produces no outbox rows; DemoReset empties outbox and inbox.
- The interceptor logs no personal data (names, phone numbers) — check the captured log output.

Record any detail §5 left open in Design.md in the same change set.
```

### M4a Booking API
```
Start milestone M4a (booking API; the web page is M4b). Split M4 in docs/BUILD-PLAN.md into
M4a (this prompt) and M4b (guest booking page with orval, react-i18next and the first Playwright
test), and keep the other milestones' numbers.

Implement the Bookings and availability slice from docs/Design.md §9, §11 and §14:
- Endpoints: availability search, create booking (online, guest, public) and phone booking (host),
  tonight's bookings for the host, confirm and cancel by host. Seat, walk-ins, guest links and
  payments come in later milestones; an online party of 6+ is created as PendingPayment with the
  deposit due, and capture arrives in M8.
- Pipeline in the ADR-004 order: central IExceptionHandler and Problem Details (code, traceId),
  logging and tracing, rate limiting, demo session authentication, venue check, FluentValidation
  endpoint filter, handler. Result<T> → HTTP through ResultToHttp: invalid_transition and
  hold_expired 409, rule_broken 422 with the specific code, slot_taken 409 (23P01 and pacing),
  version_conflict 409 (DbUpdateConcurrencyException), not_found 404, validation_failed 400.
- Pacing under pg_advisory_xact_lock per venue and slot, inside the create transaction (§12).
- Responses carry `version`; updates must send it back.
- Idempotency-Key on create: follow the design if it says where keys are stored; otherwise propose
  a table (24 h retention, infrastructure clock) in the plan.
- Demo sign-in (D3): POST /demo-login with a passcode → HttpOnly, Secure, SameSite=Strict cookie
  with role Host or Presenter, 12 h; constant-time compare; 5 attempts a minute per IP; passcodes
  from configuration (user secrets locally). Policies Host, Presenter; endpoints deny by default.
- Replace DemoVenueContext with a session-based IVenueContext (public guest endpoints take the
  venue from the route) and fill IRequestContext from the HTTP request and trace context.
- CORS: http://localhost:3000 in Development, app.<domain> later, with credentials.
- OpenAPI with the built-in Microsoft.AspNetCore.OpenApi. Commit the generated document to
  docs/api/openapi.json; CI fails if it differs from the build output, and runs oasdiff against
  main to flag breaking changes (D12).

Integration tests (WebApplicationFactory + Testcontainers):
- Every error code each endpoint can return, with the Problem Details shape.
- AC1.x without payment; BR-01, BR-04, BR-05 through the API.
- Two simultaneous requests for the last table: one 201, one 409 slot_taken.
- Same Idempotency-Key twice returns the first result and creates one booking.
- Stale version → 409 version_conflict.
- Demo sign-in: wrong passcode 401, rate limit 429, guest can't call host endpoints (401/403),
  host can't call presenter endpoints (403).
- A created booking produces an outbox row with the request's correlationId and actor.
- Logs contain no names, phone numbers or request bodies.

Ask before adding packages beyond FluentValidation. Record decisions §9/§14 left open in Design.md.
```

### M4b Guest booking page
```
Start milestone M4b (guest booking page) from docs/BUILD-PLAN.md, using the M4a API.

Dev setup:
- Run next dev over HTTPS (--experimental-https) so the SameSite=Strict cookie reaches
  https://localhost:5001; change Development Cors:AllowedOrigins to https://localhost:3000.
  Close the Design §20 localhost item.

API client:
- orval generates TanStack Query hooks and zod schemas from docs/api/openapi.json (the committed
  file, not a running API) via `npm run generate:api`. Commit the generated code; CI fails if
  regenerating changes it.
- Fetch wrapper: base URL from NEXT_PUBLIC_API_BASE_URL, credentials 'include', X-Correlation-Id per
  request, Problem Details parsed into a typed error with `code`.
- Add the FluentValidation → OpenAPI schema transformer deferred from M4a (required, lengths,
  ranges), regenerate docs/api/openapi.json, and let zod pick the rules up.

/book page (Design §18, Requirements step 1, AC1.x), mobile first, done in under a minute:
- Party size, date and time → availability; when the time is taken, show the 3 nearest slots
  (AC1.3). Party over 8: "please call us" (BR-04).
- Details: name, NZ mobile, optional email, occasion, allergies, notes; react-hook-form with the
  generated zod schema.
- Submit with an Idempotency-Key generated once per submission and reused on retry.
- Confirmation with the reference. A party of 6+ shows the deposit due and the hold expiry;
  payment itself comes in M8, so show a clear "payment step coming next" placeholder.
- Each API error code (slot_taken, party_too_large, outside_service_hours,
  outside_booking_window, validation_failed, too_many_requests, unexpected) maps to a translated
  message; slot_taken reloads availability.
- Times shown in Pacific/Auckland; labels, keyboard use and focus handling for accessibility.

Languages:
- react-i18next with English and Simplified Chinese; every string in translation files; a language
  switch on the page; the active language is sent with the booking. A test fails if a key exists
  in one language but not the other.

Playwright (tests/E2E):
- Book a party of 2 for tomorrow 19:30 in English; book in Chinese and check the confirmation text
  is Chinese; a party of 9 shows "please call us". Use tomorrow so the test doesn't depend on the
  time of day.
- CI job: PostgreSQL service, API (migrations + seed, demo passcodes from CI-only values), the
  built static site served locally, then Playwright. Upload the report on failure.

Ask before adding packages beyond orval, @tanstack/react-query, zod, react-hook-form,
@hookform/resolvers, i18next, react-i18next and @playwright/test.
Record decisions §18 left open in Design.md.
```

### M5a Real-time API
```
Start milestone M5a (real-time server side; the host screens are M5b). Split M5 in
docs/BUILD-PLAN.md into M5a (this prompt) and M5b (host sign-in page, floor plan, tonight's list,
reconnect, multi-screen Playwright), and keep the other milestones' numbers.

Implement docs/Design.md §15 and the server side of §18, ADR-002 "Real-time host screens":
- SignalR hub at /hubs/venue, Host policy via the demo session cookie. The server puts the
  connection in group venue:{venueId} from the venue_ids claim; the client never chooses its venue.
  No access_token in the query string (the cookie is enough for the demo; ADR-004 notes the pilot).
- RealtimeNotifier: event handlers registered unwrapped (idempotent: false) that turn domain
  events into small notices: floorPlanChanged {tableIds, bookingIds, sequence}, waitlistChanged,
  messageLogChanged (the last two can be stubs until M6/M7). Notices carry IDs only, no names.
  A failed push is logged and must not fail the event (screens refetch anyway).
- GET /api/v1/venues/{venueId}/floor-plan (Host): areas, tables (name, seats, shape, position),
  and each table's current state from TableStateRule at business-clock "now", with a short
  summary of the booking on it (reference, party, time, guest name, status). Tonight's
  bookings endpoint stays as it is.
- Latency: signal the outbox dispatcher on commit (already done), so a booking reaches a
  connected screen well within 2 s (QA-1).
- Local Playwright: add a Playwright project that uses channel 'msedge' when not in CI, because
  Chrome crashes under automation on this laptop; CI keeps Linux Chromium. Note it in CLAUDE.md.

Tests:
- Integration (Microsoft.AspNetCore.SignalR.Client in tests only): anonymous connection rejected;
  host joins only its venue's group; a booking made through the API produces floorPlanChanged on a
  connected client within 2 s, with no personal data in the notice; another venue's client gets
  nothing; a throwing SignalR push doesn't leave the outbox row pending.
- Floor plan endpoint: seed at 18:25 gives the expected state per table (Design §17 seed table).

Ask before adding packages beyond Microsoft.AspNetCore.SignalR.Client (tests).
Record decisions §15 left open in Design.md.
```

### M5b Host screens
```
Start milestone M5b (host screens) from docs/BUILD-PLAN.md, using the M5a API.

Fixed business clock, brought forward from M11:
- Configuration Demo:Clock:Mode (System | Fixed) and Demo:Clock:FixedLocalTime (e.g. 18:25):
  in Fixed mode the business TimeProvider returns today (Auckland date at start-up) at that
  local time and doesn't move. Development and CI E2E use Fixed 18:25; tests keep their
  TestClocks. The infrastructure clock is never affected (two-clock rule). Presenter controls,
  offset mode and reset come in M11. Record in Design §17.

Host sign-in (/demo-login):
- Passcode form; on success go to /host (host) or /presenter (presenter, empty page until M7);
  wrong passcode and too_many_requests shown translated. Pages under /host redirect to
  /demo-login on 401.

Host app (/host), Design §18 and ADR-001 floor plan:
- Floor plan as SVG React components with react-zoom-pan-pinch: tables in their positions and
  shapes, state shown by colour AND a text label or icon (QA-7), seated time or next booking time,
  truncated guest name. Tap a table → side panel with its current/next booking. Fit to screen on a
  tablet; usable on a phone.
- Tonight's bookings list in time order beside or below the plan (AC2.1).
- Live updates: @microsoft/signalr with withAutomaticReconnect, credentials included; on
  floorPlanChanged invalidate the floor-plan and tonight queries. A visible "reconnecting" banner
  while disconnected, then one full refetch on reconnect (AC2.5).
- English and Chinese for every label and status; layout editor and table combining stay out of
  scope (pilot).

Playwright (tests/E2E):
- Two host browser contexts signed in, plus a guest context. The guest books tonight 20:00 for 2;
  both host screens show it on the floor plan and in tonight's list within 2 s (AC2.1, AC2.4).
- One host context goes offline (context.setOffline) for 10 s: the "reconnecting" banner shows; a
  booking made meanwhile appears within 5 s of coming back online (AC2.5).
- Wrong passcode shows an error; /host without a session goes to /demo-login.
- CI runs over http://localhost: check that the __Host- Secure session cookie is accepted there.
  If not, propose the smallest fix (e.g. a CI-only cookie setting) instead of weakening production.

Ask before adding packages beyond @microsoft/signalr and react-zoom-pan-pinch.
Record decisions §18 left open in Design.md.
```

### M6a Walk-in and waitlist API
```
Start milestone M6a. Split M6 in docs/BUILD-PLAN.md into M6a (this prompt: catch-up items and
the walk-in/waitlist API) and M6b (host screens for walk-ins, waitlist, seat and clear, plus
Playwright for step 3), and keep the other milestones' numbers.

Catch-up items first (Design §20, "start of M6"):
1. Booking contact name: each booking stores the name given when it was made; host screens and
   tonight's list show it. The guest profile keeps its stored name. Domain, migration (backfill
   existing bookings from the guest name), API responses, generated client.
2. Area names in English and Chinese: areas carry both names (seed + migration for the existing
   Golden Dragon areas); the floor-plan response returns both and the screen picks by language.
3. Playwright: sign in once in a global setup and share the session (storageState) across spec
   files; booking.spec.ts cancels the bookings it creates, like host.spec.ts.

Then the API (Design §3.1 Seat/Complete, §3.2, §11.3, §11.4; Requirements step 3 and AC4.1, AC4.2,
AC4.5, AC4.6), Host policy, business clock throughout:
- Walk-in: suggest the smallest free table for a party now (BR-05, the availability calculator),
  host may pick another (AC3.1, AC3.2); seat creates a walk-in booking and raises BookingSeated.
- Seat a booked guest (AC3.3) and clear a seated table (Complete with ClearedByHost, TableCleared;
  "Bill paid" via the mock POS comes in M9).
- Waitlist: join with name, mobile, party size and a quoted wait from BR-08 that the host can
  adjust (AC3.4, AC3.5); list active entries; notify (status and event only; the text is M7);
  seat from the waitlist (creates the walk-in booking and ends the entry as seated); walked away;
  remove (AC3.7). Overdue flag after the notified-party timeout (AC4.6, BR-10).
- When a table frees, compute the suggested waiting party (BR-09, §11.4) and fill
  waitlistChanged's suggestedEntryId and freedTableId (AC4.2).
- Seat and seat-from-waitlist take the same table locks as booking creation (lock order from M4b),
  and every update checks version (409 version_conflict).
- OpenAPI updated, client regenerated.

Tests: every endpoint's success and error codes; the seed at 18:25 gives step 3's outcomes (walk-in
for 2 → a 2-seat table; walk-in for 4 → no table, so waitlist with a quote); clearing T4 suggests the
waiting party of 4; a walk-in and a booking racing for one table give one success and one
slot_taken; waitlistChanged and floorPlanChanged notices carry IDs only; logs have no names or
mobiles.

Ask before adding packages. Record decisions §3.2/§11 left open in Design.md.
```

### M6b Host screens: walk-ins, waitlist, seat and clear
```
Start milestone M6b (host screens for walk-ins, waitlist, seat and clear) from docs/BUILD-PLAN.md,
using the M6a API. Requirements step 3 and AC4.1, AC4.2, AC4.5, AC4.6 on screen.

- Walk-in: a "Walk-in" button → party size → the suggested table pre-selected with the other free
  tables as choices → Seat. At most three taps for the suggested table (AC3.1, AC3.2). When nothing
  fits, the dialog offers "Add to waitlist" with the party size carried over.
- Seat a booked guest from the table panel or tonight's list with one tap (AC3.3); "Seat at another
  table" lists free fitting tables. slot_taken and version_conflict refresh the data and say so.
- Clear a seated table from its panel (AC4.1). "Bill paid" through the POS comes in M9, so only Clear
  for now.
- Waitlist panel: join with name, mobile, party size, the guest's language (English / 中文,
  default the screen's language) and the quote pre-filled from the API but editable (AC3.4, AC3.5);
  entries in join order with time waited and quote; Notify (records the notification; the text
  arrives in M7, so label it plainly), Seat (choose a table, defaulting to the notified or suggested
  one), Walked away, Remove (AC3.7); an overdue badge (AC4.6).
- Suggestions: on waitlistChanged with suggestedEntryId, highlight that entry and the freed table on
  every host screen (AC4.2); after a reconnect, ask GET /waitlist?tableId= so the highlight returns.
- Every action sends the version it was shown; all text in English and Chinese; works on a tablet
  and a phone; accessible (labels, focus, keyboard).

Playwright (step 3, using the shared session):
- Walk-in for 2 is seated on the suggested table in ≤ 3 taps, and appears on a second host screen.
- Walk-in for 4 finds no table → added to the waitlist with the quote (35 min at 18:25).
- Clear T4 → both host screens highlight the waiting party of 4 and T4; seat them there; the entry
  leaves the list.
- Each test cleans up what it created (cancel bookings; end waitlist entries) so reruns stay clean.

Ask before adding packages. Record decisions §18 left open in Design.md.
```

### M7a Messaging back end
```
Milestone M7a: messaging back end (Design §3.4, §7, §7.1, §10.3, §15, §16; Requirements AC1.6, AC4.3, AC4.4).
API, workers and tests only; the presenter phone panel and the host screen change come in M7b.
Read CLAUDE.md first, including rule 14 (two clocks).

1. Docs first. In docs/BUILD-PLAN.md add an "M7a" prompt block containing this prompt (minus the last
   two lines) above "M7 onward", and rename that heading "M8 onward". Note decisions in Design.md as a
   new §7.2 Build notes (M7a) table.

2. Creating messages. A NotificationHandler (an outbox handler, inbox-idempotent, own savepoint) creates
   Message rows as Queued; it never calls a sender.
   - BookingCreated → Confirmation, only for online and phone bookings created as Booked (not walk-ins,
     not seated-from-waitlist, not PendingPayment; deposit bookings get theirs on DepositCaptured in M8).
     No manage-booking link yet: that is M10 (GuestLinkSigner, token in the query string).
   - WaitlistNotified → TableReady, to the entry's guest, in the language chosen when joining (M6b).
   - MessageComposer + ITemplateStore: per purpose and language, English and Simplified Chinese, in the
     API's resource files. Language comes from the booking/entry, not the screen. Keep bodies short, SMS-like;
     include the venue name; TableReady says the table is ready and to come to the host stand.
   - Say what a repeated Notify does (same table, different table) and test it.

3. Delivering messages: MessageDeliveryWorker as §7.1 (signal or 1 s, LIMIT 20, FOR UPDATE SKIP LOCKED,
   backoff 5 s/30 s/2 min/10 min, expiry by purpose, permanent errors Failed at once, message ID passed as
   the provider reference). IMessageSender with PhonePanelSender for the demo.
   - Two clocks: the worker's loop, claiming due rows, next_attempt_at, expires_at and the delivery delay use
     the "infrastructure" clock. With the business clock fixed at 18:25, texts must still go out, retry and
     expire. Times people see (sent at, delivered at, the waitlist "time sent") come from the business clock.
     Compare against a clock value passed in, not SQL now(), so tests control it.
   - PhonePanelSender marks Delivered about 1 s after Sent. No Task.Delay fire-and-forget: e.g. the worker
     also picks up Sent rows whose delivery is due. Delivered survives a restart.
   - MarkSent raises MessageSent through the outbox. Every status change pushes messageLogChanged {messageIds}
     to venue:{id}, only after the transaction commits. Handlers signal the worker after commit too.
   - A TableReady that ends Failed is flagged on the waitlist entry (§7.1 Visibility).
   - Never log bodies, names or phone numbers: message ID, purpose, status, attempts only.

4. API (regenerate docs/api/openapi.json and the orval client):
   - GET /venues/{venueId}/messages: Presenter role. Today's messages, newest last, at most 100: id, purpose,
     language, body, masked mobile, status, sentAt, deliveredAt, lastError for failed, subject (booking or
     waitlist entry ID). Check the presenter session has the venue_ids claim and joins the hub group.
   - Waitlist entries (GET /waitlist) gain the TableReady message's status and sentAt, plus the failed flag
     (AC4.4). Additive only.
   - Demo reset and ResetIfStale also clear messages. The seed creates no messages.

5. Tests first (Testcontainers, TestClock with manual timers):
   - Composer: every purpose used here × en/zh-CN; no missing template.
   - Handlers: each event delivered twice → one message; walk-ins and PendingPayment get no confirmation.
   - Worker: Queued → Sent → Delivered with the business clock fixed; backoff steps; expiry (TableReady
     10 min); permanent error → Failed at once with no retry; two workers in parallel never send a message
     twice; a crash after send then retry reuses the same provider reference; messageLogChanged only after
     commit.
   - End to end in the API: create an online booking → Confirmation Delivered; notify a zh-CN waitlist entry →
     Chinese TableReady, and GET /waitlist shows its status and sentAt.
   - Log scrubbing: extend LogScrubbingTests so no body or full number reaches the logs.

Verification: dotnet build, dotnet test, web lint/type-check/build with the regenerated client
(M6b screens must still compile), and the Playwright suite still green.
```

### M7b Presenter phone panel and Notify on the host screen
```
Milestone M7b: phone panel and Notify on the host screen (Design §7, §7.2, §15, §17, §18;
Requirements AC1.6, AC4.3, AC4.4; demo script steps 3 and 9). Front end + E2E + docs only. M7a
shipped the API (GET /venues/{id}/messages, waitlist tableReady* fields, messageLogChanged). No API
change expected: if one turns out to be needed, stop and ask. No new packages.
Read CLAUDE.md first. Never commit secrets or put a passcode in the repo.

1. Docs first. In docs/BUILD-PLAN.md add an "M7b" prompt block with this prompt (minus the last two
   lines) after the M7a block. Note decisions in Design.md as a new §18.7 Build notes (M7b) table.

2. Presenter screen (/presenter/):
   - Signing in at /demo-login with the presenter passcode lands on /presenter/; the host passcode
     still lands on /host/. Check how M5b does this; if the redirect by role is missing, add it. A
     host session opening /presenter/ (403 from the API) shows a clear "presenter only" message, not
     an error page. English / 中文 switch as on the host screen.
   - Layout for a laptop or projector: phone panel on the left (about 380 px wide, drawn as a phone);
     the rest of the page is left free for the POS log (M9) and the demo controls (M11). No
     placeholder text for those.

3. Phone panel (features/presenter/PhonePanel.tsx):
   - useGetMessages(venueId); messageLogChanged invalidates it; reconnect invalidates everything
     (reuse useVenueConnection with an onMessageLogChanged option, as M6b did for waitlistChanged).
   - Shown like an SMS app: oldest at the top, newest at the bottom, auto-scroll to the newest unless
     the presenter has scrolled up. Each bubble: "To +64 21 *** 456" (masked, as the API sends it),
     a small purpose label (Confirmation / Table ready), the body exactly as sent (never translated),
     the time in the venue's zone from the response's timeZone, and the status:
     Sent ✓, Delivered ✓✓, Failed with its reason in plain words. lastError "superseded" shows as
     "Replaced" in grey, not as an error (Design §7.2); "expired" shows "Not sent (too late)".
   - A new message is visible within 2 s of the event (AC1.6, AC4.3); it briefly highlights.
   - role="log" with polite announcements; empty state "No messages yet".

4. Host screen:
   - "Mark notified…" becomes "Notify…": it picks the table as before and now sends the text. Drop
     the hint "texts come in a later version".
   - Waitlist entry, from the API fields (AC4.4): "Notified 18:26 · Sent" / "· Delivered"; a failed
     text shows "Text not sent: call the guest" (colour + text), unless it was superseded by a newer
     one. Overdue badge unchanged.
   - Every new string in en.json and zh-CN.json.

5. Playwright:
   - global-setup also signs in a presenter (E2E_PRESENTER_PASSCODE, same handling as the host one;
     mind the 5/min demo-login limit) and saves a second storageState. Add the variable to the CI E2E
     job the same way E2E_HOST_PASSCODE is supplied there, and to the README/E2E notes.
   - booking.spec: with a presenter page open, the English booking's confirmation appears in the phone
     panel within 2 s with its reference; the Chinese booking's confirmation is in Chinese.
   - walk-in.spec, step 4: the party of 4 joins in 中文; after T4 is cleared, Notify on the entry
     (T4 pre-selected) → the phone panel shows the Chinese "table ready" text within 2 s and both host
     screens show "Notified … · Delivered"; then seat them. Keep the stand-in set-up and clean-up.
   - Don't hard-code bodies: match on the reference / venue name and a few words from the template.

Verification: dotnet build and dotnet test (unchanged API), web lint/typecheck/format:check/npm test/
build, generate:api leaves src/api unchanged. Playwright: I run it myself with both passcodes set;
don't read user secrets. Manual look in English and 中文.
```

### M8 onward
Use the same pattern: "Implement docs/Design.md §X for milestone Mn in docs/BUILD-PLAN.md. List the acceptance criteria it covers, write the tests first, then the code."

### M12 Deploy and rehearse
Also add a container image scan of the API image to the CI pipeline (ADR-005).
Use the Route 53 demo domain for both (`app.<domain>` for the front end, `api.<domain>` for the API); the default cloudfront.net/amazonlightsail.com hostnames won't work with the demo session cookie (Design §17, §20).
Also from Design §14.1 and §20: `ForwardedHeaders` trusting only the known proxy, so rate limits see the real client IP; `Demo:Enabled=true` with both passcodes from Secrets Manager in the demo environment; Data Protection keys that survive container restarts; `Cors:AllowedOrigins` set to `https://app.<domain>`; OpenTelemetry export to X-Ray.

## Definition of done (every milestone)

- [ ] `dotnet build` and `dotnet test` pass locally and in CI
- [ ] `npm run lint`, type-check and build pass in `web/`
- [ ] The listed acceptance criteria are checked off in the PR description
- [ ] No partner names, no personal data in logs, no secrets in the repo
- [ ] Any agreed design change is written into `docs/Design.md` in the same PR

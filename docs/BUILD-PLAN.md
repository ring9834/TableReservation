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
| M11 | Demo controls, reset, i18n polish (M11a: demo clock, presenter controls, polish) | §16, §17 | Demo script runs end to end | 1 d |
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

### M8a Payments back end
```
Milestone M8a: payments back end (Design §2.6, §3.1, §3.3, §5.5, §8, §10.1, §10.4, §14; Requirements
AC1.5, AC5.5 (API side), BR-06, BR-07; ADR-004 CC2). API, workers, tests. The guest payment step and the
host cancel button come in M8b. Read CLAUDE.md first, including rule 14 (two clocks).

0. Package: ask me before adding Stripe.net (official). Nothing else new.

1. Docs first: add an "M8a" block with this prompt (minus the last two lines) to BUILD-PLAN.md after
   M7b, and rename "M8 onward" to "M9 onward". Decisions go in a new Design §8.4 Build notes (M8a).

2. Configuration: Payments:Provider = Stripe | Fake. Stripe needs Stripe:SecretKey and
   Stripe:WebhookSecret (user secrets locally, Secrets Manager in AWS), validated at start-up with a
   clear message naming the missing setting. Fake is used by integration tests and CI (no Stripe keys
   there). Never log keys, client secrets or webhook payloads; extend LogScrubbingTests.

3. Taking a deposit (AC1.5, BR-06): online bookings of 6+ only (phone bookings take none). The amount
   comes from booking.Terms.Deposit, in NZD cents.
   - Save the booking (PendingPayment, BookingCreated) and COMMIT first. Never call Stripe while holding
     the pacing advisory lock or table locks.
   - Then IPaymentProvider.CreateDepositIntent with Stripe idempotency key "deposit:{bookingId}",
     currency nzd, metadata bookingId + venueId + reference; store payment_intent_id on the booking.
   - 201 returns clientSecret and holdExpiresAt. A replay with the same Idempotency-Key must return the
     same clientSecret (the stored response must not hold the secret; recreate it idempotently on
     replay). Stripe down → 502 payment_provider_error; the booking stays PendingPayment and its hold
     expires normally. Say how the guest can retry.
   - GET /venues/{venueId}/bookings/{id}/status (guest, anonymous, rate-limited): status only, nothing
     personal.

4. Payment hold: measure the 10 minutes in real time, i.e. on the infrastructure clock, so a hold
   still expires while the business clock is fixed at 18:25 and the guest's browser countdown matches.
   PaymentHoldExpiryJob (30 s, infrastructure clock) calls ExpireHold → Cancelled (reason HoldExpired),
   deposit Released, table freed, floorPlanChanged. Document this in §8.4 as a rule-14 decision.

5. Webhook POST /webhooks/stripe (§5.5): anonymous, exempt from the anonymous rate limit, body-size
   limit, signature verified with Stripe:WebhookSecret (bad signature → 401, nothing saved). Accept
   events in the account's API version (with Stripe.net: don't throw on an API-version mismatch; read
   only the fields we need). Inbox row consumer 'stripe-webhook' + Stripe event ID; a repeat → 200,
   no change. Report the exact path including any base path, for `stripe listen --forward-to`.
   - payment_intent.succeeded → CaptureDeposit (find the booking by metadata bookingId, check the intent
     ID and amount match). Hold expired but table still free → capture. Hold expired and table taken →
     cancel the booking and refund in full (§8.1 CaptureDepositHandler). Amount mismatch → recorded,
     no capture.
   - charge.refunded → RecordRefund → deposit Refunded, DepositRefunded.
   - Other event types → 200, ignored, inbox row kept.

6. Cancelling and refunds (BR-07, §8.3): if a host cancel endpoint doesn't exist yet, add
   POST /venues/{venueId}/bookings/{id}/cancel {version} (Host). Booking.Cancel decides the refund once
   via RefundPolicy and booking.Terms; BookingCancelled carries it. RefundOnCancelHandler (outbox,
   inbox-idempotent) calls Refund only when the decision is full, Stripe idempotency key
   "{eventId}:refund"; Stripe errors → handler retry with backoff. The deposit becomes Refunded only
   when charge.refunded arrives. The guest cancel link is M10 and must reuse the same Cancel.

7. Texts (extends M7a NotificationHandler, en + zh-CN templates): DepositCaptured → the Confirmation
   (deposit bookings get no confirmation at BookingCreated); BookingCancelled by guest or host →
   Cancellation (none for HoldExpired); DepositRefunded → Refund with the amount. Bodies short, no
   links yet (M10).

8. Tests first (FakePaymentProvider; Stripe webhooks signed in tests with a test secret):
   - Deposit amount per party size 1–8; phone booking of 6 takes none; terms are the booking's, not
     the venue's current settings.
   - Stripe never called while locks are held; Stripe failure → 502 and the hold still expires;
     replayed create → same intent, same clientSecret, one intent.
   - Webhooks: bad signature 401; duplicate event once; succeeded → Booked + Confirmation; late success
     with table free → Booked, with table taken → Cancelled + refund requested; amount mismatch; unknown
     type 200; API-version mismatch still parsed.
   - Hold expiry with the business clock fixed and only the infrastructure clock moved.
   - Refund decisions: guest ≥ 2 h full, < 2 h kept, host always full, hold expired not applicable;
     handler run twice → one refund call, same key; refund recorded only on charge.refunded.
   - Messages: Confirmation after capture, Cancellation, Refund, in en and zh-CN.

Verification: dotnet build, dotnet test, has-pending-model-changes, refresh openapi.json +
generate:api, web lint/typecheck/build. Playwright: I run it. Don't read user secrets.
```

### M8b Guest payment step, host cancel and refund status
```
Milestone M8b: guest payment step, host cancel and refund status (Design §8, §8.4, §10.1, §18;
Requirements AC1.5, AC1.7, AC1.8, AC5.5 on screen; ADR-004 CC2 card details only in Stripe's frame).
Mostly front end + E2E; the small API additions below are allowed. Read CLAUDE.md first.

0. Packages: ask me before adding @stripe/stripe-js and @stripe/react-stripe-js (official). Nothing else.

1. Docs first: an "M8b" block with this prompt (minus the last two lines) after M8a in BUILD-PLAN.md;
   decisions in a new Design §18.8 Build notes (M8b).

2. API additions (regenerate openapi.json + orval client):
   - GET /api/v1/payments/config (anonymous): {provider: "stripe" | "fake", publishableKey?}. The
     publishable key comes from Stripe:PublishableKey (required when the provider is Stripe, start-up
     message naming it like the others). One web build then works in every environment.
   - The status endpoint also returns holdSecondsRemaining (worked out by the server on the
     infrastructure clock), so the countdown doesn't depend on the guest's own clock.
   - Simulate payment, only when Payments:Provider = Fake AND Demo:Enabled = true (otherwise not
     mapped): POST /api/v1/demo/payments/{bookingId}/succeed builds a payment_intent.succeeded event
     for that booking's intent, signs it with Stripe:WebhookSecret and sends it through the SAME
     webhook processing as /webhooks/stripe (signature check, inbox, CaptureDeposit). Not a shortcut
     that sets Booked directly. Likewise the Fake's RefundAsync must lead to a signed charge.refunded
     through the same path (after commit, never inside the request), so a refund reaches Refunded in
     CI too. Integration tests for both, plus a test that the route is absent with the Stripe provider
     and with Demo:Enabled false.
   - Floor plan and tonight's list items gain depositCents and depositStatus (additive) for the host.

3. Guest payment step (features/booking): after the details step, a 201 with clientSecret shows the
   payment step instead of the current "Payment step coming next" box.
   - Header: "Deposit NZ$120.00 for 6 guests" and a countdown "We'll hold your table for 9:41" from
     holdSecondsRemaining (refreshed by each status poll, ticking locally between polls). Never show a
     clock time for the hold: the demo story's time differs from real time. At 0: "Your hold has
     ended" + "Start again", no form.
   - Stripe provider: Stripe Elements PaymentElement with the clientSecret, appearance from our colour
     tokens, Stripe locale following the page language (en / zh). "Pay NZ$120.00" →
     stripe.confirmPayment({redirect: "if_required", return_url: /book/?bookingId=…}). Handle coming
     back from a redirect (3-D Secure) on /book/: read bookingId and Stripe's payment_intent params,
     go straight to polling. Card errors show Stripe's message.
   - Fake provider: no card form; a clearly labelled "Simulate payment (test mode)" button calling the
     demo endpoint. A plain note says no real card is taken.
   - Then poll GET status every 2 s (ADR-002) until Booked: confirmation screen with reference and
     "Deposit paid NZ$120.00". Cancelled (HoldExpired) → "Your hold ended before payment; any payment
     will be refunded". Still waiting after 60 s → "Still confirming your payment…" with the reference.
   - 502 payment_provider_error on create → "Payment service unavailable" + "Try again", which resends
     the same request with the same Idempotency-Key (M8a replay).
   - Every new string in en and zh-CN; keyboard and screen-reader friendly (the countdown announces
     only at 5 min, 1 min and 0, not every second).
   - (Agreed addition) Disable "Pay NZ$…" and "Simulate payment" while a payment is in progress (label
     "Paying…"), so a double tap can't confirm twice, and hide the payment form and buttons as soon as
     the countdown reaches 0. Cover both in the payment.mjs logic or the Playwright spec.

4. Host screen:
   - A PendingPayment booking shows as held: tile status "Awaiting deposit" / 待付订金 (colour + text,
     its own legend entry), and the same in tonight's list.
   - "Cancel booking" on a Booked/Confirmed/PendingPayment booking in the table panel and tonight's
     list. Destructive, so it opens a confirm dialog ("Cancel 6 guests at 7:00 pm? The deposit of
     NZ$120.00 will be refunded in full."), then POST …/cancel {version}. Errors through
     useHostActionError.
   - Deposit line wherever the booking shows: "Deposit NZ$120.00 · Paid" / "· Refund pending" /
     "· Refunded" / "· Kept (cancelled late)" / "· Released". Refund pending → Refunded arrives live
     (floorPlanChanged), no reload.
   - Cancelled bookings stay in tonight's list, greyed, with their deposit line.

5. CSP: if the app or API sets a Content-Security-Policy, allow js.stripe.com and Stripe's frames and
   API, nothing wider (ADR-004).

6. Playwright (deposit.spec.ts), reads /payments/config:
   - Fake (CI): book 6 tonight → countdown visible, no clock time → "Simulate payment" → confirmation
     with "Deposit paid" within 5 s → the host shows it Booked with "Paid" and the phone panel shows
     the Confirmation → host cancels it → "Refund pending" then "Refunded" on the host, Cancellation
     and Refund texts on the phone panel.
   - Stripe (my machine): same flow, filling Stripe test card 4242 4242 4242 4242 in the
     PaymentElement iframe; needs `stripe listen` running, so skip with a clear message if no webhook
     arrives within 15 s.
   - Clean-up: cancel anything the spec booked that is still active.
   Add Payments__Provider: Fake and Demo__Enabled: true to the CI e2e env if not already there.

Verification: dotnet build/test, has-pending-model-changes, web lint/typecheck/format:check/test/
build, generate:api. Playwright and the manual checks: I run them. Don't read user secrets or .env files.
```

### M9a Mock POS back end
```
Milestone M9a: mock POS back end (Design §3.5, §5.5, §6.1–§6.6, §10.5, §13, §14, §15; Requirements
§6 AC6.3–AC6.6, QA-5). API, workers, tests, docs. The POS log panel, the online/offline control and
the host "Bill paid" button come in M9b. Read CLAUDE.md first, including rules 1, 3, 9 and 14.

1. Docs first: an "M9a" block with this prompt (minus the last two lines) after M8b in BUILD-PLAN.md,
   rename "M9 onward" to "M10 onward"; decisions in a new Design §6.7 Build notes (M9a).

2. Outbound messages (§6.3): PosProjectionHandler (outbox, inbox-idempotent) only INSERTS pos_messages
   rows (Waiting); it never calls the adapter. BookingCreated with a guest → UpsertMember;
   BookingSeated → OpenTable, and ApplyDeposit after it when the deposit is Captured (same event,
   OpenTable first). Idempotency key "{eventId}:{MessageType}".
   - Every payload has one envelope (AC6.3): messageId, messageType, venueId, bookingId, tableIds
     (empty for UpsertMember), occurredAt, plus the type's fields. Version the envelope (v1).
   - venue_sequence must be allocated safely under concurrent handlers (e.g. a per-venue counter row
     updated with RETURNING, or an advisory lock), never max()+1. Inbound messages use the same counter.

3. Delivery (§6.2, §6.4, AC6.5, QA-5): PosDeliveryWorker on the INFRASTRUCTURE clock (signal or 1 s;
   5 s retry while unavailable). Per venue, strictly in venue_sequence order: never send n+1 before n is
   Sent. Only one sender per venue at a time even with two API instances (advisory lock per venue or
   equivalent); test it. Unavailable doesn't count as an attempt for 24 h. Say what a permanent
   rejection does to the queue (block or skip) and record it in §6.7. Send with the idempotency key; a
   crash after the adapter accepted and before Sent is saved must not deliver twice at the POS.
   Every status change pushes posLogChanged {posMessageIds} only after commit (as M7a did for messages).

4. MockPosAdapter: online/offline state stored in the database (survives restarts; demo reset sets it
   online), not in memory. It records each idempotency key it accepted and ignores repeats, and keeps
   which booking is open on which table (from OpenTable) so BillClosed can echo bookingId (§6.5 step 1).
   PUT /api/v1/demo/pos/online {online} (Presenter; only when Demo:Enabled).

5. Inbound BillClosed (§5.5, §6.5): POST /api/v1/webhooks/pos, signed (Pos:WebhookSecret; user secrets
   locally, a "not a secret" value in CI), inbox consumer 'pos-webhook' + POS message ID, stored as an
   inbound pos_messages row. One shared PosWebhookProcessor used by the endpoint and by
   POST /api/v1/demo/pos/bill-paid {tableId, totalCents} (Host or Presenter; only when Demo:Enabled), which
   builds a signed BillClosed from the mock's open-table record and passes it through the SAME
   processor (signature, inbox, CloseBill). No shortcut that completes bookings directly.
   - CloseBillHandler, exactly §6.5 steps 1–4 with LateBillMatchMinutes from venue settings (business
     clock times). Duplicate posBillId ignored; a different second bill → Unmatched 'second_bill';
     two or more late candidates → Unmatched 'ambiguous'. Completion raises BookingCompleted and
     TableCleared and records the bill; the guest's visit and LastBillTotal update (AC6.6).
   - Manual match/dismiss (Manager) are pilot: don't build them; Unmatched rows just show in the log.

6. API: GET /api/v1/venues/{venueId}/pos-messages?status (Presenter), newest first, at most 200:
   id, direction, messageType, status, venueSequence, attempts, createdAt, deliveredAt, bookingId,
   tableIds, unmatchedReason, and the payload. The payload shown has phone numbers masked like the phone
   panel (stored unmasked for the POS). GET …/pos/status for the switch state. Demo reset and
   ResetIfStale clear pos_messages, the counter and the mock's state. The seed creates no POS messages.

7. Partner contract: docs/api/pos-messages.md describing every message (UpsertMember, OpenTable,
   ApplyDeposit, BillClosed): purpose, direction, envelope, fields with types, an example JSON, the
   idempotency and ordering guarantees, signature scheme, and the open partner question from §6.5
   (echo bookingId). Neutral wording: "the POS". Add the payload schemas to openapi.json components.

8. Tests first (Testcontainers; business clock fixed at 18:25, only the infrastructure clock moves):
   - Mapping: each event → the right messages, keys, envelope; walk-in without guest → no UpsertMember;
     deposit booking seated → OpenTable then ApplyDeposit; each event dispatched twice → one row each.
   - Sequence under concurrency: parallel handlers → unique, gap-free order per venue.
   - QA-5: offline, seat three tables → three Waiting, retried every 5 s, attempts unchanged; online →
     sent in sequence order, each accepted once by the mock; two workers in parallel still in order;
     crash after accept → no duplicate at the mock.
   - Inbound: bad signature 401; duplicate message once; §6.5 steps 1, 2, 3, 4 each; second_bill;
     ambiguous; bill-paid on a seeded table (no OpenTable recorded) completes via step 2; guest visit
     and LastBillTotal updated; posLogChanged only after commit.
   - Log endpoint: masked phone, Host 403, anonymous 401. Demo routes absent when Demo:Enabled is false.
   - LogScrubbingTests: no names, phones or payloads in logs.

(Agreed additions, 15 Nov) Every BookingCompleted with a guest counts the visit (BillClosed or
ClearedByHost), recording the bill total when there is one; BookingBillAttached only sets the bill
total without a second visit (Guest.RecordVisit(at, total?) + RecordLateBill(total)), unit-tested and
tested end to end (clear then late bill = 1 visit with the total). §6.7 notes that the
pos_venue_sequences row lock is held until the dispatcher batch commits, so a batch with a Stripe
refund call delays inbound bills for that venue; the pilot should allocate POS sequences outside slow
handlers or move the POS projection to its own batch.

Verification: dotnet build/test, has-pending-model-changes, openapi.json + generate:api, web lint/
typecheck/build. Playwright: I run it. Don't read user secrets.
```

### M9b POS log, mock POS switch and "Bill paid" on screen
```
Milestone M9b: POS log, mock POS switch and "Bill paid" on screen (Design §6, §6.7, §10.5, §15, §17,
§18; Requirements §6 AC6.1, AC6.2, AC6.5, AC6.6, QA-5; demo script steps 11–12). Front end + E2E +
docs; the small API additions below are allowed. No new packages. Read CLAUDE.md first.

1. Docs first: an "M9b" block with this prompt (minus the last two lines) after M9a in BUILD-PLAN.md;
   decisions in a new Design §18.9 Build notes (M9b).

2. API additions (additive; regenerate openapi.json + orval client):
   - POS log items gain tableNames (e.g. ["T4"]) so the presenter screen needs no floor-plan access.
   - The host needs to know whether demo controls exist (the bill-paid route is mapped only when
     Demo:Enabled). Add the smallest host-readable flag (e.g. demoEnabled on an existing venue/session
     response, or GET /demo/config); the "Bill paid" button is hidden when it is false.
   - Booking details for the host (floor-plan summary / tonight's list item, whichever the panel
     uses) gain the guest's visitCount and lastBillTotal (MoneyDto), so AC6.6 is visible.

3. Presenter screen, the free right column next to the phone panel (M7b layout):
   - "POS" header with the mock POS switch: a clear on/off toggle "POS online" / "POS offline"
     (colour + text + icon), PUT /demo/pos/online, state from GET …/pos/status, and a count of
     messages waiting ("3 waiting"). Disabled while the request runs.
   - POS log (AC6.1): newest first (by venueSequence), live via posLogChanged; reconnect refetches.
     Each row: time (venue zone), direction "To POS →" / "← From POS" (text, not colour only), message
     type, key fields in one line (OpenTable: T4 · 4 guests · name; ApplyDeposit: NZ$120.00;
     UpsertMember: name · masked phone; BillClosed: T4 · NZ$186.00), status badge (Waiting, Sent,
     Failed, Matched, Unmatched + reason in plain words), and the sequence number "#41". A Waiting row
     shows "waiting for the POS" and the attempts are not shown as failures. New rows highlight briefly.
   - Tap a row to expand (AC6.2): the full JSON as returned (already masked), pretty-printed,
     monospaced, scrollable, with a "Copy JSON" button. Only one row expanded at a time; keyboard and
     screen-reader friendly (button with aria-expanded).
   - Filter: All / Waiting / Unmatched.
   - Every string in en and zh-CN. Message types (OpenTable, ApplyDeposit, …) and JSON stay as they are.

4. Host screen:
   - "Bill paid" on a Seated table's details panel (only when demo is enabled), next to "Clear table".
     It opens a small dialog: "Bill total" in NZ$ (required, > 0, two decimals), pre-filled with a
     plausible amount (e.g. party size × NZ$45.00) the host can change, then POST /demo/pos/bill-paid
     {tableId, totalCents}. On matched: the table frees as usual (floorPlanChanged). On unmatched: a
     plain message ("The POS bill wasn't matched to a booking: {reason}"). Errors via
     useHostActionError. Disabled while the request runs.
   - Booking details show the guest's history: "First visit" or "3rd visit · last bill NZ$186.00"
     (en and zh-CN ordinal wording), only when the booking has a guest.

5. Playwright (pos.spec.ts, serial; presenter + host contexts):
   - QA-5: presenter switches the POS offline → host seats three walk-ins (API helper or UI) → within
     2 s the log shows three OpenTable rows "Waiting" with consecutive sequence numbers → presenter
     switches online → all three become "Sent" within 10 s, in sequence order, and no message
     appears twice (count rows by messageId).
   - Step 12: expand one OpenTable row → the JSON shows messageId, venueId, bookingId, tableIds and
     version 1; Copy JSON works (clipboard permission in the context or check the button state).
   - AC6.6: "Bill paid" on one seated table with a typed total → a "← From POS" BillClosed row
     "Matched" appears in the log, the table is free on the host, and that guest's next booking
     details (or the API) show the visit and the last bill.
   - afterAll: switch the POS back online, clear every walk-in the spec seated. Keep the existing
     stand-in patterns so the other specs still find their tables.

(Agreed, 16 Nov) The demo flag is demoEnabled on the floor-plan response. The visit line counts the
booking as visit N+1 (visitCount 0 → "First visit", 2 → "3rd visit"); a Completed booking, already
counted, shows "{n} visits" instead of an ordinal.

Verification: dotnet build/test, has-pending-model-changes, generate:api, web lint/typecheck/
format:check/test/build. Playwright and the manual look: I run them. Don't read user secrets.
```

### M10a Reminders and guest links, back end
```
Milestone M10a: reminders and guest links, back end (Design §2.2 ReminderRecord, §2.6, §7 incl.
"About GuestLinkSigner", §7.1, §8.3, §10.4, §14; Requirements step 5 AC5.1–AC5.6, BR-07, BR-13;
ADR-004). API, tests, docs. The guest manage page, the phone-panel buttons and the presenter
"Send reminders now" button come in M10b. Read CLAUDE.md first, including rules 9 and 14.

0. Seed check FIRST, before planning code: with the business clock at 18:25, list tonight's seeded
   bookings and say which would get a reminder under BR-13 (booked ≥ 24 h ahead, starts ≥ 2 h after
   now, Booked and not Confirmed), and whether at least one of them has a captured deposit, so demo
   step 5 can show reminder → Cancel → FULL refund. Also check the Requirements demo script for step 5.
   If the seed can't show it, propose the smallest seed change (e.g. one paid party of 6 at 20:45)
   and ASK me; don't change the seed on your own.

1. Docs first: an "M10a" block with this prompt (minus the last two lines) after M9b in BUILD-PLAN.md,
   rename "M10 onward" to "M11 onward"; decisions in a new Design §7.3 Build notes (M10a).

2. GuestLinkSigner (§7): HMAC-SHA256, URL-safe token with payload {bookingId, action
   (Manage | Confirm | Cancel), expires, keyId}; constant-time compare; invalid_link / link_expired.
   Key from GuestLinks:SigningKey (+ optional previous keys by keyId for rotation), ValidateOnStart
   with a message naming it; user secrets locally, a "not a secret" value in CI. Expiry = the booking
   start, compared on the BUSINESS clock (it's a business rule; record the rule-14 reasoning in §7.3).
   GuestLinks:BaseUrl (the web app's origin) builds the links.

3. Tokens never in a path and never in logs: links are {BaseUrl}/booking/?token=… . Make sure request
   logging, HTTP logging, exception logs and OpenTelemetry/trace attributes never record the query
   string or the token (scrub or exclude); extend LogScrubbingTests with a guest-link request. The
   API responses for guest endpoints send Referrer-Policy: no-referrer and Cache-Control: no-store.

4. Guest endpoints (anonymous, anonymous rate limit, no personal data beyond what the page needs):
   - GET /api/v1/guest/bookings?token: display only, never changes anything (link scanners):
     reference, venue name, date/time (venue zone), party size, status, deposit (amount + status),
     the cancellation terms from booking.Terms, whether cancelling now would refund (worked out with
     RefundPolicy at this moment, without cancelling), and which actions the token allows.
   - POST /api/v1/guest/bookings/confirm {token} and /cancel {token}: verify the token again, check its
     action allows it (a Confirm token can't cancel; a Manage token can cancel but not confirm), then
     Booking.Confirm / Booking.Cancel(GuestRequest) through the same code as the host. Repeats are
     no-ops returning success (Design §3.1). Record the response on the ReminderRecord when the
     booking has one (AC5.6). Problem codes invalid_link, link_expired, invalid_transition.

5. Reminders (BR-13, AC5.1, AC5.6):
   - One eligibility function used everywhere: Booked (not Confirmed, not cancelled), has a guest,
     created ≥ 24 h before its start, starts ≥ 2 h after now (business clock), no reminder yet.
   - POST /api/v1/demo/reminders/send-now (Presenter; only when Demo:Enabled): for tonight's eligible
     bookings, RecordReminder (SentAt, PolicyShown = the exact cancellation wording rendered from
     booking.Terms in the guest's language), raising an event that NotificationHandler turns into a
     Reminder message with two actions, Confirm and Cancel, each a signed link (Confirm/Cancel tokens).
     Returns how many were sent and how many were skipped and why (counts only).
     Sending twice sends nothing new.
   - Reminder message expiry = booking start, translated to the infrastructure clock as M7a planned.
   - The real 24-hour scheduler is pilot; note it in §7.3 (the eligibility function is ready for it).
   - Confirmation messages always carry a Manage link (BR-13: the guest who booked < 24 h ahead
     relies on it). Templates en + zh-CN; the link at the end, short text, no other personal data.

6. Late-cancellation warning (BR-13): the GET response says when the booking is inside the
   free-cancellation window from booking.Terms, so the page can show "cancelling now keeps your
   deposit" before the guest taps Cancel. Cancelling inside the window keeps the deposit (no refund);
   outside it the refund is full (M8a path). Host cancels stay full as before.

7. Messages API: GET …/messages items gain actions [{kind: confirm | cancel | manage, url}] so the
   phone panel (M10b) can show buttons. Host data: tonight's list items gain reminder {sentAt,
   response?, respondedAt?} for the details panel.

8. Tests first:
   - Signer: round trip, tampered payload, wrong key, old keyId still valid, expired, wrong action.
   - Eligibility: each BR-13 rule including the exact 24 h and 2 h boundaries.
   - Send-now: eligible get one Reminder each with two signed actions in their language; ineligible
     skipped with reasons; a second send sends nothing; Host 403; route absent when demo is off.
   - GET by token changes nothing (row version unchanged); POST confirm → Confirmed + floorPlanChanged
     + ReminderRecord response; POST cancel ≥ 2 h → Cancelled, refund requested in full, Cancellation
     and Refund texts; cancel < 2 h → deposit kept, no refund call; repeats are no-ops; Confirm token
     can't cancel; expired → link_expired; garbage → invalid_link.
   - Confirmation now carries a Manage link that opens the booking.
   - LogScrubbingTests: no token, query string or link appears in any log line.

(Agreed, 16 Nov) The seed already fits step 5 at 18:25: only #10 (party of 2, no deposit) and #11
(party of 6, deposit captured) get a reminder, and cancelling #11 then is in time. Send-now queues the
Reminder messages in its own transaction (Design §7, §10.4; ADR-002 has no reminder event), not through
a new event. Links are {BaseUrl}/manage/?token=… (Design §18.1). Seeded deposits carry pi_seed_*
intents Stripe has never seen: with Payments:Provider=Stripe and Demo:Enabled, a SeedDepositRouter in
front of the provider refunds those through the Fake path (signed charge.refunded through the same
webhook processing); tested with a strict Stripe double, including that the router is absent when
Demo:Enabled is false. Real test-mode intents at reset stay a possible M12 rehearsal upgrade (§7.3).

Verification: dotnet build/test, has-pending-model-changes, openapi.json + generate:api, web lint/
typecheck/build. Playwright: I run it. Don't read user secrets.
```

### M10b Step 5 on screen
```
Milestone M10b: step 5 on screen (Design §7, §7.3, §10.4, §17, §18; Requirements step 5
AC5.1–AC5.6, BR-07, BR-13). Front end + E2E + docs; small API additions below are allowed.
No new packages. Read CLAUDE.md first, including rule 9 (tokens are personal-data-grade).

1. Docs first: an "M10b" block with this prompt (minus the last two lines) after M10a in
   BUILD-PLAN.md; decisions in a new Design §18.10 Build notes (M10b).

2. API additions:
   - GET /guest/bookings?token also returns the booking's language (en | zh) so the page opens in
     the guest's language (the switch still works).
   - POST /api/v1/demo/reset (Presenter; only when Demo:Enabled): runs the existing DemoReset (data,
     clock back to the seeded evening, mock POS online), then broadcasts demoReset (§15). Clients
     invalidate everything on demoReset. M11 adds the presenter button; this milestone only needs
     the endpoint for E2E. Integration test: reset after changes → seeded shape again; Host 403;
     absent when demo is off.

3. Guest manage page /manage/?token=… (web/src/app/manage, the existing stub):
   - Reads the token from the query string once; never logs it, never sends it anywhere except the
     guest endpoints. <meta name="referrer" content="no-referrer"> on this page. No third-party
     scripts on it.
   - Shows Golden Dragon, reference, date and time (venue zone), party size, status, deposit
     (amount + status), the cancellation terms in plain words.
   - Buttons only for the actions the API returns. Confirm: one tap → "Thanks, you're confirmed".
     Cancel: a second confirmation step that says what happens to the deposit, from the API:
     "Your NZ$120.00 deposit will be refunded in full" or, inside the window (BR-13),
     "Cancelling now keeps your deposit of NZ$120.00", then "Your booking is cancelled" (+ "Your
     refund is on its way" when full). Double-tap guard as in M8b.
   - Errors in plain words, both languages: invalid_link "This link isn't valid", link_expired
     "This link has expired: the booking has started", invalid_transition (e.g. already seated or
     cancelled) shows the current status instead.
   - Opens in the guest's language from the API; en/中文 switch; mobile-first layout; 44 px targets.

4. Phone panel (presenter):
   - A bubble with actions shows them as buttons under the text: Reminder → "Confirm" / "Cancel",
     Confirmation → "Manage booking". In the body text, the long URLs are shown shortened
     (ellipsised, still links); the stored body is unchanged.
   - Tapping a button opens the manage page INSIDE the phone frame (an iframe of /manage/?token=…,
     same origin) with a "‹ Messages" back control; also an "Open in new tab" link. Same requests
     as a real guest's browser: GET to display, POST only on the guest's tap.
   - "Send reminders now" button in the presenter header area (M11 will group demo controls):
     POST /demo/reminders/send-now → "2 reminders sent" plus skipped counts in plain words
     ("7 skipped: 3 start within 2 hours, 4 seated…"); a second press "No new reminders".
     Disabled while running.

5. Host screen:
   - Booking details show the reminder line from tonight's list data: "Reminder sent 6:25 pm ·
     no reply yet" / "· Confirmed 6:26 pm" / "· Cancelled by guest 6:27 pm" (en + zh).
   - Tonight's list also refreshes on messageLogChanged (a reminder was sent), not only on
     floorPlanChanged.

6. Playwright (reminders.spec.ts, serial; presenter + host + guest-in-phone):
   - beforeAll: POST /demo/reset so seeds #10 and #11 are fresh. afterAll: reset again, so later
     specs start from the seeded evening (check walk-in.spec still passes after it).
   - Send reminders now → within 2 s the phone panel shows two Reminder bubbles: #10 in English,
     #11 in Chinese, each with Confirm and Cancel.
   - #10: tap Confirm → the manage page in the phone shows the booking and Confirm → confirm →
     "you're confirmed"; the host shows #10 Confirmed within 2 s (AC5.3) and the reminder line
     "Confirmed".
   - #11: tap Cancel → page in Chinese, says the NZ$120.00 deposit will be refunded in full →
     cancel → the host frees its table within 2 s (AC5.4); deposit reaches Refunded (AC5.5; Fake in
     CI, the seed router in Stripe mode locally); Cancellation and Refund texts appear in Chinese.
   - Reminder line shows the guest's response (AC5.6). Send again → "No new reminders".
   - Errors: a garbage token → "This link isn't valid". The late-cancel wording is covered by a
     unit test of the page logic (no table-dependent E2E for it).

(Agreed, 16 Nov) The reset can't move the clock: DemoClock in Fixed mode never moves before M11, so
there is nothing to put back; M11's clock controls add their own reset. The reset also empties the Fake
refund queue and broadcasts demoReset after the commit. The manage page applies the guest's language
without storing it (the iframe shares the presenter's origin) and calls the guest endpoints without the
staff cookie. Phone-panel action buttons are labelled in the message's language, like the body.

(Fix, 16 Nov) With the workers running the reset deadlocked with a worker batch and answered 409 slot_taken.
The reset now pauses the workers (in-process WorkerPause), locks every table in one fixed order with a
3 s lock_timeout, retries up to 5 times on 40P01 / 40001 / 55P03, and otherwise answers 503
demo_reset_busy; an integration test resets with the workers running (Design §18.10).

(Agreed, 16 Nov) The guest Cancel then got 429: all anonymous routes shared one 60-a-minute bucket per IP,
which venue Wi-Fi shares too. E2E keeps the real limits; the anonymous traffic is split: status poll per
booking (60), guest links per hashed token (30), other anonymous reads per IP (300), all under a 600 per-IP
ceiling; create (10) and demo-login (5) unchanged; all RateLimits:* (Design §18.10).

Verification: dotnet build/test, has-pending-model-changes, generate:api, web lint/typecheck/
format:check/test/build. Playwright and the manual look: I run them. Don't read user secrets.
```

### M11a Demo controls, demo clock and polish
```
Milestone M11a: demo controls, demo clock and polish (Design §16, §17, §15; Requirements §10 demo
script, AC4.6, QA-6). Read CLAUDE.md first, including rule 14 (two clocks).

0. Script check FIRST, before planning code: walk Requirements §10 rows 2–12 in order against the
   seed at 18:25 on the business clock, as one continuous run (each row sees the state the previous
   rows left). For each row say whether it works as written (which table, which time, what the
   screen shows), especially: row 4 (6 at 20:00 with a deposit), row 5 (19:00 for 4 is full and 3
   nearest times are offered), row 7 (walk-in for 4 finds nothing), row 8 ("Bill paid" on a 4-seat
   table frees it and highlights the waiting party), row 10 (confirm one, cancel another with a
   refunded deposit). If a row can't work, propose the smallest seed or script change and ASK me.

1. Docs first: an "M11a" block with this prompt (minus the last two lines) after M10b in
   BUILD-PLAN.md, rename "M11 onward" to "M12 onward"; decisions in a new Design §17.1 Build
   notes (M11a).

2. Demo clock (§17): DemoClock modes Fixed (default: tonight 18:25) and Running (starts at a set time
   and moves with real time). State stored in the database (survives restarts and works with more
   than one API instance later), not in memory; demo reset puts it back to Fixed 18:25.
   - PUT /api/v1/demo/clock {mode, at?} and POST /api/v1/demo/clock/advance {minutes} (Presenter,
     demo only); GET for the current demo time and mode.
   - The INFRASTRUCTURE clock never changes (rule 14): holds, retries, message expiry, rate limits
     are untouched. Only business time moves.
   - After any clock change, push a notice so every screen refetches (e.g. demoClockChanged, or
     reuse demoReset's "invalidate everything"), and make the overdue (AC4.6) and arriving-soon
     jobs re-evaluate at once rather than on their next tick.
   - Guard rails: advancing can't go backwards; a jump of more than 6 hours or past the venue's
     closing time is refused with a clear code. Document what moving time does to holds (none:
     infrastructure clock), reminders (eligibility changes), guest links (expire at the booking
     start), late-bill matching.
   - Tests: Running mode moves business time; advance by 11 min makes a notified entry overdue and
     pushes waitlistChanged; arriving-soon flips at the 30-minute mark; reset restores Fixed 18:25;
     infrastructure-clock behaviour unchanged; Host 403; routes absent when demo is off.

3. Presenter demo controls: one "Demo" area in the presenter header holding Reset (with a confirm
   step: "Reset the demo to 18:25 with the sample bookings?"), the demo time ("6:25 pm · fixed" /
   "6:40 pm · running") with "+5 min", "+15 min", "Run / Freeze", and the existing Send reminders
   now. Reset shows progress and handles 503 demo_reset_busy ("Busy, try again in a moment").
   en + zh-CN. Keep the POS switch in the POS panel.

4. Polish:
   - A 401 from SignalR negotiation stops retrying and goes to sign-in via onSignedOut, instead of
     logging "retrying / reconnecting".
   - Guest manage page: hide "Can't make it? Please cancel by …" once the booking is Cancelled (or
     otherwise past the point where cancelling is possible).
   - Anything you found in the script check that's a small UI fix; list it and ask before larger ones.

5. Playwright: a clock spec: notify a waiting party, advance 11 min → the overdue badge shows on both
   host screens within 2 s; Reset → back to 18:25 and the seed. afterAll resets.

(Agreed, 16 Nov) Script check: every row works at 18:25 as one run. Row 6's walk-in of 2 gets T2 (row 2
took T1 from 19:30); row 5 offers 19:15, 19:30 and 19:45. Row 10's cancelled #11 sits on T9, which #2
still occupies, so the tile stays seated: the script now says the booking leaves T9's 20:30 slot (no seed
change). The build notes are §17.2, because §17.1 already holds M5b's. Closing time for the clock guard =
last booking + longest turn (22:30, the availability service window); only Reset moves time backwards.

Verification: dotnet build/test, has-pending-model-changes, generate:api, web lint/typecheck/
format:check/test/build. Playwright and the manual look: I run them. Don't read user secrets.
```

### M11b The demo script as one end-to-end test, QA-6, QA-7, QA-8
```
Milestone M11b: the demo script as one end-to-end test, plus QA-6, QA-7, QA-8 and two wording fixes
(Requirements §10 demo script and §8 QA-1–QA-8; Design §17.2). E2E + small web fixes + docs. No API
change expected; if one is needed, stop and ask. Read CLAUDE.md first.

1. Docs first: an "M11b" block with this prompt (minus the last two lines) after M11a in
   BUILD-PLAN.md; decisions in a new Design §17.3 Build notes (M11b).

2. tests/E2E/specs/demo-script.spec.ts: Requirements §10 rows 2–12 as ONE continuous run, in order,
   with three contexts as on stage: guest phone (phone viewport), host (tablet, landscape) and
   presenter (laptop). Reset first (clock Fixed 18:25). Each row is a test.step named after the row.
   Assert what "What he sees" says, using the expectations from the M11a script check:
   - row 2: 2 at 19:30, switching to 中文 halfway; measure the booking time (QA-3 ≤ 60 s practised:
     record the measured time in the report, fail only above 60 s);
   - row 3: T1 Booked on the host within 2 s; confirmation in the phone panel;
   - row 4: 6 at 20:00 on T10 with the deposit (Fake: Simulate payment; Stripe: test card, as
     deposit.spec does); UpsertMember in the POS log;
   - row 5: 19:00 for 4 is full and 19:15, 19:30, 19:45 are offered;
   - row 6: walk-in for 2 in 3 taps, suggested T2; OpenTable in the POS log;
   - row 7: walk-in for 4 finds nothing; waitlist with the API's quote;
   - row 8: Bill paid on T4 → BillClosed Matched, T4 free, the party of 4 highlighted;
   - row 9: Notify → "table ready" in the phone panel; seat them on T4 in one tap;
   - row 10: Send reminders now → confirm #10, cancel #11 → Confirmed, #11 leaves T9's 20:30 slot,
     deposit Refunded. Then on the guest phone check whether 6 at 20:30 is bookable again; if it is,
     assert it and add it to Requirements row 10's wording; if not, report why and don't force it;
   - row 11: POS offline, seat a table, online → its messages Sent once each, in order;
   - row 12: expand one message → JSON with the envelope fields.
   QA-4: each host action in the script (seat, walk-in, notify, bill paid) must respond within 1 s:
   measure from the tap to the screen change, report the slowest, fail above 1 s.

3. QA-6 (three clean runs): the spec must pass 3 times in a row (npx playwright test demo-script
   --repeat-each=3), resetting at the start of each repetition. "No errors or reloads": fail on any
   console error, uncaught page error, failed request with status ≥ 500, or a full page reload/
   navigation on the host or presenter after the start. Document the command in CLAUDE.md.

4. Fallback recording: when DEMO_RECORD=1 (off by default and in CI), record video for all three
   contexts at a watchable pace (slowMo from DEMO_SLOWMO, e.g. 400 ms) into
   tests/E2E/demo-recordings/ (git-ignored). Document how to run it. (The real screen recording
   of all three screens together is done by me during M12 rehearsals.)

5. QA-7 (language): a spec that opens each screen (book, host, presenter, manage, demo-login) in
   中文 and checks there is no untranslated English UI text: collect visible text and flag Latin-
   letter words not on an allowlist (Golden Dragon, NZ$, message types like OpenTable, references,
   seeded guest names, JSON in the POS log, "Stripe"). And in English, no Chinese UI text except
   guest names and Chinese message bodies. Report offenders with their element; fix any real ones
   in en.json / zh-CN.json.

6. QA-8 (devices): add Playwright projects for an iPhone (WebKit) and an Android phone (Chromium
   or Edge, as the local setup allows) and a tablet. Run the guest booking, deposit-free booking and
   manage-page specs on the phones and the host specs on the tablet. Every page at phone width: no
   horizontal scrolling (scrollWidth ≤ clientWidth). Ask me before adding WebKit to CI (download
   size and time); locally `npx playwright install webkit` once.

7. Wording fixes (from the M11a manual check):
   - Waitlist: the Overdue badge says what's overdue: "⚠ Not back {n} min after notify"
     (中文 "通知后 {n} 分钟未到"), counting from notifiedAt.
   - Once an entry is Notified, the "Quoted n min" line is dimmed (or shown as "was quoted n min"),
     since it no longer applies.
   Update the host/walk-in specs that read these texts.

(Agreed, 16 Nov) Row 9 / AC4.5: seating a notified party took two taps (Seat, then Seat at T4); a
notified entry now shows a one-tap "Seat at T4" and "Seat elsewhere" for the chooser. QA-8's tablet is
an iPad in WebKit (project tablet-webkit); the Galaxy Tab in Edge stays the default project's host.

Verification: web lint/typecheck/format:check/test/build; dotnet test only if anything outside
tests/E2E and web changes. Playwright: I run demo-script with --repeat-each=3, then the full suite
twice. Don't read user secrets.
```

### M11c Host app installable as a PWA
```
Milestone M11c: the host app installable as a PWA, full screen (ADR-001 "Host app delivery",
Architecture §3/§7.5, ADR-003 offline option A for the demo). Web + small E2E + docs. No API change.
Ask before adding any package. Read CLAUDE.md first.

1. Docs first: an "M11c" block with this prompt (minus the last two lines) after M11b in
   BUILD-PLAN.md; decisions in a new Design §18.11 Build notes (M11c).

2. Manifest (works with the static export and trailingSlash):
   - name "Golden Dragon Host", short_name "GD Host", start_url "/host/", display "standalone",
     orientation any, theme_color / background_color from the existing colour tokens, lang en.
   - Scope must include /demo-login/ so signing in never leaves the installed app (on iPad an
     out-of-scope page opens in Safari with separate cookies). Choose the scope accordingly and
     explain it in §18.11.
   - Linked from the host and demo-login pages only (and presenter if it's harmless), NOT from the
     guest pages (/book/, /manage/): guests shouldn't be offered "install".

3. Icons: an original, simple mark (e.g. a "GD" monogram in our accent colour; no dragon artwork
   copied from anywhere). PNGs 192, 512, a 512 maskable with safe padding, and apple-touch-icon 180.
   Prefer generating them once from an SVG with tools we already have (e.g. a small script using the
   installed Playwright to render the SVG) and commit the PNGs plus the SVG and the script.

4. iPad / iPhone: apple-touch-icon, apple-mobile-web-app-capable / mobile-web-app-capable,
   apple-mobile-web-app-title "GD Host", status-bar style, viewport-fit=cover, and safe-area insets
   (env(safe-area-inset-*)) so nothing sits under the notch or the home bar in standalone mode.
   In standalone mode there is no browser back button: check the host and sign-in flows never
   depend on it.

5. No offline data caching (ADR-003 option A for the demo): don't cache API responses or SignalR.
   Only add a service worker if the target browsers need one to offer installation; if so, a
   minimal one that caches nothing (or only the static shell) and never serves API data, with a
   clear way to update. Say which browsers need it and why.

6. Tests:
   - E2E (default project): the manifest is served with the right content type, parses, has the
     fields above and the icons exist at their declared sizes; host and demo-login link it; /book/
     and /manage/ don't.
   - A check that in a standalone display-mode emulation the host page still fits (no horizontal
     scroll) and the sign-in redirect stays in scope.
   Real install on an iPad/iPhone/Android is my manual check after M12 deploys over HTTPS; write
   the steps in §18.11 (Safari Share → Add to Home Screen; Chrome menu → Install app).

7. Note for M12 in §18.11: when a CSP is added, allow manifest-src 'self'; and the session cookie
   in an installed iOS web app is separate from Safari's, so the host signs in once inside the app.
```

### M12 onward
Use the same pattern: "Implement docs/Design.md §X for milestone Mn in docs/BUILD-PLAN.md. List the acceptance criteria it covers, write the tests first, then the code."

### M12 Deploy and rehearse
Also add a container image scan of the API image to the CI pipeline (ADR-005).
~~Use the Route 53 demo domain for both (`app.<domain>` for the front end, `api.<domain>` for the API)~~: superseded by M12a (17 Nov): one CloudFront `cloudfront.net` origin for site and API, no custom domain yet (Design §17.4).
Also from Design §14.1 and §20: ~~`ForwardedHeaders` trusting only the known proxy~~ (M12a: `CloudFront-Viewer-Address` behind a secret origin header), so rate limits see the real client IP; `Demo:Enabled=true` with both passcodes from Secrets Manager in the demo environment (M12a); Data Protection keys that survive container restarts; ~~`Cors:AllowedOrigins` set to `https://app.<domain>`~~ (M12a: one origin, no CORS); OpenTelemetry export to X-Ray, with the ASP.NET Core instrumentation dropping `url.query` / `url.full` so guest-link tokens never reach a trace (Design §7.3).
Still open after M12a: the container image scan in CI, GitHub Actions deployment with OIDC, Data Protection keys, OpenTelemetry.

### M12a AWS infrastructure and a first manual deployment
```
Milestone M12a: AWS infrastructure and a first manual deployment (ADR-005; Design §17, §18.10,
§18.11 M12 notes, §20; BUILD-PLAN M12). Demo account 821797257125, region ap-southeast-2, AWS CLI
profile "reservation-demo" (aws login, temporary credentials, full access). Read CLAUDE.md first.

SAFETY RULES for this milestone (add them to CLAUDE.md and .claude/settings.json):
- You may run read-only commands: cdk synth, cdk diff, cdk ls, and aws ... describe-*/list-*/get-*.
- You must NOT run anything that creates, changes or deletes AWS resources (cdk deploy, cdk destroy,
  cdk bootstrap, aws ... create/put/update/delete, docker push, lightsail push-container-image).
  I run those myself; write the exact commands for me in the plan and in a RUNBOOK.
- Never create IAM users or access keys. Never print or log secret values.

0. Decisions to record first (Design §17.4 Build notes (M12a) and a dated clarification in ADR-005,
   since these change it). Ask me about any you'd decide differently:
   a) No custom domain for now: one CloudFront distribution with its default *.cloudfront.net HTTPS
      address serves everything on ONE origin, so the SameSite=Strict session cookie works and no CORS
      is needed: default → S3 (static export, origin access control, bucket private);
      /api/* and /hubs/* → the Lightsail container's HTTPS endpoint (no caching, all methods, all
      query strings, cookies and headers forwarded except Host, WebSockets working for SignalR).
      A custom domain can be added later as configuration only; design for it.
   b) The public address must survive deleting the app: split into a PERMANENT stack (S3 bucket,
      CloudFront distribution and its functions/policies, Secrets Manager secrets, anything cheap and
      stable) and an APP stack (Lightsail container service, RDS PostgreSQL db.t4g.micro single-AZ,
      20 GB, encrypted, not publicly accessible). Recreating the app stack must not change the
      cloudfront.net address; solve how the distribution learns the (possibly new) Lightsail endpoint.
   c) Secrets: Lightsail containers have no IAM role, so the API can't read Secrets Manager itself.
      Propose the safest workable route (e.g. the deploy step reads Secrets Manager and passes values
      as container environment variables), and say who can see them afterwards.
   d) Real client IP: behind CloudFront every request would come from CloudFront, so all guests would
      share one rate-limit bucket. Read the viewer IP (e.g. CloudFront-Viewer-Address via an origin
      request policy) and trust it ONLY when the request carries a secret origin header that
      CloudFront adds; requests to the Lightsail endpoint without that header are refused (except the
      health check Lightsail needs). Integration tests for both cases.
   e) Database reachability: Lightsail VPC peering to the default VPC, RDS security group allowing
      5432 only from the Lightsail peering range. Say whether peering needs a one-time call I run.
   f) Migrations and seed in the demo environment: the API migrates at start-up (single instance) and
      the demo seed/ResetIfStale behave as in Development. Use a dedicated environment name (e.g.
      ASPNETCORE_ENVIRONMENT=Demo with appsettings.Demo.json), never Development in AWS.

1. CDK in C# under infra/ (Infrastructure.sln or added to the main one; ask before new packages
   beyond Amazon.CDK.Lib and Constructs). Stacks: Permanent and App, both ap-southeast-2, tagged
   Project=reservation-demo. No NAT gateway, no load balancer, no Elastic IP. Unit tests with CDK
   assertions (bucket not public, OAC, no caching on /api/*, WebSocket-friendly policies, RDS not
   public, no NAT, deletion protection off for the demo DB, removal policies so the app stack can be
   destroyed cleanly; the permanent stack keeps the bucket and secrets).

2. Edge configuration on the distribution:
   - a CloudFront Function so /host/ etc. serve host/index.html (static export with trailingSlash);
   - S3 objects uploaded with correct Content-Type, including application/manifest+json for
     .webmanifest;
   - a response headers policy: HSTS, nosniff, Referrer-Policy, and a CSP that allows only our origin
     plus Stripe (script-src/frame-src https://js.stripe.com https://hooks.stripe.com, connect-src
     https://api.stripe.com), manifest-src 'self', frame-ancestors 'self' (the phone panel frames
     /manage/). Test the CSP against the real pages in a later step, don't loosen it blindly.

3. Demo environment configuration (no secret values in the repo): Demo:Enabled=true, Payments:Provider
   =Stripe (test mode), GuestLinks:BaseUrl = the cloudfront address, Stripe/POS/GuestLinks/passcode
   secrets from Secrets Manager. New passcodes and signing keys for AWS, never the local ones; I'll put
   the values in Secrets Manager myself with commands you write (no values in the commands' files).
   Stripe: a webhook endpoint in the Stripe sandbox for https://<cloudfront>/api/v1/webhooks/stripe
   with events payment_intent.succeeded and charge.refunded; its signing secret goes to Secrets
   Manager. Write the steps for me.

4. RUNBOOK (docs/RUNBOOK-aws.md): every command I run, in order, PowerShell syntax: cdk bootstrap,
   deploy permanent, put secrets, build and push the image, deploy app, peering if needed, upload the
   web app, invalidate CloudFront, check health, and how to DELETE the app stack and recreate it
   later. Include a cost line per resource and the US$50 spend limit.

5. Verification you can run: dotnet build/test (incl. new tests), cdk synth both stacks, cdk diff
   (needs my login; read-only), web build. Then I deploy by following the RUNBOOK; after that,
   the existing Playwright suite against the deployed address (BASE_URL) is my check.

Agreed in planning (17 Nov): CSP inline scripts by per-page sha256 hashes; secrets as CloudFormation
dynamic references; machine-only keys generated in AWS; the Lightsail host passed to Permanent as
-c ApiOrigin. Revisions: the CSP goes out report-only first (-c CspMode=report|enforce; RUNBOOK:
Playwright + a manual 4242 and 3-D Secure payment + console check, then enforce); deny rules in
.claude/settings.json as explicit prefixes; the RUNBOOK states the account's US$50 project spend
limit, which pauses all resources, and the Budget's 80/100 % alerts before it; the aws-cdk npm CLI
pinned in infra/package.json; E2E_STRIPE_WEBHOOK=deployed for deposit.spec.
```

### M12b Scan to book and host full screen
```
New branch m12b-qr-fullscreen from main.

Goal: in the live demo the partner scans a QR code on the presenter screen with his own phone, books
on /book/, and sees it appear on the host screen; the host screen can go full screen on a laptop or
iPad.

1) Presenter: a "Scan to book" card with a QR code for `${window.location.origin}/book/`, computed in
   the browser (so it follows localhost or the CloudFront address automatically). Generate it locally
   (e.g. the `qrcode` package to an SVG string, or our own SVG); no external QR service. No inline
   style attributes or <style> elements: use a CSS module (we keep CSP style-src 'self' clean).
   Show the plain URL under it. A button enlarges it to a large overlay (dark modules on white, quiet
   zone kept) that closes with Esc, a Close button or a tap outside. English and 中文 labels.
   Guest page only: never staff URLs or passcodes.
2) Host: a "Full screen" button using the Fullscreen API (document.documentElement.requestFullscreen,
   with the webkit prefix for iPad Safari). Hidden when unsupported (iPhone Safari) or when already
   running as the installed app (display-mode: standalone). The label switches to "Exit full screen"
   and back when the user leaves with Esc (listen to fullscreenchange). Bilingual. Must not break the
   host layout on phone or tablet (phone-layout and pwa specs).
3) Tests: unit tests for the QR URL and the fullscreen-support check. Playwright: the presenter shows
   the QR; decode it (e.g. jsQR on the element's screenshot, as a dev dependency) and check it equals
   <base>/book/; the overlay opens and closes; the host shows the Full screen button on mobile-edge.
   QA-7 language check must still pass.
4) Docs: Design §17 (new subsection), BUILD-PLAN M12b, Requirements demo script row where the
   partner books, if it needs a wording change.
Run web tests, lint, type-check, format check. Don't commit.

Agreed in planning (19 Nov): the card sits above the POS panel and must not push the POS heading
and the top of its log below the fold at 1366×768 or 1440×900 (else it moves right of the Demo
controls; Playwright checks both sizes); Design §17.5 notes that a booking from his phone still
texts the presenter's phone panel and the 10/min per-IP booking limit is unchanged; only qrcode's
encoder is imported (create from qrcode/lib/core/qrcode.js) and the presenter page's JS is
reported before and after. The SVG is our own path from qrcode's module matrix; jsQR reads the
screenshot through pngjs (E2E dev dependency). Requirements v1.4: row 2 is booked on his phone.
```

## Definition of done (every milestone)

- [ ] `dotnet build` and `dotnet test` pass locally and in CI
- [ ] `npm run lint`, type-check and build pass in `web/`
- [ ] The listed acceptance criteria are checked off in the PR description
- [ ] No partner names, no personal data in logs, no secrets in the repo
- [ ] Any agreed design change is written into `docs/Design.md` in the same PR

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
| M6 | Walk-ins and waitlist | §3.2, §11.3, §11.4 | Step 3 | 1 d |
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

### M6 onward
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

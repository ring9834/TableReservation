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
| M4 | Booking and availability API | §9, §11, §14 | Step 1 without payment; `slot_taken`; first Playwright test and `oasdiff` in CI | 1.5 d |
| M5 | Host app: floor plan + SignalR | §15, §18 | Step 2: booking appears within 2 s | 2 d |
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
Implement docs/Design.md §5: OutboxSaveChangesInterceptor, OutboxDispatcher
(FOR UPDATE SKIP LOCKED, per-aggregate order, backoff, failed status),
inbox with IdempotentHandlerDecorator using a savepoint inside the dispatcher's transaction,
and the event envelope from src/Contracts.
Integration tests: event saved in the same transaction as the change; rollback leaves no event;
each handler run twice has one effect; a failing handler does not undo the others; order is kept per aggregate.
```

### M4 Booking API
```
Implement the Bookings and availability slice from docs/Design.md §9, §11 and §14:
Result<T>/Error, ResultToHttp, Problem Details codes from ADR-004, FluentValidation endpoint filter,
Idempotency-Key on create, /demo-login and the demo session cookie with Host and Presenter policies
(Design §14, §17), OpenAPI document.
Cover AC1.x without payment. Integration tests for every error code the endpoints can return.
Then wire web/ to the API with orval and build the guest booking page in English and Chinese (react-i18next).
Set up tests/E2E with Playwright (guest booking in both languages) and add `oasdiff` to CI.
```

### M5 onward
Use the same pattern: "Implement docs/Design.md §X for milestone Mn in docs/BUILD-PLAN.md. List the acceptance criteria it covers, write the tests first, then the code."

### M12 Deploy and rehearse
Also add a container image scan of the API image to the CI pipeline (ADR-005).
Use the Route 53 demo domain for both (`app.<domain>` for the front end, `api.<domain>` for the API); the default cloudfront.net/amazonlightsail.com hostnames won't work with the demo session cookie (Design §17, §20).

## Definition of done (every milestone)

- [ ] `dotnet build` and `dotnet test` pass locally and in CI
- [ ] `npm run lint`, type-check and build pass in `web/`
- [ ] The listed acceptance criteria are checked off in the PR description
- [ ] No partner names, no personal data in logs, no secrets in the repo
- [ ] Any agreed design change is written into `docs/Design.md` in the same PR

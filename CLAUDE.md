# CLAUDE.md

Guidance for Claude Code in this repository. Keep this file short; details live in `docs/`.

## What this is

A restaurant reservation demo (fictional venue **Golden Dragon**, Auckland, 14 tables, English and Chinese) that will later connect to a partner's POS. Guests book and pay deposits online; hosts run the floor on a tablet; an in-process mock POS receives `OpenTable` / `ApplyDeposit` and sends `BillClosed`.

## Source of truth (read before changing anything)

| Question | Read |
| --- | --- |
| What must the demo do? Acceptance criteria, business rules BR-01–BR-13 | `docs/Requirements.md` |
| How do the parts fit together? | `docs/Architecture.md` |
| Exact classes, states, events, schema, API, sequences | `docs/Design.md` |
| Why a choice was made | `docs/adr/ADR-001` … `ADR-005` |
| What to build next | `docs/BUILD-PLAN.md` |

The files in this repository are the source of truth for every document, ADRs included (`docs/adr/` is authoritative; there are no external "live" copies). Order of authority: **ADR > Design.md > Architecture.md**. If code and docs disagree, stop and tell me; do not silently change the docs or the design.

## Stack

- **API:** .NET 10, ASP.NET Core minimal APIs, EF Core + Npgsql, SignalR. PostgreSQL 16+.
- **Web:** Next.js (React, TypeScript), `output: 'export'` (static, no server features), PWA, TanStack Query, `orval` client from OpenAPI, zod, react-hook-form, react-i18next.
- **Tests:** xUnit, Testcontainers (real PostgreSQL), Playwright.
- **Infra (later):** AWS CDK in C#, GitHub Actions with OIDC. Lightsail container + RDS for the demo.

## Layout

```
src/Api            endpoints (vertical slices by feature), pipeline, SignalR hub
src/Domain         aggregates, value objects, policies, domain events (no EF, no ASP.NET)
src/Contracts      event envelope and event types
src/Infrastructure EF Core, outbox/inbox, workers, Stripe, mock POS, message senders
web/               Next.js app: guest, host, presenter screens
tests/             Unit, Integration, E2E
infra/             CDK stacks
```

## Commands

```bash
docker compose up -d                     # PostgreSQL only (the mock POS runs inside the API)
dotnet user-secrets set ConnectionStrings:Reservations "Host=localhost;Port=5432;Database=reservations;Username=reservations;Password=local_dev_only" --project src/Api   # once
dotnet user-secrets set Demo:HostPasscode "<host passcode>" --project src/Api             # once; the API refuses to start in Development without both
dotnet user-secrets set Demo:PresenterPasscode "<presenter passcode>" --project src/Api   # once; must differ from the host passcode
dotnet user-secrets set Payments:Provider Stripe --project src/Api   # optional: real Stripe test mode (Development uses Fake); then also set Stripe:SecretKey (sk_test_…) and Stripe:WebhookSecret (whsec_… from stripe listen); the API refuses to start without them
stripe listen --forward-to https://localhost:5001/api/v1/webhooks/stripe   # with Stripe test mode only: forwards Stripe webhooks to the API (Design §8.4)
dotnet build                             # .NET 10 SDK pinned in global.json; warnings are errors
dotnet test                              # unit + integration on Microsoft.Testing.Platform (Docker must be running)
dotnet run --project src/Api             # https://localhost:5001; /health/live, /health/ready, /openapi/v1.json; in Development applies migrations + seed (Database:MigrateOnStartup)
UPDATE_OPENAPI=1 dotnet test --project tests/Integration -- --filter-class "Reservation.Tests.Integration.Api.OpenApiDocumentTests"   # after an intended API change: rewrite docs/api/openapi.json, then commit it
dotnet tool restore                      # dotnet-ef, pinned in .config/dotnet-tools.json
dotnet ef database update --project src/Infrastructure --startup-project src/Api        # manual alternative to MigrateOnStartup
dotnet ef migrations add <Name> --project src/Infrastructure --startup-project src/Api --output-dir Persistence/Migrations
dotnet ef migrations has-pending-model-changes --project src/Infrastructure --startup-project src/Api   # what CI runs
dotnet run --project src/Api --Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command=Information   # log SQL when debugging (parameter values stay hidden; Warning by default)
cd web && npm ci && npm run dev         # https://localhost:3000 (next dev --experimental-https; self-signed certificate in web/certificates/)
cd web && npm run generate:api           # after an API change: regenerate src/api (orval) from docs/api/openapi.json, then commit it
cd web && npm run lint && npm run typecheck && npm run format:check && npm test && npm run build   # what CI runs (plus "generate:api leaves src/api unchanged")
cd tests/E2E && npm ci                   # once; locally Playwright drives the installed Microsoft Edge (project mobile-edge, channel 'msedge'), so no browser download
cd tests/E2E && npx playwright test      # API and web must be running; E2E_BASE_URL defaults to https://localhost:3000. CI (CI=1) runs Linux Chromium instead (project mobile-chromium); Chrome crashes under automation on the dev laptop
E2E_HOST_PASSCODE=<host passcode> E2E_PRESENTER_PASSCODE=<presenter passcode> npx playwright test   # both required for every spec: global-setup.ts signs in a host and a presenter once per run and shares the sessions (tests/E2E/.auth/, git-ignored; CI: ci-host-passcode, ci-presenter-passcode). Development runs the business clock fixed at 18:25 today (Demo:Clock) and resets a stale database at start-up (Demo:ResetIfStale); every spec cancels the bookings it makes (API at E2E_API_BASE_URL, default NEXT_PUBLIC_API_BASE_URL or https://localhost:5001); /demo-login allows 5 attempts a minute, so leave a minute between full runs
```

Keep this section in step with the real scripts when they change.

## Rules that must not be broken

1. **Never name the partner or its products** in code, comments, commit messages or docs. Use "the POS" or "the partner".
2. **All data stays in AWS.** No third-party SaaS that stores data (no Sentry, Datadog, Auth0). Accepted exception: Stripe holds card and payment data (PCI scope stays with Stripe; we store only payment references). In the pilot, SMS and email go through AWS services.
3. **One transaction per command:** state change + outbox rows together. Nothing is sent from inside a request.
4. **Domain events are raised only inside aggregates** via `Raise(...)`, which stamps `LastEventSequence`. `Version` (xmin) is for concurrency only.
5. **Every event consumer is idempotent** (inbox row per consumer + event ID, inside a savepoint in the dispatcher's transaction). Tests send each event twice.
6. **Double booking is prevented by the database** (exclusion constraint). Do not replace it with app-only checks.
7. **Expected failures return `Result<T>` / `Error`**, mapped to Problem Details with a stable `code`. Exceptions are for the unexpected, handled once in the central `IExceptionHandler`.
8. **Validation:** FluentValidation via one endpoint filter. No MediatR, no AutoMapper, no `FluentValidation.AspNetCore` auto-validation.
9. **Never log personal data** (names, full phone numbers, emails, booking notes, tokens, request bodies).
10. **Time through `TimeProvider`**, stored in UTC, shown in `Pacific/Auckland`. Money in integer cents NZD.
11. **Secrets** come from .NET user secrets locally and Secrets Manager in AWS. Never commit them; never read `.env*` files.
12. **Staff access in the demo is a fixed demo session** issued by `/demo-login` for a passcode, as a cookie with role `Host` or `Presenter` (Design §17). Cognito is a pilot feature; don't add it.
13. **Out of scope for the demo:** see `docs/Requirements.md` §11, which is the full list. Don't build anything on it.
14. **Two clocks:** workers use the infrastructure clock; the domain uses the business clock. The business clock is the default `TimeProvider` (`DemoClock` in the demo). The infrastructure clock is the keyed `TimeProvider` `"infrastructure"` (`Clocks.Infrastructure`), always the system clock: polling, retries, timers and worker bookkeeping times use it (Design §5.6).

## How to work

- Work one milestone of `docs/BUILD-PLAN.md` at a time. Start in plan mode; show the plan before editing.
- Write the tests with the code. A milestone is done only when `dotnet build`, `dotnet test` and the web lint/type-check pass.
- Small commits, Conventional Commits (`feat(bookings): …`, `fix(pos): …`, `test(outbox): …`, `docs: …`).
- When a design detail is missing or ambiguous, ask instead of inventing. Note any agreed change in `docs/Design.md` in the same commit.
- Prefer the libraries already listed. Ask before adding a new package.

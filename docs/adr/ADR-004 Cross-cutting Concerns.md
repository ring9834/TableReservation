# ADR-004: Cross-cutting Concerns

Antonio Huang · 5 November 2026 · Accepted 9 November 2026 · [live version](https://claude.ai/artifact/85bugiUSpUYYKhJ3X7tP9t)

**Decision:** every request passes the same layers in one fixed order: a central exception handler, then logging and tracing, rate limiting, sign-in, a venue access check and FluentValidation, and only then the business code. Every failure returns one error shape (Problem Details with an error code). The front end and back end share one contract, generated from the API's OpenAPI document.

## Status and context

| Item | Value |
| --- | --- |
| Status | Accepted |
| Scope | Reservation demo, then pilot |
| Builds on | ADR-001 (technology selection), ADR-002 (event-driven architecture), ADR-003 (data architecture) |
| Related | ADR-005 (AWS hosting and deployment): network, WAF, secrets storage, log retention |

These concerns touch every feature, so they are hard to add later. If each endpoint handled errors, checks and logging its own way, the code would drift within weeks, and the partner's team would inherit that drift. This ADR fixes one way to do each, so that Claude Code and any later developer follow the same pattern.

## Decision drivers

| ID | Driver | Source |
| --- | --- | --- |
| CC1 | A host sees and changes only their own venue's data | ADR-003 (venue ID on every record) |
| CC2 | Card details never reach our servers; Stripe events cannot be forged | ADR-001 D3 |
| CC3 | Front end and back end stay in step while one developer changes both quickly | ADR-001 D6 |
| CC4 | One error shape and one set of rules everywhere, with messages in English and Chinese | ADR-001 D4 |
| CC5 | Any failure can be traced from the guest's screen to the POS message | ADR-002 (correlation IDs) |
| CC6 | Personal data is kept to the minimum and never written to logs | NZ Privacy Act 2020, ADR-003 |
| CC7 | Data, logs and traces stay in AWS | ADR-001 D10 |
| CC8 | Few extra services and low running cost | ADR-001 D7 |

## Where each concern sits

*[Diagram: request pipeline · six layers inside one exception handler. See the live version for the drawing.]*

Cheap checks run first, so a bad or unauthorised request is turned away before it reaches the database. The handler is the only layer that changes data, and it writes the outbox event in the same transaction (ADR-002).

## Security

In the demo, the host app runs as a fixed demo host session; in the pilot, staff sign in through Amazon Cognito. Guests have no accounts; they use signed links. Every API call checks the caller's venue, and card details go only to Stripe.

### Who can do what

| Caller | How they prove who they are | What they can do |
| --- | --- | --- |
| Guest | No account. The booking page is public; “manage my booking” links carry a signed token that expires after the booking | Create a booking, pay a deposit, confirm or cancel their own booking |
| Host | Demo: fixed demo host session, no sign-in. Pilot: Cognito sign-in (email and password, MFA required) | Floor plan, walk-ins, waitlist and messages for their venue |
| Manager | Cognito, MFA required | Host actions plus layout editor, settings, refunds and reports for their venue |
| Platform admin | Cognito, MFA required | Venue setup, replaying failed events (ADR-002); not used in day-to-day service |
| POS and Stripe | Signed webhooks: the Stripe signature, and a shared secret or HMAC header for the POS | Send `BillClosed` and payment events only |

### Sign-in options

| Option | Data stays in AWS (CC7) | Effort | Verdict |
| --- | --- | --- | --- |
| **Amazon Cognito user pool** | Yes | Low: hosted sign-in page, MFA, password reset built in | **Chosen** |
| ASP.NET Core Identity in PostgreSQL | Yes | Medium: we build and secure sign-in, reset and MFA screens | Fallback if Cognito limits block us |
| Auth0, Clerk or Okta | No: user data held by a third party | Low | Rejected (CC7) |

In the pilot, the host app uses the Cognito sign-in page with the authorisation code flow and PKCE. The access token is kept in memory, not in `localStorage`, and refreshed silently. The API validates the JWT on every request. In the demo, the API issues a fixed host session for Golden Dragon instead, behind the same Host policy, so swapping in Cognito changes no endpoint.

### Rules

1. **Venue check:** a `venue_ids` claim lists the venues a staff member works at. An authorisation policy compares it with the venue in the route. Every query filters by venue ID, and PostgreSQL row-level security can be added later as a second guard (ADR-003).
2. **Roles:** Cognito groups (pilot) or the demo session's fixed role map to `Host`, `Manager` and `PlatformAdmin` policies. Endpoints deny by default; each one names its policy.
3. **SignalR:** the hub uses the same credentials as the API (in the pilot, the same JWT), which the browser client sends as a query parameter. That query string is removed from all logs.
4. **Payments:** Stripe Elements collects card details in Stripe's own frame, so our servers never see them (PCI DSS SAQ A). Webhook signatures are verified, and each Stripe event ID is processed once (ADR-002 inbox).
5. **Rate limits:** ASP.NET Core rate limiting on public endpoints, such as 10 booking attempts a minute per IP address and 3 reminder resends per booking. AWS WAF in front of the API is covered in ADR-005.
6. **Browser rules:** HTTPS only with HSTS; CORS limited to the app's own domains; a content security policy that allows only the app and Stripe; cookies, if any, `Secure`, `HttpOnly` and `SameSite=Lax`.
7. **Secrets:** no secrets in code or images. They come from AWS Secrets Manager at start-up (ADR-005). Local development uses .NET user secrets.
8. **Dependencies:** Dependabot, `dotnet list package --vulnerable` and `npm audit` run in CI; a high-severity finding fails the build.
9. **Personal data:** collect only name, phone, email (optional) and booking notes. Phone numbers are stored in E.164 format and masked in screens that do not need them (ADR-003 retention rules).
10. **Target:** OWASP ASVS level 1 for the demo, level 2 for the pilot. A short threat model is reviewed before the pilot.

## Contracts between front end and back end

The C# request and response types are the single source of truth. The API publishes them as an OpenAPI document, and the front end's TypeScript client is generated from it, so a mismatch fails the build rather than showing up in production.

### Options

| Option | How the front end gets types | Drift risk | Verdict |
| --- | --- | --- | --- |
| Hand-written TypeScript types | Copied by hand | High: silent mismatches | Rejected |
| **OpenAPI first from code, generated client** | ASP.NET Core's built-in OpenAPI document, then `orval` generates the TypeScript client, TanStack Query hooks and zod schemas | Low: CI regenerates and type-checks | **Chosen** |
| Spec written first in YAML | Write the OpenAPI file by hand, then generate both sides | Low, but slower for one developer | Rejected for now |
| GraphQL or tRPC | One schema or shared TypeScript | Low, but tRPC needs a TypeScript back end and GraphQL adds a layer the POS team would not use | Rejected |

### Conventions

| Topic | Rule |
| --- | --- |
| Paths | `/api/v1/venues/{venueId}/bookings`; nouns in the plural; actions as sub-resources, e.g. `POST …/bookings/{id}/seat` |
| JSON | camelCase names; enums as strings (`"seated"`), never numbers |
| IDs | UUID v7 as strings |
| Time | ISO 8601 in UTC with `Z`; the venue's time zone is sent as an IANA name (`Pacific/Auckland`) for display |
| Money | Integer cents plus a currency code: `{ "amountCents": 2000, "currency": "NZD" }` |
| Lists | Cursor paging: `?cursor=…&limit=50`, response `{ items, nextCursor }` |
| Concurrency | Responses carry a `version`; updates send it back and get `409` if the record changed (ADR-003 row versions) |
| Idempotency | `POST` requests that create or pay accept an `Idempotency-Key` header; a repeat returns the first result |
| Errors | Problem Details (see Error handling) |
| Real-time | SignalR message types (`floorPlanChanged` and others, ADR-002) are added to the OpenAPI document as schemas, so they are generated too |

### Change rules

- Adding an optional field or a new endpoint is not a breaking change.
- Removing or renaming a field, or making one required, is breaking. It needs `/api/v2` for that endpoint, or a release in which both are supported.
- CI compares the OpenAPI document with the last release using `oasdiff`, and fails on an unplanned breaking change.
- The POS adapter's message formats are separate contracts, owned by the POS (ADR-002).

## Validation

Use FluentValidation for request validation in the API, called by one endpoint filter. Business rules stay in the domain code, and the database constraints are the last guard. Front-end checks exist only for a better form experience; the API never trusts them.

### Three layers, three jobs

| Layer | Checks | Example | Tool |
| --- | --- | --- | --- |
| Front end | Shape and format, for instant feedback | Party size 1–8 online; phone number format; required name | zod schemas generated from OpenAPI, react-hook-form |
| API request | Shape, format and simple cross-field rules; the first server-side guard | `endsAt` after `startsAt`; booking not in the past; notes ≤ 500 characters | **FluentValidation** |
| Domain and database | Rules that need current state or a lock | Table free for that time; pacing limit; deposit required for parties of 6+ | Domain code in the command handler; PostgreSQL exclusion constraint (ADR-003) |

A rule lives in exactly one server layer. Validators do not query the database for availability, because the answer can change before the save; that check runs inside the transaction, under the lock.

### Should we use FluentValidation?

| Option | Strengths | Weaknesses | Verdict |
| --- | --- | --- | --- |
| DataAnnotations attributes (`[Required]`, `[Range]`) | Built in; shown in OpenAPI automatically | Rules scattered across attributes; cross-field and conditional rules awkward; hard to localise per rule | Rejected as the main tool |
| .NET 10 built-in validation (`AddValidation()`) | Built in; returns Problem Details automatically for minimal APIs; no extra package | Still based on DataAnnotations and `IValidatableObject`; no async rules; weaker for conditional rules | Rejected for now; revisit as it matures |
| **FluentValidation** | Rules in one class per request; conditional and cross-field rules read clearly; easy unit tests; per-rule error codes and localised messages; widely known by .NET developers | Extra package; rules are not shown in OpenAPI by default; its old automatic ASP.NET integration is deprecated | **Chosen** |
| Hand-written checks in each handler | No package | Inconsistent; easy to forget; errors in different shapes | Rejected |

**Licence:** FluentValidation is Apache-2.0 on NuGet ([licence](https://www.nuget.org/packages/FluentValidation.DependencyInjectionExtensions/11.9.1/license)). Several popular .NET libraries (MediatR and AutoMapper among them) moved to commercial licences in 2025, so we check the licence again at each major upgrade.

### How it is used

1. One validator per request type, e.g. `CreateBookingRequestValidator : AbstractValidator<CreateBookingRequest>`, in the same feature folder.
2. A generic endpoint filter finds the validator, runs it, and returns `400` with Problem Details listing errors by field. We do not use the deprecated `FluentValidation.AspNetCore` auto-validation.
3. Each rule has an error code (`WithErrorCode("party_size_out_of_range")`) and a message key. The front end shows the message in the guest's language from its translation files.
4. Basic rules (required, length, range) are copied into the OpenAPI schema by a small schema transformer, so the generated zod schemas match. If that costs too much, those few fields are added to zod by hand.
5. Every validator has unit tests. Validation is checked again in Playwright tests by submitting bad forms.

## Error handling

Yes, use one central exception handler: ASP.NET Core's `IExceptionHandler` with `AddProblemDetails()`. It catches anything unexpected, logs it once and returns a safe Problem Details response (RFC 9457). Expected business failures, such as a slot just taken, are returned as results with an error code, not thrown.

### Options

| Option | Verdict | Why |
| --- | --- | --- |
| `try/catch` in each endpoint | Rejected | Repeated code; error shapes drift; easy to leak stack traces |
| Custom exception middleware | Rejected | Works, but `IExceptionHandler` (built in since .NET 8) does the same job in the standard way |
| **`IExceptionHandler` + `AddProblemDetails()` for unexpected errors; result types for expected failures** | **Chosen** | One place for logging and response shape; business code shows its failures in the method signature; exceptions stay exceptional and cheap |
| Exceptions for every failure, mapped centrally | Rejected | Hides normal outcomes (slot taken, hold expired) as exceptions; costly on hot paths; harder to read |

### Response shape

```json
{
  "type": "https://errors.example.app/slot-taken",
  "title": "The table is no longer free",
  "status": 409,
  "code": "slot_taken",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
  "errors": null
}
```

`code` is stable and is what the front end switches on, and `title` is English for logs only. The screen shows a translated message for the `code`. `errors` lists field errors for `400` responses. `traceId` lets support find the request in the logs (see Logging). The `type` domain is a placeholder until the product domain is chosen.

### Error codes (v1)

| HTTP | Code | When |
| --- | --- | --- |
| 400 | `validation_failed` | FluentValidation failed; `errors` lists the fields |
| 401 | `not_signed_in` | Missing or expired token |
| 403 | `forbidden` | Wrong role, or a venue the user does not work at |
| 404 | `not_found` | Record missing or in another venue (the API never says which) |
| 409 | `slot_taken` | Exclusion constraint or pacing check failed (PostgreSQL error `23P01` mapped centrally) |
| 409 | `version_conflict` | Row version changed since it was read |
| 409 | `hold_expired` | The 10-minute payment hold ended before payment |
| 422 | `rule_broken` | Another business rule, with a more specific `code` where useful (e.g. `deposit_required`) |
| 429 | `too_many_requests` | Rate limit hit; `Retry-After` header set |
| 502 | `payment_provider_error` | Stripe unreachable or returned an error |
| 500 | `unexpected` | Anything else; details only in the logs |

### Rules

- The central handler logs the exception once, with the trace ID. Nothing else logs and rethrows.
- Responses never contain stack traces, SQL or internal names, in any environment except local development.
- Known infrastructure exceptions are mapped centrally: concurrency → `version_conflict`, `23P01` → `slot_taken`, operation cancelled → no response logged as an error.
- The background worker (ADR-002) uses the same logging but its own retry and `failed` status, not HTTP responses.
- **Front end:** the generated client turns Problem Details into a typed error. One handler shows a toast or a field message for the `code`. `401` sends the user to sign in, and `409 slot_taken` offers the next free times. A React error boundary catches rendering errors and reports them (see Logging).

## Logging, tracing and monitoring

Write structured JSON logs through .NET's `ILogger`, with traces and metrics through OpenTelemetry. Everything goes to Amazon CloudWatch and AWS X-Ray, so no log data leaves AWS. One trace ID links the browser request, the API, the outbox event and the POS message.

### Options

| Option | Verdict | Why |
| --- | --- | --- |
| **`ILogger` + OpenTelemetry, JSON to the console; CloudWatch Logs; traces to X-Ray through the AWS Distro for OpenTelemetry** | **Chosen** | Built in; vendor neutral; data stays in AWS (CC7); no extra cost beyond CloudWatch |
| Serilog with a CloudWatch sink | Possible later | Rich formatting and sinks, but one more library; OpenTelemetry already covers structure and export |
| Datadog, New Relic or Sentry | Rejected | Good tools, but logs and errors would leave AWS (CC7) and add cost (CC8) |
| Self-hosted Grafana stack | Rejected for now | Too much to run for the demo; Amazon Managed Grafana is an option at pilot scale |

### What every log line carries

| Field | Example | From |
| --- | --- | --- |
| `timestamp`, `level`, `message` (template) | `Booking {BookingId} seated at {TableId}` | `ILogger` |
| `traceId`, `spanId` | W3C trace context | OpenTelemetry |
| `correlationId` | Same as the event envelope (ADR-002) | Request, copied into events |
| `venueId`, `userId` (Cognito sub in the pilot; the fixed demo host in the demo), `role` | IDs only | Auth claims |
| `service`, `environment`, `version` | `api`, `demo`, git SHA | Start-up config |

### What is never logged

- Names, full phone numbers and email addresses (phone numbers are masked to the last 3 digits).
- Tokens, cookies, the SignalR `access_token` query string, Stripe keys and webhook secrets.
- Card data (never present, because of Stripe Elements) and request or response bodies.
- Free-text booking notes, which may hold health details such as allergies.

A log-scrubbing test sends a sample booking through the API and fails if any of these appear in the output.

### Levels and alerts

| Level | Used for |
| --- | --- |
| Error | Unhandled exceptions; an outbox row marked `failed`; webhook signature failures |
| Warning | Retries, rate limits hit, POS unreachable |
| Information | One line per state change and per external call (Stripe, text provider, POS) |
| Debug | Local development only; off in AWS |

CloudWatch alarms for the pilot: any `failed` outbox row; API 5xx above 1% for 5 minutes; outbox delivery slower than 2 s at the 95th percentile (ADR-002); POS unreachable for more than 15 minutes. Log retention and dashboards are set in ADR-005.

### Front end

The React error boundary and the generated client send errors to a small `/api/v1/client-errors` endpoint, which writes them to CloudWatch with the same trace ID. This replaces a third-party tool such as Sentry and keeps the data in AWS. Messages are rate limited and scrubbed like server logs.

## Other cross-cutting concerns

| Concern | Decision |
| --- | --- |
| Time and time zones | Store and send UTC; convert to the venue's IANA time zone for display and for rules such as “no bookings after 21:00”. Code reads time through `TimeProvider`, so tests can set the clock. Daylight-saving changes are tested for Auckland. |
| Localisation | All UI text and error messages in translation files (English and Simplified Chinese, ADR-001 D4). The API returns codes, not sentences. Guest texts and emails are sent in the language the guest chose when booking. |
| Idempotency | `Idempotency-Key` on create and pay requests (Contracts); inbox for webhooks and events (ADR-002). Stored for 24 hours. |
| Configuration | .NET options classes, validated at start-up (`ValidateOnStart`), so a missing setting stops the app at deploy time, not at the first guest. Values from environment variables and AWS Secrets Manager (ADR-005). |
| Feature flags | A simple settings table per venue for the demo (deposits on or off, waitlist on or off). AWS AppConfig if flags need gradual rollout at the pilot. |
| Health checks | `/health/live` and `/health/ready` (database and outbox lag) for the load balancer and container restarts (ADR-005). |
| Code structure | Vertical slices by feature (`Bookings`, `Tables`, `Waitlist`), each with its request, validator, handler and tests. Cross-cutting code lives only in the pipeline, never copied into features. |
| Testing the concerns | Integration tests (Testcontainers) for: venue isolation, Problem Details for every error code, validation responses, log scrubbing, and webhook signature rejection. |

## Decision summary

| Concern | Choice |
| --- | --- |
| Staff sign-in | Demo: fixed demo host session. Pilot: Amazon Cognito user pool, authorisation code flow with PKCE, JWT validated by the API |
| Guest access | No accounts; signed, expiring links to manage a booking |
| Authorisation | Role policies (`Host`, `Manager`, `PlatformAdmin`), venue claim checked on every request, deny by default |
| Payments security | Stripe Elements (SAQ A), verified webhooks, events processed once |
| Contracts | ASP.NET Core OpenAPI document → `orval` TypeScript client, query hooks and zod schemas; `oasdiff` in CI |
| Validation | FluentValidation through one endpoint filter; business rules in handlers; database constraints last |
| Error handling | Central `IExceptionHandler` + Problem Details with stable error codes; result types for expected failures |
| Logging and tracing | `ILogger` JSON logs + OpenTelemetry → CloudWatch and X-Ray; no personal data in logs |
| Other | UTC + `TimeProvider`; translation files; idempotency keys; validated options; health checks; vertical slices |

## Consequences

**Good**

- Each new feature follows one pattern, which makes Claude Code's output consistent and easy to review.
- Front-end and back-end mismatches fail the build, not the demo.
- Guests and hosts see clear, translated messages, while support can find any failure from its trace ID.
- Personal data stays out of logs and inside AWS, which supports the Privacy Act and the partner's AWS-only rule.

**Costs and risks**

- Cognito adds set-up time in the pilot, about one day, and its sign-in page needs styling and Chinese text.
- Generated clients add a build step; the generator must run whenever the API changes.
- FluentValidation rules must be mirrored into OpenAPI for the front end; a small transformer or a few hand-written zod rules cover this.
- Two kinds of failure, results and exceptions, need a short coding guide so they are used consistently.

## Revisit when

- .NET's built-in validation supports async and conditional rules well enough to drop FluentValidation.
- Cognito pricing or limits no longer fit, such as when guest accounts or social sign-in are needed.
- The partner already runs a logging or monitoring tool inside AWS that the system should join.
- The POS team needs a formal contract for the API, which would favour writing the OpenAPI spec first.

## Open questions

- Does the partner already use Cognito or another identity service for POS staff, so hosts can use one sign-in?
- Which venue roles does the partner want beyond host and manager (e.g. owner, events coordinator)?
- How long must logs be kept for the partner's own audits?

## Sources

- [FluentValidation licence on NuGet](https://www.nuget.org/packages/FluentValidation.DependencyInjectionExtensions/11.9.1/license)
- [Minimal API validation in .NET 10](https://dev.to/adrianbailador/minimal-api-validation-in-net-10-3c02)

# Restaurant & Catering Reservation System

A reservation, waitlist and floor-plan system for restaurants, designed to connect to a partner's point-of-sale (POS) system. Guests book and pay deposits online; hosts run the floor on a tablet; seated tables, deposits and bills flow to and from the POS.

| | |
| --- | --- |
| **Phase** | Detailed design (in review) — demo build not started |
| **Next milestone** | Build the working demo (2–3 weeks), then a 20-minute walkthrough with the partner |
| **Owner** | Antonio Huang |
| **Last updated** | 7 November 2026 |

---

## Where the project is

```mermaid
flowchart LR
    p1[1. Business requirements<br/>and proposal] --> p2[2. Demo requirements] --> p3[3. Architecture<br/>ADR-001 to 005] --> p4[4. Detailed design] --> p5[5. Demo build] --> p6[6. Demo to partner] --> p7[7. Pilot]
    classDef done fill:#d9ead3,stroke:#6aa84f
    classDef now fill:#fff2cc,stroke:#e69138,stroke-width:2px
    classDef next fill:#ffffff,stroke:#999999
    class p1,p2,p3 done
    class p4 now
    class p5,p6,p7 next
```

| Phase | Status | Output |
| --- | --- | --- |
| 1. Business requirements and proposal | Done | Business requirements and delivery proposal (Word, with Appendix A) |
| 2. Demo requirements | Done, agreed | [`docs/Requirements.md`](docs/Requirements.md) |
| 3. Architecture decisions | Done, status *Proposed* | ADR-001 to ADR-005 (below), [`docs/Architecture.md`](docs/Architecture.md) |
| 4. Detailed design | **In review** | [`docs/Design.md`](docs/Design.md), `Design.docx` (changes since 5 November highlighted) |
| 5. Demo build | Not started | This repository's code |
| 6. Demo to the partner | Not scheduled | Six-step, 20-minute script (Requirements §10) |
| 7. Pilot | Not started | Partner's AWS accounts, real venues |

---

## What the demo shows

One dinner service at a fictional restaurant, **Golden Dragon** (Auckland, 14 tables, English and Chinese):

1. A guest books a table on a phone in under a minute, paying a deposit for 6+ guests (Stripe test mode).
2. The booking appears on the host's floor plan within 2 seconds, on every screen.
3. The host seats a walk-in in three taps or adds the party to a waitlist with a quoted wait.
4. A table frees up and the waiting guest gets a "table ready" text (on-screen phone panel).
5. Guests get a reminder they can confirm or cancel with one tap; deposits are refunded by rule.
6. A POS log shows exactly what the system sends to and receives from the POS, including delivery while the POS is offline.

Out of scope for the demo: real SMS and POS connections, staff sign-in, the floor-plan editor, split bills, reports and multiple venues.

---

## Documents

| Document | Purpose | Status |
| --- | --- | --- |
| [`docs/Requirements.md`](docs/Requirements.md) | Demo scope, acceptance criteria, business rules BR-01–BR-12, quality targets QA-1–QA-8, demo script | Agreed |
| [`docs/Architecture.md`](docs/Architecture.md) | One-page architecture: context, stack, events, data, cross-cutting concerns, deployment | Draft — needs the updates listed under *Known gaps* |
| [`docs/Design.md`](docs/Design.md) | Detailed design: domain model, state machines, events, outbox, POS, messaging, payments, schema, API, jobs | Draft, in review |
| [ADR-001](https://claude.ai/artifact/8uJxSk1a8d3h25LxTA1Z1K) | Technology selection (Next.js vs Nuxt, .NET, PostgreSQL, SignalR, PWA, floor plan) | Proposed |
| [ADR-002](https://claude.ai/artifact/1cCJp3NJhwEDWs5KvLfGqT) | Event-driven architecture (outbox, worker, event catalogue, delivery rules) | Proposed |
| [ADR-003](https://claude.ai/artifact/Bs3MghWHR2kpw2S5oZxfM4) | Data architecture (PostgreSQL vs SQL Server, double booking, offline, privacy) | Proposed |
| [ADR-004](https://claude.ai/artifact/85bugiUSpUYYKhJ3X7tP9t) | Cross-cutting concerns (security, contracts, FluentValidation, error handling, logging) | Proposed |
| [ADR-005](https://claude.ai/artifact/Cyq95yBMujMRR8Rzk27EtK) | AWS hosting and deployment (Lightsail demo, ECS pilot, CDK, GitHub Actions) | Proposed |

When the code starts, export the ADRs into `docs/adr/` and point these links there.

### Recent design changes (5–7 November 2026)

| Change | Design section |
| --- | --- |
| Booking terms (deposit, cancellation, turn time) copied onto each booking at booking time | §2.6 |
| Late-match rule: a POS bill that arrives after the host cleared the table still reaches the right booking | §6.5 |
| One bill per booking, keyed by POS bill ID, with room for split bills later | §6.6 |
| `MessageDeliveryWorker`: texts and emails queued in PostgreSQL, sent with retries and expiry | §7.1 |

---

## Technology (planned)

| Layer | Choice |
| --- | --- |
| Front end | Next.js (React, TypeScript), static export, installable as a PWA; SVG floor plan |
| API | ASP.NET Core, C#, EF Core (Npgsql), SignalR |
| Data | PostgreSQL (Amazon RDS) |
| Events | Domain events, transactional outbox and inbox, background workers |
| Validation and errors | FluentValidation; central exception handler with Problem Details |
| Contracts | OpenAPI → generated TypeScript client (`orval`) |
| Payments | Stripe (test mode in the demo) |
| Hosting | AWS: Lightsail container + RDS + S3/CloudFront (demo); ECS on Fargate (pilot) |
| Infrastructure and CI/CD | AWS CDK (C#), GitHub Actions with OIDC |
| Tests | xUnit, Testcontainers, Playwright |

All data stays in AWS. Exact versions will be pinned when the code starts.

---

## Repository layout (planned)

```
/
├─ docs/
│  ├─ Requirements.md
│  ├─ Architecture.md
│  ├─ Design.md
│  └─ adr/                    ← ADR-001 … ADR-005
├─ src/
│  ├─ Api/                    ← endpoints, features (vertical slices), pipeline, SignalR hub
│  ├─ Domain/                 ← aggregates, value objects, policies, domain events
│  ├─ Contracts/              ← event envelope and event types
│  └─ Infrastructure/         ← EF Core, outbox/inbox, workers, Stripe, mock POS, message senders
├─ web/                       ← Next.js app (guest, host, presenter screens)
├─ tests/                     ← Unit, Integration (Testcontainers), E2E (Playwright)
├─ infra/                     ← AWS CDK stacks
└─ docker-compose.yml         ← local PostgreSQL, API, mock POS
```

## Getting started

There is no code yet. Once the demo build starts, this section will cover:

- prerequisites (.NET SDK, Node.js, Docker)
- `docker compose up` for PostgreSQL and the mock POS
- running the API and the Next.js app
- resetting the Golden Dragon demo data

---

## Next steps

1. **Finish the design review.** Read `Design.docx` (changes highlighted) and resolve the known gaps below.
2. **Set up the repository.** Solution skeleton, `docker-compose.yml`, CI with build and tests, demo AWS account with a budget alarm.
3. **Build the demo in this order:** data model and seed → booking and availability → floor plan and SignalR → walk-ins and waitlist → messages and phone panel → Stripe deposits and refunds → mock POS and POS log → reminders → demo controls and reset.
4. **Rehearse.** Run the demo script end to end three times without errors (QA-6), and record a 3-minute fallback video.
5. **Arrange the walkthrough** with the partner, and bring the open questions below.

## Known gaps in the documents

| Gap | Fix |
| --- | --- |
| Staff sign-in: Requirements put it out of scope; ADR-004 and `Architecture.md` §8.2 use Cognito | Say "demo host session, Cognito in the pilot" in ADR-004 and `Architecture.md` (Design §17 already does) |
| ADR-001 lists the floor-plan editor and combining tables as demo needs | Mark them as pilot features |
| ADR-004 validation examples say deposit for 8+ and party size 1–20 | Change to deposit for 6+ (BR-06) and online party size 1–8 (BR-04) |
| The demo requirements doc (Claude Doc) still uses older event names | Align with the ADR-002 catalogue (`BookingSeated`, `MessageSent`) |
| `Architecture.md` doesn't yet mention the four recent design changes | Add short notes on booking terms, late bill matching, `booking_bills` and the message worker |

## Questions for the partner

- How does the POS send "bill paid" (webhook, polling or export)? Does it accept idempotency keys?
- Can the POS return the booking ID it received when the table was opened?
- Does the POS support split bills per table, and do venues use virtual tables to fake them?
- Does the partner already use Cognito or another identity service for POS staff?
- Which AWS accounts and region should the pilot use (Auckland or Sydney)?
- What uptime and recovery targets do they promise venues today?
- Which domain should guests see on booking pages and texts?

---

## Conventions

- **Decisions** are recorded as ADRs; the design must not contradict an accepted ADR.
- **Design changes** are made in `docs/Design.md` first; `Design.docx` is generated from it.
- **Commits** use Conventional Commits, e.g. `docs(design): …`, `feat(bookings): …`, `fix(pos): …`.
- **Confidentiality:** the partner and their products are not named in this repository.
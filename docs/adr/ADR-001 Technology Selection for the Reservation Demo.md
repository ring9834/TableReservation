# ADR-001: Technology Selection for the Reservation Demo

Nov 5, 2026 · @what's Parse Server

Decision: build the demo with a Next.js (React) front end, an ASP.NET Core API with SignalR, and PostgreSQL, with events published through an outbox; the same stack carries forward into the pilot.

## Status and context

| Item | Detail |
| --- | --- |
| Status | Proposed |
| Decision owner | Antonio Huang |
| Applies to | The demo, and the pilot that follows unless a revisit trigger fires |
| Inputs | Reservation System Demo — Requirements Analysis (steps 1–6, quality attributes QA-1 to QA-8, constraints); business requirements and delivery proposal |

The demo must be built within 2–3 weeks by one developer using Claude Code, then grow into a pilot and later a product run inside the POS partner's team. The stack chosen now therefore has to be fast to build with, strong at real-time updates and payments, and easy for the partner to own and staff later. Choosing it before the architecture work starts avoids rework, because the main structural decisions depend on it.

## Decision drivers

Each option below is judged against these drivers, taken from the demo requirements and the business context.

| ID | Driver | Source |
| --- | --- | --- |
| D1 | Live updates across devices within 2 seconds, with recovery after a dropped connection | QA-1, QA-2 |
| D2 | Reliable, ordered, once-only delivery of POS messages | QA-5, step 6 |
| D3 | Stripe deposits and refunds in test mode, matching the partner's existing Stripe account | Step 1, step 5, G-13 |
| D4 | English and Chinese on every screen | QA-7 |
| D5 | Fast, polished mobile and tablet UI with a floor plan | QA-3, QA-4, QA-8 |
| D6 | Delivery within 2–3 weeks by one developer with Claude Code | Constraint: time |
| D7 | Near-zero running cost for the demo | Constraint: cost |
| D8 | Easy for the partner to own, hire for and maintain in New Zealand | Business: delivery proposal |
| D9 | Path to the pilot and production without rewriting | Business: roadmap |
| D10 | All data stays within AWS, the partner's main cloud platform (no New Zealand residency requirement stated) | Partner, in conversation |
| D11 | Host app runs on tablets and phones at the host stand, with no app store release needed for the demo | QA-8, constraint: time |

## Front end: Next.js (React) vs Nuxt (Vue)

Both frameworks can deliver the demo well: they offer file-based routing, server-side and client-side rendering per page, TypeScript, and good mobile performance. The choice comes down to payments support, the size of the ecosystem, and who will maintain the product later.

| Criterion | Next.js (React) | Nuxt (Vue) | Edge | Drivers |
| --- | --- | --- | --- | --- |
| Live updates with SignalR | The official `@microsoft/signalr` client works directly | Same client, works directly | Even | D1 |
| Stripe payments | Stripe maintains an official React library for its payment elements | No official Vue library; use Stripe.js directly or a community wrapper | Next.js | D3 |
| English and Chinese | Community libraries (e.g. next-intl); a little setup | Official, mature i18n module; very little setup | Nuxt | D4 |
| UI components and floor plan | Largest choice of component libraries; SVG or canvas floor plan | Good choice (e.g. Nuxt UI, PrimeVue, Vuetify); SVG or canvas floor plan | Next.js, slightly | D5 |
| Speed with Claude Code | Most widely used; AI tools generally produce strong React code | Well supported, with fewer examples to draw on | Next.js, slightly | D6 |
| Developer's own experience | Recent: MyTodos, WeeklyMeals, UrIdeal | Recent: BrewQueue; earlier Vue work on the archive system | Even | D6 |
| Learning curve and simplicity | More concepts (server and client components) to manage | Often seen as simpler, with auto-imports and less boilerplate | Nuxt | D6 |
| Hiring and handover in New Zealand | Larger pool of React developers | Smaller pool | Next.js | D8 |
| Growth into pilot and production | Mature, widely deployed; strong SEO for the guest page | Mature, widely deployed; strong SEO for the guest page | Even | D9 |
| Fit with the partner's existing front end | Unknown | Unknown | Open question | D8 |

**Outcome:** choose **Next.js (React)**. Nuxt is equal or better on simplicity and i18n, but Next.js wins on the drivers that carry the most weight: official Stripe support (D3), a larger hiring pool for the partner (D8), and slightly faster AI-assisted building (D6).

**How to use Next.js in this project:** the host app is built as client-side pages, because it behaves like an app and needs no server rendering. Server rendering is used only where it helps, mainly the guest booking page later. The .NET API stays the single back end, so business logic does not drift into Next.js server code.

## Other technology choices

| Area | Choice | Alternatives considered | Why this choice | Drivers |
| --- | --- | --- | --- | --- |
| Back end | ASP.NET Core Web API (.NET, C#) with Entity Framework Core | Node.js (NestJS); Next.js server routes only | Developer's strongest stack; strong typing, performance and tooling; keeps one clear back end | D6, D9 |
| Real-time updates | SignalR, hosted in the API | Socket.IO; polling every few seconds | Built into .NET; automatic reconnects and fallbacks; instant updates | D1 |
| Database | PostgreSQL | SQL Server; a document database | Free and open source, low hosting cost, strong EF Core support, good fit for bookings and JSON payloads | D7, D9 |
| Events and POS delivery | Domain events behind `IEventPublisher`, written to a PostgreSQL outbox and delivered by a background worker in the API process | Amazon SNS/SQS or EventBridge; RabbitMQ (Amazon MQ); Kafka (Amazon MSK) | Reliable, ordered, once-only delivery without running a broker; can move to a broker or Kafka later without changing business logic | D2, D7, D9 |
| Payments | Stripe in test mode, using its official React payment elements | Windcave; Alipay and WeChat Pay first | Matches the partner's existing Stripe account; test mode is free; Alipay and WeChat Pay are added in the pilot | D3, D7 |
| Text messages | On-screen phone panel fed by the same events | Twilio trial | Reliable on the day, no cost or phone numbers; a real SMS provider plugs in for the pilot | D1, D7 |
| Testing | xUnit with Testcontainers for PostgreSQL; Playwright to run the demo script end to end | Manual testing only | Proves QA-1 to QA-6 automatically and makes the three practice runs repeatable | D1, D2, D6 |
| Hosting | A low-cost AWS container service for the API and front end (Amazon Lightsail containers for the demo; Amazon ECS on Fargate for the pilot), plus Amazon RDS for PostgreSQL | Azure container services; running on a laptop with a tunnel; a separate front-end host | AWS is the partner's main cloud platform, so the pilot can move into their own account; public web address for any phone; one place to manage | D7, D9 |
| Host app delivery | Responsive Next.js web app, installable on tablets and phones as a progressive web app (PWA), opening full screen | React Native (e.g. with Expo); a native iPad app | One code base for guest and host screens; instant updates with no app store review; works on any tablet; offline caching is possible with a service worker. A native app adds a second code base and app store releases with no benefit for the demo | D5, D6, D11 |

## Floor plan

**Outcome:** draw the floor plan as SVG from React components, with `react-zoom-pan-pinch` for zoom and pan and `@use-gesture/react` for dragging tables in the layout editor. Each table is a real page element, so it can be tapped, read by screen readers, translated and tested like any other part of the UI.

### What the floor plan must do

| Need | Source |
| --- | --- |
| Show 20–80 tables per area, with live status (free, booked soon, seated, needs clearing) | Demo steps 2–3, QA-1 |
| Large tap targets on a tablet or phone; tap a table to seat, clear or view a booking | D5, D11 |
| Zoom and pan on small screens; fit to screen on a tablet | D5 |
| Layout editor: add, drag, rotate and resize tables; round or rectangular shapes; seats; areas such as Main hall and Private room | Demo step 2 |
| Select two or more tables to combine them for a large party | Demo step 3 |
| Labels and status words in English and Chinese; status not shown by colour alone | D4, QA-7 |
| Testable with Playwright, like the rest of the host app | Test stack (Playwright) |

### Options considered

| Option | How it draws | Tap, keyboard and screen readers | Testing | Effort | Verdict |
| --- | --- | --- | --- | --- | --- |
| HTML and CSS (positioned `div`s) | Each table is a `div` placed with CSS | Good | Easy | Low, but rotation, round tables and scaling get awkward | Rejected |
| **SVG with React components** | Each table is a `<g>` with a shape and label; one `viewBox` scales the whole room | Good: real elements with roles and labels | Easy: Playwright finds tables by label | Low to medium | **Chosen** |
| Canvas: Konva (`react-konva`) or Fabric.js | Pixels on one canvas; the library tracks shapes | Weak: canvas content is invisible to screen readers without a hidden copy | Harder: clicks by coordinates | Medium | Keep in mind for very large rooms |
| WebGL: PixiJS | GPU drawing for thousands of objects | Weak, as with canvas | Harder | High | Rejected: far more power than 80 tables need |
| Diagram libraries: React Flow, JointJS | Nodes and edges for flow charts | Good | Medium | Medium, fighting a graph model | Rejected: a floor plan has no edges |
| Commercial floor plan or seating widgets | Ready-made editor | Varies | Varies | Low to start, but licence cost and data outside the partner's control | Rejected (D7, D8, D10) |

### How it is built

- **Coordinates:** each area has a fixed grid, e.g. 1,000 × 700 units. A table stores `x`, `y`, `rotation`, `shape`, `width`, `height`, `minSeats` and `maxSeats` in PostgreSQL (ADR-003). The `viewBox` scales the grid to any screen, so the layout never depends on device size.
- **Service view:** tables are coloured by status, with a text or icon badge as well (time seated, guest name, “due 19:30”). A tap opens a side panel. Pinch and pan come from `react-zoom-pan-pinch`.
- **Layout editor:** a separate mode. Dragging and rotating use `@use-gesture/react`, with snap to a 10-unit grid. Changes save as one layout version, so service screens never show a half-edited room.
- **Live updates:** a `floorPlanChanged` push (ADR-002) refetches only the changed tables. React then redraws just those `<g>` elements.
- **Background:** an optional photo or drawing of the room behind the tables, stored in Amazon S3 so it stays in AWS (D10).
- **Accessibility:** each table has `role="button"`, an `aria-label` such as “Table 12, 4 seats, seated 18:05”, and keyboard focus. Arrow keys move a selected table in the editor.

SVG handles a few hundred elements on a tablet without strain, well above one venue's tables. Revisit with Konva if a venue needs more than about 500 objects per area, or if drawing becomes slow on the partner's tablets.

## Decision summary

| Layer | Technology |
| --- | --- |
| Guest page and host app | Next.js (React) with TypeScript, mostly client-side pages |
| Floor plan | SVG drawn by React components; `react-zoom-pan-pinch` for zoom and pan; `@use-gesture/react` for the layout editor |
| Languages | English and Chinese (Simplified) through a Next.js i18n library, with all text in translation files |
| API | ASP.NET Core Web API, C#, Entity Framework Core |
| Real-time | SignalR in the API, `@microsoft/signalr` client in the browser |
| Data | PostgreSQL (ADR-003) |
| Events | Domain events, `IEventPublisher`, PostgreSQL outbox, background worker (ADR-002) |
| Integrations | `IPosAdapter` and `IPaymentProvider` interfaces; mock POS adapter; Stripe test mode |
| Staff sign-in | Amazon Cognito user pool (ADR-004) |
| Contracts | ASP.NET Core OpenAPI document; `orval` generates the TypeScript client and zod schemas (ADR-004) |
| Validation and errors | FluentValidation; central `IExceptionHandler` with Problem Details (ADR-004) |
| Logging and tracing | `ILogger` and OpenTelemetry to Amazon CloudWatch and AWS X-Ray (ADR-004) |
| Tests | xUnit, Testcontainers, Playwright |
| Hosting | AWS: Amazon Lightsail containers for the demo (Amazon ECS on Fargate for the pilot), plus Amazon RDS for PostgreSQL (ADR-005) |

## Consequences

**Positive**

- The demo uses the developer's strongest skills, which supports the 2–3 week timeframe.
- Stripe deposits and refunds use official, well-documented components.
- The partner gets a mainstream stack that is easy to hire for in New Zealand, hosted on AWS, the cloud platform they already use.
- Events, POS and payments sit behind interfaces, so the pilot adds real integrations without rewriting business logic.

**Negative or to manage**

- Next.js has more concepts than Nuxt; keep the host app client-side to avoid server-component complexity.
- Bilingual support needs a little more setup than Nuxt's built-in module.
- Run a single API instance for the demo. With more than one instance, SignalR needs sticky sessions on the load balancer or a Redis backplane (Amazon ElastiCache). The developer's certifications are in Azure, so AWS deployment basics need a little learning time.
- An outbox and worker inside the API process is fine for the demo, but must be reviewed for the pilot (separate worker, monitoring).

**Follow-ups**

- [ ] Pin exact framework and library versions at project start, and record them in the repository.
- [ ] Ask the partner which front-end and back-end technologies their own team uses.
- [ ] Ask the partner which AWS services, accounts and region they use; then write ADR-005 on AWS hosting and deployment.
- [x] Write ADR-002 on event-driven architecture, including the event catalogue and payload formats (the outbox tables are in ADR-003).
- [x] Write ADR-003 on data architecture.
- [x] Write ADR-004 on cross-cutting concerns: security, contracts, validation, error handling and logging.

## When to revisit this decision

| Trigger | What to reconsider |
| --- | --- |
| The partner's team builds its own front ends in Vue | Switch the front end to Nuxt before the pilot, so their team can maintain it |
| The partner requires a different back-end language or hosting provider | Back end and hosting choices; the interfaces keep the business logic portable |
| Event volume or the number of consumers grows sharply (many venues, analytics, marketing) | Move from the outbox worker to a message broker, then Kafka if needed |
| Bilingual or right-to-left languages beyond Chinese are needed | The i18n approach |
| The demo misses QA-1 or QA-2 targets | The real-time approach, e.g. a Redis backplane for SignalR or an AWS-native WebSocket service |
| The host features must live inside the partner's iPad POS app, or need device features such as receipt printers, Bluetooth or NFC | A native host app (React Native, or a native wrapper around the web app), reusing the same API and TypeScript types |

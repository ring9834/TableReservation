# ADR-005: AWS Hosting and Deployment

Nov 5, 2026 · @what's Parse Server

**Decision:** for the demo, run the API as one container on Amazon Lightsail, with Amazon RDS for PostgreSQL and the front end as static files on Amazon S3 behind Amazon CloudFront. For the pilot, the same container image moves to Amazon ECS on Fargate in the partner's own AWS account. Everything is defined in AWS CDK (C#) and deployed by GitHub Actions. All data stays in AWS.

## Status and context

| Item | Value |
| --- | --- |
| Status | Proposed; region and account to confirm with the partner |
| Scope | Reservation demo, then pilot |
| Builds on | ADR-001 (technology selection), ADR-002 (event-driven architecture), ADR-003 (data architecture), ADR-004 (cross-cutting concerns) |

The demo must be online at a public web address for 2–3 weeks of building and a 20-minute show, at close to zero cost. The pilot must run real venues reliably, in an account the partner owns. This ADR chooses a set-up for each, and keeps the move from one to the other small: the same container, database engine, front-end files and pipeline.

## Decision drivers

| ID | Driver | Source |
| --- | --- | --- |
| H1 | All data, logs and backups stay in AWS | ADR-001 D10, ADR-004 CC7 |
| H2 | Near-zero cost for the demo; predictable cost for the pilot | ADR-001 D7 |
| H3 | One developer can set it up in a day or two and redeploy in minutes | ADR-001 D6 |
| H4 | Demo to pilot without rewriting: same image, same database, same pipeline | ADR-001 D9 |
| H5 | The pilot survives a server or zone failure during service | Demo requirements QA (availability) |
| H6 | Secrets never in code; the database never on the public internet in the pilot | ADR-004 security rules |
| H7 | The partner's team can own and run it with standard AWS tools | ADR-001 D8 |

## Region and accounts

Run the demo in Sydney (`ap-southeast-2`) in my own AWS account. The pilot goes in the partner's account, in Auckland (`ap-southeast-6`) if every service we need is offered there, otherwise in Sydney.

| Region | For | Notes |
| --- | --- | --- |
| Sydney, `ap-southeast-2` | Demo; pilot fallback | Every service in this ADR is available, Lightsail included; about 25–35 ms from Auckland, which is fine for a booking app |
| Auckland, `ap-southeast-6` | Pilot, preferred | Opened in September 2025 ([AWS](https://aws.amazon.com/blogs/aws/now-open-aws-asia-pacific-new-zealand-region)). Lowest latency for NZ venues. The partner did not ask for data to stay in NZ, but NZ hosting can help with venues that ask. Check that ECS on Fargate, Cognito, ElastiCache and the other services here are offered before choosing it |

### Accounts

| Account | Owner | Holds |
| --- | --- | --- |
| Demo | Antonio | The demo only, with sample data (Golden Dragon); budget alarm at USD 50 a month |
| Pilot: `staging` | Partner, in their AWS Organization | A copy of production for testing releases |
| Pilot: `production` | Partner | Real venues and guests |

We sign in with AWS IAM Identity Center and short-lived roles, not IAM users with long-lived keys. The root user of each account has MFA and is not used day to day.

## Compute options

### API, SignalR hub and outbox worker

The API is one .NET container image. It holds the SignalR hub and, in the demo, the outbox worker (ADR-002). The image is built once and runs unchanged on Lightsail and on ECS.

| Option | Fit | Approximate cost | Verdict |
| --- | --- | --- | --- |
| **Amazon Lightsail container service** | Push an image and get an HTTPS address; custom domain and certificate built in; reaches RDS through Lightsail VPC peering | Micro node (0.25 vCPU, 1 GB): USD 10 a month ([pricing guide](https://cloudburn.io/blog/amazon-lightsail-pricing)) | **Chosen for the demo** |
| **Amazon ECS on Fargate** (standard service, or ECS Express Mode) | No servers to patch; runs in private subnets behind a load balancer; scales and spreads across zones; separate services for API and worker | Two small tasks plus a load balancer: roughly USD 50–80 a month | **Chosen for the pilot** |
| AWS App Runner | Was the simplest container service | — | Rejected: closed to new customers, no new features planned ([AWS](https://docs.aws.amazon.com/ja_jp/apprunner/latest/dg/apprunner-availability-change.html)) |
| AWS Elastic Beanstalk | Older platform around EC2 | Similar to EC2 | Rejected: more to look after than Fargate, fewer reasons to choose it today |
| One EC2 instance running Docker | Cheapest for always-on | About USD 8–15 a month | Rejected: we would patch the OS and handle restarts ourselves |
| AWS Lambda | Pay per request | Low | Rejected: long-lived SignalR connections and an always-running outbox worker do not fit |
| Amazon EKS (Kubernetes) | Powerful | Over USD 70 a month for the control plane alone | Rejected: far more than one app needs |

ECS Express Mode is AWS's suggested replacement for App Runner. In one call, it creates the Fargate service, load balancer, scaling and a default address, at no charge beyond those resources. We may use it for the pilot's first set-up and keep the resulting resources in CDK.

### Front end

| Option | Fit | Verdict |
| --- | --- | --- |
| **Static export on S3 + CloudFront** | The Next.js host app and guest page are mostly client-side (ADR-001), so `output: 'export'` produces plain files. CloudFront serves them from edge locations, including in NZ. Costs cents a month | **Chosen** |
| AWS Amplify Hosting | Supports Next.js server rendering if we need it later | Kept as the fallback if server rendering becomes necessary |
| Serve the front end from the API container | One deployable | Rejected: mixes two release cycles and adds load to the API |

A static export rules out Next.js server features (server actions, server-side rendering on request). This matches ADR-001, but it is a real limit, and it is listed under Revisit.

## Target architecture

&#91;embedded content: demo and pilot set-ups on AWS\]

The front end and the API image are the same in both set-ups. The pilot puts the API behind a load balancer, splits out the worker, and moves the database and tasks into private subnets across two zones.

## Data, files and secrets

| Item | Demo | Pilot |
| --- | --- | --- |
| Database | Amazon RDS for PostgreSQL, `db.t4g.micro`, one zone, 20 GB, encrypted, not publicly reachable; Lightsail reaches it through VPC peering | RDS for PostgreSQL, `db.t4g.small` or larger, Multi-AZ, private subnets only, encrypted with a KMS key, Performance Insights on |
| Connections | Direct from the one API container | Through RDS Proxy if connection counts grow with API and worker tasks |
| Backups | Automated backups, 7 days, point-in-time recovery | Automated backups, 14–35 days; daily snapshot copied to a second region for disaster recovery |
| Files | S3 bucket for floor plan background images (ADR-001), private, served through CloudFront | Same, with versioning and a lifecycle rule |
| Secrets | AWS Secrets Manager: database password, Stripe keys, POS webhook secret, link-signing key | Same, plus automatic rotation of the database password |
| Settings | Environment variables in the container definition (not secret) | Same, plus AWS AppConfig if feature flags need staged rollout (ADR-004) |
| Cache and SignalR backplane | None (one container) | Amazon ElastiCache for Redis (Valkey engine), only when the API runs more than one task (ADR-002) |

The API reads secrets at start-up through the .NET configuration provider for Secrets Manager, and options validation stops it if one is missing (ADR-004). The demo database holds sample data only; it is deleted when the demo account closes.

### Recovery targets for the pilot

| Measure | Target | How |
| --- | --- | --- |
| Recovery point (data lost at most) | 5 minutes | RDS point-in-time recovery |
| Recovery time, one zone failure | Under 5 minutes, automatic | Multi-AZ failover; ECS tasks in two zones |
| Recovery time, a whole region lost | Under 4 hours, manual | Restore the cross-region snapshot; redeploy with CDK |

These targets are proposals for the partner to accept or change. A restore drill runs once before the pilot starts.

## Network and edge security

| Item | Demo | Pilot |
| --- | --- | --- |
| Domains | A demo domain in Route 53, e.g. `app.<demo-domain>` (front end) and `api.<demo-domain>` (API) | The partner's domain, or a product domain they choose |
| TLS certificates | Lightsail's built-in certificate for the API; AWS Certificate Manager for CloudFront | ACM for CloudFront and the load balancer; TLS 1.2 or later |
| Front end edge | CloudFront with origin access control, so the S3 bucket is never public | Same |
| API edge | Lightsail public endpoint | Application Load Balancer in public subnets; API and worker tasks in private subnets |
| Firewall | Rate limits in the API (ADR-004) | AWS WAF on CloudFront and the load balancer: AWS managed rule sets, an IP rate rule for booking endpoints, and a geographic rule if the partner wants it |
| Database access | Only from the Lightsail containers through peering; security group allows port 5432 from that range only | Only from ECS task security groups; no public address |
| Outbound calls | Direct to Stripe, the text provider and the POS | Through a NAT gateway, or VPC endpoints for AWS services (Secrets Manager, ECR, CloudWatch) to cut NAT traffic |
| Admin access | AWS console with IAM Identity Center; no SSH | Same; ECS Exec for rare debugging, logged in CloudTrail |

**SignalR over the edge:** host screens connect to `api.…` directly, not through CloudFront. The load balancer supports WebSockets. On Lightsail, if a long-lived WebSocket connection is dropped, SignalR falls back to Server-Sent Events or long polling by itself. This is checked in the first week of the build.

**Audit:** AWS CloudTrail is on in every account. The pilot also turns on GuardDuty and AWS Config's basic security rules.

## Build and deployment

### Infrastructure as code

| Option | Verdict | Why |
| --- | --- | --- |
| **AWS CDK in C#** | **Chosen** | Same language as the API; typed; generates CloudFormation, which the partner's AWS account already understands; one stack per environment |
| Terraform | Good alternative | Widely used and cloud-neutral; choose it instead if the partner's team already uses it |
| Hand-written CloudFormation | Rejected | Verbose and slow to change |
| Clicking in the console | Rejected | Cannot be repeated or reviewed; the demo account would drift from the pilot |

### Environments

| Environment | Where | Deployed |
| --- | --- | --- |
| Local | Docker Compose: PostgreSQL, API, mock POS; `next dev` for the front end | On every save |
| Demo | Demo account, Sydney | Every merge to `main` |
| Pilot staging | Partner account | Every merge to `main`, after the demo is retired |
| Pilot production | Partner account | Manually approved release of a build already tested in staging |

### Pipeline (GitHub Actions)

1. **Check:** build, unit tests, Testcontainers integration tests, `oasdiff` contract check, dependency scan (ADR-004).
2. **Package:** build the API image once, tag it with the git commit, scan it, push to Amazon ECR. Build the front-end static files.
3. **Migrate:** run an EF Core migration bundle as a one-off task against the target database.
4. **Deploy API:** demo: a new Lightsail deployment with the new image; pilot: ECS rolling deployment with the circuit breaker, which rolls back by itself if health checks fail.
5. **Deploy front end:** upload to S3, then invalidate CloudFront.
6. **Verify:** Playwright smoke test of the demo script against the deployed address; a failure alerts and blocks the next release.

GitHub signs in to AWS through OpenID Connect and a deploy role per account, so no AWS keys are stored in GitHub.

### Database changes and rollback

- Migrations are written **expand, then contract**: add the new column or table first; drop the old one only in a later release. A new release then works with the old schema, and the old release with the new one.
- Rolling back means redeploying the previous image tag. No database rollback is needed, because of the rule above.
- A migration that cannot follow the rule, such as a large data change, gets its own release with a snapshot taken just before it.

## Monitoring, log retention and cost

### Monitoring

- **Logs:** container output goes to CloudWatch Logs as JSON (ADR-004). Lightsail collects container logs itself; the demo forwards them, or reads them in the Lightsail console.
- **Traces and metrics:** the AWS Distro for OpenTelemetry collector runs as a sidecar in the pilot and sends traces to X-Ray and metrics to CloudWatch.
- **Alarms:** the ADR-004 alarms (failed outbox row, 5xx rate, outbox lag, POS unreachable), plus RDS CPU and free storage, and ECS task restarts. Alarms send email and a phone notification through Amazon SNS to whoever is on call.
- **Dashboard:** one CloudWatch dashboard per environment, showing requests, errors, outbox lag, active SignalR connections and database load.
- **Cost:** an AWS Budgets alert at 80% and 100% of the monthly budget in every account.

### Retention (proposed)

| Data | Demo | Pilot |
| --- | --- | --- |
| Application logs | 14 days | 30 days, then deleted |
| Traces | X-Ray default (30 days) | Same |
| CloudTrail | 90 days (event history) | 1 year in an S3 bucket the partner owns |
| Database backups | 7 days | 14–35 days, plus cross-region copy |

The audit log in the database follows ADR-003, not these periods.

### Running cost estimate (USD a month, before tax)

These are my rough estimates for planning, to be checked in the AWS Pricing Calculator for the chosen region. AWS bills in US dollars.

| Item | Demo | Pilot |
| --- | --- | --- |
| API compute | 10 (Lightsail Micro) | 45 (two API tasks and one worker task on Fargate) |
| Load balancer | — | 22 |
| Database | 18 (`db.t4g.micro`, one zone) | 65 (`db.t4g.small`, Multi-AZ) |
| NAT gateway or VPC endpoints | — | 40 |
| Redis backplane | — | 15 (only with two or more API tasks) |
| WAF | — | 12 |
| CloudFront, S3, Route 53, ECR | 2 | 5 |
| Secrets Manager, CloudWatch, X-Ray | 4 | 15 |
| Cognito | 0 at demo user numbers | Small at pilot staff numbers |
| **Total, approximately** | **35** | **220** |

The text-message provider, Stripe fees and the domain name are paid separately, and are not included.

## Decision summary

| Area | Demo | Pilot |
| --- | --- | --- |
| Account and region | Antonio's account, Sydney (`ap-southeast-2`) | Partner's accounts (staging, production); Auckland (`ap-southeast-6`) if all services are offered, else Sydney |
| API, hub and worker | One Lightsail container (Micro) | ECS on Fargate: API service (2+ tasks, two zones) and worker service |
| Front end | Next.js static export on S3 + CloudFront | Same |
| Database | RDS PostgreSQL `db.t4g.micro`, one zone | RDS PostgreSQL Multi-AZ, private subnets, cross-region backup copy |
| Secrets | Secrets Manager | Secrets Manager with rotation |
| Edge | Lightsail HTTPS endpoint; CloudFront | Load balancer + WAF; CloudFront + WAF |
| Infrastructure | AWS CDK (C#) | Same stacks, new parameters |
| Pipeline | GitHub Actions with OIDC; build once, deploy on merge | Same, with a manual approval for production |
| Monitoring | CloudWatch Logs, budget alarm | CloudWatch, X-Ray, alarms through SNS, GuardDuty |

## Consequences

**Good**

- The demo costs about USD 35 a month and can be set up in a day.
- The same container image, database engine, front-end files and pipeline carry over to the pilot, so the move is a configuration change, not a rewrite.
- The pilot runs in the partner's own account, so they own the data, the bills and the access from day one.
- No servers to patch in either set-up.

**Costs and risks**

- The demo has no redundancy: one container and one database zone. That is acceptable for a demo, not for service.
- Lightsail has fewer controls than ECS (no WAF, limited networking), so security tests from the demo do not prove the pilot.
- The static export rules out Next.js server rendering; adding it later means moving the front end to Amplify Hosting or a container.
- The pilot's monthly cost is about six times the demo's, mainly the load balancer, NAT and Multi-AZ database.
- The Auckland region is new; a missing service would force Sydney or a workaround.

## Revisit when

- The partner names the account, region or tools they already use (for example Terraform, or an existing VPC).
- Any Next.js page needs server rendering or a server-side secret.
- More than about 20 venues, or API CPU above 60% at peak service times.
- Lightsail does not keep SignalR connections reliably during the first-week check.
- AWS adds or retires a service used here, as it did with App Runner.

## Open questions

- Which AWS Organization and accounts should the pilot use, and who in the partner's team will approve production releases?
- Does the partner prefer Auckland for the pilot, or keep everything in Sydney with their other systems?
- What uptime and recovery targets do they promise venues today for the POS?
- Which domain should guests see on booking pages and texts?

## Sources

- [Now open: AWS Asia Pacific (New Zealand) Region](https://aws.amazon.com/blogs/aws/now-open-aws-asia-pacific-new-zealand-region) (AWS News Blog)
- [AWS App Runner availability change](https://docs.aws.amazon.com/ja_jp/apprunner/latest/dg/apprunner-availability-change.html) (AWS documentation)
- [Amazon Lightsail pricing: 2026 guide](https://cloudburn.io/blog/amazon-lightsail-pricing) (Cloudburn)

# RUNBOOK: the AWS demo

How to deploy the reservation demo to AWS, check it, and delete and recreate the app between rehearsals. Every command
here changes AWS and is run by the operator, in **PowerShell 7.4 or later** (`pwsh`), from the repository root unless a
step says otherwise. Claude only runs the read-only ones (CLAUDE.md "AWS safety"). Background: ADR-005 (clarification of
17 November 2026) and Design §17.4.

| | |
| --- | --- |
| Account | `821797257125` (demo) |
| Region | `ap-southeast-2` (Sydney) |
| CLI profile | `reservation-demo` (temporary credentials from `aws login`) |
| Stacks | `ReservationDemo-Permanent` (address, bucket, secrets, budget: kept) and `ReservationDemo-App` (Lightsail API, RDS: deletable) |
| Public address | `https://<id>.cloudfront.net`, one origin for the site, `/api/*`, `/hubs/*` and `/health/*` |

**Never** run `aws lightsail get-container-services` without `--query`, or `aws lightsail get-container-service-deployments`,
or `aws cloudfront get-distribution(-config)`, where anyone can see your screen: their output holds the container's
secrets or the origin header. The commands below read endpoints from the stack outputs instead.

## Costs and spend limits

| Resource | US$ / month (approx., Sydney) | When the App stack is deleted |
| --- | --- | --- |
| Lightsail container service, Micro (0.25 vCPU, 1 GB, 500 GB transfer) | 10.00 | 0 |
| RDS PostgreSQL `db.t4g.micro`, single-AZ | ~18.25 | 0 |
| RDS storage, 20 GB gp3 | ~2.76 | 0 |
| RDS backups, 7 days (free up to the database size) | 0 | 0 |
| Secrets Manager: 4 in Permanent + 1 database secret in App, US$0.40 each | 2.00 | 1.60 |
| CloudFront (always-free tier: 1 TB, 10 M requests), CloudFront Functions (2 M free) | ~0 | ~0 |
| S3 (the static site, a few MB) | < 0.05 | < 0.05 |
| SSM parameter, AWS Budgets (the first two are free) | 0 | 0 |
| **Total** | **≈ 33** | **≈ 1.65** |

Stripe fees (test mode: none) and the text-message provider (the demo shows texts on screen) are not AWS costs.

**Two separate spend controls:**

- **The account's project spend limit of US$50** (AWS console → Settings → Billing). When spending reaches it, AWS
  **pauses all resources**: the site, the API and the database stop until the limit is raised or the month resets. Plan
  rehearsals so it is never reached.
- **The AWS Budget of US$50** in the Permanent stack (`reservation-demo-monthly`). It only **alerts**: an email at **80 %
  (US$40)** and at **100 % (US$50)** of actual spend, so you hear about it before the pause. It stops nothing.

At about US$33 a month a full month stays under both. Delete the App stack (§9) between rehearsal weeks to keep a margin.

## 0. Once per machine

```powershell
$PSVersionTable.PSVersion          # 7.4 or later (Windows PowerShell 5.1 mangles the JSON in step 4)
aws --version                      # AWS CLI v2
docker version                     # Docker Desktop running (the API image is built locally)
node --version                     # 22
# Lightsail's push needs the lightsailctl plugin: download lightsailctl.exe from the AWS documentation
# ("Install the Lightsail Control plugin", Windows) into a folder on PATH, then check:
lightsailctl --version
cd infra; npm ci; cd ..            # the pinned CDK CLI (npx cdk …)
```

## 1. Sign in (every session)

```powershell
aws login --profile reservation-demo
$env:AWS_PROFILE = "reservation-demo"
$env:AWS_REGION  = "ap-southeast-2"
aws sts get-caller-identity --query Account --output text   # 821797257125
```

Helper used by later steps (paste once per session):

```powershell
function Get-StackOutput([string]$Stack, [string]$Key) {
  aws cloudformation describe-stacks --stack-name $Stack --query "Stacks[0].Outputs[?OutputKey=='$Key'].OutputValue" --output text
}
$budgetEmail = "<your email for budget alerts>"   # typed here, never committed
```

## 2. Bootstrap CDK (once per account and region)

Creates the CDK toolkit stack (an S3 bucket and the deploy roles CDK assumes). No IAM users or access keys.

```powershell
cd infra
npx cdk bootstrap aws://821797257125/ap-southeast-2
cd ..
```

## 3. Deploy the Permanent stack (first time)

Without `ApiOrigin` the distribution serves the site only; the API routes are added in step 9.

```powershell
cd infra
npx cdk deploy ReservationDemo-Permanent --exclusively -c BudgetEmail=$budgetEmail
cd ..
$site   = Get-StackOutput ReservationDemo-Permanent SiteUrl          # https://dxxxxxxxxxxxx.cloudfront.net
$bucket = Get-StackOutput ReservationDemo-Permanent BucketName
$distId = Get-StackOutput ReservationDemo-Permanent DistributionId
$site
```

The stack has termination protection. It creates four secrets; three are generated in AWS and nobody needs to see them
(`reservation-demo/guest-links-signing-key`, `/pos-webhook-secret`, `/origin-verify`). The fourth,
`reservation-demo/operator`, holds empty keys until step 4.

## 4. Stripe webhook and the operator secret

1. Stripe Dashboard, **sandbox / test mode** → Developers → Webhooks → **Add endpoint**:
   - Endpoint URL: `<$site>/api/v1/webhooks/stripe`
   - Events: `payment_intent.succeeded` and `charge.refunded`
   - Save, then **Reveal** the signing secret (`whsec_…`).
2. Put the five values into `reservation-demo/operator`. Choose **new** passcodes for AWS, never the local ones, and two
   different ones. Values are typed at hidden prompts, live only in this session's memory, and never reach a file or
   the PowerShell history (only the command text does):

```powershell
function Read-Secret([string]$Prompt) {
  [System.Net.NetworkCredential]::new('', (Read-Host -AsSecureString $Prompt)).Password
}
$operator = [ordered]@{
  HostPasscode         = Read-Secret 'Host passcode (new for AWS)'
  PresenterPasscode    = Read-Secret 'Presenter passcode (new, different)'
  StripeSecretKey      = Read-Secret 'Stripe secret key (sk_test_...)'
  StripePublishableKey = Read-Secret 'Stripe publishable key (pk_test_...)'
  StripeWebhookSecret  = Read-Secret 'Stripe webhook signing secret (whsec_...)'
}
aws secretsmanager put-secret-value --secret-id reservation-demo/operator --secret-string ($operator | ConvertTo-Json -Compress) --query VersionId --output text
Remove-Variable operator
```

To change one value later, run the same block with all five (the secret is replaced as a whole), then redeploy the app
with a new `DeployId` (step 7) so the container gets it.

## 5. Lightsail VPC peering (once per account and region)

Connects Lightsail's VPC to the default VPC, where RDS runs. It stays when the App stack is deleted.

```powershell
aws ec2 describe-vpcs --filters Name=isDefault,Values=true --query "Vpcs[0].VpcId" --output text   # must exist
aws lightsail peer-vpc
aws lightsail is-vpc-peered                                                                       # isPeered: true
aws ec2 describe-vpc-peering-connections --query "VpcPeeringConnections[].[RequesterVpcInfo.CidrBlock,AccepterVpcInfo.CidrBlock]" --output text
```

One of the two ranges is Lightsail's (normally `172.26.0.0/16`), the other the default VPC's (normally `172.31.0.0/16`).
If Lightsail's is not `172.26.0.0/16`, add `-c LightsailCidr=<range>` to every App deploy below.

## 6. Deploy the App stack, then build and push the image

```powershell
cd infra
npx cdk deploy ReservationDemo-App     # first time ~10-15 min (RDS); no container deployment yet
cd ..

$tag = git rev-parse --short HEAD
dotnet publish src/Api -c Release /t:PublishContainer -p:ContainerImageTag=$tag     # local image reservation-api:$tag
aws lightsail push-container-image --service-name reservation-api --label api --image "reservation-api:$tag"
# The output ends with: Refer to this image as ":reservation-api.api.N" in deployments.
$image = ":reservation-api.api.N"      # ← the exact name from that line
```

## 7. Deploy the container

`DeployId` changes the deployment, so CloudFormation resolves the secrets again (needed after a secret changed).

```powershell
cd infra
npx cdk deploy ReservationDemo-App -c ImageName=$image -c DeployId=(Get-Date -Format yyyyMMdd-HHmmss)
cd ..
$api = Get-StackOutput ReservationDemo-App ApiEndpoint     # https://reservation-api.xxxx.ap-southeast-2.cs.amazonlightsail.com/
```

On start-up the API (environment `Demo`) migrates the empty database and seeds tonight's Golden Dragon evening at 18:25.

## 8. Build and upload the web app

Same origin, so the API base URL is empty. While the CSP is report-only (§10), build with `CSP_MODE=report`; after that,
leave it out (enforce is the default).

```powershell
cd web
$env:NEXT_PUBLIC_API_BASE_URL = ""
$env:CSP_MODE = "report"          # report phase only; Remove-Item Env:CSP_MODE for enforce
npm ci
npm run build
cd ..

# Hashed files first (immutable), then the pages, so a page never references a missing file.
aws s3 sync web/out/_next/static "s3://$bucket/_next/static" --cache-control "public, max-age=31536000, immutable"
aws s3 sync web/out "s3://$bucket" --delete --exclude "_next/static/*" --exclude "csp-script-hashes.json" --cache-control "no-cache"
aws s3 cp web/out/host.webmanifest "s3://$bucket/host.webmanifest" --content-type "application/manifest+json" --cache-control "no-cache"
aws cloudfront create-invalidation --distribution-id $distId --paths "/*" --query Invalidation.Id --output text
```

In the report phase the page hashes change with every build, so after each upload redeploy Permanent (step 9) with the
new hashes file.

## 9. Point the distribution at the API

```powershell
cd infra
npx cdk deploy ReservationDemo-Permanent -c ApiOrigin=$api -c BudgetEmail=$budgetEmail -c CspMode=report -c CspScriptHashesFile=..\web\out\csp-script-hashes.json
cd ..
```

`web\out\csp-script-hashes.json` comes from step 8's report-mode build. The `cloudfront.net` address doesn't change;
only the origin does (a few minutes to spread). Always pass `-c ApiOrigin`: a deploy without it removes the API routes.

## 10. Checks

```powershell
curl.exe -s -o NUL -w "%{http_code}`n" "$site/health/ready"            # 200: through CloudFront, database up
curl.exe -s -o NUL -w "%{http_code}`n" "$($api.TrimEnd('/'))/health/ready"   # 403: direct to Lightsail is refused
curl.exe -s -o NUL -w "%{http_code}`n" "$($api.TrimEnd('/'))/health/live"    # 200: Lightsail's own health check
curl.exe -sI "$site/host/"           | Select-String "HTTP/|content-security-policy|strict-transport"
curl.exe -sI "$site/host.webmanifest" | Select-String "content-type"         # application/manifest+json
curl.exe -sI "$site/manage"           | Select-String "HTTP/|location"        # 301 → /manage/
aws lightsail get-container-log --service-name reservation-api --container-name api --query "logEvents[-20:].message" --output text
```

`/health/ready` = 200 also proves the secrets were resolved: an unresolved `{{resolve:…}}` in the connection string
would fail the database check. The container log must show no personal data (CLAUDE.md rule 9).

After one request through the site (open `$site/book/`), the container log must **not** contain
`The CDN sent no usable CloudFront-Viewer-Address`. If it does, the managed origin request policy isn't forwarding the
viewer's address, every guest shares one rate-limit bucket, and the edge configuration needs another look (Design §17.4).

Then open `$site/demo-login/` and sign in with the new host passcode.

### 10a. CSP: report first, then enforce

1. With `CspMode=report` (steps 8–9), run the full Playwright suite against the deployed address (§11).
2. By hand, in Edge with DevTools (Console) open:
   - book a party of 6 and pay with **4242 4242 4242 4242** (any future date, any CVC);
   - book again and pay with the **3-D Secure** test card **4000 0027 6000 3184**, and complete the challenge;
   - open `/host/` (floor plan, a booking, walk-in), `/presenter/` (phone panel: open a guest's `/manage/` inside the
     phone, POS log, demo controls) and install the host app (Design §18.11 "Manual install check").
3. Look for **`[Report Only]`** Content-Security-Policy messages on every screen. If there are any, copy them to Claude
   and fix the cause; don't loosen the policy blindly.
4. When the console is clean: rebuild without `CSP_MODE` (enforce), upload and invalidate (step 8), then

```powershell
cd infra
npx cdk deploy ReservationDemo-Permanent -c ApiOrigin=$api -c BudgetEmail=$budgetEmail -c CspMode=enforce
cd ..
```

   and run Playwright once more. From now on the Permanent stack no longer depends on web builds.

## 11. Playwright against the deployed address

`Read-Secret` is the helper from step 4.

```powershell
cd tests/E2E
$env:E2E_BASE_URL       = $site
$env:E2E_API_BASE_URL   = $site
$env:E2E_STRIPE_WEBHOOK = "deployed"     # Stripe test mode through the deployed webhook; no `stripe listen`
$env:E2E_HOST_PASSCODE      = Read-Secret 'Host passcode'
$env:E2E_PRESENTER_PASSCODE = Read-Secret 'Presenter passcode'
npx playwright test --project mobile-edge
Remove-Item Env:E2E_HOST_PASSCODE, Env:E2E_PRESENTER_PASSCODE
cd ../..
```

Sign-in is limited to 5 attempts a minute per viewer IP, now the real one: leave a minute between full runs.

## 12. Everyday jobs

| Job | Steps |
| --- | --- |
| New API version | 6 (build and push only) → 7 with the new `$image` |
| New web version | 8 (and 9 again while the CSP is report-only) |
| Change an operator value | 4 (all five values), then 7 with a new `DeployId` |
| Container logs | `aws lightsail get-container-log --service-name reservation-api --container-name api` |
| Reset the demo data | The presenter's **Reset** button (or `POST /api/v1/demo/reset` as presenter); a database from an earlier day resets itself on start-up |

## 13. Delete the app, and recreate it

Delete (the address, the bucket with the site, the secrets and the budget stay; cost falls to about US$1.65 a month):

```powershell
cd infra
npx cdk destroy ReservationDemo-App    # ~10 min; the database (seeded data only) and the pushed images go with it
cd ..
```

While it is deleted, the site loads but every API call fails (CloudFront can't reach the old origin).

Recreate later: sign in (1), then steps **6 → 7 → 9** (new image push, container, and the new Lightsail host into the
distribution). No web upload is needed, and the `cloudfront.net` address and Stripe webhook stay the same. The peering
(5) and the secrets (4) are still there.

To remove everything for good (only when the demo is over): delete the App stack, then turn off termination protection
on `ReservationDemo-Permanent` in the CloudFormation console, `npx cdk destroy ReservationDemo-Permanent`, and empty and
delete the retained bucket and secrets by hand.

# Production Setup Runbook — the manual ops ledger

> **Purpose.** Terraform and CI own most of the platform, but dev only works because of a
> series of **manual operations** performed outside them — console grants, API registrations,
> credential provisioning, physical-device checks. This runbook is the ledger of every such
> operation, in the order production needs them, each with the exact commands that worked in
> dev and its **dev status** as evidence. If a step is not in this document and not in
> Terraform/CI, it did not happen.
>
> Scope: GCP + external-service operations. Deeper per-topic runbooks are linked rather than
> duplicated; this document is the index and the order.

## The order

Steps 1–5 make the platform deployable. Steps 6–9 make the AI pipeline live — including
step 7a (the WAF cutover), reachable once step 6 is done and a prerequisite of step 9's smoke
check. Steps 10–12 are gates that must clear before prod serves real families.

---

### 1. GCP projects and OAuth clients (per-project, console-only)

Four projects exist because Google's consent-screen verification is per-project
([oauth_clients.md](oauth_clients.md)): infra (`carditrack-490120`), sign-in, `carditrack-devices-dev`,
`carditrack-devices-prod`. All clients under the **cloud-ops account**
(`cloudoperations@codesistance.com`), never a personal account.

- **Dev status:** provisioned 2026-08-07. `carditrack-devices-dev` stays in Testing
  permanently (100-user cap, test users only).
- **Prod:** `carditrack-devices-prod` needs the consent screen completed and — before
  exceeding 100 users — Google **restricted-scope verification / CASA assessment** (step 12).

#### 1b. Consent-screen scope: `googlehealth.settings.readonly` (console-only)

Added to the requested scopes 2026-08-13 (PR #262) to read wearable battery from
`GET /v4/users/me/pairedDevices` (`PairedDevice.batteryLevel` / `batteryStatus`). Terraform
carries it in `dev.tfvars` / `prod.tfvars`, but **Terraform cannot add a scope to a consent
screen** — it must be added by hand in each devices project, or every authorization request
naming it is rejected and no wearer can grant it.

**Google Auth Platform → Data Access → Add or remove scopes** in `carditrack-devices-dev`
and `carditrack-devices-prod`, alongside the three existing read bundles:

```
https://www.googleapis.com/auth/googlehealth.settings.readonly
```

Two ordering points:

- **Add it in prod before the step 12 submission.** A scope added after verification starts
  means a second review — this is why it went into `prod.tfvars` now rather than later.
- **Existing wearers are unaffected and stay that way.** Their refresh tokens carry the
  original three-scope grant, so they report no battery until they reconnect. Handled as a
  normal state end to end (the sync skips the call without the scope; the client returns
  empty on the 403) — it never fails a sync and needs no coordinated migration.

- **Dev status:** ⚠️ **outstanding** — code and Terraform merged, console scope not yet
  added. Until it is, `pairedDevices` is never called and every device reports no battery.
  Verify by connecting a fresh test wearer and confirming a `batteryLevel` on
  `GET /api/v1/cardimembers/{id}/devices`.

### 2. Deployer IAM bootstrap (console/gcloud, NOT Terraform-managed)

The CI deployer service account needs roles that Terraform applies themselves require —
grants must precede the first apply that creates service accounts, schedulers, or Pub/Sub.

```
gcloud projects add-iam-policy-binding <PROJECT> --member "serviceAccount:<DEPLOYER_SA>" --role roles/iam.serviceAccountAdmin
gcloud projects add-iam-policy-binding <PROJECT> --member "serviceAccount:<DEPLOYER_SA>" --role roles/iam.serviceAccountUser
gcloud projects add-iam-policy-binding <PROJECT> --member "serviceAccount:<DEPLOYER_SA>" --role roles/cloudscheduler.admin
gcloud projects add-iam-policy-binding <PROJECT> --member "serviceAccount:<DEPLOYER_SA>" --role roles/pubsub.admin
gcloud projects add-iam-policy-binding <PROJECT> --member "serviceAccount:<DEPLOYER_SA>" --role roles/monitoring.uptimeCheckConfigEditor
```

- **Dev status:** the first four granted manually 2026-08-10 for
  `carditrack-deploy@carditrack-490120.iam.gserviceaccount.com` after applies failed with
  `iam.serviceAccounts.create` denials. `monitoring.uptimeCheckConfigEditor` added 2026-08-13
  after the same thing happened again, this time with
  `monitoring.uptimeCheckConfigs.create` denied.
- **Prod:** the prod project's deployer needs the **same five roles** before flipping any
  pipeline flag (step 6).

> **Why this one is worth grabbing before prod rather than after:** the failing apply is only
> half a failure. The deployer already holds `monitoring.alertPolicies.create`, so the
> cert-expiry **alert policy is created successfully** and only the uptime checks it reads from
> are rejected. The result is an alert that exists, looks configured in the console, and can
> never fire, because nothing is producing the metric it watches. A monitoring control that is
> silently inert is worse than one that is obviously absent — it answers "are we covered?" with
> a confident yes. Re-run the infra apply after granting, and confirm the uptime checks exist
> rather than trusting the alert policy's presence.

### 3. Secrets that Terraform cannot generate

Terraform generates what it can (the webhook secret is a `random_password`); these are
external values seeded by hand into Secret Manager:

| Secret | Source | Dev status |
|---|---|---|
| Auth0 domain/audience/client-id/client-secret (+ mobile client id) | Auth0 tenant — `scripts/set-auth0-secrets.sh <env>` ([auth0_setup_runbook.md](auth0_setup_runbook.md) §11) | Seeded |
| Google Health OAuth client secret | Devices project's OAuth client (step 1) | Seeded |
| `gemini-api-key` | AI Studio, cloud-ops account | Seeded |
| APM (Datadog) API tokens | Datadog org | **Unprovisioned even in dev** — `apm_engine` is set but tokens are pending ([apm_setup_runbook.md](apm_setup_runbook.md)) |

### 4. Auth0 tenant configuration (per-tenant, manual)

App code for social login + account linking shipped (PR #114); the per-tenant pieces —
Action deployment, M2M app, connections — are **manual per tenant** and were still pending in
dev at the time of writing. Details: [auth0_integration.md](auth0_integration.md) /
[auth0_setup_runbook.md](auth0_setup_runbook.md). Apple credentials come from the cloud-ops
Apple account.

### 5. MedGemma service

MedGemma is the **shared GPU service** `carditrack-common-medgemma`
(`infrastructure/common/cloud_run.tf`) — one instance serves every environment; there is no
per-environment service any more. Prod is **not wired to it yet**: the pipeline-job and
webhook-receiver flags are off (Pub/Sub itself is already provisioned in prod). Enabling: add
prod's service-account emails to `medgemma_invoker_members` in `common.tfvars` and apply the
common stack (the accounts must exist first — apply the prod stack before the grant). The
`carditrack-prod-medgemma-service-url` secret is already seeded with the shared service's URL.
Same `Q4_K_M` tag everywhere — an assessment made in one environment must mean the same in
another, and **no cheaper substitute models** in any environment (cost is managed by
scale-to-zero, not substitution).

### 6. Pipeline enablement flags (Terraform, listed here for ordering)

After steps 2 and 5, flip in `prod.tfvars` and apply:
`enable_pipeline_jobs` and `enable_webhook_receiver` — currently `false` with rationale
comments. `enable_pubsub` is **already `true` in prod**, so the topic/subscription exist;
the apply creates the three pipeline jobs, schedulers, receiver service, and the webhook
secret. CI then owns the images.

### 7. Health webhook Subscriber registration (the step this runbook was born from)

Registers the receiver with Google so notifications flow. **Not Terraform:** no provider
resource exists for the Health API, the Subscriber lives in the devices project (outside
Terraform's state and credentials), and first contact was expected to need iteration.

Verified against the v4 discovery document and live API responses (2026-08-10):

1. Resolve the receiver URL. Once `webhook_custom_domain` is set (dev, since the WAF cutover
   below), this is the custom domain — `https://webhook.<env>.carditrack.com` — not the
   `*.run.app` URL, which stops being externally reachable once ingress flips to
   `INGRESS_TRAFFIC_INTERNAL_LOAD_BALANCER`. Before that, or in an environment with no webhook domain configured:
   `gcloud run services describe carditrack-<env>-webhook-receiver --project <INFRA_PROJECT> --region europe-west2 --format "value(status.url)"`
2. Read the Terraform-generated secret (full `Bearer …` value, scheme included):
   `gcloud secrets versions access latest --secret carditrack-<env>-webhook-secret --project <INFRA_PROJECT>`
3. Create the Subscriber **in the devices project** (`health.subscribers.create` — no
   health-specific IAM roles exist, so the caller needs a basic role there):

```
curl -s -X POST "https://health.googleapis.com/v4/projects/<PROJECT_NUMBER>/subscribers" \
  -H "Authorization: Bearer $(gcloud auth print-access-token)" -H "Content-Type: application/json" \
  -d '{
    "endpointUri": "<RECEIVER_URL>/",
    "endpointAuthorization": {"secret": "<SECRET>"},
    "subscriberConfigs": [{
      "dataTypes": ["heart-rate", "steps", "oxygen-saturation", "active-zone-minutes"],
      "subscriptionCreatePolicy": "AUTOMATIC"
    }]
  }'
```

Facts that cost live errors to learn (all from the 2026-08-10 dev attempt):

- **Schema** (400s): the field is **`dataTypes`** (array, kebab-case), not `dataType`;
  **`subscriptionCreatePolicy` is required**. `AUTOMATIC` computes notification eligibility
  from user consents dynamically — **no per-wearer Subscription calls are ever needed** (the
  older design's "create a Subscription per enrolled wearer" step is obsolete).
- **Quota project** (403 `SERVICE_DISABLED`): user-credential calls must carry
  `-H "x-goog-user-project: carditrack-devices-<env>"`, and the Health API must be enabled on
  that project first: `gcloud services enable health.googleapis.com --project carditrack-devices-<env>`.
- **Project NUMBER, not id, in the URL** (bare 403 `The caller does not have permission`):
  `/v4/projects/carditrack-devices-<env>/…` is wrong — the webhooks guide requires the
  **numeric project number** (`gcloud projects describe carditrack-devices-<env> --format
  "value(projectNumber)"`). This bare 403 briefly masqueraded as an enrollment gate — it is
  not; there is no enrollment. (Diagnostic tell kept for posterity: real project-IAM denials
  come back verbose with the permission name and a Troubleshooter URL; the id-for-number
  mistake returns the bare form.)
- **Path-qualified `endpointUri`**: register
  `https://<receiver-service>/webhooks/google-health`, not the service root — the
  verification probes POST to the registered URI, and the root would 404 them.
- **Roles exist after all**: the guide names Google Health API Read/Editor/Admin roles for
  service-account callers (they do not surface in `list-grantable-roles`); a project
  owner/editor works for a hand-run registration.

4. The endpoint-verification handshake (documented in the webhooks guide): on create/update
   Google sends **two POST probes** (User-Agent `Google-Health-API-Webhooks`, body
   `{"type": "verification"}`) — the authorized one must be answered `200`/`201`, the
   unauthorized one `401`/`403`, else creation fails with `FAILED_PRECONDITION`. The receiver
   conforms as of PR #140 (**deploy it before retrying the create** — its earlier `204`
   acknowledgment fails the first probe). A create that completes IS the handshake verdict —
   the response carries no `state` field (see dev status below); to inspect state later,
   `GET .../v4/projects/<PROJECT_NUMBER>/subscribers`.

- **Dev status: ✅ REGISTERED (2026-08-10)** — Subscriber
  `projects/192920988822/subscribers/a0dab3ee-c9c4-427a-8d21-c197f91306cd`, created
  owner-credentialed from Cloud Shell with the command above. Response shape worth knowing for
  prod: the create returns a **long-running operation wrapper** (`done: true` inline), the
  Subscriber inside carries **no `state` field** on create, and `endpointAuthorization` echoes
  only `"secretSet": true` (the secret is never returned). Receiver logs confirmed the
  documented handshake verbatim: two POSTs from `Google-Health-API-Webhooks` one second apart,
  answered `200` (authorized) and `401` (unauthorized) — a completed create IS the handshake
  verdict, since failure would have surfaced as `FAILED_PRECONDITION`.

#### 7a. Dev WAF cutover (webhook receiver joins api/web behind the GCLB)

The receiver now has its own domain (`webhook_custom_domain` → `webhook.dev.carditrack.com`)
and sits behind the same GCLB + Cloud Armor WAF as api/web (`load_balancer.tf`); ingress
switches from `INGRESS_TRAFFIC_ALL` to `INGRESS_TRAFFIC_INTERNAL_LOAD_BALANCER` the moment the domain is set,
which makes the old `*.run.app` URL unreachable from outside GCP. This is a one-time, ordered
cutover, not a Terraform-only change:

1. `terraform apply` — creates the NEG, backend service, managed cert, and url_map entry, and
   flips the receiver's ingress.
2. Fetch the (shared) LB IP: `terraform output lb_ip_address`.
3. DNS is managed on **Cloudflare**, not Google Cloud DNS — add/update the
   `webhook.dev.carditrack.com` A record there to that IP (same record shape as the existing
   `api.dev` / `app.dev` entries).
4. Wait for the managed cert to go `ACTIVE`: `gcloud compute ssl-certificates describe
   carditrack-dev-webhook-receiver-cert --project <INFRA_PROJECT> --format
   "value(managed.status)"`.
5. Re-run the Subscriber registration in step 7 above against the new
   `https://webhook.dev.carditrack.com/webhooks/google-health` `endpointUri` — Google re-runs
   the verification handshake against the new URI, so this is a normal `subscribers.create`
   (or update), not a special case. The prior `*.run.app`-registered Subscriber stops receiving
   deliveries once ingress flips, since Google can no longer reach it.
6. Confirm with step 9's smoke check.

Until step 5 lands, notifications simply degrade to the 10-minute poll (see the closing note
below) — there is no hard outage window to coordinate.

### 8. HealthApiProbe live-wearer check (human required)

`tools/HealthApiProbe` — hand-run with a real OAuth token against a **live wearer wearing a
real device**. Answers the one question the discovery schema cannot: whether this wearer's
device *populates* the data types we read. Run before trusting an environment's data, and
before verification (step 12) locks marketing claims to data we assumed.

- **Dev status:** outstanding — the last open item from Fitbit/Health provisioning.

### 9. End-to-end pipeline smoke check

With steps 6–7 done: watch a notification arrive (receiver logs → Pub/Sub → aggregator
execution logs → targeted sync → granular rows → assessor execution → `RealtimeAssessments`
row).

> **Do not smoke-check via `/healthz` on the public URL**: Google Frontend reserves the
> exact path `/healthz` on `run.app` domains and answers its own 404 without forwarding —
> platform behavior, not an outage (verified 2026-08-10: `/Healthz` reaches the container
> and returns 200 because routing is case-insensitive while GFE's interception is
> case-sensitive; Cloud Run's internal startup probes bypass GFE and are unaffected). Probe
> a real route instead — e.g. the receiver answers 405 on `GET /webhooks/google-health`.

Webhooks are the fast path, not a dependency: if the Subscriber ever degrades, the pipeline
still runs whole on the **10-minute poll** — notifications only make it fresher.

- **Dev status (2026-08-13):** delivery ✅, aggregation ❌ **→ second fix shipped, awaiting
  confirmation.** Notifications *are* arriving — the aggregator logs `Notifications: 1` on most
  5-minute runs, where earlier in the day it logged `0`. But **every one was dropped**:
  `unparseable: 1`, `connections synced: 0`, on every run since deployment, so no notification has
  ever triggered a sync. The poll fallback is exactly why this looked healthy for days — freshness
  was silently lost, correctness never was.

  Two fixes, because the first was wrong. The parser was rejecting the notification outright, and
  the initial diagnosis assumed the body named wearers the way the `Subscription` resource does
  (`users/{id}/dataTypes/{type}`) — inferred from the discovery document, since the notification
  body itself has no published schema. It does not. Shipped alongside that guess was a deepened
  version of the shape diagnostic, and *that* is what settled it, from one live run:

  ```
  array[4]:data:{version:String,clientProvidedSubscriptionName:String,healthUserId:String,
                 operation:String,dataType:String,intervals:{array[1]:civilDateTimeInterval+...}}
  ```

  A batch of one element per changed data type, each naming its wearer with a plain
  **`healthUserId`** field — no resource name anywhere. The parser now reads that property
  directly, wherever it sits and whatever its casing, keeping resource-name matching as a
  secondary form.

  The lesson worth keeping: **the payload had no schema to reason from, so reasoning was the wrong
  tool.** The one-level shape line (`array[4]:data`) had already proved batching and could not
  prove anything more; deepening it to three levels answered the question in a single run. When
  the next envelope change breaks this, read the shape line first and infer nothing.

**How to confirm the fix** (this is what closes step 9), reading the aggregator job's own
summary line:

```
resource.labels.job_name="carditrack-dev-pipeline-jobs-aggregator"
AND "PipelineJobs run finished"
```

Success is `unparseable: 0` with **either** `connections synced` > 0 (a wearer we hold a
connection for) **or** `unknown users` > 0 (parsed fine, no matching connection — which still
proves the parse works). A continued `unparseable` > 0 means the shape is something else again;
the `top-level shape:` warning now describes three levels rather than one, so the payload can be
diagnosed from the log without another deploy.

> Reading these logs needs only Cloud Logging access. **Pulling the Pub/Sub message itself does
> not work** with the `carditrack-investigator` SA — `pubsub.subscriptions.pull` returns
> `PERMISSION_DENIED`, which is why the parse failure had to be diagnosed from log shape rather
> than from a payload. Grant `roles/pubsub.subscriber` if direct message inspection is ever wanted.

### 10. Compliance gate: Art. 22 before prod alerting

Per the [DPIA](../compliance/dpia.md) (R-B1): the
**[Art. 22 analysis](../compliance/art22_alerting_analysis.md) is drafted** (2026-08-10) —
safeguards cited, human-review pathway defined (caregiver acknowledgment), validation
protocol V1–V4 laid out. What still gates prod alerting for real families: **executing V2
(retrospective benchmark) and V3 (prod shadow period with staff-only audience)** and
recording results in that document, plus reviewer sign-off. Flags can be on while alerting
audiences remain test users — V3 is designed exactly for that window.

### 11. Edge / WAF enablement

Prod has **no load balancer / Cloud Armor** (`prod.tfvars` domains empty — deliberate
deferral 2026-08-06); WAF rules currently exercise dev only. Enable with the domain decision.

### 12. Google restricted-scope verification / CASA

`carditrack-devices-prod` must pass Google's restricted-scope verification (incl. CASA
security assessment) to exceed the 100-user unverified cap. Prerequisites: privacy policy
URL, demo video, justification per scope — and step 8's evidence that claimed data types are
real. Timeline: weeks; start early.

**Four scopes to justify, not three** — confirm step 1b landed in prod before submitting, or
`settings.readonly` needs its own later review. Its justification is a different shape from
the other three: it reads **device telemetry, not health data** (battery level and status,
hardware version), and the user-facing feature it ties to is the battery tile on the device
screen plus the `DEVICE_BATTERY_LOW` safety notification — warning a caregiver *before*
monitoring stops rather than after. The demo video should show the tile and that notification,
since Google requires each scope tied to a visible feature.

---

## Change discipline

When a manual operation is performed in **any** environment, add or update its step here in
the same PR (or the next docs PR) — the ledger is only worth keeping if it is complete.

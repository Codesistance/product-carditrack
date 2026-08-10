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

Steps 1–5 make the platform deployable. Steps 6–9 make the AI pipeline live. Steps 10–12 are
gates that must clear before prod serves real families.

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

### 2. Deployer IAM bootstrap (console/gcloud, NOT Terraform-managed)

The CI deployer service account needs roles that Terraform applies themselves require —
grants must precede the first apply that creates service accounts, schedulers, or Pub/Sub.

```
gcloud projects add-iam-policy-binding <PROJECT> --member "serviceAccount:<DEPLOYER_SA>" --role roles/iam.serviceAccountAdmin
gcloud projects add-iam-policy-binding <PROJECT> --member "serviceAccount:<DEPLOYER_SA>" --role roles/iam.serviceAccountUser
gcloud projects add-iam-policy-binding <PROJECT> --member "serviceAccount:<DEPLOYER_SA>" --role roles/cloudscheduler.admin
gcloud projects add-iam-policy-binding <PROJECT> --member "serviceAccount:<DEPLOYER_SA>" --role roles/pubsub.admin
```

- **Dev status:** granted manually 2026-08-10 for
  `carditrack-deploy@carditrack-490120.iam.gserviceaccount.com` after applies failed with
  `iam.serviceAccounts.create` denials.
- **Prod:** the prod project's deployer needs the **same four roles** before flipping any
  pipeline flag (step 6).

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

Prod has **no MedGemma**: `medgemma_image` is empty in `prod.tfvars`, and every pipeline
flag is off because of it. Enabling: build/push the image (CI lane exists), set
`medgemma_image` in prod.tfvars, apply. Same `Q4_K_M` tag as dev — an assessment made in one
environment must mean the same in another, and **no cheaper substitute models** in any
environment (cost is managed by scale-to-zero, not substitution).

### 6. Pipeline enablement flags (Terraform, listed here for ordering)

After steps 2 and 5, flip in `prod.tfvars` and apply:
`enable_pipeline_jobs`, `enable_pubsub`, `enable_webhook_receiver` — currently all `false`
with rationale comments. This creates the three pipeline jobs, schedulers, topic/subscription,
receiver service, and the webhook secret. CI then owns the images.

### 7. Health webhook Subscriber registration (the step this runbook was born from)

Registers the receiver with Google so notifications flow. **Not Terraform:** no provider
resource exists for the Health API, the Subscriber lives in the devices project (outside
Terraform's state and credentials), and first contact was expected to need iteration.

Verified against the v4 discovery document and live API responses (2026-08-10):

1. Resolve the receiver URL:
   `gcloud run services describe carditrack-<env>-webhook-receiver --project <INFRA_PROJECT> --region europe-west2 --format "value(status.url)"`
2. Read the Terraform-generated secret (full `Bearer …` value, scheme included):
   `gcloud secrets versions access latest --secret carditrack-<env>-webhook-secret --project <INFRA_PROJECT>`
3. Create the Subscriber **in the devices project** (`health.subscribers.create` — no
   health-specific IAM roles exist, so the caller needs a basic role there):

```
curl -s -X POST "https://health.googleapis.com/v4/projects/carditrack-devices-<env>/subscribers" \
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
   acknowledgment fails the first probe). Then check the returned Subscriber's `state`.

- **Dev status:** retry pending (2026-08-10) — schema, quota project, and handshake causes
  all identified and fixed; awaiting the PR #140 receiver deploy, then the create with the
  project-number URL. Until then the pipeline runs whole on **10-minute polling** by design —
  webhooks only shorten latency.

### 8. HealthApiProbe live-wearer check (human required)

`tools/HealthApiProbe` — hand-run with a real OAuth token against a **live wearer wearing a
real device**. Answers the one question the discovery schema cannot: whether this wearer's
device *populates* the data types we read. Run before trusting an environment's data, and
before verification (step 12) locks marketing claims to data we assumed.

- **Dev status:** outstanding — the last open item from Fitbit/Health provisioning.

### 9. End-to-end pipeline smoke check

With steps 6–7 done: watch a notification arrive (receiver logs → Pub/Sub → aggregator
execution logs → targeted sync → granular rows → assessor execution → `RealtimeAssessments`
row). Until step 7 completes, the pipeline runs correctly on **10-minute polling alone** —
webhooks only make it fresher.

### 10. Compliance gate: Art. 22 before prod alerting

Per the [DPIA](../compliance/dpia.md) (R-B1): LLM severity routing is automated
decision-making with significant effect — the **Art. 22 analysis, human-review pathway, and
documented model validation must exist before prod alerting goes live**. Dev operates under
the test-user population. This gate blocks step 6's effect, not its apply: flags can be on
while alerting audiences remain test users.

### 11. Edge / WAF enablement

Prod has **no load balancer / Cloud Armor** (`prod.tfvars` domains empty — deliberate
deferral 2026-08-06); WAF rules currently exercise dev only. Enable with the domain decision.

### 12. Google restricted-scope verification / CASA

`carditrack-devices-prod` must pass Google's restricted-scope verification (incl. CASA
security assessment) to exceed the 100-user unverified cap. Prerequisites: privacy policy
URL, demo video, justification per scope — and step 8's evidence that claimed data types are
real. Timeline: weeks; start early.

---

## Change discipline

When a manual operation is performed in **any** environment, add or update its step here in
the same PR (or the next docs PR) — the ledger is only worth keeping if it is complete.

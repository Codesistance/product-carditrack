# CardiTrack Terraform Infrastructure

Terraform for CardiTrack's Google Cloud platform — project **`carditrack-490120`**, region **`europe-west2`**. This is the operator guide; for the architecture reference see [docs/infrastructure.md](../docs/infrastructure.md).

## Prerequisites

1. **Terraform** `>= 1.14.7` (provider pins: `google ~> 7.23`, `random ~> 3.6`)
2. **gcloud CLI**, authenticated with access to `carditrack-490120`:
   ```bash
   gcloud auth login
   gcloud auth application-default login
   gcloud config set project carditrack-490120
   ```

## The three-stack model

| Stack | Root | State prefix | Holds |
|-------|------|--------------|-------|
| `common` | `infrastructure/common/` | `carditrack/common` | Artifact Registry (`carditrack-common`), mobile builds bucket (`carditrack-common-builds`), 9 store distribution secrets |
| `dev` | `infrastructure/` | `carditrack/dev` | Full per-env stack (Cloud Run, Cloud SQL, buckets, secrets, LB/WAF, …) |
| `prod` | `infrastructure/` | `carditrack/prod` | Same, with prod sizing/flags |

There is **no staging environment** and no `modules/` directory — all per-env resources live in the single `deployments/` module, selected via tfvars.

**Apply order:** `common` first (dev/prod images live in its Artifact Registry), then `dev`, then `prod`.

## Running Terraform

State lives in GCS; the bucket and prefix are supplied at init time (CI derives them from GitHub Variables):

```bash
# Dev / prod (root: infrastructure/)
terraform -chdir=infrastructure init \
  -backend-config="bucket=<state-bucket>" \
  -backend-config="prefix=carditrack/<env>"
terraform -chdir=infrastructure plan  -var-file="environments/<env>.tfvars"
terraform -chdir=infrastructure apply -var-file="environments/<env>.tfvars"

# Common (root: infrastructure/common/)
terraform -chdir=infrastructure/common init \
  -backend-config="bucket=<common-state-bucket>" \
  -backend-config="prefix=carditrack/common"
terraform -chdir=infrastructure/common apply -var-file="../environments/common.tfvars"
```

### Local state (experiments only)

```bash
cp infrastructure/backend_override.tf.example infrastructure/backend_override.tf
terraform -chdir=infrastructure init
```

`backend_override.tf` is gitignored and switches the backend to local state. Never use local state against real environments.

### The normal apply path is CI

The GitHub workflows `deploy-infra-dev.yml`, `deploy-infra-prod.yml`, and `deploy-infra-common.yml` are the standard way changes reach GCP. They run `fmt`/`validate`, bootstrap the state bucket if missing, plan on both the pinned and latest Terraform versions (compatibility matrix), post plans to PRs, and apply on `main` (dev) or manual dispatch (prod). Authentication is **Workload Identity Federation** as `carditrack-deploy@carditrack-490120.iam.gserviceaccount.com` — no service account keys. Prefer a PR + CI apply over local applies.

## ⚠️ The `removed {}` blocks trap

The Artifact Registry and builds bucket were **migrated from the dev/prod stacks into `common/`**. `infrastructure/artifact_registry.tf` and `infrastructure/builds_bucket.tf` now contain only `removed {}` blocks with `destroy = false` — they drop those resources from dev/prod state **without destroying them**, and were designed to be applied once after `common` was deployed.

Do **not** re-apply an old copy of this configuration (or revert these files) without understanding this: reintroducing the old resource blocks alongside the `common` stack would make two states claim the same registry and bucket, and a destroy from the wrong state would take out shared CI infrastructure.

## Secret seeding contract

- Terraform creates app secrets with a **`REPLACE_ME` placeholder** and `lifecycle { ignore_changes = [secret_data] }` — applies never overwrite operator-set values.
- Operators set real values out-of-band:
  ```bash
  echo -n "value" | gcloud secrets versions add carditrack-<env>-<name> --data-file=-
  ```
- **Secrets that need no human value are generated inside Terraform** (`random_password` for the DB password and health token, `random_bytes` for `encryption-key`) — never placed in tfvars, never committed.
- `encryption-key` carries `ignore_changes = [secret_data]` for a reason beyond the usual one: the AES-GCM envelope has no key id, so rotating it makes existing device OAuth tokens undecryptable. An environment provisioned before this became Terraform-owned keeps whatever value it holds — check it is a real base64 32-byte key, not `REPLACE_ME`.
- Terraform-owned values (DB connection string, `apm-mobile-engine`) track Terraform; do not edit them by hand.
- `medgemma-service-url` is written by CI after each MedGemma deploy.

Helper scripts (prompt for values, keep current version on empty input):

```bash
bash scripts/set-auth0-secrets.sh <dev|prod>   # auth0-domain/audience/client-id/client-secret/mobile-client-id
bash scripts/set-apm-secrets.sh   <dev|prod>   # apm-data connection JSON (Datadog / Better Stack)
```

Remaining operator-seeded secrets (`devices-fitbit-client-id`, `devices-fitbit-client-secret`, `gemini-api-key`, `apm-mobile-data`, and the `carditrack-common-*` store secrets) are set directly with `gcloud secrets versions add`.

`gemini-api-key` holds the key for whichever public AI provider is active — it is consumed as `AI__Public__ApiKey`, not as a Gemini-specific setting. Swapping provider (`public_ai_kind` + `public_ai_model`) means seeding the new provider's key into that same secret. To move to a differently-named secret instead, point `public_ai_api_key_secret_id` at it; leaving the variable unset keeps the existing secret, so no swap forces a destroy-and-recreate.

## Environment differences

| Setting | Dev | Prod |
|---------|-----|------|
| Cloud Run CPU / memory | 1 vCPU / 512 Mi | 2 vCPU / 1 Gi |
| Cloud Run instances | 0–1 | 1–3 |
| MedGemma | 8 vCPU / 16 Gi, max 1 instance (service exists only when `medgemma_image` set) | same |
| Cloud SQL tier | `db-f1-micro`, 10 GB | `db-custom-2-7680`, 100 GB |
| Cloud SQL HA | ZONAL | **REGIONAL** |
| Cloud SQL deletion protection | off | **on** |
| Public IP on Cloud SQL | no (private only) | no (private only) |
| `enable_pubsub` | false | **true** (`carditrack-prod-realtime`) |
| `enable_platform_audit_logging` | false | **true** (Cloud SQL audit flags + audit sink/bucket) |
| `audit_retention_days` | 30 (inert — sink disabled) | 90 |
| `apm_engine` | Datadog | Datadog (variable default is `BetterStack` — don't omit the tfvar) |
| `apm_metrics_enabled` | true | false |
| Custom domains | `api.dev.carditrack.com`, `app.dev.carditrack.com` | *(empty)* |
| GCLB + Cloud CDN + Cloud Armor WAF | **active** (domain-gated) | **none** — prod runs on Cloud Run default URLs; edge enablement deferred |

## Outputs

`terraform output` after an env apply:

- `gcp_project_id`, `gcp_region`
- `api_service_url`, `api_service_name`
- `web_service_url`, `web_service_name`
- `cloud_sql_connection_name`, `cloud_sql_instance_name`, `cloud_sql_database_name`
- `storage_bucket_name`, `storage_bucket_url`
- `secret_manager_project`
- `pubsub_topic_name`, `pubsub_topic_id` (null when Pub/Sub disabled)

Common stack: `artifact_registry_repository`, `builds_bucket_name`, `store_distribution_secret_ids`.

## Troubleshooting

**gcloud auth**
```bash
gcloud auth list                             # active account?
gcloud auth application-default login        # ADC for the google provider
gcloud config set project carditrack-490120
```
Permission errors during plan/apply usually mean ADC is missing or points at the wrong account — Terraform uses Application Default Credentials, not your `gcloud auth login` session.

**State lock**
```bash
terraform force-unlock <LOCK_ID>   # only after confirming no apply is running (check Actions)
```

**Backend init errors** — the GCS backend has no hardcoded bucket; always pass both `-backend-config` values shown above. Switching between local override and GCS requires `terraform init -reconfigure`.

---

*Last Updated: August 7, 2026*

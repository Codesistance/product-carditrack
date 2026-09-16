# hashicorp/google Terraform provider ships a breaking v8.0.0 — outside CardiTrack's `~> 7.23` pin today, but a future `-upgrade` would break

**Severity:** FYI
**Category:** dependencies

## Summary

The `hashicorp/google` provider jumped a major version: `7.46.0` (2026-08-25, last
release satisfying CardiTrack's `~> 7.23` constraint) → **`8.0.0`** (2026-08-26,
breaking) → `8.1.0` (2026-09-01) → `8.2.0` (2026-09-08). v8.0.0 removes
`google_beyondcorp_*`, `google_iap_brand`/`google_iap_client`, `google_ml_engine_model`,
and all `google_notebooks_*` resources/IAM bindings, plus tightens several field
constraints (e.g. `http_get.http_headers.name` now required on
`google_cloud_run_v2_worker_pool`).

CardiTrack's `infrastructure/versions.tf` and `infrastructure/common/versions.tf` both
pin `~> 7.23`, which still resolves cleanly to 7.46.0 today — nothing breaks yet.

## Sources

- https://github.com/hashicorp/terraform-provider-google/blob/main/CHANGELOG.md (HashiCorp — primary)
- https://www.hashicorp.com/en/blog/terraform-provider-for-google-cloud-7-0-is-now-ga (HashiCorp blog)

## Why flagged

Nothing breaks today, but whoever eventually widens the `~> 7.23` constraint (or runs
`terraform init -upgrade` without checking) will land on a breaking major version with
several resource removals. Worth a deliberate, scheduled v8 migration rather than an
incidental one.

## Question to answer next

Does CardiTrack's Terraform actually use any of the resources removed in v8
(`google_beyondcorp_*`, `google_iap_brand`, `google_iap_client`,
`google_ml_engine_model`, `google_notebooks_*`)? If yes, that's a pre-migration blocker
to resolve before anyone widens the version constraint.

claude "work through @research/queue/2026-09-16-terraform-google-provider-v8-breaking-release.md"

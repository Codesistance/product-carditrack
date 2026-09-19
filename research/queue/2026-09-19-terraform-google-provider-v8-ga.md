# Terraform google/google-beta provider hits v8.0.0 GA, drops resources with no deprecation grace

**Severity:** FYI
**Category:** dependencies

## Summary

`hashicorp/google` and `hashicorp/google-beta` reached v8.0.0 GA around 2026-09-03 and are
iterating quickly (v8.3.0 on 09-15, v8.4.0 on 09-18). v8 removes several resource families
outright rather than deprecating them first: `google_beyondcorp_app_connection`,
`google_beyondcorp_connector`, `google_beyondcorp_gateway` (superseded by
`google_beyondcorp_security_gateway*`), `google_iap_brand` / `google_iap_client`,
`google_ml_engine_model` (superseded by Vertex AI endpoint / Model Garden resources), and every
`google_notebooks_*` resource and IAM binding.

CardiTrack's `infrastructure/versions.tf` and `infrastructure/common/versions.tf` both pin
`~> 7.23`, so Terraform will not auto-upgrade past v7 and nothing breaks today.

## Sources

- https://github.com/hashicorp/terraform-provider-google/releases (provider release notes — primary)

## Why flagged

Not urgent — the version constraint protects CardiTrack today — but the v8 removals have no
deprecation grace period, so this is worth confirming ahead of ever widening the constraint,
rather than discovering it mid-`terraform plan` on some unrelated change.

## Question to answer next

Grep `infrastructure/**/*.tf` for `google_beyondcorp_app_connection`, `google_beyondcorp_connector`,
`google_beyondcorp_gateway`, `google_iap_brand`, `google_iap_client`, `google_ml_engine_model`, and
`google_notebooks_*`. If none are in use, this is a non-issue and the constraint can move to `~> 8.x`
whenever convenient; if any are in use, plan the migration before bumping.

claude "work through @research/queue/2026-09-19-terraform-google-provider-v8-ga.md"

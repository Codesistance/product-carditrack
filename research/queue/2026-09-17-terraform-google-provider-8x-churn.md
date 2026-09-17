# hashicorp/google Terraform provider: 8.x line now three minors deep while 7.x is still patched

**Severity:** FYI
**Category:** dependencies
**Date flagged:** 2026-09-17

## Summary

`terraform-provider-google` cut 8.0.0 on 2026-08-26 with breaking resource
removals: `google_beyondcorp_app_connection`/`connector`/`gateway`,
`google_iap_brand`/`google_iap_client`, `google_ml_engine_model`, plus a
newly-required `source_contents` argument on `google_workflows_workflow`.
Since then it has shipped 8.1.0 (Sep 1), 8.2.0 (Sep 8), and 8.3.0 (Sep 15) —
active, ongoing 8.x development inside this digest window. HashiCorp also
cut a 7.46.1 patch on Sep 4, confirming the 7.x line is still maintained for
now.

CardiTrack's `infrastructure/versions.tf` and
`infrastructure/common/versions.tf` both pin `hashicorp/google` and
`hashicorp/google-beta` to `~>7.23`, so nothing auto-upgrades into the 8.x
line and there is no immediate exposure.

## Why flagged

Not urgent, but a deliberate 8.x migration will eventually be needed, and
early awareness of exactly which resources 8.x removed lets that migration
be scoped now rather than discovered mid-upgrade.

## Sources

- https://github.com/hashicorp/terraform-provider-google/releases

## Question to answer next

Grep `infrastructure/` for any use of `google_beyondcorp_app_connection`,
`google_beyondcorp_app_connector`, `google_beyondcorp_app_gateway`,
`google_iap_brand`, `google_iap_client`, or `google_ml_engine_model` to
confirm zero exposure to the resources removed in 8.0.0, before ever
loosening the `~>7.23` constraint.

# Terraform hashicorp/google provider v8.0.0 removes BeyondCorp/IAP-brand/ML-Engine/Notebooks/Vertex-AI-Schedule resources — CardiTrack's `~>7.23` pin is safe today but blocks a future upgrade

**Date:** 2026-09-18
**Severity:** FYI
**Category:** dependencies
**Source:** https://github.com/hashicorp/terraform-provider-google/releases

## Summary

v8.0.0 (shipped ~Aug 2026) removes `google_beyondcorp_app_connection/connector/gateway`, `google_iap_brand/client`, `google_ml_engine_model`, Notebooks resources, and `google_vertex_ai_schedule`, plus tightens several validations and changes some resource defaults. CardiTrack's `~>7.23` constraint stays on the 7.x line, so this doesn't affect current applies.

## Why flagged

First-time check; a new major with real breaking changes exists, and CardiTrack is now pinned below it with a real future migration cost.

## Next question / action

Check whether any CardiTrack `.tf` file references the resources v8 removes, to scope the eventual migration now rather than at upgrade time.

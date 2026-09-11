# Vertex AI folded into the Gemini Enterprise Agent Platform — naming/docs staleness only

**Severity:** FYI
**Category:** dependencies

## Summary

As of 2026-05-21 (Google Cloud Next '26), "Vertex AI" no longer appears as a standalone
product in the GCP Console — it has been rebranded/absorbed into the Gemini Enterprise
Agent Platform, with Model Garden, Endpoints, and Prediction now living under a
"Models" sub-menu. Google states existing SDKs, APIs, and billing are unchanged, and no
breaking API calls were introduced.

## Why this matters to CardiTrack

CardiTrack's AI pipeline is built around Vertex by name: `docs/llm_design.md`,
`docs/technical/vertex_ai_setup.md`, and Terraform (`google_vertex_ai_*` resources)
all reference "Vertex AI" directly, and the `VertexGemini` public-AI provider
(`AI:Public:Kind`) is the deployed-environment default per the DPIA (decision D6). This
is a documentation/console-navigation staleness issue, not a functional break — Google
confirms no API or billing change — so it does not currently affect what CardiTrack can
build or ship. Flagged because internal runbooks pointing engineers to "the Vertex AI
console" could send them looking for a menu item that no longer exists by that name.

## Sources

- https://cloud.google.com/blog/products/ai-machine-learning/vertex-ai-io-announcements/ (Google Cloud, official announcement)
- https://en.wikipedia.org/wiki/Gemini_Enterprise_Agent_Platform (background/summary)

## Next question

Does `hashicorp/google` (pinned `~>7.23`) still expose the `google_vertex_ai_*`
resource names and IAM role bindings used in `infrastructure/main.tf` unchanged under
the new console branding, or has Google signaled a future Terraform resource rename to
track the console reorg? No urgency — check opportunistically on the next Terraform
provider bump.

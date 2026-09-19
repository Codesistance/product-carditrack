# Ollama SSRF via unvalidated blob-pull redirects (CVE-2026-85180) — unpatched

**Severity:** CRITICAL
**Category:** dependencies

## Summary

Ollama does not validate redirect destinations when pulling tensor-layer model blobs. An
attacker controlling — or having compromised — a registry Ollama is told to pull from can
redirect the blob download to an arbitrary host, including cloud metadata endpoints
(`169.254.169.254`), producing server-side request forgery. CVSS 8.7 (High, v4). Published
2026-09-03. **No patched Ollama version exists yet.**

CardiTrack's MedGemma inference image (`src/Infrastructure/MedGemma/Dockerfile`) is built
`FROM ollama/ollama:latest` — the base image is unpinned, floating to whatever "latest" resolves
to at build time — and its `RUN ollama pull "${TAG}"` step pulls the model from a third-party
HuggingFace mirror (`hf.co/unsloth/medgemma-1.5-4b-it-GGUF:Q4_K_M`), not Google's own artifact.
That is exactly the pull path this advisory describes. The build runs inside
`.github/workflows/deploy-medgemma-common.yml` (dispatch-only, GitHub-hosted runner) and the
resulting image later runs on Cloud Run, where a successful SSRF against GCP's metadata server
could leak the service account's credentials.

## Sources

- https://github.com/advisories/GHSA-57p7-34ff-7w3w (GitHub Security Advisory — primary, CVE-2026-85180, published 2026-09-03)

## Why flagged

Live, unpatched, high-severity (8.7) CVE in a dependency CardiTrack ships (via an unpinned base
image) on exactly the code path CardiTrack exercises (pulling a model blob from a third-party
mirror at build time), with a plausible path to cloud-credential exposure if the metadata
endpoint isn't otherwise blocked.

## Question to answer next

1. Does the GitHub Actions runner building the MedGemma image, and the Cloud Run service running
   it, block egress to `169.254.169.254` independent of an Ollama patch? Confirm in Terraform
   (`infrastructure/`) and in the Cloud Run service's VPC/egress config.
2. Can the build step verify the pulled blob's hash against a known-good value (rather than
   trusting whatever the registry/redirect ultimately serves) until Ollama ships a fix?
3. Watch `github.com/ollama/ollama/releases` for a patched version and re-pin the Dockerfile's
   base image (it currently floats on `:latest`) once one ships.

claude "work through @research/queue/2026-09-19-ollama-ssrf-blob-pull-redirect.md"

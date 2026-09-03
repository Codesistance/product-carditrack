# Ollama: unauthenticated API + unvetted model-registry pulls + GGUF memory-safety bug

**Severity:** CRITICAL
**Category:** dependencies
**Date flagged:** 2026-09-03

## Summary

`ollama/ollama` GitHub issue #16236 (opened 2026-05-20, still open, no fix referenced) documents a
vulnerability chain in Ollama, affecting "all versions through 0.24.0":

- The HTTP API binds to `0.0.0.0:<port>` with no authentication by default.
- Models can be pulled from arbitrary, unvetted registries with no allowlisting.
- GGUF tensor metadata is not bounds-checked before allocation, allowing heap memory disclosure
  (system prompts, env vars) and model hijacking via `/api/copy`.
- The reporter documented two already-compromised production servers as evidence.

A second CVE (`CVE-2026-7482`, "Bleeding Llama") was reported by several secondary security-news
outlets describing the same class of bug, patched in Ollama v0.17.1 — **this could not be
confirmed**: GitHub's own Security Advisories page for `ollama/ollama` currently lists no
published advisories. Do not treat CVE-2026-7482 as verified; only issue #16236 is confirmed
against a primary source.

## Sources

- https://github.com/ollama/ollama/issues/16236 (verified directly — primary source, vendor's own repo)
- https://github.com/ollama/ollama/security/advisories (checked directly — no advisory currently published, so CVE-2026-7482 is NOT independently confirmed)

## Why flagged

CardiTrack's MedGemma serving image (`src/Infrastructure/MedGemma/Dockerfile`) is built `FROM
ollama/ollama:latest` — a floating tag, so the exact Ollama build actually shipped depends on
build time, not on anything pinned in the repo. The service (`google_cloud_run_v2_service.medgemma`
in `infrastructure/common/cloud_run.tf`) has `ingress = "INGRESS_TRAFFIC_ALL"` (reachable from the
public internet, not VPC-only) with authorization enforced only by Cloud Run IAM (two named
invoker service accounts, no `allUsers`) — Cloud Run's platform-level OIDC check sits in front of
Ollama's own (absent) auth, which narrows exposure considerably versus a typical bare Ollama
deployment, but does not eliminate it: any caller holding a valid invoker identity (e.g. a
compromised pipeline service account) could still reach the full unauthenticated Ollama API
surface, including `/api/pull` and `/api/copy`, since Cloud Run IAM gates the whole service, not
individual paths.

## Question to answer next

1. Pin `ollama/ollama` to a specific, current tag in the Dockerfile instead of `:latest`, and
   confirm that tag is a build that has addressed the bounds-checking issue in #16236 (there is no
   confirmed fixed version yet — track the issue for a maintainer response/patch release).
2. Confirm the running container has no reachable path to `/api/pull` or `/api/copy` from
   CardiTrack's own service code (MedGemmaClient), and consider whether the Ollama process should
   be started with a registry allowlist or those endpoints disabled at the network layer, given the
   model is baked into the image at build time and never needs a runtime pull.
3. Re-verify CVE-2026-7482 in a few days once/if GitHub Advisories or NVD actually publish it —
   several security-news outlets (SecurityWeek, runzero, Cyera) reported it but it isn't yet
   independently confirmed.

claude "work through @research/queue/2026-09-03-ollama-gguf-unauthenticated-api.md"

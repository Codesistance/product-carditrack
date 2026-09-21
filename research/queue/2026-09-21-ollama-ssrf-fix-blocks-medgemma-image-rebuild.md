# Ollama's CVE-2026-85180 fix blocks our model pull — the MedGemma image cannot currently be rebuilt

**Severity:** CRITICAL
**Category:** dependencies

## Summary

The MedGemma image build fails at `ollama pull`, and has done since Ollama shipped the
CVE-2026-85180 redirect fix. Observed directly in our own CI on 2026-09-21
([run 35629358897](https://github.com/Codesistance/product-carditrack/actions/runs/35629358897)):

```
level=INFO source=images.go:1328 msg="request failed: Head
  \"https://us.aws.cdn.hf.co/xet-bridge-us/...medgemma-1.5-4b-it-Q4_K_M.gguf...\":
  blocked redirect to a different host"
Error: ... blocked redirect to a different host
```

`hf.co/unsloth/medgemma-1.5-4b-it-GGUF:Q4_K_M` serves its manifest from `huggingface.co` and
then redirects blob downloads to `us.aws.cdn.hf.co`. That cross-host redirect is exactly what the
fix rejects, so the pull fails and the build stops before the image is produced.

**The running service is unaffected.** Weights are baked into the image, CI deploys by immutable
tag, and the failing run's deploy step was skipped — `carditrack-common-medgemma` still serves the
image built 2026-08-10. What is broken is the ability to produce a *new* image, which blocks any
future model change, base-image bump or security patch to that service.

This is not caused by pinning the base image (#1174). `ollama/ollama:latest` resolves to the same
digest as `0.34.2` (`sha256:da6e0dc5…`, checked 2026-09-21), so an unpinned build fails
identically. The pin only made the failure deterministic instead of dependent on when `:latest`
last moved. The last successful build predates the patched release.

**This answers `2026-09-20-ollama-ssrf-status-conflict.md`**, which was open because GHSA still
read "Patched versions: Unknown" and only third-party trackers claimed the fix landed in 0.33.3+.
It did ship, it is present in 0.34.2, and we have now observed it working from our own build log
rather than inferring it from a tracker. That note can close on this evidence.

## Why the obvious fixes do not apply

- **Switch to Google's official `medgemma1.5:4b`** — it pulls cleanly (registry.ollama.ai's own
  redirect to its R2 CDN is permitted where hf.co's is not), but it is not a drop-in: the model
  reasons before answering on free-text calls. See
  `2026-09-21-medgemma-official-ollama-tag-evaluated-not-adopted.md`, updated the same day with
  the evidence that a Modelfile cannot suppress it.
- **Pin to a pre-patch Ollama** — reinstates a CVSS 8.7 SSRF on a build path that runs with GCP
  credentials. Not acceptable for this product.
- **Decision 2026-09-21: stay on the unsloth tag** until a better alternative exists, accepting
  that the image cannot be rebuilt in the meantime.

## Sources

- Our own build: [deploy-medgemma-common run 35629358897](https://github.com/Codesistance/product-carditrack/actions/runs/35629358897), job "Build & Deploy MedGemma (GPU)"
- https://github.com/advisories/GHSA-57p7-34ff-7w3w (CVE-2026-85180)
- `src/Infrastructure/MedGemma/Dockerfile`, `.model-version`

## Why flagged

A service CardiTrack depends on for every clinical generation can no longer be rebuilt. Nothing
is down today, so this will stay invisible until the moment someone *needs* a rebuild — a CVE in
the Ollama base, a model change, a config change baked into the image — and discovers the path is
blocked under time pressure. It should be fixed while it is merely inconvenient.

## Question to answer next

Pick and implement a rebuild path. In rough order of preference:

1. **Vendor the GGUF.** Host the weights we already serve in our own Artifact Registry or GCS and
   build from a local Modelfile. No cross-host redirect, so the SSRF fix is satisfied rather than
   worked around; removes the third-party mirror from the build path entirely; and makes
   `.model-digest` verifiable against a file we control. Does not touch model behaviour, so member
   chat and every clinical prompt are unaffected.
2. **Adopt the official tag and route free-text calls through a schema.** Grammar-constrained
   decoding is clean and reproducible on that tag (3/3 identical, `done_reason: stop`), and the
   assessor, statistical, digest and trend paths already use it. It would mean converting
   `ChatAsync` and `GenerateWithUsageAsync` to structured replies — a real change to member chat,
   whose behaviour `docs/technical/member_chat_routing.md` records as hard-won.
3. Re-check whether Ollama offers a supported way to allow a named redirect host. Assume it does
   not until shown otherwise, and treat any such flag as re-opening the CVE.

claude "work through @research/queue/2026-09-21-ollama-ssrf-fix-blocks-medgemma-image-rebuild.md"

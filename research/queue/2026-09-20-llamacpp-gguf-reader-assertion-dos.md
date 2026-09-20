# New CVE-2026-52131: llama.cpp reachable-assertion DoS in GGUF parser — Ollama's dependency, unpatched

**Severity:** HIGH
**Category:** dependencies

## Summary

CVE-2026-52131 / GHSA-jfjx-j76r-xrpf (CVSS 7.5, High) — a "Reachable Assertion via the
`gguf_reader::read` function" in llama.cpp, affecting version b5693 and earlier. A crafted
GGUF file can trigger the assertion remotely, with no authentication or user interaction
required, crashing the process (denial of service, not code execution). Published
2026-09-01. No patched version is listed yet on the GitHub Security Advisory (re-verified
directly), and Ubuntu's security tracker independently shows no fix status ("needs
evaluation" / "not in release").

## Sources

- https://github.com/advisories/GHSA-jfjx-j76r-xrpf (primary GHSA record)
- https://ubuntu.com/security/CVE-2026-52131 (independent tracker corroboration, no fix listed)

## Why flagged

Ollama — CardiTrack's MedGemma serving layer (`src/Infrastructure/MedGemma/Dockerfile`,
`FROM ollama/ollama:latest`) — vendors llama.cpp internally for GGUF model-file parsing.
This is the exact same class of code path the already-tracked GGUF integer-overflow CVE
(GHSA-c2q9-58w2-gjg4, now confirmed fixed upstream — see today's separate digest entry)
hit. If the llama.cpp commit baked into whatever Ollama build CardiTrack's unpinned base
image resolves to is b5693 or earlier, a malformed GGUF artifact — reachable via the
still-open SSRF blob-pull redirect issue (GHSA-57p7-34ff-7w3w) or simply a bad upstream
model mirror — could crash the shared `carditrack-common-medgemma` Cloud Run service,
taking down MedGemma inference for every environment.

## Question to answer next

Determine which llama.cpp commit is vendored into the Ollama version CardiTrack's MedGemma
image actually resolves to (tied to the same base-image-pinning work needed for the SSRF
item), and check whether it predates or postdates b5693. If vulnerable, treat as an
availability risk on the shared inference service until llama.cpp/Ollama ship a fix or
CardiTrack pins to a version that has cherry-picked one.

claude "work through @research/queue/2026-09-20-llamacpp-gguf-reader-assertion-dos.md"

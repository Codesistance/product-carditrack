# Ollama SSRF (CVE-2026-85180): GHSA still shows unpatched, third-party trackers claim otherwise

**Severity:** CRITICAL
**Category:** dependencies

## Summary

GHSA-57p7-34ff-7w3w (CVE-2026-85180, CVSS 8.7) — Ollama does not validate redirect
destinations when pulling tensor-layer model blobs, letting a malicious or compromised
registry redirect the download to an arbitrary host, including cloud metadata endpoints
(169.254.169.254). Directly re-checked today: the advisory still lists "Patched versions:
Unknown," unchanged since publication on 2026-09-03. Ollama's own GitHub Security
Advisories page shows zero published advisories for either this CVE or the GGUF
integer-overflow CVE (GHSA-c2q9-58w2-gjg4) — no fixed-version statement has ever come from
the vendor via that channel.

However, independent trackers (VulnCheck's advisory, Strix.ai) now claim the redirect-
validation fix actually landed in Ollama 0.33.3+ (a fix in the `x/transfer/` tensor
downloader, distinct from an earlier unrelated redirect check in `server/download.go`).
Current stable is 0.34.2 (2026-09-15), pre-release 0.34.3 (2026-09-19) — both well past
that claimed threshold. Neither release's changelog mentions SSRF or redirect hardening
explicitly, so this is an inference from third-party trackers, not a vendor confirmation.

## Sources

- https://github.com/advisories/GHSA-57p7-34ff-7w3w (primary — re-verified 2026-09-20, still "Unknown")
- https://github.com/ollama/ollama/releases (stable 0.34.2, pre-release 0.34.3 — no SSRF mention)
- https://github.com/ollama/ollama/security/advisories (zero published advisories)

## Why flagged

CardiTrack's MedGemma image (`src/Infrastructure/MedGemma/Dockerfile`) is `FROM
ollama/ollama:latest` — unpinned — and pulls the model from a third-party HuggingFace
mirror (`hf.co/unsloth/medgemma-1.5-4b-it-GGUF`) via `ollama pull` at build time, exactly
the code path this advisory describes. Because the base image floats, CardiTrack cannot
currently state with confidence whether the deployed image is patched or not — trusting
either the unreviewed GHSA record (says no) or the third-party trackers (say yes, since
0.33.3) is trusting an unverified claim either way.

## Question to answer next

Pin the MedGemma Dockerfile's base image to an explicit Ollama tag (≥0.34.2, given the
claimed fix threshold), and independently inspect that tag's `x/transfer/` redirect-
validation code (or the equivalent path in the version actually pulled) to confirm the fix
is present, rather than relying on the floating `:latest` tag or trusting either source on
faith. If the fix is confirmed present, downgrade this item's severity in a future digest
entry against this same URL.

claude "work through @research/queue/2026-09-20-ollama-ssrf-status-conflict.md"

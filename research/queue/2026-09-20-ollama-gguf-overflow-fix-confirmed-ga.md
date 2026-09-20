# Ollama GGUF integer-overflow fix (CVE-2026-86289) confirmed GA in stable 0.31.2, not just rc1

**Severity:** FYI
**Category:** dependencies

## Summary

Update to the item published in yesterday's digest (2026-09-19). GHSA-c2q9-58w2-gjg4
(CVE-2026-86289) — an integer overflow in Ollama's GGUF string-reading code
(`readGGUFV1String`) — was previously reported as fixed only in the release-candidate
build 0.31.2-rc1. Directly confirmed today: the fix shipped in the **stable, generally
available** 0.31.2 release (2026-07-06, "Hardened GGUF model creation"), and Ollama has
since shipped many further stable releases (current stable 0.34.2, 2026-09-15; pre-release
0.34.3, 2026-09-19).

## Sources

- https://github.com/advisories/GHSA-c2q9-58w2-gjg4 (primary — same URL as yesterday's entry, status update)
- https://github.com/ollama/ollama/releases (0.31.2 GA 2026-07-06, current stable 0.34.2)

## Why flagged

Downgrading this item from HIGH to FYI: the fix is confirmed resolved upstream, well
outside release-candidate status, and multiple stable releases have accumulated on top of
it. The remaining open question is unchanged from before and tracked separately — CardiTrack's
MedGemma Dockerfile pulls `FROM ollama/ollama:latest` unpinned, so whether the *deployed*
image actually carries this fix depends entirely on when it was last built, not on whether
the fix exists.

## Question to answer next

None specific to this CVE — folds into the general base-image-pinning question already
open against the SSRF item (GHSA-57p7-34ff-7w3w, today's other Ollama entry). Once the
MedGemma image is pinned to an explicit tag, this fix's presence becomes a one-time
confirmation rather than an ongoing unknown.

claude "work through @research/queue/2026-09-20-ollama-gguf-overflow-fix-confirmed-ga.md"

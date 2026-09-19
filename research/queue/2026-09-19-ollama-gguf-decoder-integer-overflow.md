# Ollama GGUF decoder integer overflow (CVE-2026-86289) — patched in 0.31.2-rc1

**Severity:** HIGH
**Category:** dependencies

## Summary

An integer overflow in Ollama's GGUF string-reading code (`readGGUFV1String`) is remotely
triggerable while loading a model file. CVSS 2.1 (Low, v4) — low severity on its own — but it
sits directly in the GGUF-parsing path CardiTrack exercises to load the unsloth MedGemma
artifact (`hf.co/unsloth/medgemma-1.5-4b-it-GGUF:Q4_K_M`). Published 2026-09-07. Fixed in Ollama
0.31.2-rc1 (commit `67b6a1c`), affecting versions up to 0.31.1.

CardiTrack's MedGemma image is built `FROM ollama/ollama:latest` (unpinned — see the sibling
CRITICAL brief on CVE-2026-85180 for the same root cause), so whether the currently-running
image carries this fix depends entirely on when it was last built relative to the 0.31.2-rc1
release, not on anything CardiTrack has explicitly chosen.

## Sources

- https://github.com/advisories/GHSA-c2q9-58w2-gjg4 (GitHub Security Advisory — primary, CVE-2026-86289, published 2026-09-07)

## Why flagged

A CVE in a dependency CardiTrack ships, on the exact code path (GGUF parsing) used to load the
production model. Low severity and a fix exists, which is why this is HIGH rather than CRITICAL,
but it reinforces the same underlying issue as the SSRF advisory: the MedGemma base image floats
on `:latest` with no version pin and no build-time assertion of which Ollama version shipped.

## Question to answer next

Pin the MedGemma Dockerfile's Ollama base image to a specific tag (≥0.31.2) instead of `:latest`,
so the running version is a deliberate, auditable choice rather than whatever was current on the
last build's runner. This single fix also closes the uncertainty window for CVE-2026-85180.

claude "work through @research/queue/2026-09-19-ollama-gguf-decoder-integer-overflow.md"

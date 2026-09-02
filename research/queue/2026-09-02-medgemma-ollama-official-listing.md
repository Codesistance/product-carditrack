# Possible official Ollama-library MedGemma 1.5 listing

**Severity:** HIGH
**Category:** models

## Summary

Search results (via `ollama.com/search?q=medgemma`) surface `ollama.com/library/medgemma1.5` as a
distinct entry from `ollama.com/library/medgemma` (the existing 4B/27B v1.0 listing), suggesting an
official Ollama-namespace ("library/", not a username namespace) build of MedGemma 1.5 may now
exist. This session's WebFetch to `ollama.com` was blocked by the environment's network egress
policy, so the page's actual tag list, quantization equivalence to Q4_K_M, and provenance could not
be directly confirmed — this is reported on search-index evidence only, moderate confidence.

## Source links

- https://ollama.com/library/medgemma1.5 (the page itself — could not be fetched this run; treat as
  unverified until someone with unblocked access loads it)
- https://ollama.com/search?q=medgemma (search index showing the listing exists)

## Why flagged

CardiTrack's production pin is a **third-party community quantization**
(`hf.co/unsloth/medgemma-1.5-4b-it-GGUF:Q4_K_M`), not a Google- or Ollama-vetted artifact — the
`.model-version` file and `docker-compose.yml`/`AI__Private__Model` all point at the `unsloth`
namespace. If an official `library/medgemma1.5` build now exists with a Q4_K_M-equivalent tag,
that's a lower-risk, better-provenance replacement worth evaluating for the production pin on the
exact same serving stack (Ollama) — no architecture change, but one fewer third-party trust
dependency in a clinical-adjacent pipeline.

## Question to answer next

Someone with unblocked network access needs to load `ollama.com/library/medgemma1.5` directly,
confirm: (1) it's genuinely Ollama/Google-published rather than another community upload under a
library-like name, (2) it has a tag quantized equivalent to Q4_K_M, and (3) how its checksum/file
size compares to the current unsloth build — before this becomes a swap ticket rather than a watch
item.

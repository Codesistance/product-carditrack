# Ollama 0.40.0-rc0 introduces manifest-list storage and a lazy "compatibility migration" that rewrites legacy Gemma 3 GGUFs on first load — MedGemma 1.5 4B is on that list

**Severity:** FYI
**Category:** dependencies
**Date recorded:** 2026-09-26

## Summary

Ollama published **v0.40.0-rc0** on 2026-09-25 (pre-release; the plain v0.40.0 tag does not exist
yet; Docker Hub tag `0.40.0-rc0` pushed 2026-09-25T02:54Z). The headline is "Models run on MLX on
Apple Silicon by default", which does not touch a Linux Cloud Run image.

What matters is commit **75b9527** ("llama-server: prepare to remove compatibility patch", read
directly). Its message adds "manifest-list storage so runner-specific manifests can coexist under one
tag while preserving existing v1 tags as best-effort downgrade anchors", makes show/list/copy/remove/
pull/push "understand runner and digest selection", and adds "lazy local compatibility migration for
legacy Ollama GGUFs into llama.cpp-compatible children, covering the patched model families and
preserving parser/renderer, templates, projectors, media metadata, split GGUFs". The
`compatmigrate/` directory at the tag contains `gemma3.go`, `gemma3n.go`, `gemma4.go` and
`embeddinggemma.go`. MedGemma 1.5 4B is Gemma 3-based, so the model
`src/Infrastructure/MedGemma/Dockerfile` registers with `ollama create` FROM the vendored
`medgemma-1.5-4b-it-Q4_K_M.gguf` + `mmproj-F16.gguf` is inside "the patched model families".

**Consequences of any future bump from 0.34.2 to 0.40.x.**
- The baked `/root/.ollama` store would be lazily rewritten on first load inside each Cloud Run
  instance: per-instance cold-start cost, writes to the container filesystem, and a migration the
  build never exercised.
- `ollama list` output and manifest layout change with manifest lists — exactly what the Dockerfile's
  build-time `ollama list | awk` name assertion and the `ollama cp` alias step depend on.
- The commit also touches `server/create.go` / `create/manifest.go`, so a rebuild on 0.40 produces a
  different on-disk artefact from identical inputs.

**Nothing changes today.** The image is pinned to `ollama/ollama:0.34.2` by digest (still resolves to
`sha256:da6e0dc5…` on Docker Hub, read directly); 0.34.4 (2026-09-23) remains the latest stable with
quality/perf notes only; GHSA-57p7-34ff-7w3w (SSRF) is unchanged since 2026-09-03 with no patched
version listed; no new advisories against Ollama itself since 2026-09-20. No release note mentions a
llama.cpp or CVE fix in 0.40, so there is no pull toward upgrading. Ollama's docs for 0.40 could not
be read (ollama.com blocked; docs paths moved on raw main).

## Sources

- https://github.com/ollama/ollama/releases/tag/v0.40.0-rc0 — release page (read directly)
- https://github.com/ollama/ollama/commit/75b9527 — the compatibility-migration commit (read directly)
- https://github.com/ollama/ollama/tree/v0.40.0-rc0/compatmigrate — the migration family list (read directly)
- https://github.com/advisories/GHSA-57p7-34ff-7w3w — SSRF advisory, unchanged (read directly)

## Why flagged

A structural change to how the serving runtime stores and loads exactly the model family CardiTrack
ships, arriving in the next major. It is not a deprecation with a date, but the Dockerfile's build
assertions would be the first thing to break, and a lazy migration on Cloud Run is a cold-start and
filesystem behaviour the pipeline has never been tested against. Recording it turns the next Ollama
bump into a planned experiment.

## Question to answer next

1. On a throwaway build against `ollama/ollama:0.40.0-rc0`: does `ollama create` FROM our two GGUFs
   still register `medgemma-1.5-4b-it:q4_k_m` such that the `ollama list` assertion passes, and does
   the first `/api/chat` trigger a `compatmigrate` rewrite (compare `/root/.ollama` size and cold-start
   latency before and after)?
2. Does the migration preserve the mmproj projector layer and the byte-exact template the Modelfile
   pins (hash `e0a42594d802e…`)?
3. If the rewrite is real, decide whether a future image should run the migration at build time so
   Cloud Run instances start from a settled store.

claude "work through @research/queue/2026-09-26-ollama-0-40-0-rc0-compat-migration-rewrites-gemma3-store.md"

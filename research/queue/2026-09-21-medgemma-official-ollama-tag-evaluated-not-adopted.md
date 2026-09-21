# Google's official `medgemma1.5` Ollama tag: evaluated, not adopted — chain-of-thought leaks into free-text replies

**Severity:** FYI
**Category:** models

## Summary

The September 2026 HAI-DEF newsletter announces a first-party Ollama tag (`ollama pull
medgemma1.5`). It was pulled and tested end to end on 2026-09-21 against the tag CardiTrack
actually serves (`hf.co/unsloth/medgemma-1.5-4b-it-GGUF:Q4_K_M`). It is **not** a drop-in
replacement, and was not adopted. Three measured differences:

1. **Chain-of-thought leaks into the reply.** On both `/api/generate` and `/api/chat`, with
   `"think": false` set, the official tag returns `<unused94>thought\n1. **Identify the core
   information:** …` inline in the content field, with `thinking` null — Ollama's think switch
   does not suppress it, because the tag declares no thinking tokens. A 150-token budget was
   consumed entirely by reasoning and the answer never arrived (`done_reason: "length"`).
   Schema-constrained calls were clean in 6/6 runs: grammar-constrained decoding forces valid
   JSON immediately. So the severity, statistical-judgement and digest paths would survive the
   swap and **member chat and `GenerateWithUsageAsync` would not**.
2. **No temperature in its params.** The unsloth tag carries `"temperature": 0.1`; the official
   tag carries only a stop sequence, so Ollama's default 0.8 would have applied. This is now moot
   — the client sends `temperature` as a request option as of this commit — but it is why.
3. **No 27B under 1.5.** `medgemma1.5:27b` 404s; 27B exists only under the previous-generation
   `medgemma` (17.4 GB, 27.4B, Q4_K_M). "Move to 27B" would mean moving back a generation.

Otherwise it matches: `Q4_K_M`, 4.3B, gemma3 family, `vision` capability intact, 3.34 GB,
`latest` and `4b` byte-identical (manifest `433252621ab154668b5d8be6aff6c1b771bacba045e46e6193da8d6ad1630f2c`).
Its template differs on system-role handling, which does not bite: `MedGemmaClient` emits only
`user`/`assistant` roles. Note `medgemma1.5` bare fails the Dockerfile's exact-name assertion —
it registers as `medgemma1.5:latest` — so an adoption would name `medgemma1.5:4b`.

Two things this run settled that are **already fixed** rather than open:

- **Neither tag could be pinned.** Ollama's registry 404s digest-addressed manifests and
  `ollama pull` rejects `model@sha256:…` ("invalid model name"), so no `:tag` Ollama serves is
  immutable. Closed by `.model-digest` + a build-time assertion on the stored manifest hash.
- **The sampler came from a third party.** `temperature` 0.1 reached every clinical generation
  from the unsloth uploader's params, unstated anywhere in this codebase. Now an explicit
  setting.

## Sources

- HAI-DEF Newsletter, September 2026 (`hai-def-noreply@google.com`, 2026-09-21) — §5 "Run
  MedGemma 1.5 Inference in Under 30 Minutes"
- `registry.ollama.ai/v2/library/medgemma1.5/manifests/{latest,4b}` and its config/params/template
  blobs, fetched 2026-09-21
- Local `ollama/ollama:0.34.2` run: pull, `/api/show`, and paired inference probes

## Why flagged

The newsletter reads as an invitation to move to the first-party artifact, and the move looks
like a one-line change to `.model-version`. It is not: on the free-text paths it would put model
reasoning in front of caregivers and truncate the answer. Anyone revisiting this — including a
future routine that sees "Google now publishes an official tag" — should find the measurement
rather than repeat it.

Worth recording separately: the official tag ships the **HAI-DEF Terms of Use as a license
layer**, reading "Last modified: November 15, 2024". That is a concrete, diffable baseline copy,
which is what `2026-09-04-haidef-terms-revision-unverified.md` says it needs; it does not settle
what the live terms page now says.

## Question to answer next

1. **Confirm the recorded digest on the first CI build.** `.model-digest` holds
   `c50f9c29d740f96ec94893ae6fe01db1d5cacd42621c569418355ebadd509950`, computed from the manifest
   Hugging Face serves. It could not be confirmed against Ollama's on-disk copy in this session —
   the session's egress policy blocks the HF blob CDN (`us.aws.cdn.hf.co`), so the pull never
   completed. The mechanism is verified (the official tag's served manifest hashed byte-identical
   to its stored one), but this specific value has not been. If the first build fails, the guard
   prints the actual digest: check it is the tag we expect, then record it.
2. Does baking our own Modelfile (template + params) at image build suppress the `<unused94>`
   preamble? That is the gate on adopting the official tag, and it would also let the sampler be
   pinned in the image rather than per request.
3. Does the unsloth tag leak the same way under its own template? Untestable here for the same
   egress reason. If it does, this is a live defect on the served model, not a reason to avoid
   the official one — and it would explain `done_reason: "length"` occurrences in dev.

claude "work through @research/queue/2026-09-21-medgemma-official-ollama-tag-evaluated-not-adopted.md"

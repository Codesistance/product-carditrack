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

## Update, later the same day — two of these are now answered, and one was wrong

**The Modelfile route does not work (answers Q2 below, in the negative).** Measured against
`medgemma1.5:4b` on Ollama 0.34.2:

- The thought block is `<unused94> thought … <unused95>`, with the real answer immediately after
  the closing token.
- Declaring those as thinking tokens in a Modelfile `TEMPLATE` **does** make `/api/show` report a
  `thinking` capability — and changes nothing. Ollama still does not parse them, and the preamble
  still lands in `response`/`message.content` on both `/api/generate` and `/api/chat` with
  `"think": false`.
- Prefilling an already-closed empty thought block removes `<unused94>` and the model simply
  reasons in prose instead, marked with `<unused96>`/`<unused97>`.

**The model reasons before answering, and suppressing particular tokens only relocates it.** The
one thing that does work is grammar: with a `format` schema the reply is clean and reproducible
(3/3 identical, `done_reason: stop`, no `<unused…>`). So the split is by call shape, not by
prompt — every schema-constrained path (assessor, statistical judgement, digest, trend) would be
safe on this tag, and the free-text ones (`ChatAsync`, `GenerateWithUsageAsync`) would not.

**Correction to Q1: the stated reason was wrong.** The failed `hf.co/unsloth/…` pull was not the
session's egress policy blocking `us.aws.cdn.hf.co`. It was Ollama's own CVE-2026-85180 fix
rejecting the cross-host blob redirect — the same failure our CI reproduced later that day, with
the explicit message `blocked redirect to a different host`. That also means the unsloth tag
cannot be pulled by any patched Ollama, so **`.model-digest`'s recorded value is not confirmable
by pulling** and Q3 is untestable anywhere, not just here. See
`2026-09-21-ollama-ssrf-fix-blocks-medgemma-image-rebuild.md`, which now owns that thread.

For the record, the official tag's own digest **is** confirmed: `medgemma1.5:4b` pulls cleanly,
and its stored manifest hashes to
`433252621ab154668b5d8be6aff6c1b771bacba045e46e6193da8d6ad1630f2c` — verified on disk twice.

**Decision 2026-09-21: stay on the unsloth weights** — now vendored rather than pulled, see
`2026-09-21-ollama-ssrf-fix-blocks-medgemma-image-rebuild.md` — until a better alternative
exists. This note's conclusion therefore stands, but for a firmer reason than when it was written.

## Question to answer next

1. ~~Confirm the recorded digest on the first CI build.~~ Superseded: the build cannot reach the
   digest guard, because the pull fails first. Reopens only if the rebuild path is fixed.
2. ~~Does baking our own Modelfile suppress the `<unused94>` preamble?~~ Answered: no, see above.
3. ~~Does the unsloth tag leak the same way under its own template?~~ Untestable — no patched
   Ollama can pull it. Would need a vendored copy of the weights to answer.
4. **New.** If the official tag is ever adopted, the gate is no longer a Modelfile — it is whether
   `ChatAsync` and `GenerateWithUsageAsync` can be moved onto schema-constrained replies without
   damaging the behaviour `docs/technical/member_chat_routing.md` records.

claude "work through @research/queue/2026-09-21-medgemma-official-ollama-tag-evaluated-not-adopted.md"

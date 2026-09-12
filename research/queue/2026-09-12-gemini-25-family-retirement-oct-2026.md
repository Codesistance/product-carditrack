# Gemini 2.5 Pro/Flash/Flash-Lite retirement date firmed to 2026-10-16

**Severity:** FYI
**Category:** dependencies
**Date found:** 2026-09-12

## Summary

Google has set the Gemini 2.5 Pro, 2.5 Flash, and 2.5 Flash-Lite retirement
date to 2026-10-16 (recommended successor: the gemini-3.1-flash-lite
family). CardiTrack's production general-AI slot (`AI:Public:Model`) was
already moved from `gemini-2.5-flash` to `gemini-3.5-flash` on 2026-08-25 in
both `dev.tfvars` and `prod.tfvars` for an unrelated reason (the 2.0-flash
retirement), and the Rewrite slot runs locally on Ollama
(`gemma3:4b-it-qat`), not Vertex Gemini. No shipped path calls a 2.5 model —
the only repo reference to `gemini-2.5-flash-lite` is in
`VertexAiClientTests.cs` test fixtures, unused in any environment config.

## Sources

- https://ai.google.dev/gemini-api/docs/deprecations

## Why flagged

Logged for continuity/completeness on the Vertex AI watch list; no action
needed since CardiTrack has already moved off the retiring model family.

## Next question

Confirm the `VertexGemini` code path referenced in `AiServiceExtensions` is
genuinely dead in all environments, rather than a stale test fixture masking
a config path someone could still flip on and inadvertently reintroduce a
2.5-model dependency.

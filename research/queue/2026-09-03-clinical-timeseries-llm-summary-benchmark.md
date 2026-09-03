# arXiv: Benchmarking LLM Summaries of Multimodal Clinical Time Series for Remote Monitoring

**Severity:** FYI
**Category:** models
**Date flagged:** 2026-09-03

## Summary

A 2026 arXiv paper benchmarks LLM-generated narrative summaries of clinical time-series data
specifically in a remote-monitoring context — the exact task shape CardiTrack asks MedGemma to
perform (turn SSA-decomposed heart-rate trends and baseline deviations into a family-facing
narrative, per `docs/llm_design.md`'s "deterministic code computes every number; MedGemma only
ever interprets them" design). This session's egress proxy blocks `arxiv.org`, so only the
abstract-level description from search indexing was available — the actual accuracy/hallucination
findings, and whether MedGemma specifically was one of the benchmarked models, are unconfirmed.

## Sources

- https://arxiv.org/abs/2603.01557 (abstract-level only — full text not fetchable from this environment)

## Why flagged

This is the closest thing found to independent validation (or challenge) of CardiTrack's core
"deterministic-compute, LLM-interprets" architecture for turning wearable time series into
caregiver-facing text. Worth a full read rather than acting on the abstract alone.

## Question to answer next

Fetch the full paper (outside this session's network restrictions) and check: (1) does it include
MedGemma among the benchmarked models, (2) what hallucination/factual-drift rate does it report
for time-series-to-narrative summarization, and (3) does anything in its methodology suggest a
prompt-design change worth making to `CARDITRACK_FAMILY_DIGEST_PROMPT` or
`CARDITRACK_REALTIME_ASSESSMENT_PROMPT`.

claude "work through @research/queue/2026-09-03-clinical-timeseries-llm-summary-benchmark.md"

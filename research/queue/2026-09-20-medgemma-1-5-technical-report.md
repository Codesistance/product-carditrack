# MedGemma 1.5 Technical Report published — no cardiovascular/ECG benchmarks anywhere in it

**Severity:** FYI
**Category:** models

## Summary

Google Research/DeepMind's MedGemma 1.5 Technical Report (arXiv:2604.05081) documents
gains on MedQA (+5%), EHR-QA (+22%), chest-X-ray anatomical-localization IoU (+35%), and
longitudinal chest-X-ray macro accuracy (+4%) versus the prior MedGemma generation. It
contains no cardiovascular, ECG, waveform, or vitals-time-series benchmark of any kind —
the model's own vendor-published evaluation suite doesn't claim competence in this area at
all.

## Sources

- https://arxiv.org/abs/2604.05081

## Why flagged

This is the vendor's own technical report, not just an independent stress test — a
stronger form of evidence than the already-reported ECG-Reasoning-Benchmark
(arXiv:2603.14326, near-zero multi-step completion) that MedGemma was never positioned for
primary cardiac signal reasoning. It independently confirms (doesn't change) CardiTrack's
existing architectural boundary: deterministic .NET code (SSA decomposition via Math.NET
Numerics) computes every number, and MedGemma only narrates the result against a pinned
clinical reference-range table (`docs/llm_design.md`, "Design decisions, 2026-08-10").

## Question to answer next

None required now. Worth re-checking if a future MedGemma release's technical report adds
a cardiac/ECG/vitals-reasoning evaluation section — that would be the signal to revisit
whether any interpretation work could safely move onto the model.

claude "work through @research/queue/2026-09-20-medgemma-1-5-technical-report.md"

# MedGemma near-zero completion on multi-step ECG reasoning chains

**Severity:** FYI
**Category:** models

## Summary

A new benchmark ("ECG-Reasoning-Benchmark") decomposes ECG interpretation into
criterion-selection → finding-identification → grounding → diagnosis steps and
evaluates whether a model can complete the full multi-turn verification chain. Both
MedGemma 4B and MedGemma 1.5 4B complete roughly 1% or fewer of these chains, "losing
contextual focus" over turns. The authors note the benchmark enforces exhaustive
sequential verification unlike real clinician heuristics and excludes ambiguous cases —
so this is a reasoning-chain stress test, not a clinical-validity measurement, and
should not be read as "MedGemma is 1% accurate at ECG interpretation."

## Sources

- https://arxiv.org/abs/2603.14326

## Why flagged

CardiTrack never asks MedGemma to do primary ECG/signal reasoning — deterministic .NET
code (SSA decomposition, Math.NET) computes every number, and MedGemma only narrates the
result against a pinned reference-range table (`docs/llm_design.md`, "Design decisions,
2026-08-10"). This benchmark is a direct, independent confirmation that this
architectural boundary is the right one to hold, not a reason to change anything. Worth
keeping on file as evidence for that design decision if it's ever questioned.

## Question to answer next

None required now. Re-check if a future MedGemma version claims improved multi-step
clinical reasoning — that would be the trigger to reconsider whether any interpretation
work could safely move onto the model.

claude "work through @research/queue/2026-09-04-medgemma-ecg-reasoning-benchmark.md"

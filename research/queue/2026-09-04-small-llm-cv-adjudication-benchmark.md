# Small local LLMs (Gemma 3 4B) weak at cardiovascular endpoint adjudication vs. 70B models

**Severity:** FYI
**Category:** models

## Summary

A study comparing locally-deployed Llama 3.1/3.3 and Gemma 3 (4B/12B) on adjudicating
heart-failure hospitalizations from discharge summaries found Llama 3.3 70B reaching
96.8% accuracy (comparable to human-reviewer agreement), while Gemma 3 4B/12B trailed at
68.8% accuracy — high sensitivity but poor overall accuracy from over-flagging. This
tested base Gemma 3, not MedGemma, and is a benchmark, not a clinical-validity finding.

## Sources

- https://pmc.ncbi.nlm.nih.gov/articles/PMC13256241/ (published 2026-06-12)

## Why flagged

A 70B model isn't CPU-servable at CardiTrack's cost/latency target, so this doesn't
threaten the current MedGemma 1.5 4B choice for its actual job (narration, not
adjudication). It is a useful data point if CardiTrack ever considers extending
MedGemma's role from narrative digests into structured event classification/adjudication
— that would be the point at which a 4B-class model's known weakness here becomes
directly relevant.

## Question to answer next

None required now. Flag for re-review only if a future feature proposal would have
MedGemma classify/adjudicate discrete cardiac events rather than narrate pre-computed
severity verdicts.

claude "work through @research/queue/2026-09-04-small-llm-cv-adjudication-benchmark.md"

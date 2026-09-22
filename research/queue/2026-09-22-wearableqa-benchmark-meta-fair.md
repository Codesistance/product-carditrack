# WearableQA (Meta FAIR): first public benchmark for LLM reasoning over long-horizon real-world wearable data

**Severity:** FYI
**Category:** models

## Summary

arXiv 2609.05405 (2026-09-04) introduces WearableQA: 4,084 ten-option multiple-choice questions
built from up to 500 days of wearable time series, blood biomarkers and demographics for 200 real
users, across 16 question types split on two axes — data-grounded vs health-reasoning, and
single-signal vs cross-signal. Fourteen proprietary and open LLMs were scored, ranging 19.6–72.9%
against a 10% chance floor. Code is published at github.com/facebookresearch/WearableQA. Whether
MedGemma or Gemma appears in the evaluated set could not be confirmed from the abstract.

## Sources

- https://arxiv.org/abs/2609.05405 (arXiv, 2026-09-04)
- https://github.com/facebookresearch/WearableQA (code and data)

## Why flagged

This is the closest public benchmark yet to CardiTrack's core task — reasoning over a
longitudinal wearable record to say something useful about one person — and it is reproducible.
It gives an external yardstick for the MedGemma 1.5 4B Q4_K_M configuration actually served, and
for any future "should we move to 27B / to the official tag / to a Gemini slot" decision that
`2026-09-21-medgemma-official-ollama-tag-evaluated-not-adopted.md` says needs measurement rather
than opinion. Benchmark scores are not evidence of clinical validity and must not be quoted as
such.

## Question to answer next

Run WearableQA against the served model (same weights, same quantisation, same Ollama version)
and record the per-question-type breakdown in `docs/technical/medgemma_serving_architecture.md`.
The cross-signal health-reasoning quadrant is the one that maps to the severity assessor; treat
that number as the baseline any model change has to beat.

claude "work through @research/queue/2026-09-22-wearableqa-benchmark-meta-fair.md"

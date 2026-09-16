# Ant Group ships Ling-3.0-Flash-Santé, a medical-tuned open MoE model — alternative worth tracking, not yet evaluated against MedGemma

**Severity:** FYI
**Category:** models

## Summary

Ant Group's `inclusionai/ling-3.0-flash-sante` (medical-tuned mixture-of-experts, 124B
total / 5.1B active parameters) launched **2026-09-04**, right at the edge of the prior
research run's window. It's a general medical-domain fine-tune, not cardiac-specific,
and no independent benchmark against MedGemma on narrative/explanation tasks (the role
MedGemma plays in CardiTrack's pipeline) was found. Flagged as an open alternative to
be aware of, not a recommendation to switch — MedGemma's licensing (Health AI Developer
Foundations terms) and Google Cloud/Vertex AI serving integration are why it was chosen
originally, and neither of those factors has changed.

## Sources

- https://huggingface.co/inclusionai/ling-3.0-flash-sante (Hugging Face model card — primary)

## Why flagged

Tracking the open medical-LLM landscape is part of the standing brief (alternatives),
and this is large enough (124B MoE) and recent enough to be worth a note, even though
nothing here changes CardiTrack's current MedGemma-via-Ollama architecture.

## Question to answer next

If CardiTrack ever re-evaluates its narrative-generation model choice, check whether
`ling-3.0-flash-sante`'s license permits the same commercial/clinical-narrative use case
MedGemma is licensed for under HAIDEF, and whether it's deployable via Vertex AI Model
Garden or only self-hosted (which would change the operational story from "one Ollama
Cloud Run service" to something else entirely).

claude "work through @research/queue/2026-09-16-ling-3-flash-sante-medical-model.md"

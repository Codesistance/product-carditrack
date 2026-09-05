# FDA discussion paper: "Considerations for the Regulation of Generative AI-Enabled Medical Devices"

**Date found:** 2026-09-05
**Category:** regulation
**Severity:** FYI

## Summary

The FDA's Digital Health Center of Excellence (within CDRH) published a discussion
paper and formal request for public feedback on how the agency might regulate
generative-AI-enabled medical devices. It proposes a two-axis risk-assessment
framework, a premarket "competency assessment" model (non-clinical benchmarking +
clinical confirmation, loosely modeled on how physicians are trained/evaluated),
risk-proportionate postmarket monitoring, and explicitly discusses foundation
models and agentic AI systems.

This was published 2026-08-18 — before this digest's tracking window opened, and
it was missed by both the 2026-09-04 and (until now) this run's initial pass. It
surfaced only because the regulation research agent flagged it as the single most
on-point open document for CardiTrack's standing question: whether the product's
MedGemma-based narration pipeline could be read as GenAI-enabled clinical decision
support rather than general wellness software.

## Why it matters to CardiTrack

CardiTrack's AI pipeline uses MedGemma (via Ollama on Cloud Run) to *narrate*
findings from wearable data — per `CLAUDE.md`, this is deliberately scoped away
from multi-step clinical reasoning or adjudication, which is also where MedGemma
itself benchmarks poorly (see the already-logged
`research/queue/2026-09-04-medgemma-ecg-reasoning-benchmark.md`). This discussion
paper is a leading indicator of where the FDA's eventual regulatory line for
GenAI-enabled devices will land. It is not itself a rule and has no compliance
deadline for CardiTrack today — hence FYI, not CRITICAL — but if the eventual
framework treats "narrates findings derived from physiological signal processing"
as within scope, that would move the SaMD boundary question this digest tracks
every session.

## Sources

- Discussion paper (primary): https://www.fda.gov/medical-devices/digital-health-center-excellence/considerations-regulation-generative-ai-enabled-medical-devices-discussion-paper-and-request
- Direct PDF: https://www.fda.gov/media/194242/download
- Press announcement: https://www.fda.gov/news-events/press-announcements/fda-seeks-public-feedback-inform-regulatory-approach-generative-ai-enabled-medical-devices

## Follow-up question

Is there an open comment-period deadline on this docket, and if CardiTrack (or a
UK/EU counsel proxy) wanted to submit feedback shaping the eventual framework
before it hardens into guidance, what is the window? Separately: does the paper's
"foundation models and agentic AI systems" section say anything that would apply
to a narration-only (non-adjudicating) use of a foundation model like MedGemma —
worth a follow-up read of the full PDF rather than the landing-page summary.

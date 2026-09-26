# MHRA's paid Regulatory Advice Service — a £987 one-hour meeting with device regulators on whether a specific product is a medical device — promoted on MedRegs 2026-09-21 for "apps to help patients manage their health"

**Severity:** FYI
**Category:** regulation
**Date recorded:** 2026-09-26

## Summary

The MHRA runs a paid **Regulatory Advice Service for medical devices**: a one-hour meeting with MHRA
device regulators, priced at £987, requested via a gov.uk form and paid at least two weeks in
advance. The applicant opens with a 10–15 minute presentation of "the issues or controversies
surrounding their questions"; the MHRA's view is non-binding. The service launched in December 2025.
A MedRegs blog post on **2026-09-21** promotes it, naming "medical device apps to help patients manage
their health" and "AI-enabled diagnostics" as the kind of cross-cutting or novel product it is for, with
Devices.RegulatoryAdvice@MHRA.gov.uk as the contact.

**Where CardiTrack stands.** Neither the service nor the post appears anywhere in the repo (grep for
"regulatory advice" finds only disclaimers). `docs/compliance/dpia.md` has carried
`[DECISION REQUIRED — OI-2]` — a formal device-classification assessment under EU MDR Rule 11 / UK MDR
/ FDA SaMD before launch marketing settles on claims — since it was drafted, and the 2026-09-22 update
sharpened it further: relaying an on-device AFib determination and storing beat-level evidence "pulls
harder on device classification than trend alerting does". `docs/compliance/ai_act_classification.md`
makes the Annex I question (OI-15 (b)) depend on the same answer. Nothing about the current feature
set changes; this is a route to close OI-2 with the authoritative voice, cheaply, before counsel.

**Sourcing.** gov.uk and medregs.blog.gov.uk are proxy-blocked from the sandbox; the guidance page and
the post are as indexed. The post's own URL did not surface; the guidance page is the canonical
record.

## Sources

- https://www.gov.uk/guidance/medical-devices-get-regulatory-advice-from-the-mhra — the service (proxy-blocked; indexed)
- https://www.gov.uk/guidance/medical-devices-ask-for-a-regulatory-advice-meeting-from-the-mhra — the request form (proxy-blocked; indexed)
- https://medregs.blog.gov.uk/ — the 2026-09-21 post (proxy-blocked; exact URL not surfaced)
- docs/compliance/dpia.md — OI-2; docs/compliance/ai_act_classification.md — OI-15 (b)

## Why flagged

The standing regulatory question in this repo is whether the product is a device. The regulator
offers an answer for under £1,000 and named this product category as the audience four days ago.
FYI because nothing changes until someone books it.

## Question to answer next

1. Are `docs/compliance/alerting_algorithm_card.md` and the DPIA §3 intended-purpose wording ("an
   early-warning system based on trends and patterns, not a diagnostic tool") ready to present in
   fifteen minutes? If not, what is missing?
2. If ready: book the meeting with two cases — (a) trend narration only, (b) narration plus
   severity-routed alerts (A13/A15) plus relayed AFib determinations (A26) — and record the MHRA's
   view against OI-2 and OI-15 (b).

claude "work through @research/queue/2026-09-26-mhra-regulatory-advice-service-987-meeting.md"

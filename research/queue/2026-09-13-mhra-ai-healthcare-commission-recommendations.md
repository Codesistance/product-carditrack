# MHRA National Commission into the Regulation of AI in Healthcare — final recommendations report

**Severity:** HIGH
**Category:** regulation

## Summary

The National Commission into the Regulation of AI in Healthcare — an independent body convened
by MHRA and led by NHS doctors — published its final report on 2026-09-10: 44 recommendations
for a future AI-in-healthcare regulatory framework. Headline proposals: a shift from static,
point-in-time device authorization to proportionate, lifecycle-based regulation; staged
("L-plate") authorizations that let new AI models operate under supervision before full
clearance; continuous post-market performance monitoring with power to de-authorize a model that
drifts; and a patient/user right-to-know when AI is involved in their care.

This is not law — it feeds into the dedicated AI-in-medical-devices framework MHRA already
signaled for later 2026 (tracked: AI Airlock Phase 2 completion,
`research/queue/2026-09-04-mhra-ai-airlock-phase2.md`) — but it is the first concrete shape of
what that framework's obligations could look like. The "disclosure that AI is involved"
recommendation in particular would touch CardiTrack's AI-narrated digests regardless of whether
the app is ultimately classified as a medical device, since it is framed as a general
patient/user right rather than a device-classification trigger.

## Sources

- https://www.gov.uk/government/publications/national-commission-into-the-regulation-of-ai-in-healthcare-recommendations-for-a-future-regulatory-framework (GOV.UK, primary — full report)
- https://www.gov.uk/government/news/independent-commission-led-by-nhs-doctors-sets-out-blueprint-to-accelerate-safe-ai-adoption-in-healthcare (GOV.UK, announcement)

## Why flagged

Directional signal for exactly CardiTrack's product category — continuously-updated AI software
touching health decisions outside a formal clinical setting — from the body that will write the
eventual rules. No compliance deadline yet, so HIGH rather than CRITICAL, but worth tracking
because the "AI disclosure" and "lifecycle monitoring" ideas are the kind of obligation that is
cheaper to design in now than to retrofit later.

## Question to answer next

Check whether CardiTrack's current UI already discloses AI involvement in digests/alerts clearly
enough to satisfy a right-to-know framed like this one. If not, scope the smallest UI change that
would. Revisit when MHRA publishes the dedicated AI-in-medical-devices framework it has been
signaling since AI Airlock Phase 2 — this report is likely a preview of that framework's shape.

claude "work through @research/queue/2026-09-13-mhra-ai-healthcare-commission-recommendations.md"

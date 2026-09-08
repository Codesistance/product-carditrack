# FDA discussion paper proposes a two-axis risk framework for generative-AI medical devices

**Severity:** FYI
**Category:** regulation

## Summary

FDA's Digital Health Center of Excellence published a discussion paper (not
binding guidance) on 2026-08-18, open for public comment through 2026-10-19:
"Considerations for the Regulation of Generative AI-Enabled Medical Devices."
It proposes a two-axis risk framework — (1) whether the AI function
*informs*, *directs*, or *takes* action, scaled by degree of human oversight,
and (2) severity of harm if a user relies on an incorrect output — plus a
"competency-based" premarket model (benchmarking + clinical confirmation,
echoing clinician credentialing) with postmarket monitoring scaled to risk
tier.

## Sources

- https://www.fda.gov/medical-devices/digital-health-center-excellence/considerations-regulation-generative-ai-enabled-medical-devices-discussion-paper-and-request (FDA Digital Health Center of Excellence — primary)

## Why it matters to CardiTrack

This is the clearest signal yet of where FDA's eventual SaMD line for a
MedGemma-style narration pipeline may fall. CardiTrack's "informs, human
(caregiver) always reviews" design sits at the lower-risk end of the first
axis — directionally favorable. The exposure point is **severity routing**:
the pipeline choosing an escalation tier before a caregiver ever sees the
alert could be read as "directing" rather than purely "informing," even with
mandatory human sign-off downstream. No compliance deadline exists yet — this
is a comment period, not a rule — so it does not clear the bar for CRITICAL,
but it is worth watching closely as the leading indicator for how the
informs/directs distinction gets formalized.

## Question to answer next

Should CardiTrack (or counsel) submit a comment by 2026-10-19 arguing that
severity-routing-with-mandatory-human-review belongs in the "informs" tier,
before FDA's thinking hardens into binding guidance?

claude "work through @research/queue/2026-09-08-fda-genai-medical-device-discussion-paper.md"

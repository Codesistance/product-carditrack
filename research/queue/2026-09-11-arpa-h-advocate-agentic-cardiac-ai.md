# ARPA-H ADVOCATE — $62.7M federal program funds autonomous AI agents for heart failure

**Severity:** HIGH
**Category:** competition

## Summary

On 2026-09-09, ARPA-H announced its first ADVOCATE-program awardees: Atman Health
(up to $7.7M), Tempus AI (up to $9.5M), plus UpDoc and academic teams (Stanford, Duke,
Kaiser), under a 4-year, $62.7M total program budget. The goal is FDA-authorized,
partially autonomous AI agents that assess heart-failure symptom severity, prescribe
drugs, and order labs — clinically autonomous action, not just monitoring or narration.

## Why this matters to CardiTrack

This lands squarely on CardiTrack's "agentic care-coordination AI" roadmap line
(alongside already-logged Ejenta and Cadence AI) but at a materially more advanced and
better-funded stage: federal backing, multiple well-capitalized competitors, and an
explicit target of FDA-authorized autonomous clinical action — a different (and
riskier, more regulated) product bet than CardiTrack's current design, where
deterministic code computes every number and MedGemma only narrates
(`docs/llm_design.md`: "the model cannot page a family by mumbling"). It sharpens the
question of whether CardiTrack's differentiation should be leaning harder into
"caregiver-facing, human-in-the-loop, non-autonomous" rather than competing on
autonomy — where a $62.7M federally-funded field is unlikely to be beaten head-on by a
small team.

## Sources

- https://www.prnewswire.com/news-releases/atman-health-wins-arpa-h-award-to-build-agentic-ai-for-cardiovascular-care-starting-with-heart-failure-302874043.html (Atman Health's own release)
- https://www.statnews.com/2026/09/09/arpa-h-advocate-program-autonomous-ai-bots-for-heart-failure/ (corroborating reporting)

## Next question

Do any ADVOCATE awardees target family-caregiver-facing UX (vs. clinician/EMR-facing
workflows, like Cadence)? If none do, CardiTrack's "caregiver is the audience, wearer
never logs in" positioning stays differentiated — worth confirming explicitly rather
than assuming, and worth a product-manager pass on whether to message
"non-autonomous by design" more directly against this competitive set.

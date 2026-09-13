# FDA final classification order: Cardiovascular Machine Learning-Based Notification Software → Class II

**Severity:** CRITICAL
**Category:** regulation

## Summary

FDA issued a final classification order (Federal Register docket FDA-2026-N-9907, document
2026-18612, effective 2026-09-11) formally naming "Cardiovascular Machine Learning-Based
Notification Software" as a Class II generic device type. The order defines the category as
software that uses machine-learning techniques on non-invasive physiological inputs to suggest
the likelihood a patient has a cardiovascular disease or condition, flagging the result for
further referral or diagnostic follow-up. It formalizes, as a named product code going forward,
the category the original De Novo grant (Viz.ai's Viz HCM, 2023-08-03) created.

FDA drew the line by function, not by audience: the order does not exempt the category for being
caregiver-facing rather than clinician-facing. CardiTrack's own pipeline — wearable-derived
statistical deviation detection (Math.NET SSA), MedGemma narration of the deviation, and
family-caregiver-facing alerts — sits close to this functional description. The one
distinguishing thread in CardiTrack's favor: the classified category presumes a referral pathway
back into clinical care, whereas CardiTrack routes narration to family caregivers, not clinicians
— but FDA has not tested that distinction, and it is exactly the gap the general-wellness
carve-out (already tracked: `research/queue/2026-09-04-fda-general-wellness-policy-2026.md`)
depends on. The order also explicitly excludes arrhythmia identification/detection from this
category, which narrows — but does not close — a path for any future CardiTrack feature framed
around arrhythmia-adjacent deviations.

## Sources

- https://www.federalregister.gov/public-inspection/2026-18612/medical-devices-cardiovascular-devices-classification-of-the-cardiovascular-machine-learning-based (Federal Register, primary — public inspection copy)

## Why flagged

This is a live regulatory boundary decision for software functionally adjacent to CardiTrack's
own architecture (ML on non-invasive/wearable inputs → cardiovascular condition likelihood →
flag for follow-up). It materially narrows the "clearly general wellness" safe harbor CardiTrack
currently relies on for its no-FDA-clearance, no-MDR-certification positioning (see
`docs/compliance/dpia.md` open item OI-2, medical-device classification unassessed). Not a
deadline for CardiTrack today, but a definition CardiTrack's product/legal review should measure
its own alert language against now, before a growth event (fundraise, press, App Store review)
forces the question externally.

## Question to answer next

Read the full final classification order (not just the Federal Register public-inspection
notice) and compare its predicate language against CardiTrack's actual alert/digest copy — does
"a deviation from baseline warranting attention" read as "suggesting the likelihood of a
cardiovascular disease or condition"? If yes, scope what changes (framing, disclaimers, or
feature scope) would keep CardiTrack clearly on the general-wellness side of this newly-named
line. Loop in whoever owns OI-2 in the DPIA.

claude "work through @research/queue/2026-09-13-fda-cardiovascular-ml-notification-class-ii.md"

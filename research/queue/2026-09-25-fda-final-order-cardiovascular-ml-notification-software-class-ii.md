# FDA final order codifies "cardiovascular machine learning-based notification software" as Class II with special controls (21 CFR 870.2380, product code QXO)

**Severity:** FYI
**Category:** regulation

## Summary

Federal Register document 2026-18612 (91 FR 57785, Docket FDA-2026-N-9907), published and
effective **2026-09-11**, is the final order that writes the De Novo classification granted to Viz
HCM (DEN230003, 2023-08-03) into the CFR as a generic device type. As indexed, the identification
reads: software that "employs machine learning techniques to suggest the likelihood of a
cardiovascular disease or condition for further referral or diagnostic follow-up"; it "identifies
a single condition" from "one or more non-invasive physiological inputs obtained during routine
medical care", is "intended as the basis for further testing", and "is not intended to provide
diagnostic quality output". Class II with special controls, so future entrants can clear by 510(k)
against this type instead of De Novo or Class III. The special-controls list itself was not read —
federalregister.gov, govinfo.gov and accessdata.fda.gov are all proxy-blocked from the sandbox —
so the exact intended-user wording and the false-positive/false-negative controls are unverified.

The order sits eleven days before this window and no earlier digest recorded it, so it is new to
the record rather than new this week.

**Why it matters for the SaMD line.** This is now the named US regulation number for the one
output CardiTrack's compliance docs say the product must not produce: an ML-derived "likelihood of
a cardiovascular condition" about a person. The definition hinges on two things CardiTrack does
not do — inputs from routine *medical care*, and inference of a *single condition* — so a
wearable-trend narrative with no condition inference stays on the general-wellness side FDA
finalised on 2026-01-06. Any future feature phrased as "this pattern may indicate AFib / heart
failure, see a doctor" would land squarely in §870.2380. The open item OI-2 in
`docs/compliance/dpia.md` (MDR Rule 11 / UK MDR / FDA SaMD assessment for early-warning claims)
should cite it.

## Sources

- https://www.federalregister.gov/documents/2026/09/11/2026-18612/medical-devices-cardiovascular-devices-classification-of-the-cardiovascular-machine-learning-based (primary — proxy-blocked; title, date, docket and the quoted identification come from the Federal Register public-inspection listing and search index)
- https://www.accessdata.fda.gov/cdrh_docs/pdf23/DEN230003.pdf (the Viz HCM De Novo decision summary the order codifies — proxy-blocked, not read)
- https://www.accessdata.fda.gov/scripts/cdrh/cfdocs/cfpcd/classification.cfm?id=QXO (product code — proxy-blocked, not read)

## Why flagged

The standing question is where CardiTrack sits relative to SaMD; a US regulation that names the
device type on the far side of that line is worth a row in the record even with UK/EU first. FYI
because it creates no obligation for the current feature set.

## Question to answer next

Read the codified special controls (someone with browser access, or the eCFR text of
§870.2380 once it is loaded) and add two lines to `docs/compliance/ai_act_classification.md` and
the OI-2 row of the DPIA: the identification text, and the specific phrasings in caregiver-facing
copy and MedGemma prompts that would cross it ("likelihood", "may indicate", any named condition).
Confirm the MedGemma output guard already rejects condition-naming — if it does, cite this order as
the reason it must keep doing so.

claude "work through @research/queue/2026-09-25-fda-final-order-cardiovascular-ml-notification-software-class-ii.md"

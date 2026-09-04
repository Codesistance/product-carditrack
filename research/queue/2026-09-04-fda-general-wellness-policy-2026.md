# FDA — General Wellness: Policy for Low Risk Devices (finalized 2026-01-06)

**Severity:** FYI
**Category:** regulation

## Summary

FDA finalized a revised "General Wellness: Policy for Low Risk Devices" guidance on
2026-01-06, replacing the 2019 version. It clarifies that noninvasive wearables
outputting physiological parameters can still qualify as general wellness — provided
there is no diagnosis/treatment claim, no clinical-management guidance, and no
unvalidated clinical values presented as validated. Notably, it confirms a product **may
prompt the user to consult a healthcare professional when a value falls outside a normal
range without that alone tipping it into device territory**, as long as the language
stays non-diagnostic.

## Sources

- https://www.fda.gov/regulatory-information/search-fda-guidance-documents/general-wellness-policy-low-risk-devices

## Why flagged

This is the single most on-point US document for CardiTrack's standing question: does
severity-graded caregiver alerting cross into SaMD? It suggests CardiTrack's alerts could
stay wellness-side in the US *if* alert copy is framed as "this reading is outside the
usual range — consider reaching out" rather than anything implying diagnosis, and if the
pinned reference-range table isn't presented as validated clinical cutoffs. This is US
guidance (secondary priority jurisdiction per this routine's UK/EU-first order) and does
not resolve the UK/EU analysis, but it's a useful positive design precedent.

## Question to answer next

Read CardiTrack's actual alert copy (push notification text, in-app severity
descriptions) against this guidance's language test. Does anything imply a diagnosis or
present the reference-range table as a validated clinical cutoff rather than a
contextual comparison?

claude "work through @research/queue/2026-09-04-fda-general-wellness-policy-2026.md"

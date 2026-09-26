# NHS Innovation Accelerator 2027 closes tomorrow (2026-09-27 23:59) — no application on record, and NIA states it does not support start-up or early-stage innovations

**Severity:** HIGH
**Category:** grants
**Date recorded:** 2026-09-26

## Summary

The NHS Innovation Accelerator's 2027 cohort application window closes at **23:59 on Sunday
2026-09-27** — tomorrow. The routine has carried this deadline since 2026-09-09 and has asked for the
application status every day since 2026-09-21 without an answer in the repo.

**Status check this run.** `grep -rniE 'innovation accelerator|nhsaccelerator'` across the repo's
Markdown (excluding `digests/` and `research/queue/`) returns nothing; a broader grep for "grant
application", "funding application", "sovereign ai", "productivity challenge" and "eic accelerator"
returns nothing; a GitHub issue search on Codesistance/product-carditrack for the programme returns
zero issues. There is no record of a Readiness Check, a full application, or a decision not to apply.

**Eligibility, newly surfaced.** Site-restricted search snippets of nhsaccelerator.com state that the
NIA "supports innovations that are already in use in at least one site which are able to demonstrate
positive impact" and "does not support start-up or early-stage innovations"; applicants are expected to
show evidence from trials, pilots or real-world use. The process is a Readiness Check (outcome within
three working days) before the full application, with shortlisting on 2026-11-25 and interviews
6–15 January 2027. CardiTrack is in early access with no NHS site on record, so even an application
started today would most likely stop at the Readiness Check.

**Deadline unchanged.** No extension appears in any snippet. The page itself could not be read
(proxy-blocked), so "unchanged" rests on the indexed text.

## Sources

- https://nhsaccelerator.com/about-the-nia-programme/apply-for-nia-fellowship/ — the application page (proxy-blocked from the sandbox; indexed via site-restricted search)
- digests/2026-09-09.json, digests/2026-09-21.json and the 2026-09-22..25 summaries — the prior record

## Why flagged

Last run before the deadline where a decision can still be recorded. The eligibility text is the
material change: it turns "did we apply?" into "should this deadline ever have been on the tracker?"
and answers the question for the 2028 cohort as well — CardiTrack needs a live site first.

## Question to answer next

1. Was a Readiness Check or application submitted? If yes, record the reference and outcome date in
   `docs/` (a funding log does not exist yet — create one) so future runs stop asking.
2. If no: record "NIA 2027 not pursued — early-stage exclusion" in the same log, and note that the
   2028 cohort needs at least one site in real-world use with impact evidence by roughly August 2027.
3. Decide whether the NHS Productivity Challenge round 2 (2026-12-01; supplier-list EOI by about
   2026-11-17) is the nearer UK target, given its TRL 4–8 and £250k-minimum framing.

claude "work through @research/queue/2026-09-26-nia-2027-closes-tomorrow-no-application-on-record-early-stage-ineligible.md"

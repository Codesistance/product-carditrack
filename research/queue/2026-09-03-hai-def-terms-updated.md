# Health AI Developer Foundations Terms of Use — last-modified date moved to 2026-07-06

**Severity:** HIGH
**Category:** models
**Date flagged:** 2026-09-03

## Summary

The Health AI Developer Foundations (HAI-DEF) Terms of Use page — the license governing use of
MedGemma — now shows a last-updated date of 2026-07-06. This session's network egress proxy
blocks direct fetches to `developers.google.com`, so the actual diffed content could not be
confirmed; only the updated-date signal is verified. The companion Prohibited Use Policy page
(last modified 2024-11-14, unchanged) already forbids "illegal or unlicensed practice of ...
medical ... services" — the clause CardiTrack's family-facing, non-diagnostic framing exists to
stay clear of.

## Sources

- https://developers.google.com/health-ai-developer-foundations/terms (page reachable via search index; last-updated date 2026-07-06 could not be independently content-diffed from this environment)
- https://developers.google.com/health-ai-developer-foundations/prohibited-use-policy (unchanged, last modified 2024-11-14)

## Why flagged

Licence changes govern what CardiTrack can legally ship with MedGemma far more directly than
benchmark scores do. A last-modified bump doesn't confirm a substantive change (Google sometimes
touches footer dates without content changes), but this hasn't been ruled out either, and the
digest routine could not fetch the page directly this run.

## Question to answer next

Fetch https://developers.google.com/health-ai-developer-foundations/terms directly (from a
network without the egress block) and diff it against the terms on file for CardiTrack's MedGemma
usage — specifically anything narrowing permitted use cases, adding audit/reporting obligations,
or touching liability for AI-generated clinical-adjacent narrative content.

claude "work through @research/queue/2026-09-03-hai-def-terms-updated.md"

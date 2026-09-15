# HAIDEF Terms of Use — no 2026 revision found; last-modified is Nov 15, 2024

**Category:** models · **Severity:** FYI · **Status:** resolves the 2026-09-04 open item

## Summary

On 2026-09-04 this routine flagged `developers.google.com/health-ai-developer-foundations/terms`
as showing what looked like a mid-2026 revision date, unverified. Two independent
searches this run (2026-09-15) return the same detail: the page's last-modified
date is **November 15, 2024**, not a 2026 date, and the Clinical Use / Prohibited
Use wording ("any use in diagnosis or treatment of patients, including as part of
a research study") is unchanged from what CardiTrack originally scoped MedGemma's
narration-only use against.

Neither this run's research agent nor mine could directly render the page —
`developers.google.com` is blocked by this sandbox's egress proxy — so this is
corroborated via independent web-search snippets, not a byte-level diff.

## Sources

- https://developers.google.com/health-ai-developer-foundations/terms (primary; unreachable from this sandbox, content corroborated via search)
- https://developers.google.com/health-ai-developer-foundations/prohibited-use-policy

## Why flagged

The original flag was CRITICAL-adjacent in spirit: a licence change governing
clinical use would decide what CardiTrack can legally ship with MedGemma. Good
news if confirmed — nothing changed — but the routine hasn't verified this with
its own eyes.

## Next question

Someone with unrestricted network access (not behind this environment's egress
proxy) should open the Terms of Use page directly, screenshot it with today's
date, and confirm the modification date reads 2024-11-15. Once confirmed
first-hand, this item can be closed permanently instead of being re-checked
every run.

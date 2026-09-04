# Health AI Developer Foundations Terms of Use — revision date changed, content unverified

**Severity:** FYI (flagged for manual follow-up)
**Category:** models

## Summary

The Health AI Developer Foundations (HAI-DEF) Terms of Use page — the license CardiTrack's
MedGemma serving falls under — shows a "last updated" date in the July 2026 range,
after CardiTrack's original adoption of MedGemma. This run's research agent could not
fetch developers.google.com directly (blocked by this session's egress policy), so **the
substantive content of the change, if any, is not confirmed** — only that the page's
revision date moved.

Separately confirmed: Google did **not** extend the Gemma 4 family's move to Apache 2.0
licensing to MedGemma — MedGemma variants remain under the HAI-DEF Terms of Use, not a
permissive open-source license.

## Sources

- https://developers.google.com/health-ai-developer-foundations/terms (the terms page itself — primary, but not diffable from this session)

## Why flagged

Per this routine's own priority ("licence changes matter more than benchmark scores —
they decide what we can legally ship"), any HAI-DEF terms change is potentially the
highest-value thing this routine can catch. This is a signal, not a confirmed finding —
it should not be escalated past FYI until someone with unblocked network access actually
diffs the current terms against whatever CardiTrack's compliance review last approved.

## Question to answer next

From a machine with normal (non-proxied/non-blocked) internet access, fetch
https://developers.google.com/health-ai-developer-foundations/terms and diff it against
the version CardiTrack's legal/compliance review approved when MedGemma was adopted.
Look specifically at: permitted commercial use, any new consent/attribution
requirements, and any restriction on family-facing (non-clinician) output. If nothing
material changed, downgrade this to closed with a note; if something did change, this
becomes CRITICAL immediately.

claude "work through @research/queue/2026-09-04-haidef-terms-revision-unverified.md"

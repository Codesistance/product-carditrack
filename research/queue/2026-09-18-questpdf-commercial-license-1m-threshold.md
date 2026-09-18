# QuestPDF 2026.8.0 Community license caps at $1M annual revenue (5x tighter than AutoMapper's $5M) — only a 90-day grace period after crossing it

**Date:** 2026-09-18
**Severity:** FYI
**Category:** dependencies
**Source:** https://github.com/QuestPDF/QuestPDF/blob/main/LICENSE.md

## Summary

QuestPDF's free Community license applies to individuals/companies with annual gross revenue under $1,000,000 USD (plus non-profits/academic/OSS). Crossing that threshold triggers a 90-day transition window (from fiscal year-end) to obtain a commercial license.

## Why flagged

First-time license check; the $1M threshold is 5x tighter than AutoMapper's $5M, so it could bind sooner for CardiTrack, especially since PDF generation for reports/exports is often core-product rather than a utility dependency.

## Next question / action

Track this threshold separately from AutoMapper's given the lower bar and shorter grace period.

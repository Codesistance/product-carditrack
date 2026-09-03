# GitHub Actions: Node 20 runner support removal (~2026-09-23)

**Severity:** HIGH
**Category:** dependencies
**Date flagged:** 2026-09-03

## Summary

GitHub is reported to be fully removing Node 20 support from Actions runners on 2026-09-23 (20
days from the date this item was flagged), having defaulted runners to Node 24 since 2026-06-16.
Actions that still declare `using: node20` and haven't shipped a Node 24-compatible release could
break. This session's egress proxy blocks `github.blog` and `docs.github.com`, so the date and
exact mechanics could not be independently re-verified this run — treat as reported by research,
not confirmed against a primary source directly.

## Sources

- https://github.blog/changelog/2025-09-19-deprecation-of-node-20-on-github-actions-runners (not independently fetchable this session — egress blocked)

## Why flagged

CardiTrack's CI pins several third-party actions, some from smaller/less-frequently-updated
maintainers: `r0adkll/upload-google-play@v1` (Play Store deploy) and `hashicorp/setup-terraform@v4`
(infra) are the ones most likely to lag a Node 24 release given their update cadence, and either
breaking would block a release pipeline (mobile deploy or infra apply) with three weeks' notice.

## Question to answer next

From a network that can reach github.blog/docs.github.com, confirm the exact removal date and
scope, then check whether `r0adkll/upload-google-play@v1` and `hashicorp/setup-terraform@v4` have
shipped Node 24-compatible releases; if not, identify the upgrade path (or a replacement) before
the deadline.

claude "work through @research/queue/2026-09-03-github-actions-node20-runner-removal.md"

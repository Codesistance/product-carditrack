# AutoMapper 16.2.0 requires a paid commercial license — CardiTrack is pinned well past the free tier

**Severity:** HIGH
**Category:** dependencies

## Summary

AutoMapper (now published by Lucky Penny Software, the same steward as
MediatR) has required a paid commercial license for any version **≥15.0.0**
for teams above its free tier since 2025-07-02. CardiTrack pins **16.2.0** —
well past that cutover. Enforcement today is log-warning-only (no runtime
kill-switch, no outbound license-server calls), so nothing breaks technically
today, but continued use without a licence (if CardiTrack's org exceeds the
free-tier size) is a licensing-compliance exposure, not a security one. This
was not previously reviewed and doesn't correspond to any earlier log entry.

## Sources

- https://www.jimmybogard.com/automapper-and-mediatr-commercial-editions-launch-today/ (AutoMapper/MediatR maintainer's own announcement — primary)
- https://luckypennysoftware.com/faq (Lucky Penny Software's own licensing FAQ — primary)

## Why it matters to CardiTrack

Unlike a CVE, this doesn't block a build or require a patch release — it's a
legal/commercial question the business needs to resolve deliberately (buy a
licence, or migrate off AutoMapper). Left unresolved, it's an accumulating
exposure rather than a one-time cost, so it earns a decision now rather than
being quietly carried forward.

## Question to answer next

Does CardiTrack (or its parent org) already hold a Lucky Penny Software
commercial licence covering AutoMapper for its current developer headcount?
If not, decide: purchase, or migrate the ~mapping usage off AutoMapper (the
library surface used should be checked for how large a migration that would
be).

claude "work through @research/queue/2026-09-08-automapper-commercial-license-compliance.md"

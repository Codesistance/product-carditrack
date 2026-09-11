# AutoMapper 16.2.0 is inside the commercial-license era — no license key found in repo

**Severity:** CRITICAL
**Category:** dependencies (licensing)

## Summary

AutoMapper (and MediatR) went commercial under Lucky Penny Software, run by AutoMapper's
original author Jimmy Bogard. From v14 onward, the free "Community" tier is limited to
organizations with gross annual revenue under $5M, non-profits, education, and
non-production use; everyone else needs a paid seat-tiered license (Standard /
Professional / Enterprise).

CardiTrack is pinned to **AutoMapper 16.2.0** in both
`src/Presentation/CardiTrack.API/CardiTrack.API.csproj` and
`tests/CardiTrack.IntegrationTests/CardiTrack.IntegrationTests.csproj` — fully inside
the commercial era. A repo-wide search found no `AUTOMAPPER_LICENSE` (or equivalent)
key configuration anywhere — no appsettings entry, no Secret Manager reference, no
license-key call in code.

## Why this matters to CardiTrack

This is a licence question, not a technical one, and licence changes decide what can
be legally shipped (per this routine's own mandate). If CardiTrack's operating entity
does not qualify for the sub-$5M Community tier, production use of AutoMapper 16.2.0
today is unlicensed. This is independent of the separate design-level security note
already in the API csproj (`GHSA-rvv3-g6hj-g44x`, about user-controlled lambda
expressions) — that comment addresses a vulnerability, not licensing.

## Sources

- https://www.jimmybogard.com/automapper-and-mediatr-commercial-editions-launch-today/ (official announcement)
- https://luckypennysoftware.com/faq (licence terms — Community tier eligibility, paid tiers)

## Next question

Does CardiTrack's parent organization (Codesistance) qualify for AutoMapper's
sub-$5M-revenue Community tier? If not, has a Standard/Professional/Enterprise licence
already been purchased outside this repo (e.g. held by finance/legal rather than
configured in code), or does one need to be procured before the next production
deploy? If a licence key is required in-app, confirm where Lucky Penny Software expects
it to be supplied (env var, appsettings, or a build-time key) so it can be wired through
Secret Manager like the platform's other credentials.

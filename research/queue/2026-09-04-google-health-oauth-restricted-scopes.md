# Google Health API OAuth scopes moved to Restricted `googlehealth.*` model

**Severity:** CRITICAL
**Category:** dependencies

## Summary

Google Health API OAuth scopes are now `https://www.googleapis.com/auth/googlehealth.{scope}`
(e.g. `googlehealth.activity_and_fitness.writeonly`, `googlehealth.settings.readonly`),
replacing the old per-metric Fitbit scopes. Every Health API scope — including the ones
CardiTrack already requests — is classified **Restricted**, which triggers a mandatory
Google privacy/security review (CASA-style assessment) before production use. Google's
review queue is reportedly backing up ahead of the 2026-09-30 legacy Fitbit sunset (see
the companion item on that deadline).

## Sources

- https://developers.google.com/health/scopes (Google's own scope documentation — primary)

## Why flagged

If CardiTrack's OAuth consent screen / app verification has not already cleared this
Restricted-scope review, a pending or rejected review after 2026-09-30 would mean **new
users cannot connect a wearable at all** — not a data-quality issue but a hard onboarding
block. This compounds the urgency of the legacy Fitbit sunset item; the two should be
worked together, not sequentially.

## Question to answer next

Confirm CardiTrack's Google Cloud OAuth consent screen verification status for the
`googlehealth.*` scopes right now (Google Cloud Console → APIs & Services → OAuth
consent screen). If verification is not yet granted or is still "in review," escalate
immediately — the review queue is reportedly lengthening as the 2026-09-30 deadline
approaches industry-wide.

claude "work through @research/queue/2026-09-04-google-health-oauth-restricted-scopes.md"

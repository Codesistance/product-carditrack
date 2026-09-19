# Google Health API v4 adds read-only Women's Health scopes

**Severity:** FYI
**Category:** dependencies

## Summary

The Google Health API v4 discovery document gained three new read-only OAuth scopes around
2026-09-04/09-09: `googlehealth.logged_symptoms.readonly`, `googlehealth.mindfulness.readonly`,
and `googlehealth.reproductive_health.readonly`, accepted on `dataPoints.{get,list,dailyRollUp,
reconcile,rollUp}` and `users.getIdentity`. This is the read-half of the Women's Health data
types whose write-only scopes shipped 2026-08-17. Purely additive: existing scopes, data-type
shapes, and the notification payload the webhook receiver (`CardiTrack.HealthWebhookReceiver`)
parses are unchanged.

## Sources

- https://developers.google.com/health/release-notes (Google Health API release notes — primary)

## Why flagged

Google Health API v4 is CardiTrack's only wearable-data path, so any discovery-document change
is load-bearing by definition even when, as here, it's purely additive and needs no immediate
action.

## Question to answer next

Is `logged_symptoms` (as distinct from `mindfulness` / `reproductive_health`, which are outside
CardiTrack's cardiac scope) worth ingesting as a caregiver-visible signal — e.g. a wearer-logged
symptom correlating with an SSA-flagged anomaly could strengthen the narration MedGemma
generates? Low priority; revisit if a product review of the digest surfaces caregiver demand.

claude "work through @research/queue/2026-09-19-google-health-api-womens-health-read-scopes.md"

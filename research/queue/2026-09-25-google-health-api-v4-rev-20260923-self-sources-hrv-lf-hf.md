# Google Health API v4 discovery revision 20260923: a `self-sources` data-source family with PERMISSION_DENIED semantics for write-only clients, server-side value ranges on writes, and (since rev 20260908) LF/HF spectral power on HRV points

**Severity:** FYI
**Category:** dependencies

## Summary

The live discovery document for `health.googleapis.com` moved from revision **20260916** (as
recorded by the 2026-09-23 run) to **20260923**. Read directly today and diffed against the copies
Google publishes in `googleapis/google-api-go-client` (revisions 20260825 → 20260908 → 20260923),
the changes are:

**Revision 20260923**
- A fourth data-source family, `users/me/dataSourceFamilies/self-sources`, on
  `dataPoints:reconcile`, `dataPoints:rollUp` and `dataPoints:dailyRollUp`: "only the data the
  calling client wrote through this API, that is, data points whose data source was registered
  through this API with the same OAuth client ID as the caller." The same text adds a new rule:
  callers granted **only write scopes** for a data type are implicitly restricted to
  `self-sources`, and asking for `all-sources`, `google-wearables` or `google-sources` "fails with
  `PERMISSION_DENIED`"; an empty match returns an empty list.
- Explicit "Must be in the range [..]" validation on many write fields (heart rate 1–300 bpm,
  HRV RMSSD 1–200 ms, SpO2 0–100, VO2max 0–100, steps 0–1,000,000, weight, height, body
  temperature, blood glucose, …), 13 new `Moods` enum values, and `profile.name` / `settings.name`
  marked read-only.

**Revision 20260908** (before the window; not previously recorded — the run that day only recorded
the three new read-only scopes)
- `HeartRateVariability.metadata` → `HeartRateVariabilityMetadata { highFrequencyPower (0.15–0.4
  Hz), lowFrequencyPower (0.04–0.15 Hz) }`, described as "Metadata used in 1P surfaces".
- A `WebhookNotificationCloudLog` schema — the Cloud Logging shape of a webhook delivery
  including the endpoint's HTTP response. It is a log record, not a change to the notification
  payload `WebhookNotificationParser` parses.
- `rollUp` / `dailyRollUp` now document that the final bucket is truncated when the range is not a
  multiple of the window.

**Unchanged and verified:** still 21 OAuth scopes (same set as 2026-09-08); `PairedDevice` still
`batteryLevel, batteryStatus, deviceType, deviceVersion, features, lastSyncTime, macAddress, name`;
`Subscription`, `Subscriber` and `CreateSubscriberPayload` unchanged; no `deprecated` flag
anywhere; every endpoint the code calls (`dataTypes/{dataType}/dataPoints`, `:dailyRollUp`,
`identity`, `irnProfile`, `pairedDevices`) is present.

**Where CardiTrack stands.** The OAuth client requests seven `googlehealth.*.readonly` scopes and
no `.writeonly` scope, and `DeviceSyncService` never passes a `dataSourceFamily` (so it gets the
`all-sources` default). The `self-sources` restriction and the write-range validation therefore
cannot bite. The HRV change is additive: if Google populates LF/HF for Fitbit and Pixel Watch
points, it arrives in a `metadata` object the current DTOs ignore.

## Sources

- https://health.googleapis.com/$discovery/rest?version=v4 (live, revision 20260923 — read directly and parsed with jq)
- https://github.com/googleapis/google-api-go-client/blob/main/health/v4/health-api.json (Google's published copy; the 20260908 and 20260923 regenerations were diffed by the research worker — read directly)
- https://developers.google.com/health/release-notes (the human-readable notes — proxy-blocked from the sandbox; whether Google wrote an entry for either revision is unconfirmed)

## Why flagged

Google Health API v4 is the single wearable data path, and the discovery document is the only
primary source the sandbox can read for it. Neither revision breaks a read-only client, but the
`PERMISSION_DENIED` rule is a new failure mode on the same endpoints we call, and LF/HF power is a
new physiological field on a data type the SSA baseline already consumes. Recording both here is
what makes a later change to them an update rather than news.

## Question to answer next

1. Is the daily HRV point from a Fitbit or Pixel Watch now carrying `metadata.lowFrequencyPower`
   / `highFrequencyPower`? Run the field-population probe on one live connection. If yes, decide
   whether frequency-domain HRV belongs in the SSA pre-processing inputs (the alerting algorithm
   card, `docs/compliance/alerting_algorithm_card.md`, would need a line) or stays ignored.
2. Add a one-line check to the digest routine: assert the OAuth scope list requested by
   `DeviceProviders` still contains no `.writeonly` scope, so the `self-sources` rule stays
   irrelevant by construction.

claude "work through @research/queue/2026-09-25-google-health-api-v4-rev-20260923-self-sources-hrv-lf-hf.md"

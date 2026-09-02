# Google Health API v4 adds ECG and Irregular Rhythm Notification data types

**Severity:** HIGH
**Category:** devices

## Summary

Google Health API v4's scope/data-type documentation now lists `electrocardiogram` (single-lead ECG
session: SINUS_RHYTHM/ATRIAL_FIBRILLATION/INCONCLUSIVE classification, average heart rate, sampling
frequency, raw waveform) and `irregular-rhythm-notification` (passive AFib-signal alert events) as
first-class data types, gated behind new `ecg.readonly` and `irn.readonly` OAuth scopes.

## Source links

- https://developers.google.com/health/scopes (primary — Google's own scope reference; page
  reported updated ~31 Jul 2026 per search index, could not confirm exact diff date via direct
  fetch, egress-blocked from this sandbox)
- https://developers.google.com/health/data-types/vitals (primary — data type reference)

## Why flagged

This is new cardiac-relevant data newly reachable through the only device engine CardiTrack has
wired up (GoogleHealth). ECG classification and AFib signal events are a direct fit for the
real-time assessment path (SSA → MedGemma severity routing) and would be a genuine capability
upgrade — arguably one of the more "sticky and ahead" features available with no new device
integration work, since Fitbit/Pixel Watch hardware already emits this data through the same OAuth
flow CardiTrack already uses.

## Question to answer next

Have someone with unblocked network access pull `developers.google.com/health/release-notes`
directly to confirm the exact ship date and check whether `ecg.readonly`/`irn.readonly` need to be
added to the existing OAuth consent scope list (`DeviceConnectionService` / provider config). If
confirmed, scope a product ticket: is ECG/AFib data worth surfacing in the real-time assessor and
the M1-15 device cards, and does the SSA/MedGemma prompt design need a new data shape for it?

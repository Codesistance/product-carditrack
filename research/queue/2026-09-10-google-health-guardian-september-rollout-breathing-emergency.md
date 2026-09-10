# Health Guardian ships this month (Sept 2026) + new breathing-emergency detection — still not in the Google Health API

**Severity:** HIGH (update to the 2026-09-09 item; same URL, materially more specific)
**Category:** devices

## Summary

This updates `google-health-guardian-bp-insulin-trends` (first logged 2026-09-09).
What's new since then:

1. **Firm timing.** The blood-pressure and insulin-resistance trend features are
   rolling out as a software update **this month** (September 2026) to Pixel
   Watch 3/4/5 and Fitbit Air — not a vague "this fall" as originally reported.
2. **New capability not previously logged: breathing-emergency detection.**
   Health Guardian can flag a persistent or critical drop in blood-oxygen
   saturation (e.g. overdose, choking, severe pneumonia). Initial availability
   is Pixel Watch only, in select European countries.
3. **Still confirmed app-only, not API.** Multiple secondary outlets describe
   this as a Fitbit/Pixel Health app feature; nothing indicates it is exposed
   through `health.googleapis.com` (the only path CardiTrack reads from). This
   reaffirms the original finding rather than changing it.

## Why flagged

Directly relevant to "features that keep CardiTrack sticky and ahead" — Google
is shipping background, no-cuff BP/insulin-resistance trend detection and
emergency-grade breathing-distress alerting natively on hardware CardiTrack
already connects to (Fitbit, Pixel Watch), while CardiTrack's own pipeline
still has to derive comparable signals from raw wearable time series via the
SSA/baseline pipeline. If Google ever exposes any of this through the Health
API, it would be immediately relevant data CardiTrack could ingest — and if it
doesn't, competitors integrating natively against the Fitbit/Pixel first-party
app (not the API) get a head start on "detects an emergency automatically"
messaging that CardiTrack cannot currently match for its own primary users.

## Sources

- Primary (could not be machine-fetched in this sandbox — `blog.google` is
  blocked by the network egress proxy; confirm manually):
  https://blog.google/products-and-platforms/products/google-health/pixel-watch-health-guardian/
- Corroborating trade coverage (not sole source):
  https://www.mobihealthnews.com/news/google-unveils-health-guardian-features-pixel-watch-fitbit
  https://www.phonearena.com/news/new-google-health-app-update-hands-fitbit-and-pixel-watch-users-a-much-needed-win_id181227

## Open question

Once the September rollout lands, does `GET /v4/users/me/pairedDevices` or any
Health API data-type gain a new scope for these trends (mirroring how
`electrocardiogram` / `irregular-rhythm-notification` scopes appeared after
launch, per the 2026-09-04 `google-health-api-cardiac-scopes` item)? Worth a
follow-up API discovery-document diff in ~2-4 weeks after the feature ships.

---

claude "work through @research/queue/2026-09-10-google-health-guardian-september-rollout-breathing-emergency.md"

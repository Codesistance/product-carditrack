# Health Guardian is live in the Google Health app on Pixel Watch 3/4/5 — Blood Pressure Trends, Insulin Resistance Trends and Sleep Breathing Quality; Fitbit Air later, behind Premium

**Severity:** HIGH
**Category:** devices

## Summary

Update to the 2026-09-09 item ("Google 'Health Guardian' ships BP + insulin-resistance trends to
Fitbit/Pixel Watch — not yet in the Health API"), on a new URL because Google has published a new
post for the go-live. On **2026-09-24** Google's Keyword post "Our new health and safety tools are
live in the Google Health app" announced that the three Health Guardian features began rolling out
this week to owners of the **Pixel Watch 3, 4 and 5**:

- **Blood Pressure Trends** — passively estimates a blood-pressure *trend* from pulse and motion
  patterns over the month, no cuff and no calibration, and delivers one trend report on the first
  day of each month. Google's own disclaimer, as quoted across the coverage: not a replacement for a
  blood-pressure monitor; confirm persistent changes with a home cuff or a clinician.
- **Insulin Resistance Trends** — a multi-week metabolic trend with no blood sample.
- **Sleep Breathing Quality** — a daily view of time spent with optimal breathing, rolled into a
  monthly assessment.

Users set the features up now; the first monthly reports are expected in **early October 2026**.
The three features are free on those Pixel Watches. **Fitbit Air** owners get Blood Pressure and
Insulin Resistance Trends "later this year" and only with **Google Health Premium** ($9.99/month,
$99.99/year, or bundled with Google AI Pro).

**What we do not know.** blog.google is denied by the digest sandbox's network policy, so the post
itself was not read; every fact above comes from the search index of the post and from six
same-day secondary write-ups (9to5Google, Droid-Life, Android Authority, Android Central, Trusted
Reviews, heise) that agree with each other. The country list for the go-live, whether the
breathing-emergency SpO2 alerts announced for Europe went live in the same drop, and whether the
Google Health API v4 exposes any of the three trends were **not** confirmed. On the last point the
digest checked today's discovery document directly (revision 20260923): there is no blood-pressure
trend, insulin-resistance or sleep-breathing data type in it, and the `SleepSummary` /
`RespiratoryRateSleepSummary` schemas are unchanged. So as of today the trends live only in the
Google Health app UI, not on CardiTrack's data path.

## Sources

- https://blog.google/products-and-platforms/products/google-health/health-guardian-features-live/ (primary — indexed title "Our new health and safety tools are live in the Google Health app."; blog.google is proxy-blocked from the sandbox, not read directly)
- https://blog.google/products-and-platforms/products/google-health/pixel-watch-health-guardian/ (the 2026-09-09 preview post already in the digest; same block)
- https://9to5google.com/2026/09/24/pixel-watch-blood-pressure-insulin-resistance/ and https://www.droid-life.com/2026/09/24/pixel-watchs-health-guardian-is-rolling-out-to-your-watch/ (secondary corroboration of date, devices and Premium gating — not read directly either; search index only)
- https://health.googleapis.com/$discovery/rest?version=v4 (revision 20260923 — read directly; no data type for any of the three trends)

## Why flagged

HIGH under the competition rule: Google is now shipping a plain-language monthly blood-pressure
*trend* narrative to the exact devices CardiTrack integrates, and "BP trend narration" is on our
roadmap. It also changes what we can claim for the Fitbit Air ($99, AFib alerts, previously flagged
as "on CardiTrack's existing platform"): its Health Guardian trends sit behind a paid tier the
wearer, not the caregiver, would have to hold.

## Question to answer next

1. Confirm at source (someone with browser access) the country list and whether the UK is in the
   first wave — the caregiver value depends on the wearer's region.
2. Decide the product line for "the wearer's Google Health app already shows a BP trend": either
   CardiTrack says nothing about BP until the API carries it, or the caregiver copy points the
   wearer to their own app. The open medical-device assessment in the DPIA (`docs/compliance/dpia.md`,
   OI-2) governs early-warning claims; check that the supported-devices page and the Fitbit Air
   line make no blood-pressure claim that assessment has not cleared.
3. Add the three trend names to the discovery-document watch (`SleepSummary`,
   `RespiratoryRateSleepSummary`, any new `BloodPressure*` / `InsulinResistance*` schema) so the
   day Google exposes them via the API is caught as a schema change, not as news.

claude "work through @research/queue/2026-09-25-google-health-guardian-features-live-pixel-watch.md"

# Google Play technical-quality requirements: memory thresholds and a 25% DEX-optimisation floor enforced from February 2027; "Zero-Tap Sign-In" via the Restore Credentials API required from April 2027

**Severity:** FYI
**Category:** dependencies

## Summary

Android Developers blog post of **2026-08-26**, "Elevating app quality: reducing memory usage and
improving device migration" (read directly on the developer.android.com mirror). It announces two
dated Google Play requirements that never surface as a version string:

- **From February 2027** Play enforces technical-quality thresholds on **dynamic memory usage**
  (anonymous RSS + swap), **bitmap memory usage**, and **DEX code optimisation** — "a minimum of
  25% coverage across optimization, shrinking, and obfuscation". Android vitals gains the metrics
  and per-bundle DEX-optimisation insights.
- **From April 2027** any app with user sign-in must restore the user's signed-in state on a new
  device automatically through the **Android Restore Credentials API** (Credential Manager) —
  "Zero-Tap Sign-In". Games are exempt.

Non-compliant apps "may see reduced app visibility on Google Play and reduced publishing
capabilities on Google Play". The companion App Excellence Programme guideline
(`developer.android.com/distribute/aep/aep-req-restore-credentials`, last updated 2026-08-26,
read directly by the research worker) adds two things the blog post does not: a stated exemption
for "high-security" apps, naming **healthcare** among them (AEP-RC-EAB), and a grandfathering of
existing **Block Store** integrations completed on or before **2026-09-30** (AEP-RC-EAC). Whether
the healthcare exemption applies to Play's enforcement, or only to the AEP badge, is not stated
anywhere the sandbox could read; the Play Console help page that carries the requirement
(`support.google.com/googleplay/android-developer/answer/17492799`) is proxy-blocked.

**Where CardiTrack stands.**
- Sign-in is Auth0 via the system browser; the app holds no Credential Manager or Block Store
  integration, so it is not grandfathered and would need Restore Credentials by April 2027 unless
  the healthcare exemption is confirmed to apply.
- Release Android builds already set `AndroidLinkTool` to `r8` and CI uploads `mapping.txt` to
  Play Console, so the 25% DEX floor is most likely already met — but nobody has read the number
  off Android vitals, and the memory thresholds have never been measured for this app.

## Sources

- https://developer.android.com/blog/posts/elevating-app-quality-reducing-memory-usage-and-improving-device-migration (primary, dated 26 Aug 2026 — read directly)
- https://developer.android.com/distribute/aep/aep-req-restore-credentials (AEP guideline with the healthcare exemption and the 2026-09-30 Block Store grandfathering — read directly by the research worker)
- https://support.google.com/googleplay/android-developer/answer/17492799 (Play Console technical-quality requirements — proxy-blocked, search index only)

## Why flagged

Two dated migration windows on the Android store path, one of them (Restore Credentials) a
feature the app does not have. Neither is inside 30 days, hence FYI, matching how the April 2027
iOS 27 SDK rule was recorded on 2026-09-22. Recording it now leaves six months rather than a
surprise in a Play Console warning.

## Question to answer next

1. Read the DEX-optimisation percentage and the two memory metrics for the current release off
   Android vitals; if any is below the published threshold, that is the first job.
2. Settle whether the AEP healthcare exemption covers Play enforcement for CardiTrack (a
   caregiver app with Auth0 sign-in that is not itself a medical device). If not, scope a
   Restore Credentials implementation for the MAUI app — the API is Kotlin/Jetpack Credential
   Manager, so it means a platform binding or an Android-only service.

claude "work through @research/queue/2026-09-25-google-play-quality-requirements-feb-2027-dex-april-2027-zero-tap-sign-in.md"

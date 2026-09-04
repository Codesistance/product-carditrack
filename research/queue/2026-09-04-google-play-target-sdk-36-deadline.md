# Google Play target API level 36 deadline — already passed (2026-08-31)

**Severity:** CRITICAL
**Category:** dependencies

## Summary

Google Play requires new app versions and updates to target **Android 16 (API level
36)** as of **2026-08-31** — 4 days before this run. Uploads that don't meet this are
rejected by Play Console. A one-time extension is available via a form on the Play
Console policy status page, pushing the effective deadline to **2026-11-01**.

CardiTrack's mobile manifest pins `minSdk = 31` (`SupportedOSPlatformVersion` for
android), but the *target* SDK level used for Play Console uploads was not visible in
the version pins gathered for this run — `minSdk` and `targetSdk` are independent
settings in a MAUI Android head project.

## Sources

- https://support.google.com/googleplay/android-developer/answer/11926878 (Google Play's own target-API-level policy page — primary)

## Why flagged

This is a dated, already-active deadline. If CardiTrack's next Android release is not
already built against API level 36, the next `build-mobile-android-signed` /
`deploy-play-internal` run in `deploy-apps-dev.yml` or `deploy-apps-prod.yml` risks a
Play Console rejection with no warning until upload time.

## Question to answer next

Find the effective Android `TargetFrameworkVersion` / `TargetSdkVersion` CardiTrack's
MAUI Android head actually compiles against (check
`src/Presentation/CardiTrack.Mobile/CardiTrack.Mobile.csproj` — TargetFramework is
`net10.0-android`, and .NET's Android workload maps that to a specific API level per the
installed workload/SDK, which may differ from `SupportedOSPlatformVersion`). If it is
below 36, either bump it before the next Play upload, or file the one-time extension
request now to buy time to 2026-11-01.

claude "work through @research/queue/2026-09-04-google-play-target-sdk-36-deadline.md"

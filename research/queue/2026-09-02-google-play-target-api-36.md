# Google Play target API level 36 (Android 16) deadline

**Severity:** HIGH
**Category:** dependencies

## Summary

Since **31 August 2026**, new apps and app updates on Google Play must target Android 16 (API level
36); existing apps must target at least Android 15 (API 35) to stay visible to new users on newer
devices. A one-time extension to **1 November 2026** is available on request. Today (2 September
2026) is two days past the primary deadline.

## Source links

- https://support.google.com/googleplay/android-developer/answer/11926878?hl=en (Play Console
  Help — primary, official Google source)

## Why flagged

CardiTrack's MAUI mobile project (`CardiTrack.Mobile.csproj`) sets `SupportedOSPlatformVersion`
(minimum) to 31.0 but has **no explicit `TargetSdkVersion`/`android:targetSdkVersion` override**
anywhere in the project or `AndroidManifest.xml`, and CI does not pin an Android SDK/API level for
the build. For a `net10.0-android` MAUI target, the effective target API level tracks whatever the
installed Android workload resolves to at build time — which for an actively-maintained `.NET 10`
toolchain in September 2026 should already default to a current API level — but "should" isn't
"confirmed." This is a real deadline that's already passed, so it needs a direct answer, not an
assumption either way.

## Question to answer next

Check the most recent successful `deploy-apps-*.yml` run's build output (or the uploaded AAB's
manifest via `bundletool` / Play Console's own report) for the actual resolved `targetSdkVersion`.
If it's already ≥36, this closes with no change. If not, either bump the Android workload/target
framework moniker to force API 36, or file the one-time extension to 1 November 2026 immediately to
avoid update rejection.

# Firebase Apple SDK stops publishing to CocoaPods after October 2026; Firebase 13 will be Swift Package Manager and zip only — the binding chain under CardiTrack's iOS push

**Severity:** FYI
**Category:** dependencies
**Date recorded:** 2026-09-26

## Summary

Firebase's Apple SDK will stop publishing to CocoaPods after **October 2026**. The repository README
on `main` carries the warning, and the FirebaseCore changelog names **12.19.0** as "the final scheduled
release published to CocoaPods" (a 12.19.2 patch shipped 2026-09-15 per search snippets). Existing pod
versions remain available. Google's migration page (firebase.google.com/docs/ios/cocoapods-deprecation,
proxy-blocked; snippet says last updated 2026-09-24) adds that CocoaPods itself becomes read-only in
December 2026.

**Firebase 13.0.0** is on `main` but unreleased: distributed via Swift Package Manager and zip only,
minimum iOS 15, Swift tools 6.2.1 / Xcode ≥ 26.2 to resolve, CocoaPods umbrella headers and the
`org.cocoapods.` bundle-ID prefixes removed from the zip artefacts.

**Where CardiTrack stands.** The MAUI app has no Podfile and CI runs no `pod install`; the store
uploads are GitHub Actions direct. iOS push is `Plugin.Firebase.CloudMessaging` 4.0.1, whose
`net9.0-ios18.0` dependency group (NuGet registration, read directly this run) is
`Plugin.Firebase.Core ≥ 4.1.0` and **`AdamE.Firebase.iOS.CloudMessaging ≥ 12.5.0.4`**. The AdamE
binding's latest published version is **12.10.0** (2026-04-21); nothing since. That binding wraps the
native Firebase xcframeworks. From Firebase 13 the maintainer must source them from SPM or the zip
distribution; nothing breaks on the day, but the chain can freeze on Firebase 12.x while Apple's
April-2027 iOS 27 SDK requirement arrives — and a 12.x binding built against the iOS 26 SDK may or may
not link cleanly under Xcode 27.

## Sources

- https://github.com/firebase/firebase-ios-sdk/blob/main/FirebaseCore/CHANGELOG.md — 12.19.0 "final scheduled release published to CocoaPods"; 13.0.0 unreleased notes (read directly via raw.githubusercontent.com)
- https://github.com/firebase/firebase-ios-sdk — README warning (read directly)
- https://firebase.google.com/docs/ios/cocoapods-deprecation — Google's migration page (proxy-blocked; indexed)
- https://api.nuget.org/v3/registration5-gz-semver2/plugin.firebase.cloudmessaging/4.0.1.json — dependency groups (read directly)
- https://api.nuget.org/v3-flatcontainer/adame.firebase.ios.cloudmessaging/index.json — latest 12.10.0 (read directly)

## Why flagged

A dated distribution change two levels below a pinned dependency, on the only iOS push path,
arriving in the same window as the recorded April-2027 Xcode 27 requirement. Nothing to do today;
recording it makes the Xcode 27 decision a planned one.

## Question to answer next

1. Does the AdamE.Firebase.iOS.* maintainer have a stated Firebase 13 / SPM plan (repo README or
   issues — the README 404s on main/master from here)? Does Plugin.Firebase plan a 5.x on it?
2. Confirm with a restore on the iOS TFM (`project.assets.json`) which exact AdamE version 4.0.1
   resolves to today, and record it in the mobile runbook.
3. Decide, before the Xcode 27 move, whether iOS push stays deliberately on the Firebase 12.x
   binding line, and test that binding once under the Xcode 27 preview runner image.

claude "work through @research/queue/2026-09-26-firebase-ios-sdk-leaves-cocoapods-after-october-2026.md"

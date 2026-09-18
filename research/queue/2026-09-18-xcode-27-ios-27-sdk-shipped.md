# Xcode 27 / iOS 27 SDKs shipped (2026-09-14) — new App Store Connect minimum-SDK deadline set for April 2027; CardiTrack's Xcode 26.4.1 pin remains compliant until then

**Date:** 2026-09-18
**Severity:** FYI
**Category:** dependencies
**Source:** https://developer.apple.com/news/?id=k1mtkt1k

## Summary

Xcode 27 (build 27A266a) and the iOS/iPadOS/tvOS/visionOS/watchOS 27 SDKs shipped alongside iOS 27 on 2026-09-14. Apple has set April 2027 as the deadline by which new App Store Connect submissions/updates must be built with the 27-series SDKs; today's requirement (Xcode 26 SDK, live since 2026-04-28) is unaffected in the meantime.

## Why flagged

New since the last check (Xcode 27 didn't exist on 2026-09-09); gives ~7 months runway but is a real dated deadline the MAUI/iOS pipeline will need to clear.

## Next question / action

Open a tracking ticket to validate the CardiTrack iOS build against Xcode 27 / a matching maui-ios workload well before April 2027.

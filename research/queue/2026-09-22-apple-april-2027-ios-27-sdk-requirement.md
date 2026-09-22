# Apple: from April 2027, App Store Connect uploads must be built with the iOS 27 SDK — CI pins Xcode 26.4.1 and no stable .NET 10 iOS workload for Xcode 27 exists yet

**Severity:** FYI
**Category:** dependencies

## Summary

Apple's developer news post of 2026-09-09, "App Store submissions now open for the latest OS
releases", states: "Starting April 2027, apps and games uploaded to App Store Connect need to be
built with the iOS 27 & iPadOS 27 SDK or later" (also tvOS, visionOS and watchOS 27). The exact
day is not given; Apple's "Upcoming requirements" page still lists only the 2026-04-28 Xcode 26
rule. Xcode 27 GA (27A266a) shipped 2026-09-14.

Where CardiTrack stands:

- `deploy-apps-dev.yml` selects `/Applications/Xcode_26.4.1.app` on `macos-26` (lines 462 and
  817) — i.e. the iOS 26 SDK. Uploads built this way stop being accepted from April 2027.
- `Microsoft.Maui.Controls` is pinned at 10.0.101, which requires Xcode 26.6. In `dotnet/macios`
  the .NET 10 iOS workload for Xcode 27 exists only as pre-release builds ("Xcode 27.0 Beta 6
  support", 2026-09-08). .NET 11 (GA expected November 2026) is the other route.

So the deadline is roughly seven months out, but the migration path for the current .NET 10
toolchain is not yet available — this is a "watch for the workload" item, not a "bump the pin"
item.

## Sources

- https://developer.apple.com/news/?id=k1mtkt1k (Apple Developer News, 2026-09-09)
- https://developer.apple.com/news/upcoming-requirements/ (still shows only the Xcode 26 rule)
- https://github.com/dotnet/macios/releases (Xcode 27 support for .NET 10 marked pre-release)
- https://github.com/dotnet/maui/releases (10.0.101, 2026-09-07)

## Why flagged

A dated store requirement that the current CI configuration will fail. Nothing breaks before
April 2027, but the fix depends on Microsoft shipping a stable Xcode 27 workload for .NET 10 (or
CardiTrack moving to .NET 11), neither of which is in CardiTrack's control.

## Question to answer next

Pick the route and put a date on it: (a) stay on .NET 10 and wait for a stable
`maui-ios` / macios workload with Xcode 27 GA support, then bump the CI Xcode pin; or (b) plan the
.NET 11 move for after its November 2026 GA and take Xcode 27 with it. Either way, set a check
for January 2027: if no stable path exists by then, the April deadline is at risk.

claude "work through @research/queue/2026-09-22-apple-april-2027-ios-27-sdk-requirement.md"

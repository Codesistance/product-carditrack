# Android developer verification: apps must be registered to a verified developer by 2026-09-30 in Brazil, Indonesia, Singapore and Thailand; global in 2027

**Severity:** FYI
**Category:** dependencies

## Summary

Google's Android developer verification programme reaches its first enforcement date on
**2026-09-30**: from then, certified Android devices in Brazil, Indonesia, Singapore and Thailand
block normal installs of any app whose package is not registered to a verified developer —
whether the app comes from Google Play or a participating third-party store. Google says the
requirement expands globally in 2027 and that roughly 99% of Play apps are registered
automatically through the Play Console.

CardiTrack's Android package is `com.codesistance.carditrack.mobile`, shipped through Google Play
from GitHub Actions. The product is UK-focused, so the September cut-off almost certainly does not
touch a real user; the 2027 global step will. There is no record in this repo of the developer
account's verification status or the app's registration status.

## Sources

- https://developer.android.com/developer-verification (timeline table)
- https://android-developers.googleblog.com/2026/06/android-developer-verification.html
  (Android Developers Blog, June 2026 — dates confirmed)
- https://support.google.com/android-developer-console/answer/16561738 (help centre)

(All three domains are proxy-blocked from the digest sandbox; dates are as reported consistently
across the search index of those pages.)

## Why flagged

A dated platform requirement with an install-blocking consequence. The action is trivial and the
date is eight days out, so it is cheaper to confirm now than to discover it in 2027.

## Question to answer next

Open Play Console → Home for `com.codesistance.carditrack.mobile` and confirm the developer
account shows as verified and the package as registered. If it does, note that in
`docs/technical/production_setup_runbook.md` (or wherever store credentials are documented) and
close this. If it does not, complete verification before 2026-09-30.

claude "work through @research/queue/2026-09-22-android-developer-verification-2026-09-30.md"

# Pixel Watch 5 launches with Google Health "Guardian" trend features live

**Severity:** FYI
**Category:** devices
**Date flagged:** 2026-09-17

## Summary

Google Health's "Guardian" trend-detection features — previously reported
(2026-09-09) as announced — have now actually shipped with the Pixel Watch 5
preorder launch, alongside Gemini-powered coaching and a revamped Morning
Brief. Pixel Watch is one of CardiTrack's only two actually-connectable
device types (via the Google Health API), so this is a live capability
change on a live integration, not roadmap talk.

## Why flagged

Confirms Guardian trend data is now flowing for real users through a device
CardiTrack actually connects to, not vaporware. FYI rather than HIGH because
it is additive to an existing integration, not a sunset or breaking change.

## Sources

- https://blog.google/products-and-platforms/products/google-health/pixel-watch-health-guardian/

## Question to answer next

Does the Google Health API v4 scope set CardiTrack already requests expose
these new Guardian-derived trend fields to third-party OAuth consumers, or
is Guardian output Google-app-exclusive (i.e. computed and surfaced only in
Google's own Health/Fitbit apps, not returned via the public API)?

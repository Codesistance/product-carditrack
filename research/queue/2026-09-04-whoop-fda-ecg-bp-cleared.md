# Whoop FDA enforcement dropped — ECG/AFib + Blood Pressure Insights now live

**Severity:** HIGH
**Category:** devices

## Summary

After FDA's January 2026 wellness-guidance update, FDA dropped its warning letter
against Whoop (2026-06-23) over its wrist-based Blood Pressure Insights feature. Whoop's
premium tier (WHOOP MG / LIFE) now bundles that feature with FDA-cleared ECG/AFib
screening.

## Sources

- https://www.statnews.com/2026/06/23/fda-drops-enforcement-against-wearable-maker-whoop/

## Why flagged

This is roadmap intelligence, not a live risk: Whoop has a config block in CardiTrack
today but **no provider-string mapping**, so it is entirely unreachable from the API
(confirmed against `docs/execution/backend/api/devices.md`). Whoop now has arguably the
strongest cardiac feature set (FDA-cleared ECG/AFib + continuous BP) of any device in
CardiTrack's provider list, live or stubbed — this materially raises the case for
prioritizing a real Whoop integration over leaving it as a placeholder.

## Question to answer next

Scope what a real Whoop integration would require (API access model, OAuth flow,
whether it goes through Google Health API or a direct Whoop API) and size it against the
current backlog of stubbed providers (Garmin, Withings, Oura, Samsung Health) — does
Whoop's now-stronger cardiac feature set move it to the front of that queue?

claude "work through @research/queue/2026-09-04-whoop-fda-ecg-bp-cleared.md"

## Resolution — 2026-09-05

**Dropped from the roadmap.** Whoop's API is standard OAuth 2.0 and would have fitted the
server-side convention, and its cardiac feature set is now the strongest on the list — but it
is a subscription band built for athletes, and that is not the wearer population CardiTrack
serves. Decision recorded in the release matrix (decision log #9), `devices.md`, the
solution manifest and README; removed from the public roadmap and the legal provider lists on
carditrack.com the same day. If a wearer already shares a Whoop into Google Health, it may
still arrive through the GoogleHealth engine; no dedicated work. The `DeviceType.Whoop`,
`HealthApi.Whoop` and appsettings block are cleanup. Closed.

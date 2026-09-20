# Garmin already syncs into Google Health Connect directly — possible zero-engineering path around the closed developer program

**Severity:** HIGH
**Category:** devices

## Summary

Garmin's own support documentation confirms the Garmin Connect Android app can sync
activity, calories, distance, heart rate, steps, sleep and other wellness data into Google
Health Connect once the wearer opts in (rolled out ~June 2025, still live and described
directly by Garmin, not a rumor). This is the identical bridge pattern CardiTrack already
relies on for Apple Watch and Samsung Galaxy Watch (`via_google_health` — the wearer shares
their watch data into the Google Health app on their own phone via Health Connect / Apple
Health, and CardiTrack pulls it through the same Google Health API used for Fitbit). It
works independently of Garmin's own developer API (the Connect Developer Program), whose
public application form was reported removed with no ETA in yesterday's digest
(2026-09-19).

## Sources

- https://support.garmin.com/en-US/?faq=JToBEy0jfe6pIygark2Ui5 (Garmin's own support page, "Sharing Your Garmin Connect Data With Google Health Connect")

## Why flagged

`docs/execution/backend/api/devices.md` currently plans Garmin as an R2 dedicated
`server_oauth` integration, now blocked indefinitely by the closed developer program, with
Withings named as the fallback. This finding suggests a third option: route Garmin the
same way Apple Watch and Samsung Galaxy Watch already are — add `Garmin` to the
GoogleHealth provider block's `DeviceTypes`, give it a `provider` string, and point the
mobile picker at the Google connect flow. If Garmin's data comes through Health Connect →
Google Health app → Health API v4 with acceptable fidelity, this could reach Garmin wearers
with a picker-label change instead of an entire dedicated OAuth engine — and without
depending on Garmin's developer program reopening at all.

## Question to answer next

Run the same "live-device field-population probe" already planned for Apple Watch/Samsung
Galaxy Watch (per devices.md's "Open verification" note) against a Garmin device synced
through Health Connect: confirm heart rate, steps and sleep specifically (the metrics the
alert rules depend on) actually populate through to the Google Health API with the fidelity
CardiTrack needs, before deciding whether to retire the Garmin R2 `server_oauth` plan in
favor of the `via_google_health` route.

claude "work through @research/queue/2026-09-20-garmin-health-connect-bridge-workaround.md"

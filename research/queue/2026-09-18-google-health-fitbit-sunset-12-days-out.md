# Fitbit legacy API hard-sunset now 12 days out (2026-09-30) — consumer-side migration fully closed (app v5.08); CardiTrack confirmed unaffected

**Date:** 2026-09-18
**Severity:** HIGH
**Category:** dependencies
**Source:** https://developers.google.com/health/migration

## Summary

The 2026-09-30 hard-sunset date is unchanged. New since the 2026-09-09 check: Google Health app v5.08 (released 2026-09-11) ended the Fitbit-login migration path entirely on the consumer side — Fitbit account logins no longer work and unmigrated data is now deleted. CardiTrack's own OAuth flow already targets `health.googleapis.com` v4 directly and never used legacy Fitbit login, so this consumer-side closeout does not change CardiTrack's exposure — but it does mean the whole ecosystem-wide migration window is now effectively over, not just approaching.

## Why flagged

This is CardiTrack's most imminent dated dependency deadline. Downgraded from the original CRITICAL to HIGH because the backend's own exposure has now been confirmed clear against the actual GoogleHealth provider code, not just the migration doc — but 12 days is close enough to warrant one final live check before the date passes rather than relying on a doc read alone.

## Next question / action

Run one live OAuth connect + sync test against a real Fitbit-linked account before 2026-09-30 to confirm end-to-end behaviour, not just the code path, ahead of the hard cutover.

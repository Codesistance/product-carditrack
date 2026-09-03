# Circular Ring 2 — FDA-cleared ECG/AFib, cloud-sync architecture

**Severity:** FYI
**Category:** devices
**Date flagged:** 2026-09-03

## Summary

Circular's own release notes (2026-03) confirm an FDA-cleared ECG/AFib algorithm and in-progress
blood-pressure tracking on Ring 2, continuous cloud sync, and an offline mode.

## Sources

- https://www.circular.xyz/post/circular-ring-2-release-notes-march-2026

## Why flagged

Circular has no CardiTrack provider mapping at all today. Its continuous cloud-sync architecture
is compatible in shape with CardiTrack's server-OAuth integration model (the only mode CardiTrack
currently supports in production — Fitbit and Pixel Watch, both server-side OAuth via Google
Health API), unlike Apple Watch's planned on-device bridge.

## Question to answer next

Does Circular expose a public developer/partner API for third-party server-OAuth integration
(similar to Fitbit/Google Health), and if so, is a ring form factor worth scoping for a future
device-integration wave?

claude "work through @research/queue/2026-09-03-circular-ring-2.md"

# Withings launches StethO Sense — AI heart/lung sound analysis for family use on BeamO

**Severity:** FYI
**Category:** devices

## Summary

Withings announced StethO Sense on 2026-09-04 (at IFA 2026, Berlin), a new
Withings+ subscription service that adds on-demand AI-powered analysis of
heart and lung recordings to BeamO, its FDA-cleared handheld combining a
clinical-grade thermometer, 1-lead ECG, and digital stethoscope. Heart-sound
analysis is powered by eMurmur Heart AI (abnormal murmur detection); breathing
analysis by StethoMe. It ships 2026-10-01, exclusively for Withings+ members;
new BeamO purchases from that date include a 1-month free trial, and existing
owners get a complimentary one-month trial. Withings has positioned BeamO
generally as "at-home family healthcare" (its earlier US FDA-clearance PR
uses that framing), and this launch continues it — the pitch is a family
member checking a relative's heart/lung sounds at home and getting an
AI-narrated read on it.

## Sources

- https://www.prnewswire.com/news-releases/withings-introduces-stetho-sense-bringing-heart-and-lung-sound-analysis-to-beamo-302869284.html (Withings press release via PR Newswire — primary)
- https://gadgetsandwearables.com/2026/09/04/withings-beamo-stetho-sense-ifa-2026/ (trade press, IFA 2026 coverage, corroborating)

## Why it matters to CardiTrack

**Roadmap intelligence, not live integration risk.** Withings exists in
CardiTrack only as a config stub with a placeholder client id (planned R2/R4
engine) — the GoogleHealth engine (Fitbit, Pixel Watch) is the only one
registered in DI today, confirmed against `docs/execution/backend/api/devices.md`.
Nothing here touches health.googleapis.com or a connectable provider.

The reason to track it: BeamO is a **handheld, on-demand, self-administered**
device (you place it on your own or a relative's chest), not a passive
continuous wearable — so it doesn't compete with CardiTrack's continuous
remote-monitoring model directly. But it is a concrete, shipping example of
the exact positioning CardiTrack's roadmap is aimed at — "AI narrates a
vital sign for a family member checking on a loved one" — from a company
that already holds an FDA-cleared cardiac sensor and a family-healthcare
brand story. If Withings' R2/R4 engine ever goes live, this AI-narration
layer would already exist to bolt onto continuous data.

## Question to answer next

Does Withings' StethO Sense/BeamO marketing (or user reviews after the
2026-10-01 launch) describe any caregiver-to-caregiver sharing or remote
notification flow — i.e., can a family member who isn't holding the device
receive an AI-narrated alert about someone else's reading? That would move
this from "on-demand self-check" into direct overlap with CardiTrack's
remote-caregiver-alert roadmap and would warrant re-flagging at HIGH.

claude "work through @research/queue/2026-09-08-withings-stetho-sense.md"

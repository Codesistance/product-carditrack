# Apple Watch Series 12 / Ultra 4: redesigned ECG, "Health Age" score, Longevity tab

**Severity:** FYI
**Category:** models / competition
**Date found:** 2026-09-12 (event 2026-09-09)

## Summary

Apple's 2026-09-09 event introduced Watch Series 12/Ultra 4 with redesigned
ECG electrodes (more skin-contact surface area), a "Health Age" score, and a
Longevity tab in a redesigned Health app, alongside the existing FDA-cleared
hypertension notifications feature. All of this runs on-device
statistical/ML signal processing, not an LLM narration layer. Regulatory
clearance for the new hardware's hypertension feature is still pending in
~33 countries.

Relevant to CardiTrack only as landscape signal: Apple continues to invest in
first-party, on-device cardiac signal-processing models rather than an LLM
narration layer like MedGemma — reinforcing that CardiTrack's "SSA
pre-processing + MedGemma narration" architecture is a differentiated
approach, not one converging with the market leader's.

## Sources

- https://www.macrumors.com/2026/09/10/apple-watch-series-12-hypertension-alerts/

## Why flagged

Competitive-landscape signal for the "features that keep CardiTrack sticky
and ahead" watch area — no licensing or API surface CardiTrack consumes, and
Apple Watch is not (and per the 2026-09-05 decision, will not be) a dedicated
CardiTrack device integration.

## Next question

None required — purely informational. Re-flag only if Apple ships anything
that touches an API CardiTrack could integrate against (it currently does
not expose one relevant here).

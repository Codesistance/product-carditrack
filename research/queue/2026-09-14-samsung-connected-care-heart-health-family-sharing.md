# Samsung Adds Family Heart-Data Sharing With Galaxy Watch 'Connected Care'

## Summary
At a Seoul media briefing on 2026-09-09, ahead of World Heart Day (2026-09-29), Samsung expanded Galaxy Watch heart-health features — blood pressure, ECG, and irregular heart-rhythm monitoring — and introduced *Connected Care*, a Samsung Health feature letting a watch wearer share health data (blood pressure, heart rate, ECG results, sleep, exercise, body composition, blood glucose, medication, vitals) with family members after linking Samsung accounts. Samsung cited real-world cases (Jordan, Brazil) where irregular-rhythm/ECG notifications led users to seek care. Connected Care itself was first previewed around the July 2026 Galaxy Unpacked event; the September briefing specifically tied it to expanded cardiac monitoring and framed it as a family/caregiver feature.

## Source links
- https://www.koreaherald.com/article/10867861 (Korea Herald, report on the 2026-09-09 Seoul briefing)
- https://www.thelec.net/news/articleView.html?idxno=13791 (The Elec, Samsung heart-health feature expansion)
- https://news.samsung.com/global/samsung-introduces-next-gen-galaxy-watch-features-for-ai-powered-everyday-health-companion (Samsung's own Connected Care announcement)

## Why flagged
Galaxy Watch has no dedicated CardiTrack integration and never will (decided 2026-09-05, see docs/execution/backend/api/devices.md) — it only reaches CardiTrack via the wearer's own Google Health app, and this news does not change that path or require any CardiTrack code change. It is flagged purely as competitive/market intelligence: Samsung is building a first-party "share cardiac vitals with family members" feature that functionally overlaps CardiTrack's core caregiver-monitoring value proposition, inside hardware/software CardiTrack cannot touch.

## Next question to answer
Does Connected Care's shared data (BP, ECG, irregular-rhythm flags) surface into Health Connect / the Google Health app in a form the Google Health API exposes to CardiTrack for Galaxy Watch wearers who route through Google Health — or is it a walled-garden Samsung-account-to-Samsung-account feature with no path into health.googleapis.com at all? Confirm before assuming Galaxy Watch wearers' BP/ECG data is reachable by CardiTrack's existing via_google_health path.

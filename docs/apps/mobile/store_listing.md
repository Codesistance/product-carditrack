# Play Console Store Listing — Copy

Paste-ready text for the Google Play Console **Main store listing** fields (and the App Store
equivalents, which share the wording). Field limits are enforced by the console: short description
80 characters, full description 4000.

Claims here are deliberately limited to what is shipped (see
[release_matrix.md](../../release_matrix.md)) and to what Play's Health apps policy allows: no
diagnosis, treatment or prevention claim, no medical-device framing, and an explicit "not an
emergency service" line — CardiTrack has no fall detection and no call-for-help path. Devices are
named as Fitbit and Pixel Watch only, since that is what the Google Health API connection covers
today; do not add a brand to this text before its connector ships.

Keep the two fields in sync with the app: any feature removed from the build has to leave this
file in the same change.

## Short description (80 max)

```text
Know how your parent is doing today - early signals from the watch they wear.
```

Alternates, same constraints — swap in if store-listing experiments call for it:

- `Daily insight into how your parent is doing, from the wearable they already own.` (80)
- `Preventive health monitoring for families, built on the watch they already own.` (79)
- `Wearable health monitoring for families: daily insight, early warning.` (70)

## Full description (4000 max)

```text
CardiTrack turns the wearable your parent already owns into quiet, daily reassurance for the family around them.

Most monitoring only reacts: a button is pressed after something has already happened. CardiTrack works the other way round. It learns what is normal for one person - their resting heart rate, sleep, activity - and tells you when their own pattern drifts away from it. No pendant, no new hardware, no call centre.

HOW IT WORKS

1. Create an account and add the person you care for.
2. Connect their Fitbit or Google Pixel Watch. It is a one-time, permission-based link they approve themselves; nothing is installed on their phone.
3. CardiTrack spends about 30 days learning their personal baseline, then watches it for you.

WHAT YOU GET

- Daily dashboard. One plain-language line on how the day is going, with heart rate, sleep, steps and activity at a glance.
- Alerts that explain themselves. Every alert says what changed, against which normal, and shows the chart it came from - so you can judge it, not just receive it. Acknowledge it, or undo that if you were too quick.
- Urgent alerts by push, with quiet hours, so the night stays quiet unless it shouldn't.
- CardiJournal. A short written account of each finished day, and of the week and the month, so you can catch up after a busy stretch instead of scrolling through raw numbers.
- Device health you can trust. You are told when the watch stops reporting, when its battery is low, or when a connection needs renewing. Monitoring that has quietly stopped is worse than none.
- Care context. Answer the occasional short question about how they have been - a trip away, a bad night - and later summaries read the numbers with that in mind.
- More than one person. Track several family members from a single account, and pause monitoring for anyone, any time.

WORKS WITH

Fitbit and Google Pixel Watch today, through the Google Health API. Support for further brands is planned.

YOUR DATA

- Health data is encrypted in transit and at rest.
- Data is read only after the wearer's explicit consent, and only the categories monitoring actually needs.
- Health data is never sold and never used for advertising.
- Disconnect a device, pause monitoring, or delete the account and its data whenever you choose: carditrack.com/delete-account
- Privacy policy: carditrack.com/privacy-policy

IMPORTANT

CardiTrack is for awareness, not medical advice. It is not a medical device. It does not diagnose, treat, cure or prevent any condition, and it is not an emergency service - it cannot detect a fall or call for help. In an emergency, call your local emergency number. Talk to a clinician about anything that worries you.

Includes a 30-day free trial. Questions: support@carditrack.com
```

## Notes for whoever pastes this

- **Trial line.** "Includes a 30-day free trial" is accurate today (trial provisions Complete Care
  for 30 days); there is no billing integration yet, so do **not** add prices or tier names to the
  listing until Stripe ships in R2.
- **Data safety form.** The YOUR DATA section must match the Data safety declaration exactly —
  collected categories, encryption in transit, and the deletion route
  (`carditrack.com/delete-account`). A mismatch is a listing rejection, not a warning.
- **Health-data disclosure.** The Google-mandated in-app disclosure is still missing on mobile
  (release matrix, "Health-data disclosure" row) — it is a public-launch gate independent of this
  copy.
- **No emoji, no repeated capitals, no competitor names or price comparisons** in either field:
  Play's store-listing policy treats them as promotional/spammy metadata.

## Related

- [Store provisioning — keys, certificates & secrets](./store_provisioning.md)
- [Mobile app documentation](./readme.md)

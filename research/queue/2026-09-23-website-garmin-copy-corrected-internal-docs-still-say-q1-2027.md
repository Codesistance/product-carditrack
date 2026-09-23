# Public supported-devices page no longer promises Garmin "Q1 2027" — fixed 2026-09-22; three internal docs still carry the quarter

**Severity:** FYI — update to the 2026-09-22 item on the same URL, now resolved on the public side
**Category:** devices

## Summary

Yesterday's digest flagged that carditrack.com/supported-devices listed **Garmin — Coming Q1 2027**
as a dedicated connection while the internal record (`devices.md`, the 2026-09-19 digest) said
Garmin's developer programme is closed with no reopening date.

That drift is closed on the public side. Website PR #80 ("Fix broken /privacy and /terms links, and
catch up the Garmin and wearer-flow copy", merged 2026-09-22 22:47 BST, commit 8e25519 in
`Codesistance/branding-carditrack-website`) changed the page to:

- Garmin: **"Planned"** (no quarter). Copy now reads "Garmin offers the same kind of connection in
  principle, but paused new developer access in 2026 with no date to reopen, so a dedicated Garmin
  connection depends on that reopening."
- It also points families at the Google Health route in the meantime ("Garmin's own app can already
  share readings into Google Health the same way an Apple Watch does — if your loved one's Garmin is
  already set up that way, tell us and we'll check what comes through") — option 2 from
  yesterday's brief, framed as "tell us" rather than a promise of coverage.
- Withings stays "Coming Q3 2027". Fitbit and Pixel Watch stay "Connected today". Apple Watch and
  Galaxy Watch stay "Through Google Health". All of that matches `devices.md` as of today.

Checked today against `docs/execution/backend/api/devices.md`: the page and the internal truth now
agree on what is connectable (Fitbit, Pixel Watch), what is bridged (Apple, Samsung), and that
Garmin has no date.

**What is now stale is the internal side.** Three product-repo documents still say "Garmin (Q1
2027)":

- `docs/google_credits_pitch.md` lines 38 and 101 ("Garmin support … Q1 2027")
- `docs/market_analysis.md` lines 426, 455 and 741 ("Garmin Q1 2027")
- `docs/execution/backend/api/devices.md` §Garmin access caveat still says "Apply before scheduling
  the R2 engine work" — there is currently no form to apply on.

None of these is public, but the credits pitch is a document sent to Google, so it is the next
most outward-facing of the three.

## Sources

- https://carditrack.com/supported-devices (live page; source `supported-devices.html` in
  `Codesistance/branding-carditrack-website`, commit 8e25519, PR #80, 2026-09-22)
- `research/queue/2026-09-22-website-garmin-q1-2027-vs-closed-developer-program.md` (the item this
  closes)
- `docs/execution/backend/api/devices.md`, `docs/google_credits_pitch.md`, `docs/market_analysis.md`

## Why flagged

The routine is asked to flag public-page drift from `devices.md` and to publish an update on the
same URL when an item materially changes. The public commitment is now honest; this entry records
that so the 2026-09-22 thread can close, and points at the residue so the internal documents stop
quoting a quarter the public page has withdrawn.

## Question to answer next

Sweep the three internal documents to match the public wording: Garmin "planned, timing depends on
Garmin reopening its developer programme"; keep Withings Q3 2027. In `devices.md`, replace "Apply
before scheduling the R2 engine work" with the current state (application form withdrawn, no ETA;
Withings is the next engine). One small PR, no code.

claude "work through @research/queue/2026-09-23-website-garmin-copy-corrected-internal-docs-still-say-q1-2027.md"

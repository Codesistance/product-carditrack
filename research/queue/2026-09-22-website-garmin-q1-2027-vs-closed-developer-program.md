# Public supported-devices page still promises a dedicated Garmin connection "Q1 2027" — the internal record says Garmin's developer programme is closed with no date

**Severity:** FYI
**Category:** devices

## Summary

`supported-devices.html` on carditrack.com (last changed 2026-09-05, website PR #64) says, under
"Coming next": **Garmin — Coming Q1 2027**, and explains that "Garmin and Withings each offer a
direct connection … These are the two brands we are building dedicated connections for. Dates are
our current target and may shift."

The product repo's record has moved since that copy was written:

- `docs/execution/backend/api/devices.md` (Garmin access caveat, 2026-09-05) already made the
  R2 Garmin engine conditional: "Apply before scheduling the R2 engine work; if access does not
  come, Withings is the next engine."
- The 2026-09-19 digest (`2026-09-19-garmin-connect-developer-program-closed.md`, HIGH) recorded
  that Garmin removed the developer-programme application form entirely. Garmin's own FAQ, as
  indexed this week, reads: "The application form for new API access requests is currently
  unavailable while Garmin completes updates to the Garmin Connect Developer Program. During this
  transition, new access requests are temporarily paused" — no projected re-opening date.
- Today's re-check found no reopening, no date, and no new Garmin statement since 2026-09-19.

So the public page carries a dated commitment whose only delivery path is closed, while the
internal truth is "conditional on access we cannot currently apply for, Withings otherwise". The
two documents are not contradictory on what is *connectable today* (both say Fitbit and Pixel
Watch only), but they disagree on the Garmin roadmap, and the public one is the more confident.

Note also that `docs/google_credits_pitch.md` and `docs/market_analysis.md` repeat "Garmin
(Q1 2027)" — same drift, internal audience.

## Sources

- https://carditrack.com/supported-devices (public page; source in
  `Codesistance/branding-carditrack-website` `supported-devices.html`, commit f90cb0d)
- https://support.garmin.com/en-US/?faq=JToBEy0jfe6pIygark2Ui5 (Garmin FAQ — wording per search
  index this week; the domain is proxy-blocked from the digest sandbox, so re-read it directly)
- `docs/execution/backend/api/devices.md` §Garmin access caveat
- `research/queue/2026-09-19-garmin-connect-developer-program-closed.md`
- `research/queue/2026-09-20-garmin-health-connect-bridge-workaround.md`

## Why flagged

The supported-devices page is a public commitment, and the routine is asked to flag whenever it
drifts from what `devices.md` actually says. A family choosing a watch for a parent on the
strength of "Garmin Q1 2027" is being told a date the company has no current means to hit.

## Question to answer next

Decide the Garmin line, then make the page and the docs say the same thing. Options, in rough
order of honesty-per-effort:

1. **Soften the public copy now** — "Garmin: planned, timing depends on Garmin reopening its
   developer programme" — and drop the quarter. One-line change in the website repo.
2. **Re-point Garmin to the Google Health route** on the page (Garmin Connect can share into
   Health Connect, see the 2026-09-20 brief), moving it from "Coming next" to "Through Google
   Health" alongside Apple and Samsung, once someone has confirmed which readings actually come
   through that route.
3. Leave Q1 2027 and accept the risk. Not recommended.

Whichever is chosen, update `devices.md`'s Garmin caveat (still says "apply before scheduling
R2") and the two internal docs that repeat the quarter, in the same change.

claude "work through @research/queue/2026-09-22-website-garmin-q1-2027-vs-closed-developer-program.md"

# Google Play's "request more time" extension flow for target API 36 is now live in Play Console

**Severity:** FYI (update to a previously logged CRITICAL item)
**Category:** dependencies

## Summary

The 2026-09-04 log entry flagged that Google Play's target API level 36 deadline had
already passed (2026-08-31) with "a one-time extension to 2026-11-01 available," based
on guidance that described the extension mechanism only in general terms. That mechanism
is now confirmed **live**: a "Request more time" button now appears on the app's Issue
Details page / Policy status panel in Play Console, rather than the "available later
this year" placeholder language from earlier guidance.

## Sources

- https://support.google.com/googleplay/android-developer/answer/15644220 (Google Play Console Help — primary)
- https://developer.android.com/google/play/requirements/target-sdk (Android Developers — target-SDK requirements)

## Why flagged

This is the actionable half of an already-CRITICAL item: if CardiTrack's Android app
has not yet shipped targeting API level 36, the extension request can now actually be
filed in Play Console rather than waited for.

## Question to answer next

Has CardiTrack's Android app already shipped a release targeting API 36? If not, file
the "Request more time" extension in Play Console now to secure the 2026-11-01 deadline
rather than risk the app being removed from new-user discovery.

claude "work through @research/queue/2026-09-16-google-play-target-sdk36-extension-request-live.md"

# r0adkll/upload-google-play's Node 20→24 forced-runtime cutover (2026-06-02) was already covered by CardiTrack's floating @v1 pin — no CI outage occurred

**Date:** 2026-09-18
**Severity:** FYI
**Category:** dependencies
**Source:** https://github.com/r0adkll/upload-google-play/issues/256

## Summary

GitHub's Actions-forced-to-Node-24 cutover landed 2026-06-02. The action added Node 24 support in v1.1.4 (2026-04-20), ahead of that deadline, and CardiTrack's `@v1` tag pin floats to the current v1.1.x, so the Play upload step did not break.

## Why flagged

Closes out a real risk window that was never checked before.

## Next question / action

None.

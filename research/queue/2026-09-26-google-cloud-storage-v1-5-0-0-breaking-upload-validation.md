# Google.Cloud.Storage.V1 5.0.0 (2026-09-21) moves resumable-upload checksum validation server-side and replaces the UploadValidationMode API — CardiTrack pins 4.15.0

**Severity:** FYI
**Category:** dependencies
**Date recorded:** 2026-09-26

## Summary

Google.Cloud.Storage.V1 **5.0.0** was published to NuGet on 2026-09-21 (4.16.0 on 2026-09-17).
The history entry lists one feature, "Enable full object checksum validation for resumable uploads",
and the pull request (#15769, read directly) explains the major bump: CRC32C validation moves from
client-side-after-upload to server-side via an `x-goog-hash` header on the final chunk;
`UploadValidationMode.ThrowOnly` and `DeleteAndThrow` are replaced by `RejectAndThrow` (the old
members remain, obsoleted with migration guidance); and the public `UploadValidationException` is
removed — a rejected upload now surfaces as an ordinary HTTP error. 4.16.0 adds an idempotency header
only.

**Where CardiTrack stands.** The two production callers are
`src/Infrastructure/CardiTrack.Infrastructure/ExternalClients/Storage/GcsReportStorage.cs` and
`GcsProfilePhotoStorage.cs`, both calling `UploadObjectAsync(bucket, objectName, contentType, stream,
cancellationToken: ct)` with default options. `grep -rn 'UploadValidation\|UploadObjectOptions' src
tests` is empty. The compile-time break is therefore not on CardiTrack's path; the behavioural change
is an improvement (the server rejects a corrupted object instead of the client deleting it
afterwards). No CVE, no deprecation date; the 4.x line has no announced end.

## Sources

- https://github.com/googleapis/google-cloud-dotnet/blob/main/apis/Google.Cloud.Storage.V1/docs/history.md — release history (read directly via raw.githubusercontent.com)
- https://github.com/googleapis/google-cloud-dotnet/pull/15769 — the breaking-change PR (read directly via WebFetch)
- NuGet registration for Google.Cloud.Storage.V1 (5.0.0 published 2026-09-21; read directly)

## Why flagged

A breaking major on a pinned dependency. Recording it now, with the "not on our path" analysis done,
makes the next bump a planned one rather than a surprise obsolete-warning.

## Question to answer next

1. On the next dependency bump, move to 5.0.0 and run the report-storage and profile-photo tests
   (they exercise `UploadObjectAsync`); expect zero obsolete warnings from CardiTrack code.
2. Nothing else — there is no reason to stay on 4.x once the tests pass.

claude "work through @research/queue/2026-09-26-google-cloud-storage-v1-5-0-0-breaking-upload-validation.md"

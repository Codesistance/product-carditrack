# FirebaseAdmin 3.6.0 deprecates `Message.Token` in favour of `Fid` — the version CardiTrack pins; no decommission date yet

**Severity:** FYI
**Category:** dependencies

## Summary

The Firebase Admin .NET SDK release **v3.6.0** (GitHub release dated 2026-07-08; PR #525 merged
2026-07-07) carries one feature line: *"feat(fcm): Enable `fid` and deprecate `token` for Send
API"*. `Message.Token` and `MulticastMessage.Tokens` are now marked obsolete (CS0618) and two new
properties, `Message.Fid` and `MulticastMessage.Fids`, target a **Firebase Installation ID**
instead of a registration token. The same change landed in the Node.js (14.1.0, 2026-06-24), Java
(9.10.0) and Python (7.5.0) Admin SDKs within ten days of each other, so this is a platform-wide
retargeting of FCM's send API, not a .NET quirk.

Google's own wording, in the Java PR discussion: the deprecated token targets "will continue to be
fully supported until the decommission date", with "an adoption window of at least one year after
the announcement" — and **no decommission date is published anywhere yet**. During the migration
period the `token` field also accepts an FID.

**Where CardiTrack stands.** `CardiTrack.Infrastructure.csproj` pins `FirebaseAdmin 3.6.0` — the
very release that introduces the deprecation — so the warning is already in our build.
`FcmNotificationChannel.cs` (line ~196) sends on `Message.Token` under a deliberate
`#pragma warning disable CS0618`, and the comment there is correct: what `PushDeviceToken` stores
is a registration token from `Plugin.Firebase.CloudMessaging`'s `GetTokenAsync()`
(`PushRegistrationCoordinator.cs:107`), not an Installation ID, and putting that value in `Fid`
breaks delivery. Migrating is a two-ended change — the MAUI app must register an Installation ID
(Firebase Installations API; Plugin.Firebase 4.0.1 exposes no Installations module today, so this
may mean a platform binding), the API must store and send it, and the two must switch together.
Nothing is broken today and nothing needs to change before Google names a date.

## Sources

- https://github.com/firebase/firebase-admin-dotnet/releases/tag/v3.6.0 (release notes — read directly)
- https://github.com/firebase/firebase-admin-dotnet/pull/525 (the change: obsoletes Token/Tokens, adds Fid/Fids — read directly)
- https://github.com/firebase/firebase-admin-java/pull/1211 (Java twin, merged 2026-06-29; carries Google's "supported until the decommission date… at least one year" wording — read directly)

firebase.google.com is proxy-blocked from the digest sandbox, so the FCM docs' migration text
("recommended to use the `fid` field once clients are registered using Firebase Installation IDs")
is as indexed, not as read.

## Why flagged

An announced deprecation in a pinned dependency's API surface, on the push path both platforms
depend on (`FirebaseAdmin` server-side, `Plugin.Firebase.CloudMessaging` client-side). It has
never been in a digest, so there was no URL to hang the eventual decommission date on. The code
already accounts for it; this entry records the state so the date, when it comes, is an update
rather than a surprise.

## Question to answer next

Not "migrate now" — Google has not set the date. Two smaller things:

1. Confirm whether `Plugin.Firebase` (or its `Plugin.Firebase.Installations` sibling, if one
   exists at 4.x) surfaces `FirebaseInstallations.GetIdAsync()` on both platforms. If it does, the
   client half is a one-line addition alongside `GetTokenAsync()`, and `RegisterPushDeviceRequest`
   can start carrying the FID next to the token so the server has both before the switch.
2. Add the decommission announcement to what the digest watches (the Firebase Admin .NET release
   notes and `firebase.google.com/support/release-notes/admin/dotnet`), and record in
   `docs/execution/backend/api/notifications.md` (or wherever push registration is documented)
   that the token→FID migration is a paired mobile+API release.

claude "work through @research/queue/2026-09-24-firebaseadmin-3-6-0-deprecates-token-for-fid.md"

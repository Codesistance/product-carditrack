# Fitbit legacy Web API — final sunset, September 2026

**Severity:** CRITICAL
**Category:** devices

## Summary

The legacy Fitbit Web API (OAuth + REST, the pre-Google-Health-API integration path) has been
running in parallel with the Google Health API v4 since May 2026 and is being fully decommissioned
in September 2026. Google's own community thread describes the cutover as imminent but has not
nailed the exact day. Legacy OAuth tokens will not carry over — anything still pointed at the old
Fitbit endpoints stops syncing once it lands.

## Source links

- https://support.google.com/googlehealth/thread/439040688 (Google Health Community — primary,
  Google-run channel, but informal/thread format; no dated official changelog entry found)
- https://sahha.ai/blog/fitbit-api-sunset-migration/ (secondary, corroborating)
- https://www.fitabase.com/blog/post/google-health-api-announcement/ (secondary, corroborating)

## Why flagged

CardiTrack's device integration is already built exclusively on the Google Health API v4
(`GoogleHealth` is the only engine wired into DI; Fitbit and Pixel Watch both route through it —
see `docs/execution/backend/api/devices.md`). So the direct risk is narrow. But this is exactly the
kind of silent breakage the digest exists to catch: any leftover reference to the old Fitbit
OAuth app/endpoints (docs, onboarding copy, stored client credentials, support runbooks) would stop
working with no compile-time signal, in the same month this digest runs.

## Question to answer next

Grep the repo and Terraform/Secret Manager config for any Fitbit-specific OAuth client id, redirect
URI, or API host that predates the Google Health API v4 migration, and confirm none is still live.
Also check `infrastructure/deployments/secret_manager.tf` and any provider config in
`devices.md`/`DeviceProviders` for a legacy `api.fitbit.com` host rather than
`health.googleapis.com`. If everything already routes through Google Health API v4 (expected), this
closes with no code change — but it needs to be confirmed once, not assumed.

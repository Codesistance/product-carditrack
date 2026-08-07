# Health API Probe

One-shot diagnostic that answers a single question: **do the field names
`FitbitApiClient` looks for actually match what the Google Health API returns?**

Google's v4 reference documents the request shape and a few rollup value
schemas (`steps.count`, `heartRate.beatsPerMinute_min/max/avg`) but not the
rest, so several field names in `FitbitApiClient` were inferred from the
documented `{field}_{aggregation}` convention. A wrong guess **does not throw** —
the value simply comes back `0`, so a sync looks healthy while producing empty
data. This has to be checked once against a live account.

Not in `CardiTrack.sln` and not built by CI: it needs a real OAuth token and a
real wearer's data, so it can only be run by hand.

## Prerequisites

- A `CardiTrack Devices ({env})` OAuth client, with the operator's Google
  account added as a **test user** (see
  [oauth_clients.md](../../docs/technical/oauth_clients.md)).
- A Google account with a Fitbit or Pixel Watch that has **synced data for the
  day you probe**. Pick a day the wearer actually wore the device — an empty
  day produces empty rollups and proves nothing.
- An **access token** for that account carrying the three `googlehealth.*`
  read scopes. Easiest source is the
  [OAuth 2.0 Playground](https://developers.google.com/oauthplayground):
  gear icon → *Use your own OAuth credentials* → paste the Devices client ID
  and secret (add the Playground's redirect URI to the client first), select
  the `googlehealth.*` scopes, authorize, exchange for an access token.
  Tokens last ~1 hour.

## Running

```bash
# Preferred: token via environment variable, never as an argument
# (arguments leak into shell history and the process list).
export HEALTH_ACCESS_TOKEN='ya29....'
dotnet run --project tools/HealthApiProbe -- --date 2026-08-06
```

Omit `HEALTH_ACCESS_TOKEN` and the probe prompts for the token on stdin.
`--date` defaults to yesterday (UTC); today is usually still partial.

## Output

Two sections:

1. **Per data type** — the HTTP status and the JSON *shape* of the response:
   field names and value types, with values elided. Error bodies print verbatim
   (they carry no health data and are the useful part when a scope is missing).
2. **`FitbitApiClient.GetHealthSnapshotAsync`** — what the real client extracts
   from the same account, with zero/null values flagged.

The comparison is the point: a metric whose shape dump shows data but whose
parsed value is `0` means the field name in `FitbitApiClient` is wrong.

## Sharing the output

Default output is **shape only** — safe to paste into a PR or issue. Passing
`--raw` prints actual values, which are a real person's health data: keep that
local. The access token is never printed in either mode.

# Health API Probe

One-shot diagnostic that answers a single question: **do the field names
`FitbitApiClient` looks for actually match what the Google Health API returns?**

Every name `FitbitApiClient` reads is now taken from the v4 reference
(`steps.countSum`, `heartRate.beatsPerMinuteAvg`, …), but a name being right on
paper is not the same as it being populated for a given wearer, and the failure
mode is silent either way: a name that matches nothing **does not throw**, the
value simply comes back `0`, so a sync looks healthy while producing empty data.
The pairing still has to be seen once against a live account.

It also separates two things the reference cannot: a data type the wearer's
device never records looks exactly like a parsing bug on our side.

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

Omit `HEALTH_ACCESS_TOKEN` and the probe prompts for the token, masking it as
you type so it does not linger in terminal scrollback. Piped input
(`echo $TOKEN | dotnet run …`) cannot be masked — the prompt says so rather
than implying otherwise.

`--date` takes an ISO date (`yyyy-MM-dd`) and defaults to yesterday (UTC); today
is usually still partial. Anything else is rejected rather than silently falling
back to yesterday: probing the wrong day looks identical to a field-name bug, and
a locale-sensitive format like `08/06/2026` would mean different days on
different machines.

## Output

Two sections:

1. **Per data type** — the HTTP status and the JSON *shape* of the response:
   field names and value types, with values elided. Error bodies print verbatim
   (they carry no health data and are the useful part when a scope is missing).
2. **`FitbitApiClient.GetHealthSnapshotAsync`** — what the real client extracts
   from the same account, with zero/null values flagged.

The comparison is the point: a metric whose shape dump shows data but whose
parsed value is `0` means the field name in `FitbitApiClient` is wrong.

Three request shapes are probed, because the filter grammar differs per record
type and each spelling is rejected outright if used on the wrong one:

| Record type | Data types | Method |
|---|---|---|
| Interval / Sample | `steps`, `distance`, `active-minutes`, `total-calories`, `floors`, `heart-rate`, `sedentary-period` | `dataPoints:dailyRollUp` |
| Daily | `daily-resting-heart-rate`, `daily-oxygen-saturation`, `daily-vo2-max`, `daily-respiratory-rate`, `daily-sleep-temperature-derivations` | `list`, filtered on `{data_type}.date` |
| Sample series | `oxygen-saturation` | `list`, filtered on `{data_type}.sample_time.civil_time` |

`StressScore` never appears in a shape dump and is always null in the parsed
result: v4 exposes no stress or readiness data type, so no request can populate
it. That null is expected, not a field-name bug.

## Sharing the output

Default output is **shape only** — safe to paste into a PR or issue. Passing
`--raw` prints actual values, which are a real person's health data: keep that
local. The access token is never printed in either mode.

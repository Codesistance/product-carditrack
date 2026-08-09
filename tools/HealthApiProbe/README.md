# Health API Probe

One-shot diagnostic that answers a single question: **does this wearer's device
actually populate the data types `FitbitApiClient` reads?**

Not in `CardiTrack.sln` and not built by CI: it needs a real OAuth token and a
real wearer's data, so it can only be run by hand.

## Check the schema first — it is free, and it is stricter

The probe used to carry a second job, confirming that the *field names* were
right. That question is better answered without a token at all, because the API
publishes a machine-readable schema:

```bash
curl -s 'https://health.googleapis.com/$discovery/rest?version=v4' -o discovery.json
```

It needs no auth, no test user and no wearer. It is also **stronger evidence
than a live probe** for anything about spelling: it lists every field of every
data type, each field's wire `format`, and each enum's exact members — so it
proves a name wrong, where an empty response merely fails to prove it right.

Three things it settles that a payload sample cannot, each of which has already
cost this codebase a silent bug:

| Ask the schema | Because |
|---|---|
| the field's `format` | `int64` → JSON **string** (`"9423"`); `google-duration` → string with an **`s` suffix** (`"28800s"`); `double` → a bare number. All three look like "a number" in an example, and the wrong parse yields null, not an error |
| the enum's members | `ActiveMinutesRollupByActivityLevel.activityLevel` is `LIGHT`/`MODERATE`/`VIGOROUS`. The similarly named `ActivityLevelRollupByActivityLevelType.activityLevelType` is `SEDENTARY`/`LIGHTLY_ACTIVE`/`MODERATELY_ACTIVE`/`VERY_ACTIVE`. Using the second set on the first type matches nothing and sums to 0 |
| which union member carries the type | `DailyRollupDataPoint` for rollups, `DataPoint` for `list` — the member name is the camelCase data type (`dailyRestingHeartRate`), while the *filter* path is snake_case (`daily_resting_heart_rate.date`) |

Example — dump one type's real shape:

```bash
python -c "import json;d=json.load(open('discovery.json',encoding='utf-8'));print(json.dumps(d['schemas']['SedentaryPeriodRollupValue'],indent=2))"
```

Do this before running the probe. If a name is wrong, the probe will show a zero
and you will not know whether the field is misnamed or the wearer simply has no
data for it — the schema tells you which.

## What only a live run can tell you

Whether the wearer's device populates a type at all. A Fitbit that derives no
VO2 max and a misspelled `vo2Max` produce byte-identical output, and the failure
mode is silent in both directions: a name that matches nothing **does not
throw**, it comes back null, so a sync looks healthy while producing empty data.
That is the question this tool exists for.

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

The comparison is the point, and it reads in two directions:

- **Shape dump shows data, parsed value is null** — the field name, format or
  enum member in `FitbitApiClient` is wrong. Take it to the discovery document
  above; it will say which of the three.
- **Shape dump is empty, parsed value is null** — this wearer's device does not
  populate that type. Not a bug, but worth recording per device model.

A parsed `0` is neither: it means the API sent an explicit zero, which
`FitbitApiClient` deliberately preserves as a measurement. Absent data is null,
never 0 — see the "absent is not zero" note in `docs/llm_design.md`.

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

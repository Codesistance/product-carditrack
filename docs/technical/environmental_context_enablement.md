# Environmental context enablement — turning on exercise-GPS weather

**Status:** Proposed (2026-08-17)
**Scope:** What it takes to make `EnvironmentalEnrichmentService` actually run, so weather and air quality reach family summaries. Covers the OAuth scope, the consent surface, the job deployment, and the preconditions that block all three. Does **not** propose any new stored location field — the coordinate stays transient, exactly as designed.
**Relationship to other docs:** [llm_design.md](../llm_design.md) owns the prompt contract this data feeds. [data_protection_architecture.md](./data_protection_architecture.md) §9 owns the Google Maps Platform subprocessor row and §5.1 the retention entry. [dpia.md](../compliance/dpia.md) owns risks R-A15/R-A16 and mitigation M14, which gates everything here.

---

## 1. Context

The feature is built end to end and has never run. Every consumer already exists:

| Component | State |
|---|---|
| `GoogleHealthApiClient.GetExerciseSessionsAsync` / `GetExerciseGpsPointAsync` | Built, never executed against a live account |
| `GoogleEnvironmentalClient` (Weather + Air Quality) | Built, no API key provisioned |
| `EnvironmentalEnrichmentService` | Built, consent-gated, no members qualify |
| `EnvironmentalReading` entity + partitioned table + 90-day retention | Built and migrated |
| `EnvironmentalContextSource` | Built, feeds **every** prompt (`Purposes => PromptPurpose.All`) |
| Mobile weather surface | Shipped |

Nothing on the summary side needs writing. What is missing is a supply of coordinates, and three independent things stop them arriving:

1. **No scope.** `googlehealth.location.readonly` is absent from the Google Health `Scopes` list in `appsettings.json`, so no token can read exercise data.
2. **No consent.** Nothing in `src/` writes `CardiMember.EnvironmentalContextConsentGranted`; it is read in five places and set in none.
3. **No job.** `PipelineJobs` implements the `enrich` verb, but `infrastructure/` declares no Cloud Run job or scheduler for it.

Any one of the three is sufficient to keep the feature inert, which is why the DPIA rates R-A15 as contained.

### 1.1 Accepted limitation — coverage

Location arrives **only** from a GPS-tagged exercise session, and `EnvironmentalContextSource.MaxAgeFor` gives the digest a 24-hour window from `SessionEndUtc`. A member therefore gets weather in a daily summary only if they completed a GPS-tracked workout in the preceding day.

For a monitored population whose *inactivity* is the thing being watched, that will be a minority of member-days, and it will correlate inversely with concern: the quieter the member, the less likely the summary can say anything about the conditions. **This is understood and accepted** — the alternative (a stored coarse home location covering every member every day) was considered and declined for this increment because it introduces stored location PII and a DPIA amendment. Revisit if measured coverage after rollout is too low to be useful (see §6.3).

## 2. Blocking preconditions

None of the work in §3 may ship before these close.

### P1 — Google Maps Platform DPA (mitigation M14)

[dpia.md:327](../compliance/dpia.md) requires M14 to close **before either** of the two containments is removed — that is, before the scope is requested *or* before any path can set the consent flag. [data_protection_architecture.md](./data_protection_architecture.md) §9 records the same posture: the Maps row is *"⚠ Blocked as used until the DPA question is resolved"*, because Maps Platform is a distinct product that does **not** inherit the Google Cloud BAA.

This is a commercial/legal action, not an engineering one, and it is the long pole. Start it first.

### P2 — Fix the outbound-URL log leak

`GoogleEnvironmentalClient.TryGetWeatherAsync` builds the coordinate into the **query string**:

```
/v1/currentConditions:lookup?key=<API KEY>&location.latitude=51.5&location.longitude=-0.12
```

`Microsoft.Extensions.Http` logs `"Sending HTTP request {HttpMethod} {Uri}"` at **Information** under `System.Net.Http.HttpClient.*`. The two hosts differ:

| Host | `MinimumLevel.Default` | `System` override | Outcome |
|---|---|---|---|
| API | `Warning` | `Warning` | Suppressed |
| **PipelineJobs** | **`Information`** | **absent** | **URL logged, shipped to Datadog** |

PipelineJobs is the host that runs `enrich`. On first real execution it would log the coordinate **and the API key**, once per enriched session. This directly contradicts the subprocessor register's claim that the coordinate is *"never logged, never stored"*, and it would make that row inaccurate the day the feature goes live.

Fix — add the override PipelineJobs is missing:

```json
"Override": {
  "Microsoft": "Warning",
  "Microsoft.EntityFrameworkCore": "Warning",
  "System": "Warning"
}
```

Also correct `GoogleEnvironmentalClient`'s class comment, which claims the coordinate never reaches logs. That is true of its own `_logger` calls and false of the handler beneath it; the guarantee is a property of host log configuration, and the comment should say so rather than implying the class enforces it alone.

**APM is already safe** and needs no change: `OpenTelemetry.Instrumentation.Http` is `1.17.0`, which redacts query-string values in `url.full` by default, and `OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION` is set nowhere in the repo. Add a check to §6.1 confirming that holds after any OTel bump — a future upgrade that changed the default would silently reopen this.

### P3 — Verify the exercise field names against the live discovery document

`GoogleHealthApiClient.GetExerciseSessionsAsync`'s own remarks flag that `hasLocationData` and the `exercise` union member follow this client's naming convention but have **never been confirmed against a live v4 response**, because the scope was never granted. The established practice for this API is to read `https://health.googleapis.com/$discovery/rest?version=v4` rather than the prose docs.

This matters more than a normal field check: the client's failure mode for a wrong name is **silent zeros, not errors**. A mistyped `hasLocationData` yields `false` for every session, and the feature would look "enabled but nobody exercises" rather than broken. Verify before the first real run, not after.

## 3. Work plan

Ordered; each step assumes the ones above it.

| # | Step | Notes |
|---|---|---|
| 1 | P2 — log override + comment correction | Independent of everything else; ship now as its own PR |
| 2 | P1 — close M14 | Long pole; blocks 3 onward |
| 3 | P3 — verify field names against the discovery doc | Cheap; do while 2 is in flight |
| 4 | Provision the Maps Platform API key | Secret Manager → `EnvironmentalContextSettings.ApiKey`; restrict the key to the Weather + Air Quality APIs and, if supported, by caller |
| 5 | Add `googlehealth.location.readonly` to the Google Health scope list | See §4 — this is the re-consent and re-verification step |
| 6 | Build the consent surface | See §5 |
| 7 | Deploy the `enrich` Cloud Run job + scheduler | See §6 |
| 8 | Update DPIA, subprocessor register, privacy policy | See §7 |

## 4. The scope, re-consent, and verification

Adding the scope is not a config edit with local consequences.

- **Existing connections do not gain it.** `EnvironmentalEnrichmentService.HasLocationScope` reads the stored `DeviceConnection.Scopes`, so every already-connected wearer must re-authorise before they can ever be enriched. There is no silent upgrade path; plan a re-consent prompt, and expect partial uptake indefinitely.
- **Consent-screen verification is per-project.** The OAuth projects are split (`sign-in`, `devices-dev`, `devices-prod`) precisely because verification is scoped per project. Adding a location scope to `devices-*` is likely to require re-submission, and unverified apps are capped at 100 users — confirm the current cap and review status before committing to a launch date.
- **Two gates, one flag each.** Keep the scope check *and* the consent check. The scope alone is not consent, and the consent flag alone cannot read data. `EnvironmentalEnrichmentReachTests` exists to keep any new caller from bypassing either; if a new call site is genuinely needed, the test's allow-list is the place to declare it deliberately.

## 5. Consent surface

Nothing sets the flag today. What ships must be:

- **Explicit and specific.** Not folded into device connection or a general "improve my summaries" toggle. The thing being consented to is *reading where they exercised, and sending that point to Google Maps Platform* — the disclosure, not just the storage, is what needs consenting.
- **Default off, revocable.** The column already defaults `false`. Revocation must stop future enrichment; existing derived rows age out on the 90-day partition drop, and should also be deletable on request — note that `SubjectDataMap` already carries `EnvironmentalReading` keyed by `cardi_member_id`, so erasure has a path.
- **Set by the caregiver, about the wearer.** Wearers never log in, so the person granting is not the person whose location is read. That asymmetry is worth stating in the consent copy itself; it is not a normal "allow location" prompt and should not look like one.
- **On both platforms at once.** Android may never carry a feature iOS lacks — ship the toggle on both or neither.

API shape: a small endpoint on the CardiMember, mirroring the existing preference-update patterns rather than inventing a consent framework. This flag deliberately predates `ConsentRecords` and does **not** migrate into it automatically — that migration stays future work, per the note in data_protection_architecture §8.

## 6. Deployment and verification

### 6.1 The job

Add a Cloud Run job for `enrich` plus a scheduler entry, alongside the existing `digest`/`aggregate`/`assess` jobs. Cadence should be **infrequent** — the natural key makes re-checking an enriched session a no-op, and `LookbackDays = 2` means a daily or twice-daily run loses nothing. There is no latency argument for anything tighter: the reading feeds a digest with a 24-hour window.

Register `EnvironmentalServiceExtensions` only for this job, as `Program.cs` already does — the Maps key and the exercise/GPS client methods must stay unregistered in every other process.

Checks to add to the runbook:
- OTel query-redaction default still holds after any `OpenTelemetry.Instrumentation.Http` bump (P2).
- `System` log override still present in PipelineJobs.

### 6.2 First-run validation

Against one consented test member with a real GPS workout:
1. `GetExerciseSessionsAsync` returns sessions with `HasGpsTrack = true` — if every session reads false, suspect P3, not the member.
2. An `EnvironmentalReading` row is written with at least one non-null derived value.
3. Datadog carries **no** log line containing `location.latitude` or the API key.
4. The reading appears in a digest prompt within the 24-hour window, under "Conditions they have recently been out in".

### 6.3 Measure coverage

Instrument what §1.1 accepts as unknown: the share of member-days where a digest had a usable reading. If that number is low enough that the feature rarely speaks, the coarse-home-location option is the follow-up, and this measurement is what would justify opening that DPIA conversation.

## 7. Documentation that must change with the code

| Doc | Change |
|---|---|
| [dpia.md](../compliance/dpia.md) | R-A15 is no longer inert — rewrite the containment note; record M14 as closed with date and terms |
| [data_protection_architecture.md](./data_protection_architecture.md) §9 | Flip the Maps row from "⚠ Blocked as used"; keep "never logged" **only if** P2 shipped |
| `Privacy.razor` | Currently silent on location entirely. Must state that a consented member's exercise location is read and sent to a weather provider, that it is never stored, and how to withdraw |
| [llm_design.md](../llm_design.md) | Drop the "not yet provisioned" qualifier on the environmental section |

## 8. Open questions

1. **M14 terms** — does the Maps Platform DPA cover the Weather and Air Quality APIs specifically, or only core Maps? The register already notes Maps does not inherit the Cloud BAA; confirm the same for these two products individually.
2. **Verification impact** — does adding `googlehealth.location.readonly` re-open consent-screen review on `devices-prod`, and does that reset the 100-user cap?
3. **Re-consent UX** — is a location re-authorisation prompt acceptable to push at already-connected wearers' caregivers, or does it wait for a natural reconnection?
4. **Historical sessions** — `LookbackDays = 2` means enabling the feature enriches nothing older than two days. Is a one-off backfill wanted, and if so, is enriching months-old sessions with *current* conditions honest enough to be worth it? (Probably not — the client's approximation argument only holds for sessions that just ended.)

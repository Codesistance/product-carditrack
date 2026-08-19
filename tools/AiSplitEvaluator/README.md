# AI Split Evaluator

Answers one question with real data: **does splitting the real-time assessor into a
clinical-only MedGemma pass plus a separate rewrite pass actually recover anything**,
or does today's single pass (clinical judgment and caregiver prose in one decode) do
just as well?

Not in `CardiTrack.sln` and not built by CI: it needs a real dev database and a real
MedGemma/rewrite-model endpoint, so it can only be run by hand.

## Why this exists

MedGemma is a 4B, Q4-quantized model asked, in one generation, to interpret an hour of
SSA-denoised heart-rate data *and* write the result in a specific caregiver register
(plain language, pronoun rules, no jargon). The concern: satisfying the register
constraint might cost the model something on the clinical judgment itself.

That's a real, testable claim — this tool tests it, on the one path where genuine
clinical judgment (the SSA-deviation severity call) currently shares a decode with
caregiver prose: the real-time assessor. It is **not** wired into any live path. It
never writes to the database, never raises or resolves an alert, and never touches a
caregiver's dashboard, digest, or notification — see "What this does and does not do"
below.

## What it does

For up to `--sample-size` real members whose latest hour of heart-rate data is an SSA
"jump" (deviation score ≥ 3 — the same threshold that would make the real assessor call
MedGemma in production), it:

1. Rebuilds the exact same window, SSA decomposition, and member-context block the real
   assessor would build (`RealtimeAssessmentService`'s own data-gathering code, called
   directly — not re-implemented).
2. Sends the **baseline** prompt (`RealtimeAssessmentService.AssessmentInstructions`,
   today's production prompt, unmodified) to MedGemma.
3. Sends a **clinical-only** variant (same data, same severity-token instructions,
   `Tone`/`Pronouns`/`CaregiverRegister` stripped — defined in this tool only, never in
   production code) to MedGemma, then rewrites its `Message` field into caregiver
   language via the new rewrite-model slot (`AI:Rewrite`).
4. Reports, per sample: whether the two passes agree on severity, both message texts
   side by side (only with `--raw`), and the latency cost of one call versus two.

## What this does and does not do

- **Read-only, end to end.** It calls the same repositories the real assessor calls,
  but never `UpsertAsync`, `RaiseAlertAsync`, or `SaveChangesAsync` — nothing it finds
  is written anywhere, and no push notification, alert, or digest is ever touched by
  running it.
- **In-estate only.** Both the clinical pass and the rewrite pass call self-hosted
  models on the `AI:Private`/`AI:Rewrite` slots. No `AI:Public` provider (Gemini,
  Anthropic) is registered anywhere in this tool — health data never has a path off
  the project when this runs.
- **Not a verdict by itself.** It measures severity agreement and shows the prose
  side by side; whether the split is worth shipping anywhere is a judgement call for
  whoever reads the output, not something this tool decides.
- **Samples independently of production dedup.** The real assessor skips a window it
  has already stored an assessment for (`RealtimeAssessments.ExistsAsync`) — that rule
  exists purely to avoid a duplicate inference, and does not apply here, since this
  tool asks "what would the model(s) say", not "what is due".

## Running

Needs three things from the environment: a Postgres connection string for a dev (or
local docker-compose) database with real granular heart-rate data, the matching
encryption key (so `MedicalNotes` can be decrypted the same way production does), and
a reachable MedGemma + rewrite-model endpoint (local Ollama via docker-compose, or a
dev Cloud Run MedGemma URL with `AI__Private__UseIdentityToken=true` /
`AI__Rewrite__UseIdentityToken=true`).

```bash
export ConnectionStrings__DefaultConnection='Host=localhost;Port=5432;Database=carditrack;Username=postgres;Password=postgres'
export Encryption__Key="<the same key the target environment's API/PipelineJobs uses>"

dotnet run --project tools/AiSplitEvaluator -- --sample-size 5 --raw
```

- `--sample-size N` — how many qualifying jump windows to evaluate (default `3`).
- `--scan-limit N` — how many candidate members to scan looking for them before giving
  up (default `50`); raise this if dev traffic is quiet and no samples are found.
- `--raw` — print the actual prompt output (real member health text). Omit it and the
  tool prints only severities, agreement, lengths and latency — safe to paste into a
  PR or issue. With real member text, keep it local, the same convention
  `tools/HealthApiProbe` uses.

To point it at local Ollama instead of a deployed environment, the checked-in
`appsettings.json` already defaults `AI:Private`/`AI:Rewrite` to
`http://localhost:11434` with the same model tags `docker-compose.yml` pulls — no
extra config needed if you're running `docker compose --profile full up ollama
medgemma-init rewrite-init`.

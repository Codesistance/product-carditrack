# CardiTrack — Claude Instructions

## Code Quality
- All changes must be verified best practices before being applied.

## Architecture
- **Non-AI** background jobs (OAuth token refresh, baseline recalculation, trial reminders, retention/cleanup) and any DB polling belong exclusively in `CardiTrack.Worker`. No other project may host these.
- The **AI ingestion/inference pipeline** (webhook aggregation, SSA-LSTM pre-processing, MedGemma calls, severity routing, digests) runs in Azure Functions per `docs/llm_design.md` — it is the only sanctioned exception to the Worker rule, and it must not host non-AI jobs.
- `CronBackgroundService`, `WorkerOptions`, and `WorkerServiceExtensions` live in `CardiTrack.Worker` — they are not shared infrastructure.

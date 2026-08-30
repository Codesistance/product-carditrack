# MedGemma / Medical-AI Daily Digest — Tracking Log

Append-only record of what has already been reported to the Slack digest, so future runs
skip repetition and only surface genuinely new information. Each entry: date, item, status.
Before compiling a new digest, scan this file for items already covered and only re-surface
one if its status materially changed (e.g. "flagged" → "resolved", or a new deadline).

## 2026-08-30 (first run — no prior log existed)

### Reported as CRITICAL
- Google Health API: legacy Fitbit Web API sunset, ~2026-09-30. Old Fitbit-issued OAuth
  tokens do not carry over to the Google Health API; CardiTrack's only two live device
  integrations (Fitbit, Google Pixel Watch) both ride this API. No Fitbit-migration-specific
  code found in the repo as of this run. Status: **flagged, unresolved** — needs a migration
  sprint before 2026-09-30. Sources: https://sahha.ai/blog/fitbit-api-sunset-migration/ ,
  https://support.google.com/googlehealth/thread/439040688/
- Ollama CVE-2026-7482 ("Bleeding Llama", CVSS 9.1, fixed in Ollama 0.17.1, 2026-02-25):
  heap OOB read via `/api/create` + `/api/push`, can leak env vars/keys/other users' in-flight
  data. `src/Infrastructure/MedGemma/Dockerfile` and `docker-compose.yml` pin
  `ollama/ollama:latest` (unpinned) — patch status of the currently deployed
  `carditrack-common-medgemma` revision could not be confirmed from code alone. Status:
  **flagged, needs verification** (confirm deployed image build date ≥ 2026-02-25, and that
  no invoker identity beyond the model-bake step can reach `/api/create`/`/api/push` on the
  running service). Source: https://www.securityweek.com/critical-bug-could-expose-300000-ollama-deployments-to-information-theft/

### Reported as informational / lower priority
- Gemini 2.0 Flash retired 2026-06-01; Gemini 2.5 Flash retires 2026-10-16. **Already handled**
  by the team — `dev.tfvars` shows the model was bumped to `gemini-3.5-flash` on 2026-08-25.
  No action needed; noted here only so a future run doesn't re-flag it as new.
- MedGemma 1.5 (4B/27B) is Google's current release (shipped 2026-01-13); no MedGemma 2.x
  exists yet. CardiTrack is already on current-gen weights.
- No open <10B medical-tuned model beats MedGemma 4B for CardiTrack's Private slot as of this
  run (Meditron/OpenBioLLM/BioMistral now trail general-purpose Qwen2.5-32B on aggregate
  benchmarks). Re-check periodically, not urgent.
- Anthropic ("Claude for Healthcare") and OpenAI both launched HIPAA-ready healthcare AI
  suites in January 2026 — relevant background for CardiTrack's pluggable Public-provider
  slot, no action forced.
- MedGemma 4B vs 27B: 64.4% vs 87.7% on MedQA — a real accuracy gap worth periodically
  sanity-checking against real severity-routing transcripts, not a new finding requiring
  immediate action.
- Garmin Connect Developer Program: reportedly not accepting new partner applications as of
  this run. Affects CardiTrack's *scaffolded-but-unbuilt* Garmin integration only — no live
  break. Re-check before starting that build.
- Samsung Health: migrating from legacy Android SDK to Samsung Health Data SDK, requiring
  re-approval even for previously-approved apps. Same status as Garmin — scaffolded, not
  built, no live break.
- Whoop API exposes only daily-aggregate HR/HRV/SpO2, not continuous series — CardiTrack's
  SSA decomposition would not work on Whoop data as currently exposed. Worth deprioritizing
  or scoping as summary-only if/when built.
- Oura: Personal Access Tokens deprecated Dec 2025; CardiTrack's scaffold already uses OAuth2
  so no change needed.
- New device candidates surfaced: AliveCor Kardia 12L/6L (FDA-cleared, has KardiaPro cloud
  API — good complement for episodic ECG confirmation), Circular Ring 2 (FDA-cleared
  hardware-ECG AFib, no subscription, $380 — elder-accessible price point; API access
  unconfirmed).
- Aggregator platforms: Terra API (500+ providers) is the strongest candidate to replace
  one-client-per-provider development; Spike API acquired by Raintree (deprioritize); Human
  API exited the market (not viable).
- FDA (2026-01-06) relaxed CDS software oversight but explicitly left open how it applies to
  generative/LLM-based CDS — CardiTrack's MedGemma severity verdicts sit in this gray zone.
  No deadline; monitor.
- EU AI Act: high-risk obligations in force from Aug 2026, but AI Act/MDR-overlap systems
  (patient monitoring, CDS) get an extended transition to Aug 2027 (possibly further to
  2027/2028). No immediate deadline for CardiTrack; track before any EU launch of AI-generated
  severity verdicts.
- Google Cloud for Startups AI Program (up to $350K credits) — worth checking renewal/
  eligibility status. Google.org Impact Challenge: AI for Science closed 2026-04-17 (missed
  this cycle, watch for the next one).

# AI & third-party API digest — run log

Tracks what has already been reported to the `#carditrack` Slack digest (scheduled routine,
"MedGemma / Medical Grade AI" + "3rd-party API & device schema" watch), so each new run can
report deltas instead of repeating itself. Not a design doc — a dedupe ledger for the routine.

## 2026-08-24 — Digest #1 (baseline)

First run; nothing prior on file, so this run reported broadly rather than as a delta. Topics
covered, for future runs to check before re-reporting as "new":

- **MedGemma 1.5** (2026-01-13 release: 3D CT/MRI, histopathology, better EHR/lab-report
  text understanding) — noted as already the tag CardiTrack runs
  (`hf.co/unsloth/medgemma-1.5-4b-it-GGUF:Q4_K_M`). MedASR (medical speech-to-text) noted as
  unused/not integrated.
- **MedGemma Impact Challenge** ($100K, HAI-DEF apps) — noted as a program to watch next cycle.
- **Alternatives**: Meditron-3 (EPFL/Yale, Llama-3.1-70B) noted as open-source accuracy ceiling,
  not a swap candidate. GPT-5.6 (OpenAI, Jul 2026) and Claude Opus 4.5 healthcare connectors
  noted as relevant only to non-medical provider slots.
- **Performance**: MedGemma 4B 64.4% MedQA, MedGemma 27B 87.7% MedQA (near DeepSeek R1, beats
  average physician on AgentClinic-MedQA) — recorded as the benchmark ceiling for the 4B we run.
- **Grants**: Google for Startups Cloud Program (up to $350K AI-tier) — cross-referenced against
  existing `docs/google_credits_pitch.md`. Google.org Impact Challenge: AI for Science ($30M
  pool, $500K–$3M grants, health/life-sciences eligible) — flagged as unexplored.
- **Regulation**: FDA CDS guidance finalized Jan 2026; PCCP now routine in submissions; 1,350+
  FDA-authorized AI devices. EU AI Act high-risk deadline for Annex I embedded medical-device AI
  pushed Aug 2027 → **Aug 2, 2028**; MDR/IVDR alone governs until then.
- **Stickiness**: Oura Health Radar + Counsel Health clinician-in-the-loop partnership flagged as
  the one competitive gap CardiTrack's caregiver loop doesn't yet cover. Whoop's proactive daily
  guidance flagged as directionally similar to our Daybook work. Apple deliberately not yet
  shipping an AI health coach — noted as an open competitive window for the family/caregiver
  angle specifically (still uncontested by the big three, who are all wearer-facing).
- **Security**: Prompt injection (OWASP LLM01) and the newer Chain-of-Thought-forgery pattern
  flagged against our existing untrusted-caregiver-note guarding and `MemberContextComposer`
  injection defusing — recommended a specific look at CoT-forgery resistance, not yet done.
- **Third-party APIs**:
  - Fitbit Web API September 2026 decommission (exact date still TBD by Google) — confirmed as
    already migrated away from (Google Health API), flagged only for a final dead-code sweep.
  - Google Health API — active doc churn (last updated 2026-08-18), no breaking change found
    against our `list`/`dailyRollUp` usage as of this run.
  - Vertex AI rebrand to "Gemini Enterprise Agent Platform" (2026-05-21, console/name only) — no
    action, REST endpoint unchanged.
  - Vertex AI **SDK** generative-module removal (2026-06-24) — confirmed not applicable; we call
    REST directly via `HttpClient`, not the SDK.
  - Anthropic Claude Sonnet 4 / Opus 4 retired 2026-06-15; Opus 4.1 retires 2026-08-05 — no model
    string pinned in this repo to either (Anthropic isn't the active `Kind` anywhere today), so no
    live exposure; flagged only for whoever activates that provider kind in future.
- **Flagged as time-sensitive / actionable** (raised at CRITICAL prominence in the Slack post):
  - `infrastructure/variables.tf` `public_ai_model` **default** is `gemini-2.0-flash`, shut down
    by Google 2026-06-01. `dev.tfvars` overrides correctly to `VertexGemini` / `gemini-2.5-flash`;
    `prod.tfvars` sets **no** AI overrides, so prod would inherit the dead default if/when its API
    is deployed on current Terraform. **Not yet fixed as of this run.**
  - `gemini-2.5-flash` itself (what dev actually runs) has a **2026-10-16** shutdown date —
    re-check this item until the migration to its replacement is done or the date passes.

### Still open / carry forward to next run

- Confirm whether prod's `public_ai_model`/`public_ai_kind` defaults have been fixed.
- Confirm gemini-2.5-flash → replacement migration status ahead of 2026-10-16.
- Re-check Fitbit Web API's actual decommission date once Google publishes it (was "TBD" as of
  this run).
- No need to re-report MedGemma 1.5's base release, the benchmark numbers above, the EU AI Act
  2028 date, or the Vertex AI rebrand unless something about them changes — only report deltas.

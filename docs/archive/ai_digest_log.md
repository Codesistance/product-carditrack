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

## 2026-08-25 — Digest #2

- **`public_ai_model` default still `gemini-2.0-flash`** (dead since 2026-06-01) — checked
  `infrastructure/variables.tf:603`, `prod.tfvars` again: still no override in prod, `main.tf`'s
  "transitional" block (line ~156-165) still hardcodes `gemini-2.0-flash` for
  `AI__Providers__1__Model` too. **Unresolved since Digest #1 — still live risk if prod's AI
  service deploys off current Terraform.** Not re-flagged as CRITICAL (no change), but not
  dropped either — carry forward until fixed.
- **New: `docs/google_credits_pitch.md` cites "Gemini 2.0 Flash"** as the model powering
  conversational insights/report generation (two places) — that model has been dead for the same
  reason as above since 2026-06-01, and the repo's own `dev.tfvars` actually runs
  `gemini-2.5-flash`. This is a live Google-for-Startups credits pitch document with a factually
  wrong, already-shut-down model name in it. **Flagged CRITICAL this run** — worth a quick fix
  before/if this pitch is (re)submitted.
- Fitbit Web API decommission date, previously "TBD": multiple sources (Sahha, Thryve, Motion)
  now converge on **September 2026** as the sunset month (still no exact day). Matches what
  `docs/execution/backend/api/devices.md` already says. No CardiTrack action needed — migration
  to Google Health API already shipped — but the final dead-code sweep for legacy Fitbit Web API
  paths should be scheduled now that "next month" is concrete. [Sahha](https://sahha.ai/blog/fitbit-api-sunset-migration/) [Thryve](https://www.thryve.health/blog/fitbit-api-deprecation)
- **New: FDA discussion paper on generative-AI-enabled medical devices**, published 2026-08-18,
  public comment window open through **2026-10-19**. Not a rule yet; relevant to MedGemma-based
  severity routing/alerts if that pipeline is ever framed as SaMD. Watch, no action yet.
  [TechJack summary](https://techjacksolutions.com/ai-brief/fda-genai-medical-device-discussion-paper-comment-2026/)
- EU AI Act: confirmed the Digital Omnibus is now in force as **Regulation (EU) 2026/1744**
  (2026-07-27), pushing the high-risk embedded-medical-device deadline to 2028-08-02 (same date
  already on file from Digest #1 — not new). New detail: **Article 50 AI-transparency duties are
  now active as of 2026-08-02** (disclosing AI-generated content/AI interaction to users) — worth
  a quick check that CardiTrack's AI-generated digests/Advise suggestions/chat replies carry
  adequate "this is AI-generated" disclosure. [Gardner Law](https://gardner.law/news/eu-ai-act-compliance-timeline) [HealthSeed VC](https://www.healthseed.vc/insights/vital-signs-ai-healthcare-august-2026)
- MedGemma Impact Challenge ($100K, flagged in Digest #1 as "watch next cycle"): **resolved/closed**
  — final submission was 2026-02-24, winners announced late March 2026. No new cycle open. Drop
  from active tracking unless Google announces a new round.
- Google.org Impact Challenge: AI for Science ($30M pool, flagged in Digest #1 as "unexplored"):
  **deadline already passed** (2026-04-17) before we ever flagged it as actionable — closing as
  moot. Google.org also launched a separate $30M "AI Breakthrough Fund" for crisis
  resilience/environmental science this cycle, but it is not health-focused — not tracked.
- No material change on: MedGemma 1.5 capabilities/benchmarks, Meditron-3/GPT-5.6/Claude Opus 4.5
  as alternatives, Vertex AI SDK/rebrand items, Anthropic Sonnet 4/Opus 4 retirements (still no
  live exposure). Apple Watch Series 12 (expected Sept 2026 announcement) rumored to add blood
  pressure monitoring — noted as a competitive watch item, not urgent.
- OWASP's 2026 LLM security report puts prompt injection up 340% YoY as the top LLM attack
  category — general trend confirmation, not new to CardiTrack specifically. The CoT-forgery
  resistance review recommended in Digest #1 is still not done — carry forward.

### Still open / carry forward to next run

- Fix (or explicitly accept and document) `public_ai_model`'s dead default in
  `infrastructure/variables.tf` / `prod.tfvars` / `main.tf`'s transitional block.
- Fix `docs/google_credits_pitch.md`'s stale "Gemini 2.0 Flash" model references before it's next
  submitted or shared.
- Confirm gemini-2.5-flash → replacement migration status ahead of 2026-10-16 (unchanged from
  Digest #1).
- Schedule the legacy-Fitbit-Web-API dead-code sweep given the September 2026 sunset is now one
  month out.
- Verify AI-generated surfaces (digests, Advise, chat) carry EU AI Act Art. 50-adequate
  AI-disclosure now that duty is active.
- Watch the FDA GenAI-medical-device discussion paper's comment period (closes 2026-10-19) for
  anything that would reclassify MedGemma-based alerting.

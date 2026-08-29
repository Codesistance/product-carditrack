# AI / MedGemma Daily Digest — Run Log

Tracks what has already been reported by the recurring "MedGemma & Medical AI" Slack digest
(updates, alternatives, performance, grants, regulation, sticky features, security; 3rd-party
device API monitoring; device suitability) so future runs report deltas, not repeats.

Each entry is what was **new or still-open** as of that run. An item not repeated in a later
entry is assumed unchanged/still valid unless a later entry says otherwise.

---

## 2026-08-29 — Baseline run (first digest; no prior log existed)

**Current architecture pinned facts** (for future diffing):
- Model: `hf.co/unsloth/medgemma-1.5-4b-it-GGUF:Q4_K_M` (MedGemma 1.5 4B), pinned in
  `src/Infrastructure/MedGemma/.model-version`. Served via Ollama on Cloud Run
  (`carditrack-common-medgemma`, 1x NVIDIA L4, europe-west1).
- Device integrations actually built: **Google Health API only** (covers Fitbit + Pixel Watch).
  Apple HealthKit is "planned" (no code). Garmin/Samsung/Withings/Oura/Whoop are enum
  placeholders (`DeviceType`, `HealthApi`) with no client implementation.
- Public/general AI provider: Gemini 2.0 Flash (swappable to Anthropic or VertexGemini).
- LSTM dropped 2026-08-10; `TrendInterpreter` (deterministic trend + MedGemma narrative) is
  design-only, not yet built.

**Reported items (CRITICAL flagged):**
1. MedGemma 1.5 released 2026-01-13 (MedQA 64%→69%); MedASR released alongside it.
2. **CRITICAL** — Pre-mid-2025 MedGemma multimodal checkpoints had an "end-of-image-token" bug
   (fixed ~Jul 2025). Not urgent for CardiTrack today (text-only prompts, no image inputs yet),
   but re-check if/when image-based prompts are added.
3. Anthropic Claude for Healthcare and OpenAI ChatGPT Health/Clinicians launched Jan–Jul 2026 —
   API-only, not self-hostable; noted as context, not an action item.
4. MedGemma is not top-of-leaderboard on raw MedQA accuracy (frontier general models score
   higher) — value is open weights/self-hosting/multimodal grounding, not benchmark supremacy.
   No cardiology/ECG-specific benchmark exists yet for MedGemma — flagged as an unvalidated gap.
5. **CRITICAL (ongoing, not one-time)** — Open-weight Gemma-family models (MedGemma's base)
   show high jailbreak-attack success rates in 2026 research, and prompt-injection attacks are
   up sharply industry-wide. CardiTrack's `AI:Private` prompts include free-text `MedicalNotes`
   — recommend an input-sanitization/guardrail review of the assessor/digest prompt paths.
   **Action owner: engineering. Re-check status in next run.**
6. **CRITICAL, deadline** — NIH SBIR/STTR standard due date **2026-09-08** (Phase I ~$314K,
   cardiac/HRV AI eligible under NHLBI parent NOFO). Verify application status in next run.
7. **CRITICAL, deadline** — British Heart Foundation Cardiovascular Grand Challenge (up to
   £10M/5yr, AI-powered cardiovascular theme) outline applications reported closing "August
   2026" — exact date unverified as of this run; may already have closed. Verify next run.
8. **CRITICAL, near-term** — FDA discussion paper on generative-AI medical devices (published
   2026-08-18) open for public comment via docket FDA-2026-N-7874 through **2026-10-19**.
   Directly relevant to MedGemma-driven severity routing/digests. Track for final framing.
9. EU AI Act high-risk deadlines pushed to 2027-12-02 (stand-alone) / 2028-08-02 (embedded in
   regulated devices) — more runway than previously assumed; not urgent.
10. HIPAA: no blanket "AI HIPAA certification" exists — confirm the existing Google Cloud BAA
    explicitly enumerates Cloud Run, Pub/Sub, and Cloud SQL (self-hosted MedGemma path) and
    Vertex AI (VertexGemini public-provider path). Verify next run whether this has been
    confirmed with Google/legal.
11. CA/TX now require meaningful human oversight of AI-driven clinical decisions; FTC signaling
    tension with state AI-discrimination rules — relevant to severity-routing design; suggested
    as a sticky/compliance-by-design feature (clinician attestation UI) rather than pure risk.
12. Device/API monitoring: legacy Fitbit Web API fully decommissioned 2026-09-30 and legacy
    Google Fit REST retiring end of 2026 — **not a risk for CardiTrack**, already on the current
    Google Health API. Oura PAT deprecation (Dec 2025), Whoop v1→v2 webhook breaking change,
    Garmin auth change (Mar 2026) — **not applicable today** since those vendors are unbuilt
    placeholders; relevant only if/when CardiTrack builds those integrations.
13. HL7 Caliper FHIR Accelerator (launched 2026-03-05) and Personal Health Device IG v2.0 draft
    — worth tracking given CardiTrack's SSA/FHIR-adjacent pipeline design; no action needed yet.
14. Device suitability candidates surfaced: AliveCor KardiaMobile 6L, Withings BPM Core/Connect,
    Vivalink wearable ECG patch, WHOOP 5.0/MG (supplementary only, not FDA-cleared for
    arrhythmia), Oura Gen4 (adjunct only), iRhythm Zio (clinically strong, poor real-time API
    fit). No integration decision made — for product/roadmap discussion.

**Not repeated going forward unless status changes:** items 2, 9, 12, 13 above (stable/settled).
**Re-verify explicitly in the next run:** items 6, 7, 8, 10 (all have live deadlines or open
verification questions).

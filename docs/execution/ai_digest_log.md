# AI & Device Intelligence Digest — run log

Tracking log for the "Carditrack Scout" daily routine (MedGemma / medical-grade AI landscape +
3rd-party API & device schema monitoring, posted to Slack). Each run reads the most recent
entries here before researching, so it can skip items already reported and only surface what's
new or has materially changed. Newest entry first. Keep entries short — a link and a one-line
"what was said," not the full digest text (that lives in Slack history).

---

## 2026-08-28

First run with a persisted log (no prior tracking file existed in the repo, so this run could
not de-duplicate against earlier Slack posts — everything below was reported as current state,
not necessarily "new since yesterday").

- **CRITICAL — Ollama CVE-2026-7482 ("Bleeding Llama"), CVSS 9.1.** Heap OOB read in the GGUF
  loader, patched in Ollama 0.17.1 (fix shipped 2026-02-25). `src/Infrastructure/MedGemma/Dockerfile`
  and `docker-compose.yml` pin `ollama/ollama:latest`, not an explicit version — can't confirm the
  deployed `carditrack-common-medgemma` revision is patched from the pin alone, and its deploy
  workflow is dispatch-only (no auto-rebuild). Flagged for action; not yet verified against the
  live revision's actual Ollama version.
- MedGemma 1.5 (already the pinned model, `hf.co/unsloth/medgemma-1.5-4b-it-GGUF:Q4_K_M`) —
  no newer MedGemma release since. MedASR (medical dictation ASR) shipped alongside it, unused by
  CardiTrack today.
- EU AI Act: Digital Omnibus (Reg 2026/1744, in force 2026-07-27) pushed the high-risk deadline for
  AI embedded in regulated products (e.g. an AI-enabled medical device) to 2028-08-02.
- FDA: QMSR (ISO 13485 alignment) effective 2026-02-02; Jan-2025 draft AI/ML SaMD lifecycle
  guidance still not finalized but treated as the direction of travel (SBOM, PCCPs, real-world
  performance monitoring).
- Google for Startups Cloud: up to $350K credits for AI-first startups (vs $200K non-AI tier) —
  worth checking `docs/google_credits_pitch.md` targets the right tier.
- Fitbit Web API: full shutdown September 2026 (CardiTrack already migrated to Google Health API
  v4 — on track, not urgent). Google Health API's post-launch breaking-change window closed end of
  May 2026.
- Garmin Connect Developer Program: still closed to new applications (no ETA).
- Apple HealthKit: spring 2026 added regulatory-status declarations for Health/Medical App Store
  categories — not applicable today (CardiTrack ingests via Google Health API, not HealthKit
  directly).
- Competitive: WHOOP shipped GPT-powered "WHOOP Coach," EHR sync via HealthEx, and on-demand
  clinician video calls (summer 2026). Oura Ring 5's "Health Radar" does continuous proactive
  cardiovascular/respiratory screening framed like CardiTrack's real-time assessor. Neither targets
  the family-caregiver-alert model CardiTrack is built around.

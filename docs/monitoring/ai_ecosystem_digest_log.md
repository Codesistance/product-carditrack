# AI & Third-Party Ecosystem Digest Log

Tracking log for the daily automated digest (MedGemma/medical-AI landscape + third-party
device API/schema monitoring) sent to Slack. Each entry lists what was already reported so
future runs can avoid repeating the same finding and instead report deltas/new developments.

Do not delete prior entries — the whole point is a standing record of "already told them this."

---

## 2026-08-27 (first run)

**Status:** first-ever run of this digest; no prior history to dedupe against.

### MedGemma / Medical AI — items reported
- MedGemma 1.5 (4B/27B, HAI-DEF, Jan 2026) — the version CardiTrack already runs; no MedGemma 2.x announced yet.
- Gemini 2.5 family retirement confirmed for 2026-10-16 (matches CardiTrack's own tracked deadline from PR #479).
- gemini-3.1-flash-lite EU serving region for europe-west2 specifically — **unconfirmed**, flagged for direct Vertex Model Garden check.
- Gemini 3.6 Flash / 3.5 Flash-Lite now GA — potential fallback if 3.1-flash-lite EU access stays blocked.
- vLLM's hybrid KV-cache manager now handles Gemma 3's sliding-window/full-attention mix — de-risks CardiTrack's planned Ollama→vLLM move (blocker remains HAI-DEF gated-weights access, not vLLM readiness).
- Cloud Run GPU (L4) confirmed available in europe-west1/west4, not europe-west2 — consistent with CardiTrack's existing europe-west1 MedGemma placement (2026-08-21 move).
- IQ4_XS / Q6_K quantization gaining ground over Q4_K_M — worth a future benchmarking pass.
- Google for Startups Cloud Program (up to $350K credits) — applicable, not yet pursued as far as this digest knows.
- NIH SBIR/STTR standard due date 2026-09-08 — flagged as time-sensitive if pursuing.
- Horizon Europe Health 2026 calls already closed (deadline passed 2026-04-16); EIC Accelerator/EU4Health remain open.
- EU AI Act Digital Omnibus: high-risk obligations delayed to 2027-12-02, but Article 50 transparency obligations took effect on schedule 2026-08-02 — flagged for compliance check on AI-generated-content disclosure.
- GDPR Article 22 reform in progress but not expected to loosen private-sector automated-decision restrictions — CardiTrack's human-in-the-loop framing should hold.
- **Competitive intel (most important item this cycle):** Google "Health Guardian" (Pixel Watch/Fitbit, background BP-trend + insulin-resistance-trend detection, launching fall 2026) and **Luffu** (Fitbit founders' new family-care AI startup, hardware unveiled 2026-08-25, ships early 2027) — both are direct positioning competitors to CardiTrack's family/caregiver cardiovascular monitoring pitch.
- Security: Ollama CVE-2026-7482 ("Bleeding Llama", CVSS 9.1, patched in v0.17.1) and CVE-2026-5530 (SSRF in model-pull API); several llama.cpp CVEs (2026-2069, 2026-21869, 2026-17500, 2026-27940, 2026-43629/43630); HuggingFace July-2026 red-team breach (no evidence of model/dataset tampering); medical-LLM prompt-injection benchmark (MPIB, arXiv 2602.06268) — flagged the Ollama version-check action item.

### Third-party API / device schema — items reported
- Google Health API: possible webhook coverage gap (steps/altitude/distance/floors/weight/sleep only — **not confirmed** to include heart-rate/HRV/SpO2/respiratory-rate) — flagged CRITICAL, needs a direct fetch of developers.google.com/health/webhooks to confirm against CardiTrack's real subscriber config (this is an architecture-relevant risk if true).
- Google restricted-scope OAuth verification: Tier-2 self-assessment reportedly removed, CASA required for all restricted scopes, renewed annually; verification turnaround running 3+ months per multiple forum reports — relevant to the still-unsubmitted `carditrack-devices-prod` verification.
- Fitbit Web API decommission: still September 2026, exact day still unconfirmed by Google/Fitbit; no delay reported.
- Garmin Connect Developer Program: paused accepting new API-access applicants as of ~Aug 2026 (existing partners unaffected); relevant only if/when CardiTrack activates its Garmin stub.
- Withings: no findable 2026 breaking changes (stub integration, low urgency).
- Samsung Health: old Android SDK deprecated 2025-07-31, successor Health Data SDK v1.1.0 (2026-03-12) added `IrregularHeartRhythmNotificationType`/`SleepApneaType`; still no server-side/cloud API (confirms CardiTrack's phone-local-only assumption).
- Oura: Personal Access Tokens deprecated Dec 2025 (OAuth2-only going forward); has `daily_cardiovascular_age`/`vO2_max` metrics if activated later.
- Whoop: v1 API/webhooks fully retired, v2 is the only supported version since 2026-05-31; v2 recovery webhooks key off sleep UUID, not cycle ID.
- Apple HealthKit: apps categorized Medical/Health & Fitness may need to declare regulatory status (medical-device functionality) in App Store metadata starting spring 2026 — flagged for product/legal review ahead of the planned HealthKit bridge.

**Confidence note:** the third-party API research agent's direct WebFetch to vendor docs was blocked by network egress policy for most domains; several items above rest on search-result snippets rather than a fetched primary source and are marked "unconfirmed"/"recommend direct check" in the corresponding Slack post. Treat those as leads to verify, not settled facts.

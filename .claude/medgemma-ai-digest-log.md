# MedGemma / Medical-Grade AI Daily Digest — coverage log

Internal tracking file for the scheduled "MedGemma & Medical Grade AI Models" Slack digest
routine. Each run reads this file first, then only reports what's new or materially changed
since the last entry — do not repeat a fact already logged below unless it has updated
(new number, resolved status, reversed decision, etc.), in which case log the update, not
a restatement of the old fact.

Posts to: `#` Slack channel via incoming webhook (URL in the routine config, not repeated here).

---

## 2026-08-21 — first run

No prior digest existed (checked git history and repo for any tracking file — none found).
Full sweep across all seven focus areas. Key facts now considered "already shared":

**Updates**
- MedGemma 1.5 (released 2026-01-13) is the version CardiTrack already runs
  (`medgemma-1.5-4b-it-GGUF:Q4_K_M`) — no version gap to close.
- MedGemma 1.5 gains: +5% MedQA, +22% EHRQA, 3D CT/MRI, whole-slide histopathology,
  longitudinal chest-X-ray, full DICOM support in Vertex AI.
- Companion release: MedASR (medical dictation speech-to-text), open-source.
- MedGemma 27B Multimodal variant also shipped (joins 4B multimodal / 27B text-only).
- Gemma family passed 1B cumulative downloads, 100K+ community variants.

**Alternatives**
- Meditron-3 (EPFL, Llama-3.1-70B) is the strongest open clinical-LLM competitor, too heavy
  for CardiTrack's CPU Cloud Run footprint.
- Anthropic launched "Claude for Healthcare" (~2026-01-15): HIPAA BAA, CMS/ICD-10/NPI
  connectors, no training on health data. Relevant to CardiTrack's `AI:Public` Anthropic
  option (reports/chat), not the private MedGemma slot.
- No open alternative beats MedGemma's multimodal-per-parameter efficiency for the private
  medical slot — architecture's "MedGemma only, no provider selector" design still holds.

**Performance**
- MedGemma 27B-text: 87.7% MedQA (within 3pts of DeepSeek R1, ~1/10th cost).
- MedGemma 4B fine-tuned: SOTA chest-X-ray report generation (RadGraph F1 30.3) — not used
  by CardiTrack today (no imaging sent to MedGemma).
- vLLM's hybrid KV-cache manager has documented layer-specific prefix-cache rules for
  Gemma 3's sliding-window/full-attention split — potentially resolves the "SWA prevents
  prefix caching" problem `medgemma_serving_architecture.md` §5 flagged for Ollama/llama.cpp.
  Still GPU-only, still gated on HAI-DEF weights access. Open vLLM bug: speculative decoding
  can zero out cache-hit rate — re-check before committing to the vLLM migration step.
- Cloud Run L4 GPU: ~$0.0001867/s (~$0.67/hr) in Tier-1 regions, no zonal redundancy —
  close to `medgemma_serving_architecture.md` Option B's ~$40/mo estimate. Tier classification
  of europe-west1/west4 specifically was NOT confirmed this run — still open for MS-1.

**Grants**
- Google.org "AI for Science" $30M Impact Challenge (health/life-sciences track) — deadline
  already passed (2026-04-17). Missed this cycle.
- EIT Health: 4 calls (3 AI-focused), closes **2026-09-16**. Requires EU-member/Horizon-Europe
  entity — eligibility for CardiTrack/Codesistance not yet checked.
- Innovative Health Initiative (HORIZON-JU-IHI-2026-13): stage-1 deadline **2026-10-08**.
  Same EU-entity eligibility caveat.

**Regulation**
- FDA (2026-01-06) loosened Clinical Decision Support oversight — CDS is exempt from device
  regulation only when it recommends to a *healthcare professional* who independently reviews
  the basis. **Flagged as needing review**: CardiTrack's real-time assessor pages family
  caregivers (non-HCPs) directly with auto-generated severity verdicts and no independent-review
  step — the opposite shape of the exemption. This has NOT been resolved as of this digest;
  a future run should check whether this was escalated to counsel/product and follow up.
- FDA reaffirmed: software analyzing *medical images* for diagnostic recommendations stays
  regulated regardless of the CDS carve-out — not currently relevant (no imaging sent), watch
  if imaging is ever added.
- EU AI Act high-risk deadlines proposed to slip (Digital Omnibus): standalone high-risk →
  Dec 2027, AI-embedded-in-regulated-products (medical devices) → Aug 2028 (was Aug 2026).
  Not yet finalized as of this digest — a future run should confirm if/when this is adopted.

**Stickiness features**
- Competitor remote-cardiac-monitoring vendors pitch AI alert triage claiming ~80% cut in
  alert-response time — validates CardiTrack's SSA-gate-before-MedGemma design direction.
- Apple Watch Family Setup is the closest mass-market analog to CardiTrack's caregiver-visibility
  pitch — differentiation continues to rest on the clinical narrative (Daybook/Weekbook/Monthbook),
  not raw sensor accuracy.
- CardioMEMS HF System (implantable, FDA-cleared Feb 2026, -57% HF hospitalizations) — ceiling
  reference point for "clinically credible" claims, not a direct competitor.

**Security**
- Comparative VLM study: MedGemma had the *lowest* text prompt-injection ASR (38%) among
  models tested (vs Claude 4 Sonnet 48%, GPT-5 57%), but >80% ASR under white-box adversarial
  *image* perturbation. Image angle not currently relevant (no imaging sent to MedGemma).
- OCR/regulators increasingly scrutinizing membership-inference attacks against health LLMs.
  CardiTrack's existing untrusted-input framing for caregiver notes is the right shape of
  defense — no gap identified.

---

<!-- Next entry: append below this line, dated, listing only NEW developments or STATUS
     CHANGES to items above (e.g. "FDA CDS review — still unresolved" only if actually
     re-checked, or "EIT Health deadline passed, no application filed" as a closure note).
     Do not re-paste the full context each day. -->

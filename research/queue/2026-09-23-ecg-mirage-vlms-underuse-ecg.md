# ECG Mirage: small vision-language models look at ECG images and mostly ignore them — supports keeping ECG out of MedGemma's inputs as pixels

**Severity:** FYI
**Category:** models

## Summary

arXiv 2609.21755, "ECG Mirage: Revealing and Mitigating the Underutilisation of ECGs in
Vision-Language Models for Clinical Prediction" (September 2026; companion repository
JasonZuu/ECG-Mirage created 2026-09-14), names a failure mode where a VLM given a patient's ECG
image produces predictions that do not usefully depend on it. It separates "ECG neglect" (the ECG
adds little) from "ECG confusion" (a matched ECG beats no image, but not a mismatched one). On the
MDS-ED emergency-department dataset, four VLMs given the matched ECG gained no consistent advantage
for ICU-admission or clinical-deterioration prediction. The mitigation trains four restricted
visual prompt tokens (supervised, then conditional DPO) with the backbone frozen — the repository
README confirms a frozen Qwen3.5-4B backbone, the two tasks and the matched / mismatched / no-image
design — reaching balanced accuracy 70.6% (ICU) and 67.5% (deterioration). Weights and clinical
data are not released.

Relevance is to a design decision CardiTrack has already taken and should not quietly reverse.
The ECG path built 2026-09-22 fetches the **classification only**, drops `waveformSamples` with a
`fields` selector so the 15,000-sample lead-I trace never crosses the wire, and hands MedGemma a
label (`ATRIAL_FIBRILLATION`, or "the device declined to judge"). This paper is evidence that the
tempting next step — render the strip and let the 4B multimodal model "read" it — would add PHI
handling and a claim ("the AI reads your ECG") without adding signal. Together with ModaLens
(same digest) the picture for a 4B model is consistent: give it the finding in text; do not expect
it to derive the finding from pixels.

## Sources

- https://arxiv.org/abs/2609.21755 (primary — arXiv is denied by the sandbox's network policy;
  abstract details are from the arXiv listing as indexed)
- https://github.com/JasonZuu/ECG-Mirage (companion repository; README read directly, created
  2026-09-14)
- `docs/llm_design.md` rows "ECG classification" and "Irregular-rhythm notification"

## Why flagged

Bears on what the product can claim about the AI and ECG, and on a foreseeable roadmap request.
Not a clinical-validity claim in either direction.

## Question to answer next

Record in `docs/llm_design.md`, next to the ECG row, the one-line design rationale "classification
label only; waveform never fetched; see ECG Mirage / ModaLens" so the next person asked "can the
AI look at the ECG?" finds the reasoning rather than the omission. No code.

claude "work through @research/queue/2026-09-23-ecg-mirage-vlms-underuse-ecg.md"

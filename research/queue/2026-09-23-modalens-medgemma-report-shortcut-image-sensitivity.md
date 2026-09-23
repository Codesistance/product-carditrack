# ModaLens: MedGemma-27B answers from the text it is given and largely ignores the image once a report is present — evidence about what our prompts make the model do

**Severity:** FYI
**Category:** models

## Summary

arXiv 2609.15635, "ModaLens: Measuring Image Sensitivity in Report-Conditioned Medical VLMs"
(v1 2026-09-15), audits **MedGemma-27B** with a paired image-swap test on 3,199 MIMIC-CXR cases
(293 patients, 14 questions per case): the chest X-ray is replaced with one from a different study
while the question and the radiology report are held fixed. Reported result: with the report in
the prompt the model's answer changes on only **4.26%** of swaps; without the report it changes on
**20.94%** — a paired difference of 16.7 points (patient-clustered 95% CI 15.6–17.7). The authors'
reading is that the report is a textual shortcut: once the finding is stated in text, the model
answers from the text and mostly stops looking at the image. They are explicit that the method
measures sensitivity to the input, not medical correctness.

This is the MedGemma family specifically, though the 27B multimodal model on radiology, not the
4B text path CardiTrack runs. The transferable point is about prompt design, not images:
CardiTrack already hands MedGemma pre-computed facts — SSA baselines, statistical-alert flags, the
Google Health ECG classification label and irregular-rhythm counts (built 2026-09-22, classification
only, no waveform) — and asks for a plain-language digest. ModaLens says a MedGemma model given the
finding in text will restate the finding, faithfully and deterministically, rather than
independently re-derive it. That is the behaviour the architecture wants (auditability, the
`docs/llm_design.md` severity routing stays in code), and it is also the boundary of what can be
claimed: the model **explains and summarises** signals the pipeline detected; it is not the
detector. Wording that says or implies MedGemma "spots" or "detects" a change would be over-claiming
on this evidence.

## Sources

- https://arxiv.org/abs/2609.15635 (primary — arXiv is denied by the sandbox's network policy;
  existence and the 2026-09-15 date were confirmed from the arXiv HTML rendering that the
  Hugging Face papers index served, and the numbers above are as quoted consistently by three
  independent search-index entries. Re-read the abstract before quoting the numbers anywhere.)
- `docs/llm_design.md` (what the pipeline hands the model)

## Why flagged

It is direct evidence about the model CardiTrack ships, on the question that decides both the
regulatory story (who detects: code or model) and the marketing copy. Not a benchmark score and
not being restated as clinical validity.

## Question to answer next

Grep the app strings, the website (`index.html`, `enterprise.html`) and `docs/market_analysis.md`
for "detects", "spots", "identifies" applied to the AI, and make each one say what the code does
(statistical rules, device classifications) versus what MedGemma does (explains, summarises,
narrates). Optionally, replicate the audit cheaply on our own path: run the digest prompt with the
alert flags swapped between two members and measure how often the narrative follows the flags
rather than the raw series — it should be near 100%, and that number is a useful line in the
serving-architecture doc.

claude "work through @research/queue/2026-09-23-modalens-medgemma-report-shortcut-image-sensitivity.md"

# Guide-grounded prompting cuts hallucination in LLM-generated ECG reports

**Category:** models · **Severity:** FYI

## Summary

"Enhancing Explainable Cardiac Diagnosis with Guide-Grounded Multimodal LLMs"
(arXiv, submitted July 2026) shows that injecting a curated, offline-distilled
"ECG Interpretation Guide" as a fixed context block into a multimodal LLM's
report-generation prompt — alongside CNN/Grad-CAM class probabilities and
heatmaps — raises BERTScore of generated impressions from 0.818 to 0.953 on
PTB-XL. The authors attribute the gain to fewer hallucinated / guideline-
inconsistent claims.

BERTScore is a semantic-similarity metric, not a clinical-accuracy one — this
is a mitigation pattern, not evidence of clinical validity, and should not be
cited as such.

## Sources

- https://arxiv.org/abs/2607.20814

## Why flagged

The architecture — deterministic signal-processing output + a fixed clinical-
reference block fed to the LLM before narration — maps closely to CardiTrack's
own design: MedGemma narrates over a deterministic severity engine rather than
making autonomous clinical judgments. This is a concrete, testable lever if
digest hallucination rates ever need to come down.

## Next question

Does CardiTrack's current MedGemma prompt template inject a fixed clinical-
reference/guideline block, or only the severity engine's raw verdict? If only
the latter, is a narrow pilot of guide-grounded prompting worth testing against
any digest hallucination cases already on file?

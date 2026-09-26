# EU AI Act Article 50: the Commission's final transparency guidelines (2026-07-20) and the Code of Practice on AI-generated content (adequacy 2026-07-08) — the official answer sheet for open item OI-15 (c)

**Severity:** FYI
**Category:** regulation
**Date recorded:** 2026-09-26

## Summary

The Commission published its **final Guidelines on the transparency obligations of providers and
deployers of AI systems (Article 50)** on 2026-07-20, and on 2026-07-08/09 the Commission and the AI
Board assessed the **Code of Practice on Transparency of AI-generated Content** (published 2026-06-10)
as adequate, making adherence to the Code "the only current EU-wide practical compliance tool assessed
as adequate" for Art. 50(2), (4) and (5). The obligations themselves have applied since 2026-08-02.
Search snippets describe the Code as requiring providers to implement multilayered machine-readable
marking of AI-generated outputs and deployers to label deepfakes and certain AI-generated text.

**Where CardiTrack stands.** `docs/compliance/ai_act_classification.md` §7.1 treats Art. 50 as live
since 2 August and records two open gaps: the in-product "AI-written" line on **seven of the eight**
surfaces — member chat (squarely under 50(1)) has shown a one-time notice since 2026-09-25, and the
other seven, treated conservatively, are tracked as issue #1244 — and
machine-readable marking on API responses serving generated text (OI-15 (c), pending counsel's view on
whether 50(2) reaches them). Neither the guidelines nor the Code is cited, and neither URL was on the
digest record. The DPIA's 2026-09-24 entry records no EEA users, so exposure is prospective.

**Sourcing.** Both Commission pages are proxy-blocked; content is from search snippets. The Digital
Omnibus (Regulation (EU) 2026/1744, OJ 2026-07-24, in force 2026-07-27) is confirmed by the same
sweep — it moved Annex III to 2 December 2027 and does not touch Art. 50.

## Sources

- https://digital-strategy.ec.europa.eu/en/library/guidelines-transparency-obligations-providers-and-deployers-ai-systems — the guidelines (proxy-blocked; indexed)
- https://digital-strategy.ec.europa.eu/en/policies/code-practice-ai-generated-content — the Code of Practice (proxy-blocked; indexed)
- docs/compliance/ai_act_classification.md §7.1 and OI-15 (c); issue #1244

## Why flagged

The repo has an open compliance item whose answer now exists as two official instruments, and an
in-flight issue (#1244) rolling the disclosure line out to the seven remaining surfaces without
reference to the Code that defines "adequate". Reading two documents closes a question counsel would otherwise bill for.

## Question to answer next

1. Read the guidelines' scope section: do the seven non-chat surfaces (digest, summaries, alert
   narration, exports) fall under 50(2) as "AI-generated text" published to inform, or under 50(1)
   interaction disclosure only?
2. Read the Code's text-marking commitments and compare with #1244's planned marker: is a response
   header or JSON field "multilayered machine-readable" in the Code's sense, or does it need a
   content-level mark too?
3. Close OI-15 (c) either way and cite both instruments in §7.1.

claude "work through @research/queue/2026-09-26-eu-ai-act-article-50-guidelines-and-code-of-practice.md"

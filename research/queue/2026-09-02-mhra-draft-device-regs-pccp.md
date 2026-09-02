# MHRA draft Medical Devices (Amendment) Regulations 2026 — PCCP for SaMD/AI

**Severity:** HIGH
**Category:** regulation

## Summary

Draft published 8 May 2026 (stakeholder survey closed 19 June 2026); expected adoption December
2026, in force ~June 2027. The most significant post-Brexit overhaul of the GB device framework so
far: introduces formal Predetermined Change Control Plans (PCCPs) for SaMD/AI-enabled devices, an
International Reliance Pathway (accepting FDA/Health Canada/TGA-cleared devices without UKCA),
revised IVD classification, mandatory UDI, and tighter post-market surveillance.

## Source links

- https://www.lw.com/en/insights/mhra-publishes-draft-amendment-to-the-uk-medical-devices-regulations
  (law-firm summary — used because the primary gov.uk publication was unreachable from this
  sandbox's egress proxy this run; verify against gov.uk directly before relying on specifics)

## Why flagged

Older than the usual 30-day window but carries a live compliance horizon (in force mid-2027). If
CardiTrack is ever classified as SaMD in GB, PCCP is the mechanism that would let future
MedGemma/model updates ship without full re-certification each time — directly relevant to how the
AI ingestion pipeline (webhook aggregation, SSA pre-processing, MedGemma calls, severity routing)
would need to be governed if that day comes.

## Question to answer next

Confirm the specifics (PCCP scope, International Reliance Pathway eligibility) directly against
gov.uk once egress allows a direct fetch — the law-firm summary is a reasonable proxy but not the
regulation text itself. No action needed before adoption (expected Dec 2026).

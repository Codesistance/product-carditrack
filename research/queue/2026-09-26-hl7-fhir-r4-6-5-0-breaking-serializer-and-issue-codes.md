# Hl7.Fhir.R4 6.5.0 (2026-09-14) renumbers two OperationOutcome issue codes and stops serialising empty objects and elements — CardiTrack pins 6.4.0

**Severity:** FYI
**Category:** dependencies
**Date recorded:** 2026-09-26

## Summary

Firely's .NET SDK **6.5.0** (Hl7.Fhir.R4 on NuGet 2026-09-14; before this run's window but not on
the record) flags two breaking changes: the numeric issue codes `PROFILE_ELEMENTDEF_SLICENAME_NOMATCH`
(10012 → 10018) and `PROFILE_STRUCTURE_TYPE_MISSING` (10014 → 10019) are renumbered, and the
serialisers "no longer write empty objects (`{}`) or empty elements (`<x/>`)". Non-breaking: markedly
lower parsing and validation allocations, snapshot-generator crash fixes, a terminology-service
concurrency fix.

**Where CardiTrack stands.** The only production use is
`src/Infrastructure/CardiTrack.Infrastructure/Services/Reports/FhirR4ReportRenderer.cs`, which builds a
`Bundle` and serialises it with `new JsonSerializerOptions().ForFhir(ModelInfo.ModelInspector)`. It
does not construct or inspect `OperationOutcome` issue codes and does not run the validator, so the
renumbering cannot bite. The empty-element change alters emitted JSON only if a rendered bundle
currently contains an empty object or element; `tests/CardiTrack.UnitTests/Services/ReportRendererTests.cs`
would show the difference. No CVE.

## Sources

- https://github.com/FirelyTeam/firely-net-sdk/releases/tag/v6.5.0 — release notes (read directly via WebFetch)
- NuGet registration for Hl7.Fhir.R4 (6.5.0 published 2026-09-14; read directly)

## Why flagged

A breaking change on a pinned dependency that shapes an export CardiTrack hands to third parties (the
FHIR bundle). The export's byte shape is the compatibility contract, so a serializer change is worth
one deliberate diff.

## Question to answer next

1. On the next bump: render the reference bundle on 6.4.0 and on 6.5.0 and diff them. If identical,
   bump with no further action. If an empty element disappears, decide whether any consumer relied on
   it (unlikely — FHIR forbids empty elements — but check the report tests' fixtures).

claude "work through @research/queue/2026-09-26-hl7-fhir-r4-6-5-0-breaking-serializer-and-issue-codes.md"

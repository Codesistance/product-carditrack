# QuestPDF 2026.9.0 makes missing fonts and glyphs throw by default and stops using system fonts — breaking on the next bump from our pinned 2026.8.0

**Severity:** FYI — no date; breaks when Dependabot (or anyone) bumps the pin
**Category:** dependencies

## Summary

QuestPDF 2026.9.0 was released 2026-09-14. The release notes list breaking changes in exactly the
area CardiTrack has hand-tuned for its chiseled runtime images:

- System fonts are **no longer used by default**: `Settings.UseEnvironmentFonts` becomes
  `Settings.UseSystemFonts`, default `false`.
- `Settings.ThrowOnMissingFontFamilies` is now **`true`** — an unknown family throws
  `DocumentDrawingException` instead of falling back.
- `Settings.CheckIfAllTextGlyphsAreAvailable` becomes `Settings.ThrowOnMissingTextGlyphs`, now
  **`true`** — a character with no glyph in the registered fonts throws.
- `Settings.FontDiscoveryPaths` (plural) becomes a single `Settings.FontDiscoveryPath`.
- The bundled Lato ships as a compressed archive (`QuestPDF.Fonts.Lato.br` / `.gz`) rather than a
  folder of TTFs.
- `RegisterFontWithCustomName` and `QuestPDF.Helpers.Fonts` are deprecated;
  `SemanticHeader1–6` become `SemanticHeading1–6`; image output defaults to a white background.

Licence unchanged for us: the package is still "Community and commercial"; LICENSE.md v3.0
(effective 2026-06-07) keeps the Community tier at under USD 1M annual gross revenue.
`StorageServiceExtensions.cs:72` sets `LicenseType.Community`; nothing there moves.

What it touches here (`CardiTrack.Infrastructure.csproj` pins `QuestPDF` **2026.8.0**):

- `Services/Reports/ReportFonts.cs` registers fonts from streams via `FontManager.RegisterFont`
  (not deprecated) with a fallback chain, because the `aspnet:10.0-noble-chiseled-extra` runtime
  has no system fonts and the API Dockerfile copies fonts in at a dedicated build stage.
- `PdfReportRenderer.cs` and `ChatTranscriptDocument.cs` import `QuestPDF.Helpers` (for colours
  and page sizes — the namespace itself is not deprecated, only `Helpers.Fonts`).
- Chat-transcript exports render **member-typed text**: any character outside the registered
  fonts' coverage (an emoji, a non-Latin name) that today renders as a box or falls back will,
  after the bump, throw and fail the export. That is the one behaviour change that reaches a user.

Nothing breaks today. The pin is exact, and no CVE is involved.

## Sources

- https://github.com/QuestPDF/QuestPDF/releases/tag/2026.9.0 (release notes, 2026-09-14 — read
  directly)
- https://github.com/QuestPDF/QuestPDF/blob/main/LICENSE.md (v3.0, Community tier unchanged)
- `src/Infrastructure/CardiTrack.Infrastructure/CardiTrack.Infrastructure.csproj`,
  `Services/Reports/ReportFonts.cs`, `Extensions/StorageServiceExtensions.cs`

## Why flagged

A breaking change in a pinned dependency's next version, in the one subsystem (fonts on a chiseled
image) that has already needed bespoke work. Cheap to prepare for now; a surprise if it arrives as
a Dependabot PR that passes the build and fails on the first transcript containing an emoji.

## Question to answer next

Before or with the 2026.9.0 bump: (1) decide the policy for glyphs outside the registered fonts in
member-authored text — keep the throw and add a fallback font with broad coverage (Noto Sans +
Noto Color Emoji or similar) to the Dockerfile font stage, or set `ThrowOnMissingTextGlyphs =
false` explicitly and accept boxes; (2) rename the two settings and confirm `UseSystemFonts=false`
is what the chiseled image already relies on; (3) add a transcript-export test whose fixture
contains an emoji and a non-Latin name, so the behaviour is pinned whichever way it goes.

claude "work through @research/queue/2026-09-23-questpdf-2026-9-0-font-breaking-changes.md"

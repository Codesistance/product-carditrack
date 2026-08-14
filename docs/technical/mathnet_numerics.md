# Math.NET Numerics — in-process statistical engine

**Status:** Accepted (2026-08-14)
**Scope:** Where numerical linear algebra and descriptive statistics live in the CardiTrack .NET 10 solution; which product formulas stay hand-rolled; which documentation gaps the swap surfaces for GDPR Art. 13–15 / Art. 22.
**Relationship to other docs:** [llm_design.md](../llm_design.md) owns the SSA → MedGemma contract. [art22_alerting_analysis.md](../compliance/art22_alerting_analysis.md) owns contestability of the numbers. [data_protection_architecture.md](./data_protection_architecture.md) §9 owns subprocessors — this library is **not** one.

---

## 1. Context

CardiTrack computes every number in-process and lets MedGemma only interpret them. Two numerical stages existed as dependency-free Application code:

| Stage | Code | Solver / formula |
|---|---|---|
| Real-time SSA | `SsaDecomposition` | Lag-covariance (Broomhead–King) + cyclic Jacobi eigen-decomposition, L=30 |
| Daily baselines | `BaselineCalculator` | Arithmetic mean, sample σ (n−1), circular mean of clock times |
| R1 alerts | `StatisticalAlertRules` | 30% of mean; HR = avg + max(2σ, 5 bpm); 4× ≥5% weekly step decline |

Jacobi at L=30 is numerically adequate. The product gaps are elsewhere (robust location, trend tests, eigentriple grouping). A commercial “stats SDK” was rejected: PHI must not leave the estate, Application stays package-free, and unit economics do not support a per-developer numerical library. Math.NET Numerics (MIT, `5.0.0`) is the in-process engine that fits those constraints.

## 2. Decision

1. **Math.NET Numerics is the numerical engine.** Package lives only on `CardiTrack.Infrastructure`. Ports (`ISsaDecomposition`, `IDescriptiveStatistics`) live in Application. Domain and Application still have zero `PackageReference`.
2. **SSA eigen-decomposition uses Math.NET's symmetric EVD** (`Matrix<double>.Evd(Symmetricity.Symmetric)`). Embedding, eigentriple grouping (first = trend, next two = oscillation), hankelization, and noise-as-residual stay CardiTrack code — the Art. 22 reconstructable algebra does not move into a black box.
3. **Baseline mean/σ and the five R1 rules do not change in this increment.** Median and unscaled MAD are now **persisted** on `PatternBaseline` alongside mean/σ. Live alerts still threshold on the mean (steps/sleep 30%) and on mean+max(2σ, 5 bpm) for HR. Switching the live fence is a product change that would retune every alert and must land with Art. 22 V4 after G2 shadow evaluation.
4. **Math.NET is not a subprocessor.** It is an in-process MIT library. No reading leaves the Cloud Run/Worker process. It does not belong on the [subprocessor register](./data_protection_architecture.md#9-subprocessor-register). It does belong on the SBOM / software inventory.

Engine identifier for audit: `SsaParameters.Engine = "MathNet.Numerics.Evd"`.

## 3. Alternatives considered

| Option | Outcome |
|---|---|
| Keep Jacobi | Adequate at L=30; leaves the “homemade eigen” audit story and blocks SVD/W-correlation later |
| Microsoft.ML.TimeSeries SSA spike/change-point | Prototype later as a *shadow* detector; the API returns alert/p-value, not Trend/Oscillation/Noise the assessor stores |
| Numerics.NET / ALGLIB / NMath | Commercial seats, no extra accuracy on a 30×30 covariance |
| Cloud anomaly API | Killed — PHI, subprocessor, Azure Anomaly Detector retires 2026-10-01 |
| Put `MathNet.Numerics` in Application | Violates the zero-package invariant |

## 4. Gaps Math.NET solves — now vs next

### Solved in this increment

| Gap | Before | After |
|---|---|---|
| Homemade Jacobi eigen-solver | Cyclic Jacobi, ε=1e-12, 50 sweeps, Application | LAPACK-quality symmetric EVD via Math.NET, Infrastructure |
| “Dependency-free SSA” vs inspectable library | Docs claimed homemade numerics; no named engine | Named engine `MathNet.Numerics.Evd`; MIT licence; reconstructable grouping still ours |
| No port for descriptive stats | Mean/σ duplicated in `BaselineCalculator` only | `IDescriptiveStatistics` + `MathNetDescriptiveStatistics` (mean, sample σ, median, MAD, percentile) |
| Layering | SSA lived in Application only because Jacobi needed no package | SSA implementation follows the package; Application keeps the result contract |

At L=30 the numerical delta vs Jacobi is small. Existing SSA contract tests (reconstruction identity, constant → trend, ramp → trend, cycle → oscillation, noise-RMS yardstick) still define “correct.”

### Not solved — product formula gaps (Math.NET can implement; we have not switched)

These are the accuracy levers on the &lt;10% MVP / &lt;5% steady-state false-positive targets. They are **not** the eigen solver.

| # | Gap | What fires today | What Math.NET (or ~40 lines on top) would give | Why it is deferred |
|---|---|---|---|---|
| G1 | Mean/σ baselines poisoned by one unusual day | `BaselineCalculator` arithmetic mean + sample σ | Median, MAD now **persisted** on the same row via `IDescriptiveStatistics`; IQR/percentiles available | Live R1 still uses mean/σ. Switching is a product change + V4 |
| G2 | Steps/sleep alerts ignore the member's own variability | 30% of the mean | `RobustThresholdShadow` compares 30%-of-mean vs 3× unscaled-MAD on the same reading | Shadow only — `StatisticalAlertRules` unchanged until FP rate is measured on real families |
| G3 | Long-term trend is four consecutive 5% weekly drops | Misses a smooth 4%/week decline | Mann–Kendall + Theil–Sen slope (not in Math.NET; small Application function) | New rule semantics |
| G4 | SSA eigentriple grouping is naive | Component 0 = trend, 1–2 = oscillation | W-correlation of reconstructed components (Math.NET matrix ops) | Would move energy between Trend/Oscillation/Noise and change `HrDeviationScore` |
| G5 | No change-point / spike p-value on the minute series | Noise-RMS score only | Microsoft.ML.TimeSeries `DetectSpikeBySsa` as a *shadow* path | Heavier dependency; does not replace the three-series contract |
| G6 | No algorithm version on `RealtimeAssessment` | Features stored, engine not named on the row | **Done.** `RealtimeAssessment.SsaEngine` stamped `SsaParameters.Engine` | — |
| G7 | Circular bedtime mean | Hand-rolled resultant length | Math.NET has no circular clock-mean | Keep homemade |

### Out of scope for Math.NET

- Replacing MedGemma, restoring LSTM/ONNX, calibrated risk scores
- Cloud stats APIs
- Putting any package in `src/Core`

## 5. Documentation gaps that cause (or will cause) compliance issues

Art. 13–15 require “meaningful information about the logic involved” in profiling. Art. 22-grade safeguards (even on the conservative reading) require the numbers to be reconstructable. Several docs were already stale against the *Jacobi* implementation; leaving them stale against Math.NET would be worse.

### Fixed in this change (docs now match the running engine)

| Doc | Stale claim | Correction |
|---|---|---|
| [llm_design.md](../llm_design.md) | SSA is “dependency-free .NET (lag-covariance + Jacobi)” in Application | SSA is BK lag-covariance + Math.NET symmetric EVD in Infrastructure; grouping unchanged |
| [architecture_c4.md](../architecture_c4.md) | `SsaDecomposition` “Application, dependency-free” | Infrastructure, Math.NET EVD |
| [art22_alerting_analysis.md](../compliance/art22_alerting_analysis.md) | “deterministic .NET” unnamed solver | Names `SsaParameters.Engine`; V4 now includes numerical-engine changes |

### Still open — these can fail an Art. 13–15 / Art. 22 / DPIA review

| # | Gap | Why it is a compliance problem | Owner |
|---|---|---|---|
| D1 | **Privacy-policy “how alerting works.”** | **Shipped** on `/privacy`. Still a short policy overall (no Google Limited Use section, no Art. 14 wearer notice) | Product + compliance |
| D2 | **Art. 22 V2/V3 never executed.** Retrospective benchmark and prod shadow are the gate on prod alerting (`art22_alerting_analysis.md` §5). V4 now includes numerical-engine changes. | This SSA swap is a recorded V4 event. Stored `HrDeviationScore` from before 2026-08-14 is the same algebra, a different solver. Mixing pre/post rows in a bit-stability claim would be false. V2 needs real stored assessments — not executable in a VM with no family data. | Compliance + engineering — run V2 on post-swap windows before prod families |
| D3 | **Art. 22 re-run after push dispatch (2026-08-11) is still an open action.** | Push makes “similarly significant effect” more plausible. An engine swap on top of an unreviewed analysis compounds the gap. | Privacy professional sign-off |
| D4 | **DPIA §13 did not name “numerical engine / alerting formula” as a review trigger.** Adding a library that computes profiling numbers is not a new processor, but it is a change to automated profiling logic. | A DPIA that only triggers on “new processor or model provider” misses this class of change. | DPIA §13 (updated in increment 0) |
| D5 | **Python reference in llm_design (`pyts` SVD grouping `[[0],[1,2]]`) never matched production (BK EVD, not trajectory SVD).** | Contestability: the “reference implementation” a reviewer copies will not reproduce `HrTrendLast`. | llm_design — annotate that pyts is pedagogical, production is BK+Math.NET EVD (increment 0) |
| D6 | **Algorithm card.** | **Shipped** — [alerting_algorithm_card.md](../compliance/alerting_algorithm_card.md) | — |
| D7 | **`PatternBaseline` stored mean/σ only.** | Median/MAD now persisted; A10 updated. Live HR margin is still max(2σ, 5) so the false-negative pathway remains until G2 ships as a live fence | DPIA A10 |
| D8 | **Assessments do not record the engine id.** | **Shipped.** `RealtimeAssessment.SsaEngine`; null on pre-column rows | — |
| D9 | **Math.NET is not on any software inventory in-repo.** Subprocessor register correctly excludes it; HIPAA/SBOM reviewers will still ask. | This ADR is the inventory entry. Link it from the DPIA processor table as “in-process library, not a processor.” | DPIA §4.3 note (increment 0) |
| D10 | **AlertPreferences (low/medium/high) documented as unbuilt.** Only medium (30%) exists. | Privacy notice and algorithm card state there is no sensitivity slider | Product |

## 6. Plan

### Increment 0 — engine (shipped)

- Add `MathNet.Numerics` 5.0.0 to Infrastructure.
- Move SSA implementation behind `ISsaDecomposition`; swap Jacobi for Math.NET EVD.
- Register `IDescriptiveStatistics` / `MathNetDescriptiveStatistics` (Worker + PipelineJobs).
- Keep `BaselineCalculator` mean/σ and `StatisticalAlertRules` formulas unchanged.
- Align llm_design, C4, Art. 22, DPIA with the running engine; record D1–D10.

### Increment 1 — before prod families (compliance)

- Execute Art. 22 V2 on stored `RealtimeAssessments`: agreement of `HrDeviationScore` bands vs the rule-based reference, split by age/sex as already specified. **Still outstanding** — needs real stored assessments.
- Optionally recompute a sample of windows with the old Jacobi fixture in a branch if any pre-swap assessments must be compared.
- **Done:** algorithm card (D6) and privacy-policy “how alerting works” (D1). No invented tunable sensitivity.

### Increment 2 — robust baselines (this PR)

- **Done:** persist median + MAD on `PatternBaseline` alongside mean/σ (additive columns, no silent replacement).
- **Done:** `RobustThresholdShadow` (G2) — 30%-of-mean vs 3× unscaled-MAD; unit-tested disagreement cases; live rules unchanged.
- **Done:** stamp `SsaParameters.Engine` on `RealtimeAssessment.SsaEngine` (G6).
- **Not done here:** switching live steps/sleep fences to MAD. That waits on G2 measured against historical `ActivityLogs` from real families.

### Increment 3 — trend + grouping (only if Increment 2 still misses)

- Mann–Kendall / Theil–Sen for `long_term_trend` (G3).
- W-correlation eigentriple grouping (G4).
- ML.NET TimeSeries spike detector as a **shadow** path only (G5) — never as the MedGemma input series.

### Explicitly not planned

- Commercial numerical libraries.
- Cloud anomaly APIs.
- Replacing MedGemma with a statistical model.

## 7. Consequences

- PipelineJobs and Worker take a new singleton registration (`AddNumerics()`). API/Web/Mobile do not — they never decompose series.
- SSA contract tests now exercise Infrastructure. Application remains package-free.
- `HrDeviationScore` on assessments written after this deploy may differ from Jacobi at floating-point noise. Treat pre/post rows as the same algebra, different solver; do not mix them in a V2 that claims bit-stability.
- Licence: MIT. No runtime fees. Optional Intel MKL provider is **not** enabled (native binaries on Cloud Run are a later performance decision; a 60-sample EVD is sub-millisecond managed).

## 8. Trigger to revisit

- V2 shows SSA-score disagreement that grouping (G4) or a trajectory-matrix SVD would fix.
- False-positive rate on statistical alerts exceeds the manifest target after real families.
- A compliance review demands named textbook tests (Anderson–Darling, etc.) — that is Numerics.NET territory, not Math.NET.
- Math.NET 6.0 stable ships; bump from 5.0.0 is then a V4-class engine change.

---

*Prepared as an architecture decision record. Increments 0–2 land in this PR; V2 execution and any live-fence switch are separate.*

# Granular Time-Series Storage (ADR)

**Status:** Accepted — 2026-08-09 · Implemented (status notes updated 2026-08-13)
**Decision owner:** Architecture
**Related:** [llm_design.md](../llm_design.md) · [data_sync_architecture.md](./data_sync_architecture.md) · [data_protection_architecture.md](./data_protection_architecture.md) · [infrastructure.md](../infrastructure.md)

Sub-daily wearable samples (1-minute heart rate, steps, active-zone minutes; ~5-minute SpO2) are stored in **the existing Cloud SQL PostgreSQL instance**, in day-partitioned hour-vector tables in the clinical schema, behind a repository interface. **No new storage engine is introduced.**

---

## 1. Context

Everything CardiTrack stores about a wearer's day today is **one row per member per day** (`ActivityLogs`, merged from per-device `DeviceActivityLogs` — see the [allocation view](./data_sync_architecture.md)). That grain is sufficient for the dashboard and the 7/14/30/60/90-day `PatternBaseline`s, and insufficient for everything the [LLM design](../llm_design.md) commits to next:

- the **real-time path** consumes 1-minute intraday series (SSA decomposition over a 30-minute lag window, 5-minute assessment windows);
- **moving-window inference** ("assess the last hour, every few minutes") has no substrate — a daily row cannot answer an intra-day question;
- the agreed product direction is **granular points plus multi-horizon rollups** (hour / day / week / month), with inference running on a moving window over the granular series.

The Google Health API already serves the granular series (`list` methods at 1-minute grain for heart rate, steps, and active-zone minutes; ~5-minute for SpO2 — see the data-type table in [llm_design.md](../llm_design.md)). The question this ADR answers is **where those points live**: another storage type, or the existing data plane.

### Scale envelope

| | Points/wearer/day | At 100 wearers (today's cap) | At 10,000 wearers (design ceiling) |
|---|---|---|---|
| Granular points | ~4,600 (HR 1440 + steps 1440 + AZM 1440 + SpO2 ~288) | ~460 K/day | ~46 M/day (~530 writes/s average, arriving pre-batched) |
| Hour-vector rows (this design) | ~96 (4 metrics × 24 h) | ~9.6 K/day | ~1 M/day |
| Granular storage @ 90-day retention | — | ~4 GB | ~30–60 GB |

For calibration: Bigtable's justification zone starts around **10 K+ sustained writes/second**; this workload's ceiling is ~0.5% of that, and its reads are OLTP-shaped (single-member window scans, rollup lookups).

---

## 2. Decision

### Storage layout

| Table | Grain | Written by | Notes |
|---|---|---|---|
| `GranularMetricHours` | one row per **member × device × metric × hour**, holding a 60-slot value array + sample count | `GranularIngestionService` (via `DeviceSyncService.IngestGranularWindowAsync`), reached by two triggers: `WearableSyncWorker`'s routine pull and the webhook-driven aggregator | **Per-device, merged on read** by device priority — mirrors the `DeviceActivityLogs` → `ActivityLogs` precedent without rewriting merged hours on every 10-minute sync. Range-partitioned by day. |
| `MetricRollupsHourly` | one row per member × metric × hour (min/max/avg/sum), merged across devices | same service — recomputed from the **merged** window after the hour upsert | The hour horizon of the rollup ladder. Idempotent by recomputation, not by a single transaction. |
| `ActivityLogs` (existing, unchanged) | one row per member × day | existing merge | **Remains the canonical daily rollup.** `BaselineCalculator`, `DashboardService`, and every existing reader keep working untouched. |
| Week / month horizons | derived from daily | SQL views — **shipped** as `ActivityLogsWeekly` / `ActivityLogsMonthly` | Materialize only if measured slow — not preemptively. |

### Boundaries

- **`IGranularMetricRepository`** — **shipped**, at exactly the proposed path (`src/Core/CardiTrack.Application/Interfaces/Repositories`), with `UpsertHoursAsync`/`UpsertRollupsAsync`/`GetWindowAsync`/`GetRollupsAsync` — is the only read/write surface. The GCP aggregator reads through the repository layer exactly as [llm_design.md](../llm_design.md) already prescribes for token reads. This interface is the entire migration surface if storage ever changes.
- **Partition lifecycle and rollup derivation run in `CardiTrack.Worker`** — non-AI DB work, per the binding rule in `CLAUDE.md`. **Shipped** as `PartitionMaintenanceWorker`: hourly at :15, creating future partitions ahead of need and dropping expired ones (PostgreSQL has no TTL; the same machinery pattern as other retention jobs), with `RunOnStartup: true` because scale-to-zero left a cold-start window with no partitions (2026-08-11 incident).
- Partition DDL ships as raw SQL inside EF Core migrations; EF maps the tables, the migrations own the partitioning.

### Retention

Consistent with the [data-protection ADR](./data_protection_architecture.md) retention table, which gains these rows:

| Data | Retention | Disposal |
|---|---|---|
| `GranularMetricHours` | **90 days** (aligned with `realtime_results`) | partition drop |
| `MetricRollupsHourly` | **13 months** (one year of hour-grain comparisons + a month of slack) | partition drop |
| `ActivityLogs` (daily) | 25 months — unchanged, already recorded | existing policy |

---

## 3. Alternatives considered

| Alternative | Why rejected |
|---|---|
| **Bigtable** (the decision-tree default for wide-column time series) | Justified at ≥10 K writes/s or multi-TB series; this workload peaks near 530 writes/s and ~60 GB. Costs ~£90–130/mo minimum in prod before a byte is stored, adds a second backup/DR/audit/encryption story **for PHI**, a new .NET client stack, and loses SQL joins — family-read scoping joins `UserCardiMembers` at the query layer, and the schema-grant separation in the data-protection ADR cannot span engines. |
| **BigQuery** | Wrong shape for operational reads on a 5-minute cadence; streaming-insert and per-query costs; DML-based retention is awkward; and it would put identified PHI in a second analytics plane. BigQuery's correct future role is **Tier-3 de-identified analytics export only** — never the operational store. |
| **Point-per-row in PostgreSQL** | Works, but 60× the row count (~46 M rows/day at ceiling) for no read-pattern benefit; the hour-vector layout is the standard Postgres compression for fixed-cadence series. |
| **Separate Cloud SQL database/instance** | The data-protection ADR already records that **Postgres schemas give the physical separation with per-role grants — no second database needed at current scale**. A second instance doubles cost and backup surface for a boundary that grants already enforce. |
| **Firestore / Memorystore** | Wrong shapes: document-per-entity and cache respectively; neither serves window scans over ordered series. |

Precedent: [llm_design.md](../llm_design.md) made the same one-data-plane call for the AI JSONB result tables, for the same reasons (one backup story, direct joins for family scoping).

---

## 4. Consequences

**Gained**
- **Zero new Terraform resources.** Only Cloud SQL disk needs watching as wearers grow (a tfvars bump, not a new service).
- One data plane: the existing backup, DR, encryption, audit, and schema-grant story covers granular PHI on day one.
- Existing readers untouched — the daily contract (`ActivityLogs`) is preserved, so this ships without touching the dashboard or baseline code paths.
- The SSA substrate exists the moment ingestion lands, unblocking the real-time pipeline design.

**Paid**
- Partition management is on us (a Worker job + raw DDL in migrations) — Postgres gives no TTL for free.
- Hour-vector upserts rewrite a row up to 6× as a 10-minute sync fills an hour; acceptable write amplification at this scale, and the reason granular rows are per-device (append-shaped) rather than merged-on-write.
- A 60-slot array is opaque to ad-hoc SQL; anything needing per-minute SQL analysis goes through the rollups or the repository.
- Cloud SQL disk becomes a growth-watch item (~30–60 GB at ceiling and 90-day retention — sized, not scary).

### Compliance notes

Granular streams are **biometric-adjacent** (data-protection ADR §4.3): they live in the clinical schema keyed by pseudonymous GUID, are **never sent to the public AI provider** (private MedGemma only, per the DPIA A5 control), and **never enter Tier-3 export** without the quasi-identifier generalization of §4 — high-resolution clock-time patterns narrow candidates sharply in a small elderly-cardiac cohort. The DPIA processing inventory gains a row for sub-daily ingestion.

---

## 5. Revisit triggers

Reopen the storage decision only when one of these is observed (not projected):

1. Sustained active wearers approaching **10,000**;
2. Granular working set past **~0.5–1 TB** despite retention;
3. Write-latency or autovacuum pressure that partition tuning does not fix.

Escape hatch: a Bigtable adapter behind `IGranularMetricRepository` — row key `member#metric#hour`, the same hour-vector payload. BigQuery enters only for de-identified analytics, regardless of scale.

## 6. Open questions

| Question | Owner | Unblocks |
|---|---|---|
| How far back does the Google Health API serve **intraday** (`list`) history? Daily types go ~90 days; granular depth is unverified. Check with [`tools/HealthApiProbe`](../../tools/HealthApiProbe/README.md) against a live account. | Engineering | Whether backfill-at-connect can seed granular history or only daily. |

Two questions originally listed here are **settled and built**, and now belong to the decision (§2):

- Granular AZM **is stored** — `GranularMetric.ActiveZoneMinutes` is one of the four granular series (an activity-context feature for the SSA/assessment path).
- SpO2 is stored in **60-slot hours with nulls** — one shape everywhere, as the default proposed.

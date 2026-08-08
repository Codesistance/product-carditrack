# Workload Spec Schema

The three scripts in `scripts/` all take `--workload-config <file>`. The upstream skill ships no schema or example, so this file documents the keys the scripts **actually read**, extracted from their source.

## Two important gotchas

1. **Unknown keys are silently ignored, and missing keys silently take a default.** Nothing validates your spec. If you write `size_gb` where the script reads `storage_gb`, you get a plausible-looking number computed from the default, with no warning. Always sanity-check that the SKU column in the cost output reflects the numbers you supplied.
2. **The three scripts read overlapping but different key sets.** The cost estimator wants sizing keys (`avg_instances`, `vcpus`); the CAF scorer and validator want posture booleans (`compute.multi_zone`, `security.scc_enabled`). A spec written for one gives thin results in the others — write the union.

The bundled YAML parser handles a simple subset only: nested maps by indentation, `- ` lists, and plain scalars. No anchors, multi-line strings, flow collections, or comments-after-values. Write the file UTF-8 **without a BOM** — a BOM corrupts the first key (PowerShell's `Out-File -Encoding utf8` adds one on Windows PowerShell 5.1; use `[System.IO.File]::WriteAllText` instead).

## Top level

| Key | Read by | Notes |
|-----|---------|-------|
| `name` | scorer | Label only. Defaults to `unnamed`. |
| `tier` | scorer, validator | `1` = critical. Tier 1 triggers the multi-zone/regional and RTO/RPO rules. Defaults to `2`. |
| `compute` | all three | Map. See below. |
| `data` | all three | **List** of maps, each keyed by `service`. |
| `network`, `observability`, `identity`, `security`, `operations`, `cost`, `reliability`, `performance` | scorer, validator | Maps of mostly booleans. |

## compute

`type` selects the cost model: `gce` / `compute-engine`, `gke`, `cloud-run`. **Hyphens, not underscores** — `cloud_run` matches nothing and yields zero cost lines.

| `type` | Cost keys (defaults) |
|--------|----------------------|
| `gce` | `sku` (`n2-standard-4`), `count` (1), `cud_purchased`, `preemptible_used`, `workload_type` |
| `gke` | `mode` (`autopilot`); autopilot: `pods` (100), `avg_pod_cpu` (0.25), `avg_pod_memory_gib` (0.5); standard: `node_sku` (`n2-standard-4`), `node_count` (3), `cud_purchased` |
| `cloud-run` | `avg_instances` (1), `vcpu_per_instance` (1), `memory_gib_per_instance` (0.5), `requests_millions_per_month` (1) |

Posture keys read by the scorer/validator: `multi_zone`, `multi_region`, `regional`, `autoscale`.

## data (list)

Each entry needs `service`: `cloud-sql`, `spanner`, `bigquery`, `gcs`, or `memorystore`.

| `service` | Cost keys (defaults) |
|-----------|----------------------|
| `cloud-sql` | `vcpus` (2), `memory_gb` (8), `storage_gb` (100), `ha`, `cud_purchased` |
| `spanner` | `nodes` (1), `multi_region`, `storage_gb` (50), `tier` |
| `bigquery` | `pricing_model` (`on-demand`), `storage_gb` (1000), `monthly_scan_tb` (10), `slots` (100) |
| `gcs` | `size_gb` (100) — **not** `storage_gb` — plus `storage_class` (`standard`), `avg_access_days` |
| `memorystore` | `size_gb` (4) |

Also read by the scorer: `ha`, `multi_region`, `backup_tested`, `pricing_model`, `monthly_scan_tb`.

Validator-only keys, read per entry rather than from a posture section:

| Key | Applies to | Finding when true |
|-----|-----------|-------------------|
| `public_ip` | any `data[]` entry | WS014 — public IP on the service |
| `public_access`, `all_users_access` | `gcs` entries only | WS017 — bucket open to allUsers |

## Posture sections

Booleans unless noted. The scorer awards points for `true`; absent counts as failed.

- **operations** — `iac`, `ci_cd`, `staging_env`, `health_probes`, `runbooks`, `on_call_defined`, `gradual_rollout`, `traffic_split`, `retry_policies`, `org_policies`, `blameless_pir`, `chaos_last_run`
- **security** — `encryption_at_rest`, `secret_manager`, `min_tls_1_2`, `mfa_enforced`, `scc_enabled`, `scc_premium`
- **identity** — `workload_identity`, `service_account_keys` (having keys is the *finding* — aim for `false`), `uses_basic_roles` (likewise)
- **network** — `vpc` (name string), `private_service_connect`, `firewall_least_privilege`, plus validator-only `default_vpc_in_use`. Public-exposure keys are **not** read here — they live on the `data[]` entries above
- **reliability** — `rto_minutes`, `rpo_minutes` (numbers)
- **observability** — `cloud_logging` + `ingest_gb_per_day` (default 1) drive the cost line; `slo_alerts`, `audit_log_export` drive the score
- **cost** — `right_sized`, `cuds_purchased`, `spot_used`, `storage_tiered`, `budgets_configured`, `log_retention_tuned`, `devtest_schedule`, `orphans_cleaned`
- **performance** — `slos_defined`, `load_tested`, `caching`, `cdn_assets`, `db_optimized`, `data_partitioned`, `async_io`, `profiler`, `payload_reviewed`

## network (cost)

`egress_gb_per_month` (default 100) drives the internet-egress line; omit the key entirely and no egress line is emitted.

## Example

See [example-workload.yaml](example-workload.yaml) — a spec exercising every cost path, verified to parse and produce non-default output.

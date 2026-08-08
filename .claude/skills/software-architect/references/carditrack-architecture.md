# CardiTrack — verified architecture reference

Every claim here was read from the repository, not inferred. Each section ends with the command that re-verifies it, because this file will drift and a stale architecture map is worse than none.

**Solution:** `CardiTrack.sln` (legacy format) · `CardiTrack.Server.slnf` (server-only filter: excludes MAUI so the API/Web/Worker build without mobile workloads).
**Target framework:** `net10.0` everywhere except MAUI, which multi-targets per OS.

---

## 1. Project graph

```
src/
├── Core/
│   ├── CardiTrack.Domain              → (nothing)
│   └── CardiTrack.Application         → Domain
├── Infrastructure/
│   ├── CardiTrack.Shared              → (nothing)
│   ├── CardiTrack.Observability       → Shared
│   ├── CardiTrack.Infrastructure      → Application, Domain, Shared
│   └── MedGemma/                      → not a .NET project — a Dockerfile for the
│                                         remotely-hosted model image
├── Presentation/
│   ├── CardiTrack.API                 → Application, Infrastructure, Observability, Shared
│   ├── CardiTrack.Web                 → Application, Infrastructure, Observability, Shared
│   ├── CardiTrack.Mobile.Core         → Application, Shared
│   └── CardiTrack.Mobile              → Application, Mobile.Core
└── Worker/
    └── CardiTrack.Worker              → Application, Infrastructure, Observability, Shared

tests/
├── CardiTrack.UnitTests               → Domain, Application, Infrastructure, Observability, Mobile.Core, Web
├── CardiTrack.IntegrationTests        → API, Infrastructure
└── CardiTrack.E2ETests                → Web
```

**Re-verify:**
```bash
for f in $(find src tests -name "*.csproj" | sort); do
  echo "--- $f"
  grep -o 'ProjectReference Include="[^"]*"' "$f"
done
```

### Reading the Presentation → Infrastructure edge

`API`, `Web`, and `Worker` each reference `Infrastructure` directly. Under a strict reading of Clean Architecture that is a violation; here it is the standard **composition root** allowance — the host must see concrete implementations in order to register them in DI. It stays legitimate only while:

- the reference is consumed in `Program.cs` and the `Extensions/` registration files, and
- no controller, Blazor component, or worker names an Infrastructure type in a constructor or field.

The second condition is what the conformance check probes. `Mobile` and `Mobile.Core` have no Infrastructure reference at all and must keep it that way — mobile reaches the server over HTTP.

---

## 2. Per-project contents and responsibilities

### `src/Core/CardiTrack.Domain`

`Common/` · `Entities/` · `Enums/` · `Extensions/` · `Interfaces/`

Entities: `ActivityLog`, `ActivityLogMerge`, `Alert`, `AuditLog`, `CardiMember`, `Device`, `DeviceActivityLog`, `DeviceConnection`, `Organization`, `PatternBaseline`, `Subscription`, `User`, `UserCardiMember`.

`Interfaces/` contains exactly two entity contracts — `IEntity`, `ISoftDeletable`. It is **not** the general-purpose interface bucket; repository and client abstractions live in `Application`.

> **Invariant — zero packages.** `CardiTrack.Domain.csproj` has no `PackageReference` and no `ProjectReference`. It cannot see EF Core, `HttpClient`, or configuration even by accident. Entity names here are canonical and match [entity_summary.md](../../../../docs/technical/entity_summary.md).

### `src/Core/CardiTrack.Application`

`DTOs/` · `Exceptions/` · `Interfaces/{Clients,Repositories,Security,Services}` · `Services/`

Services: `ActivityLogAggregationService`, `BaselineCalculator`, `BaselineProgress`, `CardiMemberAccessService`, `CardiMemberService`, `DashboardService`, `OnboardingService`, `OrganizationService`, `SubscriptionService`, `UserService`.

This is the **port layer**. Anything the core needs but cannot implement itself gets an interface under `Interfaces/` and an implementation in `Infrastructure`.

> **Invariant — zero packages.** Also no `PackageReference`. This is what lets `CardiTrack.UnitTests` exercise business logic with no host, no database, and no container.
>
> **Known leftover:** `Class1.cs` is template residue and should be deleted.

**Re-verify both invariants:**
```bash
grep -r "PackageReference Include=" src/Core/   # expect: no matches
```

### `src/Infrastructure/CardiTrack.Shared`

`ConfigurationKeys.cs`, `ConfigurationLoader.cs`, `Json/`. A dependency-free leaf, referenced by nearly everything — which makes it the easiest place to dump anything awkward. Constants and pure helpers only. If a candidate for `Shared` needs `Application` or `Domain`, it is misfiled.

### `src/Infrastructure/CardiTrack.Observability`

APM and Serilog wiring (`ApmExtensions`, `DeploymentInfo`). Depends only on `Shared`, so it can be referenced by every host without dragging the core along. Engine selection is via `Apm:Engine`; see [apm_setup_runbook.md](../../../../docs/technical/apm_setup_runbook.md).

### `src/Infrastructure/CardiTrack.Infrastructure`

`Extensions/` · `ExternalClients/` · `Migrations/` · `Persistence/` · `Repositories/` · `Security/` · `Services/` · `Settings/`

- `Persistence/` — `CardiTrackDbContext`, `CardiTrackDbContextFactory` (design-time factory used for migrations), `Configurations/` for `IEntityTypeConfiguration<T>`.
- `Repositories/` — `Repository.cs` (generic base), `UnitOfWork.cs`, plus one per aggregate: `ActivityLog`, `Alert`, `AuditLog`, `CardiMember`, `DeviceActivityLog`, `DeviceConnection`, `Device`, `Organization`, `PatternBaseline`, `Subscription`, `UserCardiMember`, `User`.
- `Extensions/` — the DI registration surface the hosts call.

Everything here implements an interface declared in `Application`. A public Infrastructure type with no corresponding Application interface is a design smell: either it is genuinely host-internal, or the port is missing.

### `src/Presentation/CardiTrack.API`

`Controllers/` · `Extensions/` · `Infrastructure/` · `Middleware/` · `Validators/` · `Program.cs` · `Dockerfile` · `Dockerfile.migrate`

MVC controllers (not minimal APIs). `Dockerfile.migrate` is a separate image that applies EF migrations — schema changes ship through it, not through app startup. Endpoint contracts are specified in [docs/execution/backend/api/](../../../../docs/execution/backend/api/readme.md); the spec is canonical, so a controller that diverges from it is a bug in the controller.

### `src/Presentation/CardiTrack.Web`

Blazor. `Components/`, `wwwroot/`. Pages are **full-bleed** — edge-to-edge backgrounds, safe-area insets for system UI only, no page-level rounded cards or clipped chrome. Corner radius belongs on components. This is binding, from [CLAUDE.md](../../../../CLAUDE.md).

### `src/Presentation/CardiTrack.Mobile{,.Core}`

MAUI. `Mobile.Core` holds the testable half (referenced by `CardiTrack.UnitTests`); `Mobile` holds platform and UI code. Neither references `Infrastructure` — the mobile app is an API client. The same full-bleed rule applies.

### `src/Worker/CardiTrack.Worker`

`CronBackgroundService.cs` · `WorkerOptions.cs` · `WorkerServiceExtensions.cs` · `Workers/` · `Program.cs` · `Dockerfile`

Existing jobs: `BaselineCalculationWorker`, `OrphanedOrganizationCleanupWorker`, `WearableSyncWorker`.

Per [CLAUDE.md](../../../../CLAUDE.md), this project is the **only** permitted home for non-AI background jobs and any DB polling. `CronBackgroundService`, `WorkerOptions`, and `WorkerServiceExtensions` are Worker-local by rule — they are not shared infrastructure and must not be promoted to `Shared` or `Infrastructure` so another host can schedule work. See [docs/apps/worker/readme.md](../../../../docs/apps/worker/readme.md).

### AI: `src/Infrastructure/MedGemma` and the AI clients

`src/Infrastructure/MedGemma/` contains a **`Dockerfile` only** — it builds the image for the remotely-hosted model. It is not a .NET project and has no `.csproj`.

The AI ingestion/inference **pipeline** — webhook aggregation, SSA-LSTM pre-processing, severity routing, digests — **runs on GCP** (Pub/Sub + Cloud Run) per [llm_design.md](../../../../docs/llm_design.md). It is the only sanctioned exception to the Worker rule, and it must not host non-AI jobs.

What *is* in-process is the outbound adapter: `Infrastructure/ExternalClients/Medical/MedGemmaClient.cs` and `Infrastructure/ExternalClients/General/GeminiClient.cs` implement `IExternalAiClient` (declared in `Application/Interfaces/Clients`) and call a remote Ollama/Gemini endpoint via `IHttpClientFactory`, wired up in `Infrastructure/Extensions/AiServiceExtensions.cs`. That is a port-and-adapter, and it is correct. Do not mistake it for the pipeline.

### `tests/`

| Project | Scope |
|---|---|
| `CardiTrack.UnitTests` | Domain + Application logic, Observability, Mobile.Core, Web components. No host required. |
| `CardiTrack.IntegrationTests` | API + Infrastructure against real dependencies. |
| `CardiTrack.E2ETests` | Web, end to end. |

---

## 3. Conventions in force

| Decision | Evidence | Consequence for new code |
|---|---|---|
| Repository + Unit of Work | `Infrastructure/Repositories/{Repository,UnitOfWork}.cs`, ports in `Application/Interfaces/Repositories` | New aggregate → new repository interface + implementation. Never inject `CardiTrackDbContext` above Infrastructure. |
| MVC controllers | `API/Controllers`, `API/Validators` | New endpoint → controller action + validator. |
| Application services, no mediator | `Application/Services`; **no MediatR/Mediator package anywhere in `src/`** | Do not add a handler pipeline for a single feature. |
| Layer-grouped solution folders | `src/{Core,Infrastructure,Presentation,Worker}` | A new project goes in the folder matching its layer, and is added to `CardiTrack.sln` (plus `CardiTrack.Server.slnf` if server-side). |
| Migrations ship in their own image | `API/Dockerfile.migrate` | Schema changes are a deploy step, not a startup side effect. |

**Re-verify the mediator claim:**
```bash
grep -rE "MediatR|IRequestHandler|Mediator\.Abstractions" src/   # expect: no matches
```

---

## 4. Known structural gaps

Real, worth fixing, each a standalone PR — do not fold these into an unrelated feature branch.

| Gap | Effect | Fix |
|---|---|---|
| No `Directory.Build.props` | `TargetFramework`, `Nullable`, `ImplicitUsings` repeated in every `.csproj` | One props file at the root; delete the duplicated properties. MAUI keeps its conditional `TargetFrameworks`. |
| No `Directory.Packages.props` | Package versions are per-project; drift is possible and invisible | Enable `ManagePackageVersionsCentrally`, move versions up, strip `Version=` from `PackageReference`. |
| No `global.json` | SDK version unpinned across dev, CI, and the devcontainer | Pin with `rollForward: latestFeature`. |
| Legacy `.sln` | Merge conflicts on every project add | Migrate to `.slnx`; keep the `.slnf` filter working. |
| `Application/Class1.cs` | Template residue | Delete. |

**Re-verify:**
```bash
ls Directory.Build.props Directory.Packages.props global.json 2>/dev/null   # expect: none present
```

---

## 5. Fast facts for answering placement questions

- Ports (repository/client/service/security interfaces) → `Application/Interfaces/**`. Never `Domain/Interfaces`.
- Anything touching EF Core, HTTP, GCP SDKs, secrets, or hashing → `Infrastructure`, behind an Application interface.
- Scheduled or polling non-AI work → `Worker/Workers`. No exceptions.
- AI inference and its pre-processing → GCP, per `docs/llm_design.md`.
- Config key names → `Shared/ConfigurationKeys.cs`.
- A cross-cutting helper that needs `Application` or `Domain` → not `Shared`.
- Domain entities never cross a transport boundary — project to a DTO in `Application/DTOs`.

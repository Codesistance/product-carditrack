# Architecture conformance check

Verifies that CardiTrack still matches the architecture it claims to have. Architectures rot through small, individually-reasonable changes — a Domain project that gains a package, a job that lands in the API because the DbContext was already there, a controller that takes a concrete repository.

Run this when asked to "check the architecture", after a large feature lands, before a release, or when onboarding to an unfamiliar area.

**Baseline:** Clean Architecture as mapped in [carditrack-architecture.md](carditrack-architecture.md), plus the binding rules in [CLAUDE.md](../../../../CLAUDE.md). Never infer the baseline — if a new project appears that the map does not cover, ask before judging it.

There is **no Roslyn MCP server configured** in this repo, so these checks use project references, package references, and namespace probes. Run them in order: the cheapest checks catch the violations that make every other violation possible.

---

## Step 1 — Dependency direction (catches the most)

```bash
for f in $(find src -name "*.csproj" | sort); do
  echo "--- $f"
  grep -o 'ProjectReference Include="[^"]*"' "$f"
done
```

Compare against the allowed arrows:

| Project | Allowed references |
|---|---|
| `CardiTrack.Domain` | *(none)* |
| `CardiTrack.Application` | Domain |
| `CardiTrack.Shared` | *(none)* |
| `CardiTrack.Observability` | Shared |
| `CardiTrack.Infrastructure` | Application, Domain, Shared |
| `CardiTrack.API` / `.Web` / `.Worker` | Application, Infrastructure, Observability, Shared |
| `CardiTrack.Mobile.Core` | Application, Shared |
| `CardiTrack.Mobile` | Application, Mobile.Core |

Anything else is 🔴 **Critical** — a single wrong arrow legitimises every downstream leak. Two specific ones to look for: `Domain` or `Application` gaining any reference at all, and `Mobile*` gaining `Infrastructure` (mobile is an HTTP client, not a data-access host).

## Step 2 — The zero-package invariant

```bash
grep -r "PackageReference Include=" src/Core/
```

**Expected: no matches.** Any hit is 🔴 **Critical** — the core stops being framework-independent and the unit test project starts needing a host. Fix: declare an interface in `Application/Interfaces/**` and implement it in `Infrastructure`.

## Step 3 — Background jobs outside the Worker

```bash
grep -rn "BackgroundService\|IHostedService\|PeriodicTimer" src/ --include=*.cs
```

Every hit must be under `src/Worker/CardiTrack.Worker/`. A hit in `API`, `Web`, `Infrastructure`, or `Mobile` is 🔴 **Critical** — [CLAUDE.md](../../../../CLAUDE.md) makes the Worker the exclusive host for non-AI background jobs and all DB polling. Fix: move the class to `src/Worker/CardiTrack.Worker/Workers/` and register it with the existing scheduling helpers.

Also confirm the scheduling primitives have not been promoted out of the Worker:

```bash
grep -rn "CronBackgroundService\|WorkerOptions\|WorkerServiceExtensions" src/ --include=*.cs
```

These are Worker-local by rule. A copy or a move into `Shared`/`Infrastructure` so another host can schedule work is 🔴 **Critical**, and the real fix is to move the *job*, not the scheduler.

## Step 4 — AI work in the wrong place

The AI ingestion/inference **pipeline** — webhook aggregation, SSA pre-processing (Math.NET), severity routing, digest generation — runs on GCP (Pub/Sub + Cloud Run) per [llm_design.md](../../../../docs/llm_design.md). Calling out to a model over HTTP is not the pipeline.

```bash
grep -rln "MedGemma\|Gemini\|IExternalAiClient" src/ --include=*.cs
```

**Known good, do not report:** `Infrastructure/ExternalClients/Medical/MedGemmaClient.cs` and `Infrastructure/ExternalClients/General/GeminiClient.cs` implement the `IExternalAiClient` port from `Application/Interfaces/Clients` and call a remote Ollama/Gemini endpoint over `IHttpClientFactory`. They are adapters — exactly where an adapter belongs. `src/Infrastructure/MedGemma/` is a `Dockerfile` for the remotely-hosted model image, not a .NET project.

🔴 **Critical** is *pipeline stages* implemented in-process: aggregation windows, SSA pre-processing, severity routing, or digest assembly running inside the API, Web, or Worker. Conversely, the GCP pipeline must not pick up non-AI work — a Pub/Sub subscriber doing token refresh or cleanup belongs in `CardiTrack.Worker`.

## Step 5 — Composition-root leaks

`API`, `Web`, and `Worker` legitimately reference `Infrastructure` so they can register implementations in DI. That allowance ends at `Program.cs` and the `Extensions/` registration files.

```bash
grep -rn "using CardiTrack.Infrastructure" src/Presentation/ src/Worker/ --include=*.cs
```

Hits in `Program.cs` or `*/Extensions/*.cs` are expected — at the time of writing that is exactly `API/Program.cs`, `Web/Program.cs`, `Worker/Program.cs`, and `API/Extensions/ServiceCollectionExtensions.cs`. A hit in a controller, Blazor component, middleware, or worker class is 🟠 **Major** — the host is bound to a concrete implementation. Fix: depend on the `Application` interface instead.

The sharpest version of the same check:

```bash
grep -rn "CardiTrackDbContext" src/Core/ src/Presentation/ src/Worker/ --include=*.cs
```

**Expected: `src/Core/` clean, and hits in the hosts confined to `Program.cs`** — `AddDbContext<CardiTrackDbContext>` / `AddDbContextFactory<CardiTrackDbContext>` is composition-root registration and is fine. A DbContext *injected into* a controller, component, worker, or any Application-layer service is 🟠 **Major**: this solution reads and writes through repositories and `UnitOfWork`. Any hit under `src/Core/` is 🔴 **Critical** — the core cannot see EF Core at all.

## Step 6 — Domain entities crossing the wire

```bash
grep -rn "using CardiTrack.Domain.Entities" src/Presentation/ --include=*.cs
```

A controller action or Blazor component **returning** `User`, `CardiMember`, or `Alert` over the wire is 🟠 **Major** — it couples the transport contract to the persistence model and risks leaking identifier/clinical fields that the schema split deliberately separates ([data_protection_architecture.md](../../../../docs/technical/data_protection_architecture.md)). Fix: project to a DTO in `Application/DTOs`, matching the contract in [docs/execution/backend/api/](../../../../docs/execution/backend/api/readme.md).

**Known good, do not report:** `API/Middleware/AuditLoggingMiddleware.cs` constructs an `AuditLog` entity and persists it through `IAuditLogRepository`. It writes an entity rather than serialising one, and request-scoped auditing is a deliberate choice documented in the file — a `SaveChanges` interceptor would miss every read, which is most of health-data access. The check flags entity *use* in Presentation; only egress is a finding.

## Step 7 — `Shared` as a junk drawer

```bash
grep -o 'ProjectReference Include="[^"]*"' src/Infrastructure/CardiTrack.Shared/CardiTrack.Shared.csproj
```

**Expected: no matches** — `Shared` is a leaf. If it has gained a reference to `Domain` or `Application`, that is 🔴 **Critical** (it inverts the graph for every host that depends on it). If it has gained I/O, business rules, or anything beyond configuration keys and JSON helpers, that is 🟠 **Major**: the code belongs in `Application` (if it is policy) or `Infrastructure` (if it does I/O).

## Step 8 — Ports in the wrong project

```bash
ls src/Core/CardiTrack.Domain/Interfaces/
```

**Expected: `IEntity.cs`, `ISoftDeletable.cs`** and little else. Repository, client, security, and service abstractions belong in `Application/Interfaces/**`. A new `IWhateverRepository.cs` in `Domain/Interfaces` is 🟡 **Minor** but worth fixing immediately — it is how the two interface locations start to blur.

## Step 9 — Competing patterns

```bash
grep -rE "MediatR|IRequestHandler|Mediator\.Abstractions" src/
```

**Expected: no matches.** This solution uses Application services, not a mediator pipeline. Introducing one for a single feature is 🟠 **Major**: two orchestration styles in one codebase is worse than either alone. If the case for a mediator is real, it is an ADR and a migration, not a feature branch.

## Step 10 — Test coverage of the boundary

```bash
grep -o 'ProjectReference Include="[^"]*"' tests/CardiTrack.UnitTests/CardiTrack.UnitTests.csproj
```

`CardiTrack.UnitTests` should reference `Domain` and `Application` — the zero-package invariant is what makes host-free unit testing possible, and unused capacity there usually means logic has drifted into Infrastructure. Business logic reachable only through `IntegrationTests` is 🟡 **Minor** and a hint that a rule is sitting in the wrong layer.

---

## Reporting

Group findings by severity, most severe first. Each finding needs three things:

```
🔴 Critical — src/Presentation/CardiTrack.API/Services/TokenRefreshService.cs:14
   `BackgroundService` hosted in the API. CLAUDE.md reserves all non-AI background
   jobs and DB polling for CardiTrack.Worker.
   Fix: move to src/Worker/CardiTrack.Worker/Workers/TokenRefreshWorker.cs and
   register via WorkerServiceExtensions.
```

Then state the scope explicitly — **"Steps 1–10 clean across `src/` and `tests/`; no other violations found"** — so a short report is not mistaken for a shallow one. If you skipped a step, say which and why.

| Severity | Meaning |
|---|---|
| 🔴 Critical | Wrong-direction reference, package in `src/Core`, non-AI job outside the Worker, `Shared` inversion. Blocks merge. |
| 🟠 Major | Concrete Infrastructure type outside a composition root, entity crossing the wire, competing orchestration pattern. |
| 🟡 Minor | Misplaced file inside the right project, port in the wrong interface folder, naming drift. |

Structural gaps that are already known and accepted — no `Directory.Build.props`, no central package management, no `global.json`, legacy `.sln`, the `Class1.cs` leftover — are listed in [carditrack-architecture.md § 4](carditrack-architecture.md#4-known-structural-gaps). Mention them once as standing debt; do not re-report them as new findings on every run.

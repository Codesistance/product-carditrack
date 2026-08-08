# Choosing an architecture

Adapted from the [dotnet-claude-kit](https://github.com/codewithmukesh/dotnet-claude-kit) architecture advisor.

**Use this only for genuinely new scope** — a new service, a new deployable, or a serious restructure proposal. For a change inside the existing solution, the answer is already decided: Clean Architecture, as mapped in [carditrack-architecture.md](carditrack-architecture.md). Re-running the questionnaire on a routine feature is theatre.

## Core principles

1. **Ask before recommending.** Never prescribe an architecture without understanding the domain, team, lifetime, and constraints. Run the questionnaire first.
2. **Right-size it.** The best architecture is the simplest one that handles the actual complexity. CRUD apps do not need DDD. Two developers do not need microservices. Match real requirements, not aspirations.
3. **Architecture is not permanent.** Every choice has an evolution path. Start simple, add structure when complexity demands it, and write down the trigger so the team knows when to move.
4. **Four candidates.** Vertical Slice (VSA), Clean Architecture (CA), DDD + Clean Architecture, Modular Monolith. Each has real strengths and a real cost.

---

## The questionnaire

Ask across these six categories. Skip questions that plainly do not apply — but do not skip a whole category because you have a hunch.

### 1. Domain complexity

| # | Question | Low signal | High signal |
|---|---|---|---|
| 1 | How many distinct business entities? | < 10 | 20+ with rich relationships |
| 2 | Do business rules span multiple entities? | Per-entity CRUD | Complex invariants across entity groups |
| 3 | Are there multi-step workflows? | Request → response | Sagas, approval chains, state machines |
| 4 | Do domain experts use specialised vocabulary? | Generic (create, update) | Ubiquitous language (underwrite, adjudicate, *baseline*, *severity routing*) |

### 2. Team & organisation

| # | Question | Low signal | High signal |
|---|---|---|---|
| 5 | Team size? | 1–3 developers | 8+, multiple teams |
| 6 | Do different teams own different parts? | One team owns everything | Teams aligned to business domains |
| 7 | Team experience with .NET? | Junior or mixed | Senior, pattern-fluent |

### 3. Lifetime & scale

| # | Question | Low signal | High signal |
|---|---|---|---|
| 8 | Expected lifetime? | < 2 years, MVP | 5+ years, long-lived product |
| 9 | Concurrent load? | < 100 RPS | 1000+ RPS, spiky |
| 10 | Independent scaling by feature area? | Uniform load | Hot spots needing separate scaling |

### 4. Regulatory & compliance

| # | Question | Low signal | High signal |
|---|---|---|---|
| 11 | Audit trail or compliance requirements? | Basic logging | Full audit trail, HIPAA/GDPR/SOX/PCI |
| 12 | Different security boundaries within the system? | Single auth boundary | Multi-tenant, data isolation |

### 5. Existing codebase

| # | Question | Low signal | High signal |
|---|---|---|---|
| 13 | Greenfield or brownfield? | Starting fresh | Migrating from legacy |
| 14 | Established architectural patterns? | None | Strong conventions already in place |

### 6. Integration complexity

| # | Question | Low signal | High signal |
|---|---|---|---|
| 15 | How many external systems? | 0–2 simple APIs | 5+ with complex contracts |
| 16 | Event-driven or async needs? | Synchronous | Event sourcing, pub/sub, eventual consistency |

---

## Decision matrix

| Profile | Recommendation | Why |
|---|---|---|
| Low complexity, small team, short lifetime | **Vertical Slice** | Minimal ceremony, fast delivery, easy to read |
| Low–medium complexity, API-focused, any team size | **Vertical Slice** | Feature cohesion; one file per operation |
| Medium complexity, medium team, long lifetime | **Clean Architecture** | Boundaries enforced by project references; testable core |
| High complexity, specialised vocabulary, hard invariants | **DDD + Clean** | Aggregates protect invariants; value objects model concepts; domain events decouple effects |
| Multiple bounded contexts, team-per-domain | **Modular Monolith** | Module isolation with an extraction path |
| Brownfield N-tier needing modernisation | **Clean Architecture** | Familiar migration; fixes dependency direction without a rewrite |
| Compliance-heavy | **Clean or DDD + Clean** | Regulation rewards enforced boundaries and auditable seams |

### When signals conflict

1. **Default to simpler.** In doubt, start with VSA and evolve.
2. **Domain complexity wins** over team size and lifetime.
3. **Team familiarity counts.** A team fluent in CA ships faster in CA than while learning VSA.
4. **Compliance drives structure.** Regulatory requirements usually force the stricter option.

### CardiTrack thumbs on the scale

- **Compliance is live, not hypothetical.** HIPAA/GDPR, the identifier/clinical schema split, per-metric consent, and audit requirements ([data_protection_architecture.md](../../../../docs/technical/data_protection_architecture.md), [dpia.md](../../../../docs/compliance/dpia.md)) push toward enforced boundaries — question 11 is a high signal by default.
- **Team is small.** Anything justified by "team-per-domain" does not apply yet.
- **One split already exists.** The AI pipeline runs on GCP for latency and cost reasons ([llm_design.md](../../../../docs/llm_design.md)). A second deployable split needs its own evidence; symmetry is not evidence.
- **A new service must still obey CLAUDE.md.** If it would host non-AI background jobs, it is not a new service — that work belongs in `CardiTrack.Worker`.

---

## The four architectures

### Vertical Slice (VSA)

Organise by feature, not by layer. Each operation is self-contained.

```
src/MyApp.Api/
  Features/
    Orders/CreateOrder.cs      # request + handler + response + endpoint
    Orders/GetOrder.cs
  Common/
    Behaviors/ValidationBehavior.cs
    Persistence/AppDbContext.cs
```

**Best for:** CRUD-heavy APIs, MVPs, small–medium teams, short–medium lifetime.
**Cost:** weak enforcement — nothing stops a slice reaching into a sibling except discipline.

### Clean Architecture (CA) — *CardiTrack's baseline*

Concentric layers with dependency inversion; domain at the centre, infrastructure at the edge.

```
src/
  MyApp.Domain/           # entities, entity contracts
  MyApp.Application/      # use cases, DTOs, ports
  MyApp.Infrastructure/   # EF Core, external services
  MyApp.Api/              # endpoints, middleware
```

**Best for:** medium complexity, long-lived systems, compliance pressure, teams comfortable with layers.
**Cost:** more projects and more indirection per feature; a port + adapter for things that could have been a method call.

### DDD + Clean Architecture

CA plus tactical DDD: aggregates, value objects, domain events.

**Best for:** complex domains with specialised vocabulary and strict invariants, experienced teams.
**Cost:** high. Aggregate design is genuinely hard and a bad aggregate boundary is expensive to undo.

### Modular Monolith

Independent modules in one deployable, each free to use its own internal architecture.

```
src/
  MyApp.Host/             # wires modules
  Modules/Orders/         # own features, own DbContext
  Modules/Catalog/
  MyApp.Shared/           # integration event contracts only
```

**Best for:** multiple bounded contexts, team-per-domain, plausible future extraction.
**Cost:** cross-module communication becomes a design problem; shared data gets harder on purpose.

---

## Evolution paths

| From | To | Trigger | How |
|---|---|---|---|
| VSA | CA | Domain logic outgrowing handlers | Extract Domain + Application; keep features as use cases |
| VSA | Modular Monolith | Distinct bounded contexts emerging | Group features into modules, then enforce the boundary |
| CA | DDD + CA | Invariants sprawling; primitive obsession | Introduce aggregates, value objects, domain events |
| Monolith | Modular Monolith | Teams colliding; shared-DB coupling | Split into modules with their own DbContexts/schemas |
| Modular Monolith | Microservices | Genuine independent scaling or deployment need | Extract modules into separate deployables |

State the trigger as something **observable** — "when a second team owns alerting", not "when it gets complex".

---

## Anti-patterns

**Clean Architecture for a CRUD app.** Four projects and six files to insert a row.

```
# Overkill for simple CRUD
src/MyApp.Domain/Entities/Product.cs
src/MyApp.Application/Products/CreateProduct/{Command,Handler,Validator}.cs
src/MyApp.Infrastructure/Persistence/ProductRepository.cs
src/MyApp.Api/Endpoints/ProductEndpoints.cs

# VSA equivalent
src/MyApp.Api/Features/Products/CreateProduct.cs
```

**DDD everywhere.** Aggregates and value objects for a settings table.

```csharp
// Overkill
public class UserSettings : AggregateRoot
{
    public ThemeName Theme { get; private set; }          // a value object for "dark"/"light"?
    public void ChangeTheme(ThemeName theme) { /* domain event? */ }
}

// Right-sized
public class UserSettings
{
    public Guid UserId { get; init; }
    public string Theme { get; set; } = "light";
}
```

**Premature microservices.** Five services, two developers, day one. Start as a modular monolith and extract on evidence.

**Skipping the questionnaire.** "I always use Clean Architecture" — then scaffolding four projects for a todo app. Ask first, recommend second.

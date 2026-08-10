# CardiTrack Documentation

Welcome to the CardiTrack documentation. This directory contains comprehensive documentation for the entire CardiTrack platform.

## 📚 Documentation Structure

### Core Documentation

#### [solution_manifest.md](./solution_manifest.md)
**The complete solution overview and product vision.**
- Executive summary and business model
- Technical architecture overview
- Core features and capabilities
- Pricing tiers and unit economics
- Development roadmap and milestones
- Team requirements and success criteria

**Start here** if you're new to CardiTrack or need a comprehensive overview.

#### [architecture_c4.md](./architecture_c4.md)
**The C4 architecture — Context, Containers, Components.**
- The system as actually built and deployed, diagrammed at three C4 levels
- The two binding placement rules (Worker vs AI pipeline) and the dependency rule
- Updated in the same PR as any architectural change

#### [release_matrix.md](./release_matrix.md)
**The canonical release plan.**
- Single feature × platform × release × plan-gate matrix
- Resolves sequencing across the manifest, UI specs, and API priorities
- All other docs defer to this matrix for what ships when

#### [market_analysis.md](./market_analysis.md)
**Comprehensive competitive analysis and market positioning.**
- Market size and growth projections
- Target customer segments
- Detailed competitor analysis with feature comparisons
- Value-added features vs each competitor
- Market positioning strategy
- Go-to-market strategy and risks

**Read this** to understand the competitive landscape and CardiTrack's market position.

#### [llm_design.md](./llm_design.md)
**AI pipeline design on GCP (Pub/Sub + Cloud Run).**
- MedGemma (Ollama on Cloud Run) as the medical model + Gemini as the general model
- Google Health API ingestion feeding the pipeline
- Pre-processing, prompt structure, and severity routing
- Predictive monitoring, cost estimates, and caveats

#### [infrastructure.md](./infrastructure.md)
**Complete infrastructure and database documentation.**
- Cloud SQL PostgreSQL 16 as the system of record
- Database schema and entity relationships
- Entity Framework Core setup and migrations (via the migrator Cloud Run Job)
- Security and encryption (AES-256-GCM, Secret Manager)
- GCP resources: Cloud Run, Cloud SQL, GCS, Pub/Sub, optional LB/Cloud Armor
- Terraform configuration and deployment (common/dev/prod stacks)
- CI/CD pipeline and monitoring
- Scaling strategy and disaster recovery

#### [google_credits_pitch.md](./google_credits_pitch.md)
**Google for Startups Cloud credits application** — company overview, problem/solution narrative, and GCP usage plan for the credits programme.

**Reference this** for infrastructure setup, deployment, and database operations.

---

### API Specification (canonical)

Located in [`/execution/backend/api/`](./execution/backend/api/readme.md) — the **source of truth for all REST endpoints** (`/api/v1/*`), organized by domain (auth, cardimembers, devices, health-data, alerts, family, notifications, subscriptions, reports). The app-level READMEs below link here and do not duplicate endpoint documentation.

### UI Specifications

Located in `/execution/ui/`:
- [Mobile screen specs](./execution/ui/mobile/ui_screens_maui_mobile.md) and [mobile user stories](./execution/ui/mobile/user_stories.md) (.NET MAUI) — MVP 1 extracts live in [`/execution/ui/mobile/mvp1/`](./execution/ui/mobile/mvp1/screens.md)
- [Web screen specs](./execution/ui/web/ui_screens_blazor_web.md) and [web user stories](./execution/ui/web/user_stories.md) (Blazor Server)

---

### Application Documentation

Located in `/apps/` — each application has its own README covering stack, structure, configuration, and local development.

#### [apps/api/](./apps/api/readme.md)
**ASP.NET Core Web API** — stack, project structure, middleware, configuration, running locally. Endpoint documentation lives in [`/execution/backend/api/`](./execution/backend/api/readme.md).

#### [apps/web/](./apps/web/readme.md)
**Blazor Web App** — current state (template shell, health-data disclosure banner, privacy page, APM/DataProtection wiring), planned dashboard features, running locally, deployment.

#### [apps/mobile/](./apps/mobile/readme.md)
**.NET MAUI Mobile App** — cross-platform architecture (iOS, Android), code-behind pages + Mobile.Core (Auth0 native login, API client), onboarding flow, APM/crash reporting, store publishing; planned platform integrations (HealthKit, Health Connect, push, offline).

#### [apps/mobile/store_provisioning.md](./apps/mobile/store_provisioning.md)
**Store provisioning** — one-time keys, certificates, and Secret Manager secrets that let CI deliver signed builds to TestFlight and the Google Play internal testing track.

#### [apps/worker/](./apps/worker/readme.md)
**CardiTrack.Worker Background Service** — the .NET Worker Service hosting **non-AI background jobs** (10-minute wearable data sync with in-path OAuth token refresh, daily orphaned-organization cleanup and baseline calculation, weekly device-sync audit) using cron scheduling via Cronos. The AI ingestion/inference pipeline is designed to run on GCP (Pub/Sub + Cloud Run) — see [llm_design.md](./llm_design.md).

---

### Technical Reference

Located in `/technical/` — detailed technical guides and specifications.

#### [auth0_integration.md](./technical/auth0_integration.md)
Complete guide to Auth0 authentication integration, OAuth flows, and security configuration.

#### [auth0_setup_runbook.md](./technical/auth0_setup_runbook.md)
Operator runbook for configuring the Auth0 tenant per environment (dev, prod) to match the implemented mobile auth and the API's JWT validation.

#### [apm_setup_runbook.md](./technical/apm_setup_runbook.md)
**Observability/APM** — Serilog + OpenTelemetry with a switchable APM engine (`Apm:Engine`; Datadog deployed, console-only when unset locally); token provisioning and setup steps.

#### [oauth_clients.md](./technical/oauth_clients.md)
Inventory of every OAuth client (identity vs device-data), social log-on scope, and provisioning steps for the Auth0 and Google Health API clients.

#### [entity_summary.md](./technical/entity_summary.md)
Detailed summary of all domain entities, their properties, and relationships.

#### [data_sync_architecture.md](./technical/data_sync_architecture.md)
**Data sync & data pull allocation view** — which component runs on which node, over which technology, at which cadence: the 10-minute Worker poll, per-connection due-ness, the trailing window, manual sync, the weekly audit pull, and the R2 webhook pipeline.

#### [data_protection_architecture.md](./technical/data_protection_architecture.md)
HIPAA/GDPR data architecture (ADR): identifier/clinical schema separation, Safe Harbor de-identification pipeline, retention & erasure jobs, audit/consent models, and the subprocessor register.

#### [granular_timeseries_storage.md](./technical/granular_timeseries_storage.md)
**Granular time-series storage (ADR)** — sub-daily wearable samples stay in the existing Cloud SQL instance as day-partitioned hour-vector tables (no Bigtable/BigQuery); rollup ladder, retention, alternatives, and the triggers that would reopen the decision.

#### [enum_extensions_guide.md](./technical/enum_extensions_guide.md)
Guide to enum extensions and helper methods used throughout the solution.

#### [user_onboarding_process.md](./technical/user_onboarding_process.md)
Step-by-step guide to the user onboarding process, device connection flows, and OAuth integration.

---

### Compliance

Located in `/compliance/`.

#### [dpia.md](./compliance/dpia.md)
Data Protection Impact Assessment (GDPR Art. 35) — processing inventory, risk assessment, and mitigations for the platform's health-data processing.

---

### Additional Documentation

#### `/archive/`
Deprecated or superseded documentation kept for historical reference. Nothing in `/archive/` is canonical.

---

## 🚀 Quick Start Guides

### For New Developers

1. **Read**: [solution_manifest.md](./solution_manifest.md) — understand the product
2. **Read**: [infrastructure.md](./infrastructure.md) — understand the architecture
3. **Read**: [release_matrix.md](./release_matrix.md) — understand what ships when
4. **Explore**: Review application docs in `/apps/` for your area of work

### For Business Stakeholders

1. **Read**: [solution_manifest.md](./solution_manifest.md) — product vision and roadmap
2. **Read**: [market_analysis.md](./market_analysis.md) — market opportunity and competition
3. **Review**: Pricing tiers and unit economics in solution_manifest.md

### For DevOps/Infrastructure

1. **Read**: [infrastructure.md](./infrastructure.md) — complete infrastructure guide
2. **Read**: [llm_design.md](./llm_design.md) — AI pipeline design (Pub/Sub, Cloud Run, MedGemma on Cloud Run)
3. **Read**: [technical/apm_setup_runbook.md](./technical/apm_setup_runbook.md) — observability/APM wiring (Datadog)
4. **Reference**: Terraform stacks (common/dev/prod) and GCP resource setup in infrastructure.md

### For API Consumers

1. **Read**: [execution/backend/api/readme.md](./execution/backend/api/readme.md) — canonical API documentation
2. **Test**: Use Swagger UI at https://localhost:7130/swagger (http: 5230) — Swagger is enabled in non-production environments only

---

## 📖 Documentation Conventions

### File Naming
- All documentation files use `lowercase_snake_case.md`
- `readme.md` — index files for directories

### Sections
All major documentation files include:
- **Table of Contents** — for easy navigation
- **Overview** — high-level summary
- **Detailed Content** — organized by topic
- **Code Examples** — where applicable
- **References** — links to related docs

### Code Blocks
Code examples specify language for syntax highlighting:
```csharp
// C# example
public class Example { }
```

```bash
# Bash example
dotnet build
```

---

## 🔄 Keeping Documentation Updated

### When to Update Documentation

**Always update documentation when:**
- Adding new features or endpoints
- Changing database schema
- Modifying infrastructure
- Adding new integrations
- Changing pricing or business model
- Updating deployment procedures

**When docs conflict**, the precedence is:
1. [release_matrix.md](./release_matrix.md) for release sequencing
2. [execution/backend/api/](./execution/backend/api/readme.md) for API contracts
3. [llm_design.md](./llm_design.md) for the AI pipeline architecture
4. [infrastructure.md](./infrastructure.md) for infrastructure and the transactional data model
5. [solution_manifest.md](./solution_manifest.md) for business/product facts

### Documentation Ownership

| Documentation | Owner | Update Frequency |
|--------------|-------|------------------|
| solution_manifest.md | Product Lead | Monthly or on major changes |
| release_matrix.md | Product Lead | On release planning changes |
| market_analysis.md | Business/Marketing | Quarterly |
| infrastructure.md | DevOps Lead | On infrastructure changes |
| llm_design.md | Tech Lead | On AI pipeline changes |
| execution/backend/api/ | Backend Team | On API changes |
| execution/ui/ | UI/UX + Frontend Teams | On design changes |
| apps/api/ | Backend Team | On API changes |
| apps/web/ | Frontend Team | On UI changes |
| apps/mobile/ | Mobile Team | On mobile app changes |
| apps/mobile/store_provisioning.md | Mobile Team / DevOps | On store or signing changes |
| apps/worker/ | Backend Team | On worker changes |
| /technical/ | Tech Lead | As needed |
| technical/apm_setup_runbook.md | DevOps Lead | On observability changes |
| technical/auth0_setup_runbook.md | Tech Lead | On auth changes |
| /compliance/ | Compliance Owner | On processing or legal changes |
| google_credits_pitch.md | Product Lead | On application updates |

---

## 📝 Documentation Version History

### Version 2.2 (August 7, 2026)
- ✅ Aligned the index with the GCP platform: llm_design.md (Pub/Sub + Cloud Run + MedGemma via Ollama + Gemini), infrastructure.md (Cloud SQL PostgreSQL 16, Cloud Run, Secret Manager, GCS), and Terraform Google Provider references
- ✅ Corrected the Worker description to the implemented jobs (30-minute wearable sync, daily orphan cleanup)
- ✅ Indexed new docs: apm_setup_runbook.md, auth0_setup_runbook.md, google_credits_pitch.md, apps/mobile/store_provisioning.md, compliance/dpia.md
- ✅ Fixed the Swagger URL (https://localhost:7130; non-production only) and the GitHub repository URL

### Version 2.1 (July 17, 2026)
- ✅ Reconciled the spec around the target architecture ([llm_design.md](./llm_design.md)): webhook ingestion + Event Hubs + Azure Functions + MedGemma, with Azure SQL as the transactional system of record and Cosmos DB for AI pipeline outputs
- ✅ Standardized on Auth0 Universal Login (no local password endpoints)
- ✅ Aligned pricing tiers to the subscription API spec
- ✅ Declared `/execution/backend/api/` the canonical API spec
- ✅ Created [release_matrix.md](./release_matrix.md) as the canonical release plan
- ✅ Renamed `apps/functions/` to `apps/worker/` to match its content
- ✅ Fixed cross-links, file-name casing, and version drift (.NET 10, iOS 16+, Android API 29+)

### Version 2.0 (January 8, 2026)
- Reorganized documentation structure
- Created solution manifest, market analysis, infrastructure guide
- Created app-specific documentation in /apps/
- Moved technical guides to /technical/
- Archived deprecated documentation

### Version 1.0 (January 5, 2026)
- Initial documentation structure
- Basic technical documentation
- Entity and infrastructure setup guides

---

## 🆘 Getting Help

### Documentation Issues
If you find errors, outdated information, or missing documentation:
1. Create an issue on GitHub
2. Tag with `documentation` label
3. Assign to documentation owner (see table above)

### Questions
For questions about:
- **Product/Business**: Contact product team
- **Technical Architecture**: Contact tech lead
- **API Usage**: See [execution/backend/api/](./execution/backend/api/readme.md) or contact backend team
- **Deployment**: Contact DevOps team

---

## 🔗 External Resources

### CardiTrack Resources
- **GitHub Repository**: https://github.com/Codesistance/product-carditrack
- **Website**: (Coming soon)
- **Support**: support@carditrack.com

### Technology Documentation
- [.NET 10 Documentation](https://docs.microsoft.com/dotnet/)
- [ASP.NET Core](https://docs.microsoft.com/aspnet/core/)
- [Entity Framework Core](https://docs.microsoft.com/ef/core/)
- [Blazor](https://docs.microsoft.com/aspnet/core/blazor/)
- [.NET MAUI](https://docs.microsoft.com/dotnet/maui/)
- [Google Cloud Documentation](https://cloud.google.com/docs)
- [Terraform Google Provider](https://registry.terraform.io/providers/hashicorp/google/latest/docs)

### Device Integration Documentation
- [Google Health API](https://developers.google.com/health) (Fitbit, Pixel Watch, third-party sources — replaces the legacy Fitbit Web API, decommissioned Sept 2026)
- [Apple HealthKit](https://developer.apple.com/documentation/healthkit)
- [Garmin Connect API](https://developer.garmin.com/gc-developer-program/)
- [Samsung Health SDK](https://developer.samsung.com/health)

---

## 📄 License

All documentation is proprietary and confidential.

---

**Last Updated**: August 7, 2026
**Maintained By**: CardiTrack Development Team
**Version**: 2.2

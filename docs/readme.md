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
**The target AI pipeline: MedGemma 1.5 4B inference on Azure Container Apps.**
- Model selection rationale (4B vs 27B, T4 vs A100)
- vLLM serving configuration and flags
- Fitbit webhook ingestion (Event Hubs + 5-min batching)
- SSA-LSTM pre-processing, prompt structure, and prefix caching
- Predictive monitoring, severity routing, cost estimates, and caveats

#### [infrastructure.md](./infrastructure.md)
**Complete infrastructure and database documentation.**
- Storage boundary: Azure SQL (transactional) + Cosmos DB (AI pipeline)
- Database schema and entity relationships
- Entity Framework Core setup and migrations
- Security and encryption (AES-256-GCM)
- Cloud infrastructure (Azure resources)
- Terraform configuration and deployment
- CI/CD pipeline and monitoring
- Scaling strategy and disaster recovery

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
**Blazor Server Web Dashboard** — component structure, SignalR real-time integration, authentication flow, running locally, deployment, testing.

#### [apps/mobile/](./apps/mobile/readme.md)
**.NET MAUI Mobile App** — cross-platform architecture (iOS, Android), MVVM pattern, platform integrations (HealthKit, Health Connect), push notifications, offline support, store publishing.

#### [apps/worker/](./apps/worker/readme.md)
**CardiTrack.Worker Background Service** — the .NET Worker Service hosting **non-AI background jobs** (OAuth token refresh, baseline recalculation, cleanup) using cron scheduling via Cronos. The AI ingestion/inference pipeline runs in Azure Functions — see [llm_design.md](./llm_design.md).

---

### Technical Reference

Located in `/technical/` — detailed technical guides and specifications.

#### [auth0_integration.md](./technical/auth0_integration.md)
Complete guide to Auth0 authentication integration, OAuth flows, and security configuration.

#### [entity_summary.md](./technical/entity_summary.md)
Detailed summary of all domain entities, their properties, and relationships.

#### [enum_extensions_guide.md](./technical/enum_extensions_guide.md)
Guide to enum extensions and helper methods used throughout the solution.

#### [user_onboarding_process.md](./technical/user_onboarding_process.md)
Step-by-step guide to the user onboarding process, device connection flows, and OAuth integration.

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
2. **Read**: [llm_design.md](./llm_design.md) — AI pipeline deployment (Container Apps GPU, Event Hubs, Cosmos DB)
3. **Reference**: Terraform modules and Azure resource setup in infrastructure.md

### For API Consumers

1. **Read**: [execution/backend/api/readme.md](./execution/backend/api/readme.md) — canonical API documentation
2. **Test**: Use Swagger UI at https://localhost:7001/swagger (local development)

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
| apps/worker/ | Backend Team | On worker changes |
| /technical/ | Tech Lead | As needed |

---

## 📝 Documentation Version History

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
- **GitHub Repository**: https://github.com/marigbede/CardiTrack
- **Website**: (Coming soon)
- **Support**: support@carditrack.com

### Technology Documentation
- [.NET 10 Documentation](https://docs.microsoft.com/dotnet/)
- [ASP.NET Core](https://docs.microsoft.com/aspnet/core/)
- [Entity Framework Core](https://docs.microsoft.com/ef/core/)
- [Blazor](https://docs.microsoft.com/aspnet/core/blazor/)
- [.NET MAUI](https://docs.microsoft.com/dotnet/maui/)
- [Azure Documentation](https://docs.microsoft.com/azure/)
- [Terraform Azure Provider](https://registry.terraform.io/providers/hashicorp/azurerm/latest/docs)

### Device Integration Documentation
- [Fitbit Web API](https://dev.fitbit.com/build/reference/web-api/)
- [Apple HealthKit](https://developer.apple.com/documentation/healthkit)
- [Garmin Connect API](https://developer.garmin.com/gc-developer-program/)
- [Samsung Health SDK](https://developer.samsung.com/health)

---

## 📄 License

All documentation is proprietary and confidential.

---

**Last Updated**: July 17, 2026
**Maintained By**: CardiTrack Development Team
**Version**: 2.1

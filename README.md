# CardiTrack

> Multi-device elderly health monitoring platform with AI-powered preventive alerts

CardiTrack is an affordable health monitoring service that connects to wearable devices to provide families with peace of mind through preventive health monitoring powered by AI pattern analysis.

## 🎯 Key Features

- **Wearable Integration**: Fitbit today (via the Google Health API), with Garmin, Apple Watch, and Samsung planned
- **Preventive Alerts**: AI detects concerning patterns BEFORE emergencies
- **Affordable**: 50-70% cheaper than traditional medical alert systems ($8-15/month vs $40-70/month)
- **Device-Agnostic**: Works with devices elderly users already own
- **HIPAA-Aligned**: Encryption, audit logging, and a documented data-protection architecture
- **Family Dashboard**: Health monitoring for caregivers on web and mobile

## 🏗️ Architecture

CardiTrack follows **Clean Architecture** principles with clear separation of concerns:

```
┌─────────────────────────────────────────────┐
│    Presentation Layer                       │
│  (API, Web, Mobile + Mobile.Core)           │
└─────────────────────────────────────────────┘
              ↓
┌─────────────────────────────────────────────┐
│    Application Layer                        │
│  (Use Cases, DTOs, Interfaces)              │
└─────────────────────────────────────────────┘
              ↓
┌─────────────────────────────────────────────┐
│    Domain Layer                             │
│  (Entities, Value Objects)                  │
└─────────────────────────────────────────────┘
              ↑
┌─────────────────────────────────────────────┐
│    Infrastructure Layer                     │
│  (EF Core, External APIs, Observability,    │
│   MedGemma model image)                     │
└─────────────────────────────────────────────┘

┌─────────────────────────────────────────────┐
│    CardiTrack.Worker (separate service)     │
│  (Cron-scheduled non-AI background jobs)    │
└─────────────────────────────────────────────┘
```

`CardiTrack.Worker` is its own deployable service hosting all non-AI background jobs — wearable data sync, baselines, partition retention, statistical + inactivity alerting, notification dispatch, device-auth recovery, cleanup, and more (11 cron jobs in total). The AI ingestion/inference pipeline runs on GCP (Pub/Sub + Cloud Run) and is **live in dev** — see [docs/llm_design.md](docs/llm_design.md).

## 📁 Solution Structure

```
CardiTrack/
├── src/
│   ├── Core/
│   │   ├── CardiTrack.Domain           # Business entities & value objects
│   │   └── CardiTrack.Application      # Use cases & interfaces
│   ├── Infrastructure/
│   │   ├── CardiTrack.Infrastructure   # EF Core (Npgsql), external services
│   │   ├── CardiTrack.Observability    # Serilog + OpenTelemetry, switchable APM
│   │   ├── CardiTrack.Shared           # Common utilities
│   │   └── MedGemma/                   # Ollama container image for the MedGemma model
│   ├── Presentation/
│   │   ├── CardiTrack.API              # ASP.NET Core REST API
│   │   ├── CardiTrack.Web              # Blazor web dashboard
│   │   ├── CardiTrack.Mobile           # .NET MAUI app
│   │   └── CardiTrack.Mobile.Core      # Shared mobile logic
│   ├── Pipeline/
│   │   ├── CardiTrack.HealthWebhookReceiver  # AI pipeline ingress (publishes raw to Pub/Sub)
│   │   └── CardiTrack.PipelineJobs     # AI pipeline Cloud Run jobs (digest, aggregate, assess)
│   └── Worker/
│       └── CardiTrack.Worker           # Non-AI background jobs (11, cron-scheduled)
├── tests/
│   ├── CardiTrack.UnitTests
│   ├── CardiTrack.IntegrationTests
│   └── CardiTrack.E2ETests
├── tools/
│   └── HealthApiProbe                  # Live Google Health API field-population probe
├── infrastructure/                     # Terraform (GCP)
│   ├── *.tf                            # Root stack (dev/prod via tfvars)
│   ├── common/                         # Shared stack (Artifact Registry, builds bucket)
│   ├── datadog/                        # Datadog monitors + OTel severity log pipeline
│   ├── deployments/                    # Cloud Run, Cloud SQL, GCS, Pub/Sub, LB modules
│   └── environments/                   # common.tfvars, dev.tfvars, prod.tfvars
├── docs/                               # Documentation (see docs/readme.md)
├── .github/workflows/                  # CI/CD (deploy-apps-*, deploy-infra-*)
├── docker-compose.yml                  # Local multi-service compose
└── carditrackapi-docker-compose.yml    # API-focused compose
```

## 🚀 Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [PostgreSQL](https://www.postgresql.org/) (a local Docker container is fine)
- [Google Cloud account](https://cloud.google.com/) (for deployment only)
- [Terraform](https://www.terraform.io/) >= 1.14.7 (for infrastructure)

### Dev Container (recommended)

`.devcontainer/` defines a container with all of the above already installed —
.NET 10 SDK, EF Core tooling, Terraform, the PostgreSQL client, the gcloud CLI —
plus PostgreSQL 17 and Redis 7 as compose services. Open the repository in VS Code
and choose **Reopen in Container**, or run `devcontainer up --workspace-folder .`;
package restore, dev certificates, and the initial migration run automatically.

See the [dev container README](.devcontainer/README.md) for the toolchain matrix
and the opt-in mobile layer. Claude Code cloud sessions provision the same
toolchain automatically via a `SessionStart` hook. To configure a Claude Code
**cloud environment** itself (network access, environment variables, setup script),
see [docs/technical/claude_cloud_environment_setup.md](docs/technical/claude_cloud_environment_setup.md).

Everything except `CardiTrack.Mobile` and `tools/HealthApiProbe` is covered by the
`CardiTrack.Server.slnf` solution filter — the MAUI project needs the `maui-android`
workload and the Android SDK, and the probe is a standalone live-API tool — so use
the filter for server work:

```bash
dotnet build CardiTrack.Server.slnf
dotnet test  CardiTrack.Server.slnf
```

### Local Development

1. **Clone the repository**
   ```bash
   git clone https://github.com/Codesistance/product-carditrack.git
   cd product-carditrack
   ```

2. **Restore dependencies**
   ```bash
   dotnet restore
   ```

3. **Set up the database** (EF Core migrations live in the Infrastructure project but need the API as startup project)
   ```bash
   cd src/Infrastructure/CardiTrack.Infrastructure
   dotnet ef database update --startup-project ../../Presentation/CardiTrack.API
   ```

4. **Run the API** (https://localhost:7130, Swagger UI in non-production)
   ```bash
   cd src/Presentation/CardiTrack.API
   dotnet run
   ```

5. **Run the Web Dashboard**
   ```bash
   cd src/Presentation/CardiTrack.Web
   dotnet run
   ```

6. **Run the Worker** (background sync jobs)
   ```bash
   cd src/Worker/CardiTrack.Worker
   dotnet run
   ```

Docker Compose files (`docker-compose.yml`, `carditrackapi-docker-compose.yml`) are available for containerised local runs.

### Running Tests

```bash
# Run all tests
dotnet test

# Run specific test project
dotnet test tests/CardiTrack.UnitTests
```

## 🏥 HIPAA Compliance

CardiTrack is designed with HIPAA compliance in mind:

- ✅ Encryption at rest (Cloud SQL disk encryption)
- ✅ Encryption in transit (TLS 1.2+)
- ✅ Platform audit logging (prod only, 90-day retention; the feature is off in dev)
- ✅ Access controls (RBAC, Auth0 authentication)
- ✅ Secure secret storage (GCP Secret Manager)
- ✅ Data retention policies

See the [DPIA](docs/compliance/dpia.md) and the [data protection architecture](docs/technical/data_protection_architecture.md) for the full compliance picture.

## 📊 Supported Devices

### Current Support
- ✅ **Fitbit** via the **Google Health API** (the legacy Fitbit Web API is decommissioned September 2026; the codebase has migrated, Google console registration was completed 2026-08-07 with field mappings verified 2026-08-09; restricted-scope verification + the annual CASA assessment are still outstanding, and unverified apps are capped at 100 users until they complete)

### Planned Support
- 🔄 **Garmin** (Venu, Forerunner, Vivoactive)
- 🔄 **Apple Watch** (Series 4+)
- 🔄 **Samsung Galaxy Watch** (5, 6)
- ⏳ **Withings** (ScanWatch)
- ⏳ **Oura Ring** (Gen 3)
- ⏳ **Whoop** (4.0)

## 🧠 AI Features

CardiTrack uses a two-provider LLM setup surfaced through the API's chat, insights, and reports endpoints:

- **Medical provider — MedGemma 1.5 4B** (`hf.co/unsloth/medgemma-1.5-4b-it-GGUF:Q4_K_M`) served by **Ollama on Cloud Run** (custom image in `src/Infrastructure/MedGemma/`): health-data interpretation and severity assessment
- **General provider — Gemini 2.0 Flash**: conversational and general-purpose responses

Health data is ingested by the Worker's 10-minute polling sync (`WearableSyncWorker`) as the guaranteed fallback, and — **live in dev** — by the webhook-driven AI ingestion/inference pipeline on GCP (Pub/Sub + Cloud Run): webhook receiver, aggregator, real-time assessor, and family digests. See [docs/llm_design.md](docs/llm_design.md).

## 🌐 Deployment

All infrastructure runs on **Google Cloud** (project `carditrack-490120`, region `europe-west2`): Cloud Run services (api, web, worker, medgemma, webhook-receiver) plus Cloud Run Jobs (the migrator and the three AI-pipeline jobs — digest, aggregator, assessor), Cloud SQL PostgreSQL 16, Secret Manager, GCS, Pub/Sub (both environments), and an optional domain-gated Load Balancer with Cloud Armor.

### Infrastructure Setup (Terraform)

Three stacks — `common`, `dev`, and `prod` — share the root configuration and are selected via tfvars (GCS backend):

```bash
terraform -chdir=infrastructure init
terraform -chdir=infrastructure plan  -var-file="environments/dev.tfvars"
terraform -chdir=infrastructure apply -var-file="environments/dev.tfvars"
```

See the [infrastructure README](infrastructure/README.md) for detailed instructions.

### CI/CD

GitHub Actions workflows in `.github/workflows/` handle deployment: `deploy-apps-dev` / `deploy-apps-prod` build and roll out the Cloud Run services (running EF migrations via the migrator Job), and `deploy-infra-common` / `deploy-infra-dev` / `deploy-infra-prod` apply the Terraform stacks.

## 📖 Documentation

- [Documentation Index](docs/readme.md)
- [Solution Manifest](docs/solution_manifest.md)
- [Infrastructure & Database Guide](docs/infrastructure.md)
- [Application Docs](docs/apps/) — API, Web, Mobile, Worker
- [Technical Reference](docs/technical/) — Auth0, OAuth clients, APM, data protection, entities

## 🤝 Contributing

This is a private repository. All changes go through pull requests with review.

## 📄 License

Proprietary and confidential. All rights reserved. This code is not licensed for external use, copying, or distribution.

## 🆘 Support

For issues, questions, or feature requests:
- Open an issue on GitHub
- Email: support@carditrack.com
- Documentation: [docs/](docs/readme.md)

## 🙏 Acknowledgments

- Google Health API
- Apple HealthKit
- Garmin Connect API
- MedGemma (Google) & Ollama
- Google Cloud Platform

---

**Built with ❤️ for family caregivers**

**Last Updated**: August 13, 2026

# CardiTrack Dev Container

A container carrying everything needed to build, run, test, and deploy CardiTrack:
the .NET 10 SDK, EF Core tooling, Terraform, the PostgreSQL client, and — as
compose services — PostgreSQL 17 and Redis 7.

## What's in here

| File | Purpose |
| --- | --- |
| `devcontainer.json` | Dev Containers entry point — compose service, ports, extensions |
| `docker-compose.yml` | Workspace container + PostgreSQL 17 + Redis 7 |
| `Dockerfile` | The image: Ubuntu 24.04 base plus the toolchain |
| `install-toolchain.sh` | **Single source of truth** for tool versions and installation |
| `bootstrap.sh` | Provisions the same toolchain on images we don't build (Claude Code cloud sessions) |
| `post-create.sh` | Workspace setup after container creation: restore, dev certs, migrations |

`install-toolchain.sh` is shared deliberately: the image build and the cloud-session
bootstrap install identical versions, so "works in the dev container" and "works in
a Claude Code session" don't drift apart.

## Toolchain

| Tool | Version | Source |
| --- | --- | --- |
| .NET SDK | 10.0.x | Ubuntu archive (`dotnet-sdk-10.0`) |
| dotnet-ef | 10.0.x | NuGet global tool |
| Terraform | 1.14.9 | `releases.hashicorp.com`, SHA256-verified |
| PostgreSQL client | 16.x | Ubuntu archive |
| gcloud CLI | latest | `packages.cloud.google.com` (image build only) |
| OpenJDK + maui-android + Android SDK | opt-in | `INSTALL_MAUI=1` |

Versions are pinned at the top of `install-toolchain.sh`. Keep them in step with
`.github/workflows/_env.yml` (`dotnet_version`, `tf_version`) and
`infrastructure/versions.tf` (`required_version`).

### Why the Ubuntu archive for .NET

`packages.microsoft.com` and `builds.dotnet.microsoft.com` are blocked by several
of the networks this repository is developed on (Claude Code cloud containers,
locked-down CI). The distro mirrors carry `dotnet-sdk-10.0` and are reachable from
all of them. `dotnet-install.sh` remains a fallback where Microsoft's hosts work.

## Getting started

Open the repository in VS Code and choose **Reopen in Container**, or:

```bash
devcontainer up --workspace-folder .
```

`post-create.sh` then restores packages, trusts the dev HTTPS certificate, and
applies EF Core migrations to the `db` service. After that:

```bash
dotnet run --project src/Presentation/CardiTrack.API   # → http://localhost:5230
dotnet run --project src/Presentation/CardiTrack.Web   # → http://localhost:5026
dotnet run --project src/Worker/CardiTrack.Worker      # → http://localhost:8080/healthz
dotnet test CardiTrack.Server.slnf
terraform -chdir=infrastructure plan -var-file="environments/dev.tfvars"
```

## `CardiTrack.Server.slnf`

A solution filter covering everything except `CardiTrack.Mobile`. The MAUI project
targets `net10.0-android` on Linux, which needs the `maui-android` workload and the
Android SDK; without them even `dotnet restore CardiTrack.sln` fails with
`NETSDK1147`. Use the filter for day-to-day server work:

```bash
dotnet build CardiTrack.Server.slnf
dotnet test  CardiTrack.Server.slnf
```

To build mobile in here as well, rebuild the image with `INSTALL_MAUI=1` (see
`docker-compose.yml`) and use `CardiTrack.sln` directly. CI builds mobile on
dedicated runners — see `.github/workflows/deploy-apps-*.yml`.

## Docker access

Testcontainers backs the integration tests and part of the unit suite, so the
workspace container mounts the host's `/var/run/docker.sock` and the test
containers run as siblings on the host daemon. Without a reachable daemon those
tests fail at fixture setup.

## AI providers

MedGemma (via Ollama) is not in this stack — it is a multi-GB pull that only some
work needs. Start it from the repository compose file when required:

```bash
docker compose --profile full up ollama medgemma-init
```

Point the app at it with `AI__Private__BaseUrl=http://host.docker.internal:11434`.

## Claude Code cloud sessions

`.claude/settings.json` registers `bootstrap.sh` as a `SessionStart` hook, so cloud
sessions — which start from a generic image with no .NET SDK and no Terraform —
provision the toolchain automatically. The hook additionally starts `dockerd` when
the binaries are present but no daemon is running, and warms the NuGet cache. It is
idempotent: a few seconds on a warm container.

Run it by hand with:

```bash
./.devcontainer/bootstrap.sh
```

For the **New cloud environment** dialog itself (name, network access, environment
variables, setup script) — including why the setup script can't reference anything
in this repo, since it runs before the checkout exists — see
[docs/technical/claude_cloud_environment_setup.md](../docs/technical/claude_cloud_environment_setup.md).

### Network-policy caveats

Some egress policies block hosts this repository's tooling needs. Observed in
Claude Code cloud containers:

| Host | Used by | Effect when blocked |
| --- | --- | --- |
| `registry.terraform.io` | `terraform init` | Providers cannot be downloaded; `plan`/`validate` unavailable. `fmt` still works. |
| `production.cloudfront.docker.com` | Docker Hub image layers | Testcontainers cannot pull `postgres`; those tests fail. |
| `dl.google.com` | Android SDK | `INSTALL_MAUI=1` installs the workload but not the SDK. |
| `packages.cloud.google.com` | gcloud CLI | `INSTALL_GCLOUD=1` is skipped with a warning. |

The scripts degrade gracefully — the core .NET and Terraform toolchain installs
regardless. Report blocked hosts rather than routing around them.

# .NET September 2026 servicing release (10.0.12) — five CVEs, none in CardiTrack's live exploit path

**Severity:** HIGH
**Category:** dependencies

## Summary

.NET shipped its September 2026 Patch Tuesday servicing release (SDK 10.0.111/10.0.400,
runtime/ASP.NET Core/EF Core 10.0.12) fixing five CVEs (69304, 71328, 69522, 58649, 69806 —
corrected from an initial miscount of six against the five sourced below). CardiTrack pins
`Microsoft.EntityFrameworkCore` (and `.Design`/`.Relational`) at `10.0.11` in
`CardiTrack.Infrastructure.csproj` and `Microsoft.AspNetCore.Authentication.JwtBearer` at
`10.0.11` in `CardiTrack.API.csproj` — not every project in the solution references either
package (Worker, Web and PipelineJobs don't). Every service's Docker image builds from
`mcr.microsoft.com/dotnet/sdk:10.0` (build stage) and
`mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra` (final stage) — both floating
minor tags, so every service picks up 10.0.12 on its next rebuild regardless of which NuGet
packages it references.

None of the five CVEs land on CardiTrack's actual shipped topology:

- **CVE-2026-69304** (CVSS 5.9, unauthenticated DoS via decompression bomb) is scoped to
  `Microsoft.AspNetCore.Server.IISIntegration`. CardiTrack runs Linux containers on Cloud Run,
  not IIS out-of-process hosting — does not apply.
- **CVE-2026-71328 / CVE-2026-69522** (CVSS 8.8, heap overflow in
  `Microsoft.DiaSymReader.Native` via malicious PDB files) is a build-tooling path — exposure
  requires processing an untrusted PDB, which no CardiTrack pipeline does.
- **CVE-2026-58649 / CVE-2026-69806** (`dotnet watch` BrowserRefreshServer/AspireServerService
  info disclosure and code injection) is a dev inner-loop tool, never runs in a shipped
  container.

So this is a routine patch-hygiene bump, not an active exploit path — hence HIGH rather than
CRITICAL: worth doing on the next dependency-bump PR, not a fire drill.

## Sources

- https://github.com/dotnet/announcements/issues/443 (CVE-2026-69304)
- https://github.com/dotnet/announcements/issues/440 (CVE-2026-71328)
- https://github.com/dotnet/announcements/issues/439 (CVE-2026-69522)
- https://github.com/dotnet/announcements/issues/442 (CVE-2026-69806)
- https://github.com/dotnet/announcements/issues/441 (CVE-2026-58649)

## Why flagged

CVEs in the .NET 10 servicing train that every CardiTrack service builds against, via the
shared Docker base images if not always via a direct NuGet reference. Even though none of the
five exploit paths apply to CardiTrack's Cloud Run/Linux deployment shape, staying on a patched
minor keeps future defense-in-depth intact and costs one version-bump PR.

## Question to answer next

Bump `Microsoft.AspNetCore.Authentication.JwtBearer`, `Microsoft.EntityFrameworkCore`,
`Microsoft.EntityFrameworkCore.Design`, `Microsoft.EntityFrameworkCore.Relational`,
`Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.AspNetCore.OpenApi`,
`Microsoft.Extensions.Configuration.*`, `Microsoft.Extensions.DependencyInjection*`,
`Microsoft.Extensions.Http`, `Microsoft.Extensions.Logging.Abstractions`, and
`Npgsql.EntityFrameworkCore.PostgreSQL` from `10.0.11` to `10.0.12` (or whatever the current
10.0.x servicing version is) in the next routine dependency PR. Confirm the CI SDK pin
(`actions/setup-dotnet@v5`) also picks up 10.0.400+.

claude "work through @research/queue/2026-09-09-dotnet-sept-2026-servicing-cves.md"

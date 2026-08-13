# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Release version — CI passes the deploy's semver tag (--build-arg VERSION=1.2.3) so the
# running container can report which release it is, in logs and as OTel's service.version.
# The default marks images built outside the release pipeline.
ARG VERSION=0.0.0-local

# Copy solution and project files
COPY ["CardiTrack.sln", "./"]
COPY ["src/Worker/CardiTrack.Worker/CardiTrack.Worker.csproj", "src/Worker/CardiTrack.Worker/"]
COPY ["src/Core/CardiTrack.Application/CardiTrack.Application.csproj", "src/Core/CardiTrack.Application/"]
COPY ["src/Core/CardiTrack.Domain/CardiTrack.Domain.csproj", "src/Core/CardiTrack.Domain/"]
COPY ["src/Infrastructure/CardiTrack.Infrastructure/CardiTrack.Infrastructure.csproj", "src/Infrastructure/CardiTrack.Infrastructure/"]
COPY ["src/Infrastructure/CardiTrack.Observability/CardiTrack.Observability.csproj", "src/Infrastructure/CardiTrack.Observability/"]
COPY ["src/Infrastructure/CardiTrack.Shared/CardiTrack.Shared.csproj", "src/Infrastructure/CardiTrack.Shared/"]

# Restore dependencies
RUN dotnet restore "src/Worker/CardiTrack.Worker/CardiTrack.Worker.csproj"

# Copy all source files
COPY . .

# Build
WORKDIR "/src/src/Worker/CardiTrack.Worker"
RUN dotnet build "CardiTrack.Worker.csproj" -c Release -o /app/build /p:Version=$VERSION

# Stage 2: Publish
FROM build AS publish
ARG VERSION=0.0.0-local
RUN dotnet publish "CardiTrack.Worker.csproj" -c Release -o /app/publish \
    /p:Version=$VERSION \
    /p:UseAppHost=false \
    /p:PublishTrimmed=false \
    /p:PublishSingleFile=false
# Create logs directory in publish stage (chiseled final stage has no shell)
RUN mkdir -p /app/publish/logs

# Stage 3: Runtime - Chiseled Ubuntu (Ultra-minimal, non-root, distroless-like)
# Uses aspnet chiseled (not runtime) — Cloud Run requires an HTTP listener for health probes.
# CardiTrack.Worker exposes a minimal /healthz endpoint via a hosted HTTP listener.
# The "-extra" variant (vs. plain "-chiseled") adds ICU and tzdata — the Worker's OAuth
# token refresh, baseline recalculation, and trial reminder jobs all reason about member
# time zones, which needs a real timezone database on top of the plain chiseled base.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS final
ARG VERSION=0.0.0-local
WORKDIR /app

# Chiseled images are:
# - Ultra-minimal (no package manager, no shell, no unnecessary packages)
# - Non-root by default (runs as user 'app' with UID 1654)
# - Security-hardened (minimal attack surface)
# - Small size ("-extra" adds a few MB for ICU/tzdata)

# Copy published files (including pre-created logs dir) with correct ownership
COPY --chown=1654:1654 --from=publish /app/publish .

# Expose health check port only (no public traffic)
EXPOSE 8080

# Health check — polls the internal /healthz endpoint
HEALTHCHECK --interval=30s --timeout=3s --start-period=20s --retries=3 \
  CMD ["dotnet", "CardiTrack.Worker.dll", "--health-check"]

# Set environment variables — clear the base image's ASPNETCORE_HTTP_PORTS so the
# app's UseUrls (bound to Cloud Run's PORT) is the sole binding source and Kestrel
# doesn't warn about overriding it
ENV ASPNETCORE_HTTP_PORTS=
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_EnableDiagnostics=0

# Entry point
ENTRYPOINT ["dotnet", "CardiTrack.Worker.dll"]

# Labels
LABEL org.opencontainers.image.title="CardiTrack Worker"
LABEL org.opencontainers.image.description="HIPAA-compliant wearable data sync worker with chiseled Ubuntu base"
LABEL org.opencontainers.image.vendor="CardiTrack"
LABEL org.opencontainers.image.version="$VERSION"

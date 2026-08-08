# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Release version — CI passes the deploy's semver tag (--build-arg VERSION=1.2.3) so the
# running container can report which release it is, in logs and as OTel's service.version.
# The default marks images built outside the release pipeline.
ARG VERSION=0.0.0-local

# Copy solution and project files
COPY ["CardiTrack.sln", "./"]
COPY ["src/Presentation/CardiTrack.Web/CardiTrack.Web.csproj", "src/Presentation/CardiTrack.Web/"]
COPY ["src/Core/CardiTrack.Application/CardiTrack.Application.csproj", "src/Core/CardiTrack.Application/"]
COPY ["src/Core/CardiTrack.Domain/CardiTrack.Domain.csproj", "src/Core/CardiTrack.Domain/"]
COPY ["src/Infrastructure/CardiTrack.Infrastructure/CardiTrack.Infrastructure.csproj", "src/Infrastructure/CardiTrack.Infrastructure/"]
COPY ["src/Infrastructure/CardiTrack.Observability/CardiTrack.Observability.csproj", "src/Infrastructure/CardiTrack.Observability/"]
COPY ["src/Infrastructure/CardiTrack.Shared/CardiTrack.Shared.csproj", "src/Infrastructure/CardiTrack.Shared/"]

# Restore dependencies
RUN dotnet restore "src/Presentation/CardiTrack.Web/CardiTrack.Web.csproj"

# Copy all source files
COPY . .

# Build
WORKDIR "/src/src/Presentation/CardiTrack.Web"
RUN dotnet build "CardiTrack.Web.csproj" -c Release -o /app/build /p:Version=$VERSION

# Stage 2: Publish
FROM build AS publish
ARG VERSION=0.0.0-local
RUN dotnet publish "CardiTrack.Web.csproj" -c Release -o /app/publish \
    /p:Version=$VERSION \
    /p:UseAppHost=false \
    /p:PublishTrimmed=false \
    /p:PublishSingleFile=false
# Create logs directory in publish stage (chiseled final stage has no shell)
RUN mkdir -p /app/publish/logs

# Stage 3: Runtime - Chiseled Ubuntu (Ultra-minimal, non-root, distroless-like)
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
ARG VERSION=0.0.0-local
WORKDIR /app

# Chiseled images are:
# - Ultra-minimal (no package manager, no shell, no unnecessary packages)
# - Non-root by default (runs as user 'app' with UID 1654)
# - Security-hardened (minimal attack surface)
# - Small size (~110MB vs ~220MB for standard aspnet image)

# Copy published files (including pre-created logs dir) with correct ownership
COPY --chown=1654:1654 --from=publish /app/publish .

# Expose ports (non-privileged ports since running as non-root)
EXPOSE 8080
EXPOSE 8081

# Health check using built-in HTTP client (no curl in chiseled images)
HEALTHCHECK --interval=30s --timeout=3s --start-period=15s --retries=3 \
  CMD ["dotnet", "CardiTrack.Web.dll", "--health-check"]

# Set environment variables — port binding comes from the base image's
# ASPNETCORE_HTTP_PORTS=8080 (setting URLS too makes Kestrel warn about the override)
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_EnableDiagnostics=0

# Entry point
ENTRYPOINT ["dotnet", "CardiTrack.Web.dll"]

# Labels
LABEL org.opencontainers.image.title="CardiTrack Web"
LABEL org.opencontainers.image.description="HIPAA-compliant health monitoring Blazor dashboard with chiseled Ubuntu base"
LABEL org.opencontainers.image.vendor="CardiTrack"
LABEL org.opencontainers.image.version="$VERSION"

# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files
COPY ["CardiTrack.sln", "./"]
COPY ["src/Presentation/CardiTrack.API/CardiTrack.API.csproj", "src/Presentation/CardiTrack.API/"]
COPY ["src/Core/CardiTrack.Application/CardiTrack.Application.csproj", "src/Core/CardiTrack.Application/"]
COPY ["src/Core/CardiTrack.Domain/CardiTrack.Domain.csproj", "src/Core/CardiTrack.Domain/"]
COPY ["src/Infrastructure/CardiTrack.Infrastructure/CardiTrack.Infrastructure.csproj", "src/Infrastructure/CardiTrack.Infrastructure/"]
COPY ["src/Infrastructure/CardiTrack.Observability/CardiTrack.Observability.csproj", "src/Infrastructure/CardiTrack.Observability/"]
COPY ["src/Infrastructure/CardiTrack.Shared/CardiTrack.Shared.csproj", "src/Infrastructure/CardiTrack.Shared/"]

# Restore dependencies
RUN dotnet restore "src/Presentation/CardiTrack.API/CardiTrack.API.csproj"

# Copy all source files
COPY . .

# Build
WORKDIR "/src/src/Presentation/CardiTrack.API"
RUN dotnet build "CardiTrack.API.csproj" -c Release -o /app/build

# Stage 2: Publish
FROM build AS publish
RUN dotnet publish "CardiTrack.API.csproj" -c Release -o /app/publish \
    /p:UseAppHost=false \
    /p:PublishTrimmed=false \
    /p:PublishSingleFile=false
# Create logs directory in publish stage (chiseled final stage has no shell)
RUN mkdir -p /app/publish/logs

# Stage 3: Runtime - Chiseled Ubuntu (Ultra-minimal, non-root, distroless-like)
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
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
# Using dotnet tool for health check instead
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
  CMD ["dotnet", "CardiTrack.API.dll", "--health-check"]

# Set environment variables — port binding comes from the base image's
# ASPNETCORE_HTTP_PORTS=8080 (setting URLS too makes Kestrel warn about the override)
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_EnableDiagnostics=0

# Entry point
ENTRYPOINT ["dotnet", "CardiTrack.API.dll"]

# Security Labels
LABEL org.opencontainers.image.title="CardiTrack API"
LABEL org.opencontainers.image.description="HIPAA-compliant health monitoring API with chiseled Ubuntu base"
LABEL org.opencontainers.image.vendor="CardiTrack"
LABEL org.opencontainers.image.version="1.0.0"

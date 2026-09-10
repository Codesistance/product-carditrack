# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Release version — CI passes the deploy's semver tag (--build-arg VERSION=1.2.3) so the
# running container can report which release it is, in logs and as OTel's service.version.
# The default marks images built outside the release pipeline.
ARG VERSION=0.0.0-local

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
RUN dotnet build "CardiTrack.API.csproj" -c Release -o /app/build /p:Version=$VERSION

# Stage 2: Publish
FROM build AS publish
ARG VERSION=0.0.0-local
RUN dotnet publish "CardiTrack.API.csproj" -c Release -o /app/publish \
    /p:Version=$VERSION \
    /p:UseAppHost=false \
    /p:PublishTrimmed=false \
    /p:PublishSingleFile=false
# Create logs directory in publish stage (chiseled final stage has no shell)
RUN mkdir -p /app/publish/logs

# Stage 2b: Fonts for the PDF export. QuestPDF bundles Lato (Latin, Greek, Cyrillic); a
# member's name can be in any script, and the chiseled runtime has no fonts at all, so a
# subset of Noto Sans rides along under /app/fonts and ReportFonts registers it at startup.
# One face per script, Regular and Bold where a weight is used — about 4 MB. Noto Sans CJK
# is left out on purpose (20 MB on disk, ~70 MB resident); see ReportFonts.cs to add it.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS fonts
RUN apt-get update \
    && apt-get install -y --no-install-recommends fonts-noto-core \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /fonts \
    && cd /usr/share/fonts/truetype/noto \
    && cp NotoSans-Regular.ttf NotoSans-Bold.ttf NotoSans-Italic.ttf NotoSans-BoldItalic.ttf \
          NotoSansArabic-Regular.ttf NotoSansArabic-Bold.ttf \
          NotoSansHebrew-Regular.ttf NotoSansHebrew-Bold.ttf \
          NotoSansDevanagari-Regular.ttf NotoSansDevanagari-Bold.ttf \
          NotoSansBengali-Regular.ttf NotoSansGurmukhi-Regular.ttf NotoSansTamil-Regular.ttf \
          NotoSansThai-Regular.ttf NotoSansArmenian-Regular.ttf NotoSansGeorgian-Regular.ttf \
          NotoSansSymbols2-Regular.ttf \
          /fonts/ \
    && cp /usr/share/doc/fonts-noto-core/copyright /fonts/NOTO-LICENSE

# Stage 3: Runtime - Chiseled Ubuntu (Ultra-minimal, non-root, distroless-like)
# The "-extra" variant (vs. plain "-chiseled") adds ICU and tzdata — the plain chiseled
# base has no timezone database, so TimeZoneInfo.TryFindSystemTimeZoneById fails for every
# IANA zone at runtime even though it resolves fine under `dotnet test` on a full CI image.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS final
ARG VERSION=0.0.0-local
WORKDIR /app

# Chiseled images are:
# - Ultra-minimal (no package manager, no shell, no unnecessary packages)
# - Non-root by default (runs as user 'app' with UID 1654)
# - Security-hardened (minimal attack surface)
# - Small size (~110MB vs ~220MB for standard aspnet image; "-extra" adds a few MB for ICU/tzdata)

# Copy published files (including pre-created logs dir) with correct ownership
COPY --chown=1654:1654 --from=publish /app/publish .
COPY --chown=1654:1654 --from=fonts /fonts ./fonts

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
LABEL org.opencontainers.image.version="$VERSION"

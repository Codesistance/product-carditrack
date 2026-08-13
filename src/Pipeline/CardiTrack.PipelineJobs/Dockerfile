# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Release version — CI passes the deploy's semver tag (--build-arg VERSION=1.2.3) so the
# running container can report which release it is, in logs and as OTel's service.version.
ARG VERSION=0.0.0-local

# Copy solution and project files
COPY ["CardiTrack.sln", "./"]
COPY ["src/Pipeline/CardiTrack.PipelineJobs/CardiTrack.PipelineJobs.csproj", "src/Pipeline/CardiTrack.PipelineJobs/"]
COPY ["src/Core/CardiTrack.Application/CardiTrack.Application.csproj", "src/Core/CardiTrack.Application/"]
COPY ["src/Core/CardiTrack.Domain/CardiTrack.Domain.csproj", "src/Core/CardiTrack.Domain/"]
COPY ["src/Infrastructure/CardiTrack.Infrastructure/CardiTrack.Infrastructure.csproj", "src/Infrastructure/CardiTrack.Infrastructure/"]
COPY ["src/Infrastructure/CardiTrack.Observability/CardiTrack.Observability.csproj", "src/Infrastructure/CardiTrack.Observability/"]
COPY ["src/Infrastructure/CardiTrack.Shared/CardiTrack.Shared.csproj", "src/Infrastructure/CardiTrack.Shared/"]

# Restore dependencies
RUN dotnet restore "src/Pipeline/CardiTrack.PipelineJobs/CardiTrack.PipelineJobs.csproj"

# Copy all source files
COPY . .

# Build
WORKDIR "/src/src/Pipeline/CardiTrack.PipelineJobs"
RUN dotnet build "CardiTrack.PipelineJobs.csproj" -c Release -o /app/build /p:Version=$VERSION

# Stage 2: Publish
FROM build AS publish
ARG VERSION=0.0.0-local
RUN dotnet publish "CardiTrack.PipelineJobs.csproj" -c Release -o /app/publish \
    /p:Version=$VERSION \
    /p:UseAppHost=false \
    /p:PublishTrimmed=false \
    /p:PublishSingleFile=false
RUN mkdir -p /app/publish/logs

# Stage 3: Runtime — chiseled Ubuntu, same base as the Worker. A Cloud Run *job* runs to
# completion with no listener and no health probe, so no port is exposed.
# The "-extra" variant adds ICU and tzdata — daily digest generation reasons about each
# member's IANA time zone, which needs a real timezone database on top of plain chiseled.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS final
ARG VERSION=0.0.0-local
WORKDIR /app

COPY --chown=1654:1654 --from=publish /app/publish .

ENV ASPNETCORE_HTTP_PORTS=
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_EnableDiagnostics=0

ENTRYPOINT ["dotnet", "CardiTrack.PipelineJobs.dll"]

LABEL org.opencontainers.image.title="CardiTrack Pipeline Jobs"
LABEL org.opencontainers.image.description="Scheduled AI-pipeline jobs (daily digests) on chiseled Ubuntu"
LABEL org.opencontainers.image.vendor="CardiTrack"
LABEL org.opencontainers.image.version="$VERSION"

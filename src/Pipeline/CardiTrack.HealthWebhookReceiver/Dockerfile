# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

ARG VERSION=0.0.0-local

COPY ["CardiTrack.sln", "./"]
COPY ["src/Pipeline/CardiTrack.HealthWebhookReceiver/CardiTrack.HealthWebhookReceiver.csproj", "src/Pipeline/CardiTrack.HealthWebhookReceiver/"]
COPY ["src/Infrastructure/CardiTrack.Observability/CardiTrack.Observability.csproj", "src/Infrastructure/CardiTrack.Observability/"]
COPY ["src/Infrastructure/CardiTrack.Shared/CardiTrack.Shared.csproj", "src/Infrastructure/CardiTrack.Shared/"]

RUN dotnet restore "src/Pipeline/CardiTrack.HealthWebhookReceiver/CardiTrack.HealthWebhookReceiver.csproj"

COPY . .

WORKDIR "/src/src/Pipeline/CardiTrack.HealthWebhookReceiver"
RUN dotnet build "CardiTrack.HealthWebhookReceiver.csproj" -c Release -o /app/build /p:Version=$VERSION

# Stage 2: Publish
FROM build AS publish
ARG VERSION=0.0.0-local
RUN dotnet publish "CardiTrack.HealthWebhookReceiver.csproj" -c Release -o /app/publish \
    /p:Version=$VERSION \
    /p:UseAppHost=false \
    /p:PublishTrimmed=false \
    /p:PublishSingleFile=false
RUN mkdir -p /app/publish/logs

# Stage 3: Runtime — chiseled Ubuntu, same base as the other services. This is the platform's
# only public-ingress pipeline surface, so the minimal image matters more here than anywhere.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
ARG VERSION=0.0.0-local
WORKDIR /app

COPY --chown=1654:1654 --from=publish /app/publish .

EXPOSE 8080

ENV ASPNETCORE_HTTP_PORTS=
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_EnableDiagnostics=0

ENTRYPOINT ["dotnet", "CardiTrack.HealthWebhookReceiver.dll"]

LABEL org.opencontainers.image.title="CardiTrack Health Webhook Receiver"
LABEL org.opencontainers.image.description="Authenticates, acknowledges and forwards Google Health API webhook notifications"
LABEL org.opencontainers.image.vendor="CardiTrack"
LABEL org.opencontainers.image.version="$VERSION"

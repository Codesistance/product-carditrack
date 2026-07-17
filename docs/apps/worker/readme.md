# CardiTrack Worker Service

## Overview

`CardiTrack.Worker` is a standard .NET Worker Service (`Microsoft.NET.Sdk.Worker`) that runs the platform's **non-AI scheduled background jobs** — OAuth token refresh, pattern-baseline recalculation, trial expiration reminders, and data-retention/cleanup — using cron expressions and the [Cronos](https://github.com/HangfireIO/Cronos) library. It runs as an ordinary .NET executable — no Azure Functions runtime, no Azure Storage dependency.

> **Scope note:** device data ingestion is **webhook-driven** and handled by the Azure Functions AI pipeline (see [llm_design.md](../../llm_design.md)). The `WearableSyncWorker` polling job documented below is the interim MVP ingestion path and a backfill/fallback mechanism; it is superseded as the primary ingestion route once the webhook pipeline ships (see [release_matrix.md](../../release_matrix.md)).

## Technology Stack

- **.NET 10**: Core framework (`Microsoft.NET.Sdk.Worker`)
- **Cronos 0.8.4**: Cron expression parsing (HangfireIO)
- **BackgroundService**: Built-in .NET hosted service base class
- **Keyed DI** (.NET 10): Per-provider sync service dispatch
- **Entity Framework Core**: SQL Server data access
- **Serilog / `ILogger`**: Structured logging

## Project Structure

```
src/Worker/CardiTrack.Worker/
├── CronBackgroundService.cs    # Abstract base — parses cron, loops on schedule
├── WearableSyncWorker.cs       # Concrete worker — dispatches per-device sync
├── Program.cs                  # Host setup, DI registration
├── appsettings.json            # Configuration skeleton
└── CardiTrack.Worker.csproj    # SDK: Microsoft.NET.Sdk.Worker, Cronos package
```

## Core Components

### CronBackgroundService

Abstract base class that drives any scheduled job via a cron expression.

```csharp
public abstract class CronBackgroundService : BackgroundService
{
    private readonly CronExpression _cron;
    private readonly TimeZoneInfo _timeZone;

    protected CronBackgroundService(string cronExpression, TimeZoneInfo? timeZone = null)
    {
        _cron = CronExpression.Parse(cronExpression, CronFormat.IncludeSeconds);
        _timeZone = timeZone ?? TimeZoneInfo.Utc;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var next = _cron.GetNextOccurrence(now, _timeZone);
            if (next is null) break;

            var delay = next.Value - now;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, stoppingToken);

            if (!stoppingToken.IsCancellationRequested)
                await ExecuteJobAsync(stoppingToken);
        }
    }

    protected abstract Task ExecuteJobAsync(CancellationToken stoppingToken);
}
```

### WearableSyncWorker

The active worker. Reads the cron schedule from config, creates a DI scope per run, and dispatches to the keyed `IDeviceSyncService` for each device type.

```csharp
public class WearableSyncWorker : CronBackgroundService
{
    public WearableSyncWorker(
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        ILogger<WearableSyncWorker> logger)
        : base(configuration["Worker:SyncCronExpression"] ?? "0 */30 * * * *")
    { ... }

    protected override async Task ExecuteJobAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var deviceConnections = scope.ServiceProvider
            .GetRequiredService<IDeviceConnectionRepository>();

        var connections = await deviceConnections.GetDueForSyncAsync(thresholdMinutes: 30);

        foreach (var connection in connections)
        {
            // Keyed by DeviceType — returns null for unregistered providers
            var syncService = scope.ServiceProvider
                .GetKeyedService<IDeviceSyncService>(connection.DeviceType);

            if (syncService is null)
            {
                _logger.LogWarning("No sync service for {DeviceType}. Skipping.", connection.DeviceType);
                continue;
            }

            await syncService.SyncCardiMemberAsync(connection);
        }
    }
}
```

### Multi-Provider Dispatch

Providers register keyed services by `DeviceType` enum via extension methods:

```csharp
// In DeviceProviderServiceExtensions.cs
services.AddKeyedScoped<IDeviceApiClient, FitbitApiClient>(DeviceType.Fitbit);
services.AddKeyedScoped<IDeviceSyncService>(DeviceType.Fitbit,
    (sp, _) => new DeviceSyncService(
        sp.GetRequiredService<IOAuthTokenRefreshService>(),
        sp.GetRequiredKeyedService<IDeviceApiClient>(DeviceType.Fitbit),
        ...));

// To add Garmin later:
// services.AddGarminProvider();
```

Unknown device types produce a `LogWarning` and are skipped — no crash.

## Configuration

### appsettings.json

```json
{
  "ConnectionStrings": {
    "DefaultConnection": ""
  },
  "Encryption": {
    "Key": ""
  },
  "DeviceProviders": [
    {
      "Provider": "Fitbit",
      "ClientId": "",
      "ClientSecret": "",
      "TokenUrl": "https://api.fitbit.com/oauth2/token",
      "TokenLifetimeHours": 8
    }
  ],
  "Worker": {
    "SyncCronExpression": "0 */30 * * * *"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Hosting.Lifetime": "Information"
    }
  }
}
```

### Cron Format

The worker uses 6-field cron with seconds (Cronos `IncludeSeconds`):

| Expression            | Meaning                   |
|-----------------------|---------------------------|
| `0 */30 * * * *`      | Every 30 minutes          |
| `0 0 * * * *`         | Every hour                |
| `0 0 2 * * *`         | Daily at 2 AM UTC         |
| `0 0 2 * * MON`       | Every Monday at 2 AM UTC  |

### Production Secrets

Store sensitive values in environment variables or Azure Key Vault — never in `appsettings.json`:

```
ConnectionStrings__DefaultConnection = <sql connection string>
Encryption__Key                      = <base64 256-bit key>
DeviceProviders__0__ClientId         = <Fitbit client id>
DeviceProviders__0__ClientSecret     = <Fitbit client secret>
```

## Running Locally

```bash
# Navigate to worker project
cd src/Worker/CardiTrack.Worker

# Restore and run
dotnet run
```

The worker logs when it starts and after each sync run:
```
info: CardiTrack.Worker.WearableSyncWorker[0]
      WearableSync triggered at 2026-03-12T06:00:00.000Z
info: CardiTrack.Worker.WearableSyncWorker[0]
      WearableSync complete. Success: 12, Failed: 0.
```

## Deployment

### As a standalone executable

```bash
dotnet publish src/Worker/CardiTrack.Worker -c Release -o ./publish/worker

# Run
./publish/worker/CardiTrack.Worker
```

### As a Docker container

```dockerfile
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "CardiTrack.Worker.dll"]
```

```bash
docker build -t carditrack-worker .
docker run -e ConnectionStrings__DefaultConnection="..." carditrack-worker
```

### On Azure (App Service or Container Apps)

Deploy as a Docker container or zip-deploy the published output to an **App Service** (Always On) or **Azure Container Apps**. Set environment variables via the portal or `az webapp config appsettings set`.

### CI/CD (GitHub Actions)

```yaml
name: Deploy Worker

on:
  push:
    branches: [main]
    paths:
      - 'src/Worker/CardiTrack.Worker/**'
      - 'src/Infrastructure/CardiTrack.Infrastructure/**'

jobs:
  deploy:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v6

      - name: Setup .NET
        uses: actions/setup-dotnet@v5
        with:
          dotnet-version: '10.0.x'

      - name: Build & Test
        run: |
          dotnet build --configuration Release
          dotnet test --no-build

      - name: Publish Worker
        run: dotnet publish src/Worker/CardiTrack.Worker -c Release -o ./publish/worker

      - name: Deploy to Azure Container Apps
        uses: azure/container-apps-deploy-action@v1
        with:
          appSourcePath: ./publish/worker
          acrName: ${{ secrets.ACR_NAME }}
          containerAppName: carditrack-worker
          resourceGroup: carditrack-rg
```

## Adding a New Device Provider

1. Create `GarminApiClient : IDeviceApiClient` in `CardiTrack.Infrastructure/ExternalClients/`
2. Add `AddGarminProvider()` extension in `DeviceProviderServiceExtensions.cs`
3. Call `services.AddGarminProvider()` in `Program.cs`
4. The worker picks it up automatically — no other changes needed

## Monitoring

The worker uses `ILogger<WearableSyncWorker>` with structured log properties. Route to Application Insights, Seq, or any logging sink by configuring the host:

```csharp
Host.CreateDefaultBuilder(args)
    .ConfigureLogging(logging =>
    {
        logging.AddApplicationInsights(config["ApplicationInsights:ConnectionString"]);
    })
```

### Key log events

| Message | Level | Meaning |
|---|---|---|
| `WearableSync triggered at {Time}` | Info | Job started |
| `Synced DeviceConnection {Id}` | Info | One device synced OK |
| `No sync service for {DeviceType}` | Warning | Provider not registered |
| `Failed to sync DeviceConnection {Id}` | Error | API/network failure |
| `WearableSync complete. Success: {S}, Failed: {F}` | Info | Run summary |

## Related Documentation

- [API Documentation](../api/readme.md)
- [Web Dashboard Documentation](../web/readme.md)
- [Mobile App Documentation](../mobile/readme.md)
- [Infrastructure Guide](../../infrastructure.md)

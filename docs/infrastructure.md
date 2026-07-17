# CardiTrack Infrastructure Guide

## Overview

This document covers the complete infrastructure setup for CardiTrack, including database schema, encryption, deployment, and cloud resources.

## Table of Contents

1. [Storage Boundary](#storage-boundary)
2. [Database Infrastructure](#database-infrastructure)
3. [Entity Framework Core Setup](#entity-framework-core-setup)
4. [Security & Encryption](#security--encryption)
5. [Cloud Infrastructure (Azure)](#cloud-infrastructure-azure)
6. [Terraform Configuration](#terraform-configuration)
7. [CI/CD Pipeline](#cicd-pipeline)
8. [Monitoring & Observability](#monitoring--observability)
9. [Scaling Strategy](#scaling-strategy)

---

## Storage Boundary

CardiTrack uses two data planes with a strict boundary:

| Plane | Store | Holds |
|-------|-------|-------|
| **Transactional (system of record)** | **Azure SQL** + EF Core | Identity (users, orgs, roles), CardiMembers, device connections + **encrypted OAuth tokens**, normalized activity logs, baselines, alerts, subscriptions, invitations, notes, preferences, audit logs |
| **AI pipeline** | **Azure Cosmos DB** (serverless) | MedGemma inference results, prediction cards, trend aggregates, digest logs — see [llm_design.md](./llm_design.md) |

Rules:
- Azure SQL is the **only** system of record. Cosmos DB holds derived AI outputs that can be regenerated; nothing in Cosmos is authoritative for identity, consent, billing, or audit.
- OAuth tokens, user profiles, and family relationships live in Azure SQL (encrypted where sensitive) — **not** in Table Storage.
- The AI pipeline reads reference data (tokens, relationships, sensitivity settings) from SQL and writes results to Cosmos DB.

---

## Database Infrastructure

### Database Provider
- **Primary**: SQL Server (Azure SQL Database)
- **Alternative**: PostgreSQL (supported)
- **HIPAA Compliance**: TDE (Transparent Data Encryption) enabled

### Core Tables

#### Organizations
Multi-tenant support for families and businesses.

```sql
CREATE TABLE Organizations (
    Id UNIQUEIDENTIFIER PRIMARY KEY,
    Name NVARCHAR(200) NOT NULL,
    Type NVARCHAR(50) NOT NULL, -- 'Family' or 'Business'
    SubscriptionId UNIQUEIDENTIFIER,
    IsActive BIT DEFAULT 1,
    CreatedDate DATETIME2 DEFAULT GETUTCDATE(),
    UpdatedDate DATETIME2
);
```

#### Users
Family members and caregivers.

```sql
CREATE TABLE Users (
    Id UNIQUEIDENTIFIER PRIMARY KEY,
    OrganizationId UNIQUEIDENTIFIER NOT NULL,
    Auth0UserId NVARCHAR(128) UNIQUE NOT NULL, -- Auth0 owns credentials; no local passwords
    Email NVARCHAR(255) UNIQUE NOT NULL,
    FirstName NVARCHAR(100) NOT NULL,
    LastName NVARCHAR(100) NOT NULL,
    Phone NVARCHAR(20),
    Role NVARCHAR(50) NOT NULL, -- 'Admin', 'Staff', 'Viewer'
    IsActive BIT DEFAULT 1,
    CreatedDate DATETIME2 DEFAULT GETUTCDATE(),
    UpdatedDate DATETIME2
);
```

#### CardiMembers
People being monitored (elderly individuals).

```sql
CREATE TABLE CardiMembers (
    Id UNIQUEIDENTIFIER PRIMARY KEY,
    OrganizationId UNIQUEIDENTIFIER NOT NULL,
    FirstName NVARCHAR(100) NOT NULL,
    LastName NVARCHAR(100) NOT NULL,
    Email NVARCHAR(255),
    Phone NVARCHAR(20),
    DateOfBirth DATE NOT NULL,
    Gender NVARCHAR(20),
    MedicalNotes NVARCHAR(2000), -- Encrypted
    MonitoringPausedUntil DATETIME2, -- NULL = monitoring active
    MonitoringPauseReason NVARCHAR(255),
    IsActive BIT DEFAULT 1,
    CreatedDate DATETIME2 DEFAULT GETUTCDATE(),
    UpdatedDate DATETIME2
);
```

#### DeviceConnections
Multi-device OAuth connections per CardiMember.

```sql
CREATE TABLE DeviceConnections (
    Id UNIQUEIDENTIFIER PRIMARY KEY,
    CardiMemberId UNIQUEIDENTIFIER NOT NULL,
    DeviceId UNIQUEIDENTIFIER NOT NULL, -- Reference to Devices table
    DeviceType NVARCHAR(50) NOT NULL, -- 'Fitbit', 'AppleWatch', 'Garmin', etc.
    DeviceUserId NVARCHAR(100),
    AccessToken NVARCHAR(2000), -- Encrypted
    RefreshToken NVARCHAR(2000), -- Encrypted
    TokenExpiry DATETIME2,
    Scopes NVARCHAR(500), -- JSON array
    Status NVARCHAR(50) NOT NULL, -- 'Pending', 'Connected', 'Disconnected', 'TokenExpired', 'AuthError', 'SyncError'
    IsPrimary BIT DEFAULT 0,
    ConnectedDate DATETIME2,
    LastSyncDate DATETIME2,
    Metadata NVARCHAR(MAX), -- JSON for device-specific data
    IsActive BIT DEFAULT 1,
    CreatedDate DATETIME2 DEFAULT GETUTCDATE(),
    UpdatedDate DATETIME2
);
```

#### ActivityLogs
Normalized, device-agnostic health data.

```sql
CREATE TABLE ActivityLogs (
    Id UNIQUEIDENTIFIER PRIMARY KEY,
    CardiMemberId UNIQUEIDENTIFIER NOT NULL,
    DeviceConnectionId UNIQUEIDENTIFIER,
    DataSource NVARCHAR(50) NOT NULL, -- Which device provided data
    Date DATE NOT NULL,
    Steps INT,
    Distance DECIMAL(10,2),
    Floors INT,
    ActiveMinutes INT,
    SedentaryMinutes INT,
    CaloriesBurned INT,
    RestingHeartRate INT,
    AvgHeartRate INT,
    MaxHeartRate INT,
    HrvAverage INT, -- Heart Rate Variability
    SleepMinutes INT,
    SleepStartTime DATETIME2,
    SleepEndTime DATETIME2,
    SleepEfficiency INT,
    DeepSleepMinutes INT,
    LightSleepMinutes INT,
    RemSleepMinutes INT,
    AwakeMinutes INT,
    SpO2Average DECIMAL(5,2),
    SpO2Min DECIMAL(5,2),
    SpO2Max DECIMAL(5,2),
    VO2Max DECIMAL(5,2),
    BreathingRate DECIMAL(5,2),
    Temperature DECIMAL(5,2),
    StressScore INT,
    RawData NVARCHAR(MAX), -- JSON for device-specific extras
    CreatedDate DATETIME2 DEFAULT GETUTCDATE(),
    CONSTRAINT UQ_ActivityLog_CardiMember_Date_Source UNIQUE(CardiMemberId, Date, DataSource)
);
```

#### PatternBaselines
AI-learned normal health patterns.

```sql
CREATE TABLE PatternBaselines (
    Id UNIQUEIDENTIFIER PRIMARY KEY,
    CardiMemberId UNIQUEIDENTIFIER NOT NULL,
    CalculatedDate DATETIME2 NOT NULL,
    PeriodDays INT NOT NULL, -- e.g., 30, 60, 90
    AvgSteps INT,
    StdDevSteps DECIMAL(10,2),
    AvgActiveMinutes INT,
    StdDevActiveMinutes DECIMAL(10,2),
    AvgRestingHeartRate INT,
    StdDevRestingHeartRate DECIMAL(10,2),
    AvgSleepMinutes INT,
    StdDevSleepMinutes DECIMAL(10,2),
    TypicalBedtime TIME,
    TypicalWakeTime TIME,
    AvgSleepEfficiency INT,
    StepsByDayOfWeek NVARCHAR(500), -- JSON array
    IsActive BIT DEFAULT 1,
    CreatedDate DATETIME2 DEFAULT GETUTCDATE()
);
```

#### Alerts
Health alerts generated by pattern analysis.

```sql
CREATE TABLE Alerts (
    Id UNIQUEIDENTIFIER PRIMARY KEY,
    CardiMemberId UNIQUEIDENTIFIER NOT NULL,
    AlertType NVARCHAR(50) NOT NULL, -- 'activity_decline', 'elevated_heart_rate', 'no_morning_activity', 'irregular_sleep', 'device_disconnected', 'long_term_trend'
    Severity NVARCHAR(20) NOT NULL, -- 'yellow', 'orange', 'red' (alerts exist only for non-green states)
    Title NVARCHAR(255) NOT NULL,
    Message NVARCHAR(MAX) NOT NULL,
    MetricValues NVARCHAR(MAX), -- JSON with relevant metrics
    TriggeredDate DATETIME2 NOT NULL,
    AcknowledgedDate DATETIME2,
    AcknowledgedBy UNIQUEIDENTIFIER, -- UserId
    IsResolved BIT DEFAULT 0,
    ResolvedDate DATETIME2,
    ResolvedBy UNIQUEIDENTIFIER, -- UserId
    CreatedDate DATETIME2 DEFAULT GETUTCDATE()
);
```

#### AuditLogs
HIPAA-compliant audit trail.

```sql
CREATE TABLE AuditLogs (
    Id UNIQUEIDENTIFIER PRIMARY KEY,
    UserId UNIQUEIDENTIFIER,
    CardiMemberId UNIQUEIDENTIFIER,
    Action NVARCHAR(100) NOT NULL,
    EntityType NVARCHAR(100),
    EntityId UNIQUEIDENTIFIER,
    Timestamp DATETIME2 DEFAULT GETUTCDATE(),
    IpAddress NVARCHAR(50),
    UserAgent NVARCHAR(500),
    DataAccessed NVARCHAR(MAX), -- JSON summary
    ChangedFields NVARCHAR(MAX), -- JSON of before/after
    CreatedDate DATETIME2 DEFAULT GETUTCDATE()
);
```

> **AuditLogs retention:** 6 years (HIPAA documentation retention). Rows older than 1 year are moved to an archive tier (Azure Blob, immutable storage); the most recent year stays queryable in SQL.

#### Devices (Catalog)
Reference data for supported wearable devices.

```sql
CREATE TABLE Devices (
    Id UNIQUEIDENTIFIER PRIMARY KEY,
    DeviceType NVARCHAR(50) NOT NULL, -- 'Fitbit', 'AppleWatch', 'Garmin', ...
    Manufacturer NVARCHAR(100) NOT NULL,
    ModelName NVARCHAR(100) NOT NULL,
    Capabilities NVARCHAR(MAX), -- JSON: heartRate, spo2, ecg, steps, sleep, gps
    IntegrationMode NVARCHAR(30) NOT NULL, -- 'server_oauth' | 'on_device_bridge' (Apple Health)
    OAuthConfig NVARCHAR(MAX), -- JSON; NULL for on-device providers
    IsActive BIT DEFAULT 1,
    CreatedDate DATETIME2 DEFAULT GETUTCDATE(),
    UpdatedDate DATETIME2
);
```

#### Subscriptions
Billing state per organization (Stripe-backed).

```sql
CREATE TABLE Subscriptions (
    Id UNIQUEIDENTIFIER PRIMARY KEY,
    OrganizationId UNIQUEIDENTIFIER NOT NULL,
    Tier NVARCHAR(50) NOT NULL, -- 'Basic', 'Complete', 'Plus'
    Status NVARCHAR(50) NOT NULL, -- 'Trialing', 'Active', 'PastDue', 'Cancelled', 'Suspended'
    BillingCycle NVARCHAR(20) NOT NULL, -- 'Monthly', 'Annual'
    PricePerMonth DECIMAL(10,2) NOT NULL,
    Currency NVARCHAR(3) DEFAULT 'USD',
    MaxCardiMembers INT NOT NULL, -- Basic: 2, Complete: 5
    MaxUsers INT NOT NULL,        -- Basic: 5, Complete: 20
    TrialEndsAt DATETIME2,
    CurrentPeriodStart DATE,
    CurrentPeriodEnd DATE,
    CancelAtPeriodEnd BIT DEFAULT 0,
    StripeCustomerId NVARCHAR(100),
    StripeSubscriptionId NVARCHAR(100),
    Features NVARCHAR(MAX), -- JSON
    CreatedDate DATETIME2 DEFAULT GETUTCDATE(),
    UpdatedDate DATETIME2
);
```

#### UserCardiMembers
Many-to-many join between Users and CardiMembers.

```sql
CREATE TABLE UserCardiMembers (
    Id UNIQUEIDENTIFIER PRIMARY KEY,
    UserId UNIQUEIDENTIFIER NOT NULL,
    CardiMemberId UNIQUEIDENTIFIER NOT NULL,
    RelationshipType NVARCHAR(50) NOT NULL, -- 'Self', 'Parent', 'Spouse', ...
    IsPrimaryCaregiver BIT DEFAULT 0,
    CreatedDate DATETIME2 DEFAULT GETUTCDATE(),
    CONSTRAINT UQ_UserCardiMember UNIQUE(UserId, CardiMemberId)
);
```

### Feature Tables

These tables back API features defined in [/execution/backend/api/](./execution/backend/api/readme.md).

```sql
-- Emergency contacts (up to 5 per CardiMember) — cardimembers.md
CREATE TABLE EmergencyContacts (
    Id UNIQUEIDENTIFIER PRIMARY KEY,
    CardiMemberId UNIQUEIDENTIFIER NOT NULL,
    Name NVARCHAR(200) NOT NULL,
    Phone NVARCHAR(20) NOT NULL,
    Relationship NVARCHAR(50),
    SortOrder INT DEFAULT 0,
    CreatedDate DATETIME2 DEFAULT GETUTCDATE()
);

-- Consent history (append-only; latest row is current) — cardimembers.md
CREATE TABLE ConsentRecords (
    Id UNIQUEIDENTIFIER PRIMARY KEY,
    CardiMemberId UNIQUEIDENTIFIER NOT NULL,
    ShareActivity BIT NOT NULL,
    ShareHeartRate BIT NOT NULL,
    ShareSleep BIT NOT NULL,
    ConsentedByName NVARCHAR(200) NOT NULL,
    ConsentMethod NVARCHAR(30) NOT NULL, -- 'digital_signature' | 'verbal_confirmed'
    ConsentedAt DATETIME2 DEFAULT GETUTCDATE()
);

-- Family invitations (7-day expiry) — family.md
CREATE TABLE FamilyInvitations (
    Id UNIQUEIDENTIFIER PRIMARY KEY,
    OrganizationId UNIQUEIDENTIFIER NOT NULL,
    Email NVARCHAR(255) NOT NULL,
    Role NVARCHAR(50) NOT NULL, -- 'Admin', 'Staff', 'Viewer'
    Message NVARCHAR(500),
    Status NVARCHAR(30) NOT NULL, -- 'Pending', 'Accepted', 'Revoked', 'Expired'
    InvitedByUserId UNIQUEIDENTIFIER NOT NULL,
    ExpiresAt DATETIME2 NOT NULL,
    SentAt DATETIME2 DEFAULT GETUTCDATE()
);

-- Shared care-coordination notes with @mentions — family.md
CREATE TABLE SharedNotes (
    Id UNIQUEIDENTIFIER PRIMARY KEY,
    CardiMemberId UNIQUEIDENTIFIER NOT NULL,
    AuthorUserId UNIQUEIDENTIFIER NOT NULL,
    Content NVARCHAR(2000) NOT NULL,
    MentionedUserIds NVARCHAR(MAX), -- JSON array of UserIds
    ViewReceipts NVARCHAR(MAX),     -- JSON array of {userId, viewedAt}
    CreatedDate DATETIME2 DEFAULT GETUTCDATE()
);

-- CardiMember self-authored notes — cardimembers.md
CREATE TABLE CardiMemberNotes (
    Id UNIQUEIDENTIFIER PRIMARY KEY,
    CardiMemberId UNIQUEIDENTIFIER NOT NULL,
    Content NVARCHAR(1000) NOT NULL,
    CreatedDate DATETIME2 DEFAULT GETUTCDATE()
);

-- Alert follow-up notes and photo attachments — alerts.md
CREATE TABLE AlertNotes (
    Id UNIQUEIDENTIFIER PRIMARY KEY,
    AlertId UNIQUEIDENTIFIER NOT NULL,
    AuthorUserId UNIQUEIDENTIFIER NOT NULL,
    Content NVARCHAR(2000) NOT NULL,
    ActionTaken NVARCHAR(50), -- id from recommendedActions (analytics)
    CreatedDate DATETIME2 DEFAULT GETUTCDATE()
);

CREATE TABLE AlertPhotos (
    Id UNIQUEIDENTIFIER PRIMARY KEY,
    AlertId UNIQUEIDENTIFIER NOT NULL,
    UploadedByUserId UNIQUEIDENTIFIER NOT NULL,
    BlobUrl NVARCHAR(500) NOT NULL, -- Azure Blob Storage
    Caption NVARCHAR(255),
    UploadedAt DATETIME2 DEFAULT GETUTCDATE()
);

-- Per-CardiMember alert preferences (quiet hours, sensitivity, routing) — alerts.md
CREATE TABLE AlertPreferences (
    Id UNIQUEIDENTIFIER PRIMARY KEY,
    CardiMemberId UNIQUEIDENTIFIER NOT NULL UNIQUE,
    Sensitivity NVARCHAR(20) NOT NULL DEFAULT 'medium', -- 'low' | 'medium' | 'high'
    Channels NVARCHAR(MAX),          -- JSON: {push, email, sms}
    QuietHours NVARCHAR(MAX),        -- JSON: {enabled, from, to, timezone, overrideForSeverity}
    AlertTypeSettings NVARCHAR(MAX), -- JSON array: [{type, enabled, minSeverity}]
    FamilyRoutingRules NVARCHAR(MAX),-- JSON array: [{userId, receivesSeverity}]
    UpdatedDate DATETIME2
);

-- Push notification device tokens — notifications.md
CREATE TABLE PushNotificationTokens (
    Id UNIQUEIDENTIFIER PRIMARY KEY,
    UserId UNIQUEIDENTIFIER NOT NULL,
    DeviceId NVARCHAR(100) NOT NULL,
    Platform NVARCHAR(10) NOT NULL, -- 'ios' | 'android'
    PushToken NVARCHAR(500) NOT NULL,
    AppVersion NVARCHAR(20),
    LastSeenAt DATETIME2,
    CreatedDate DATETIME2 DEFAULT GETUTCDATE(),
    CONSTRAINT UQ_PushToken_User_Device UNIQUE(UserId, DeviceId)
);

-- Per-user global notification preferences — notifications.md
CREATE TABLE NotificationPreferences (
    Id UNIQUEIDENTIFIER PRIMARY KEY,
    UserId UNIQUEIDENTIFIER NOT NULL UNIQUE,
    GlobalChannels NVARCHAR(MAX), -- JSON: {push, email, sms}
    WeeklyDigest NVARCHAR(MAX),   -- JSON: {enabled, deliveryDay, deliveryTime, timezone}
    UpdatedDate DATETIME2
);

-- Async report generation — reports.md
CREATE TABLE Reports (
    Id UNIQUEIDENTIFIER PRIMARY KEY,
    RequestedByUserId UNIQUEIDENTIFIER NOT NULL,
    CardiMemberIds NVARCHAR(MAX) NOT NULL, -- JSON array (max 5)
    Format NVARCHAR(20) NOT NULL, -- 'pdf' | 'csv' | 'fhir_r4' | 'hl7_v2'
    Parameters NVARCHAR(MAX), -- JSON: dateRange, sections, fhirProfile, fhirResources, title
    Status NVARCHAR(20) NOT NULL, -- 'Pending', 'Ready', 'Failed', 'Expired'
    BlobUrl NVARCHAR(500),
    FileSizeBytes BIGINT,
    Error NVARCHAR(1000),
    DownloadExpiresAt DATETIME2, -- 24h after completion
    CreatedDate DATETIME2 DEFAULT GETUTCDATE(),
    CompletedAt DATETIME2
);
```

> **Biometric login:** no server-side table. Under the Auth0-only auth model, biometrics are a **local device gate** that unlocks the securely stored Auth0 refresh token — see [auth.md](./execution/backend/api/auth.md).

### Indexes for Performance

```sql
-- Activity Logs
CREATE INDEX IX_ActivityLogs_CardiMemberId ON ActivityLogs(CardiMemberId);
CREATE INDEX IX_ActivityLogs_Date ON ActivityLogs(Date);
CREATE INDEX IX_ActivityLogs_DataSource ON ActivityLogs(DataSource);

-- Alerts
CREATE INDEX IX_Alerts_CardiMemberId ON Alerts(CardiMemberId);
CREATE INDEX IX_Alerts_TriggeredDate ON Alerts(TriggeredDate);
CREATE INDEX IX_Alerts_Severity ON Alerts(Severity);

-- Device Connections
CREATE INDEX IX_DeviceConnections_CardiMemberId ON DeviceConnections(CardiMemberId);
CREATE INDEX IX_DeviceConnections_Status ON DeviceConnections(Status);

-- Audit Logs
CREATE INDEX IX_AuditLogs_CardiMemberId_Timestamp ON AuditLogs(CardiMemberId, Timestamp);
CREATE INDEX IX_AuditLogs_UserId_Timestamp ON AuditLogs(UserId, Timestamp);
```

---

## Entity Framework Core Setup

### DbContext Configuration

```csharp
public class CardiTrackDbContext : DbContext
{
    public DbSet<Organization> Organizations { get; set; }
    public DbSet<User> Users { get; set; }
    public DbSet<CardiMember> CardiMembers { get; set; }
    public DbSet<DeviceConnection> DeviceConnections { get; set; }
    public DbSet<ActivityLog> ActivityLogs { get; set; }
    public DbSet<PatternBaseline> PatternBaselines { get; set; }
    public DbSet<Alert> Alerts { get; set; }
    public DbSet<AuditLog> AuditLogs { get; set; }

    public override int SaveChanges()
    {
        UpdateTimestamps();
        return base.SaveChanges();
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        UpdateTimestamps();
        return await base.SaveChangesAsync(cancellationToken);
    }

    private void UpdateTimestamps()
    {
        var entries = ChangeTracker.Entries()
            .Where(e => e.Entity is BaseEntity && e.State == EntityState.Modified);

        foreach (var entry in entries)
        {
            ((BaseEntity)entry.Entity).UpdatedDate = DateTime.UtcNow;
        }
    }
}
```

### Creating Migrations

```bash
# Navigate to Infrastructure project
cd src/Infrastructure/CardiTrack.Infrastructure

# Create migration
dotnet ef migrations add InitialCreate --startup-project ../../Presentation/CardiTrack.API

# Update database
dotnet ef database update --startup-project ../../Presentation/CardiTrack.API
```

---

## Security & Encryption

### AES-256-GCM Encryption

CardiTrack uses AES-256-GCM (Galois/Counter Mode) for field-level encryption of sensitive data.

```csharp
public class AesEncryptionService : IEncryptionService
{
    private readonly byte[] _key;

    public string Encrypt(string plaintext)
    {
        using var aes = new AesGcm(_key);
        var nonce = new byte[AesGcm.NonceByteSizes.MaxSize];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        var ciphertext = new byte[plaintext.Length];

        RandomNumberGenerator.Fill(nonce);
        aes.Encrypt(nonce, Encoding.UTF8.GetBytes(plaintext), ciphertext, tag);

        return Convert.ToBase64String(nonce.Concat(tag).Concat(ciphertext).ToArray());
    }

    public string Decrypt(string ciphertext)
    {
        var combined = Convert.FromBase64String(ciphertext);
        // Decryption logic...
    }
}
```

### Encrypted Fields

- `DeviceConnection.AccessToken`
- `DeviceConnection.RefreshToken`
- `CardiMember.MedicalNotes`

### Key Management

**Development:**
- Store in `appsettings.Development.json`

**Production:**
- **Azure Key Vault** (recommended)
- Managed identities for access

```json
{
  "Encryption": {
    "Key": "<<NEVER COMMIT THIS>>"
  },
  "KeyVault": {
    "VaultUri": "https://carditrack-kv.vault.azure.net/"
  }
}
```

---

## Cloud Infrastructure (Azure)

### Resource Group Structure

```
carditrack-dev-rg
├── carditrack-dev-app (App Service — API)
├── carditrack-dev-sql (SQL Database — transactional system of record)
├── carditrack-dev-worker (App Service / Container App — Worker, non-AI jobs)
├── carditrack-dev-kv (Key Vault)
├── carditrack-dev-insights (Application Insights)
├── carditrack-dev-signalr (SignalR Service)
│
│   AI pipeline (from AI rollout — see llm_design.md)
├── carditrack-dev-eh (Event Hubs — fitbit-raw ingestion buffer)
├── carditrack-dev-cosmos (Cosmos DB serverless — AI results)
├── carditrack-dev-func (Azure Functions — pipeline logic, dotnet-isolated)
├── carditrack-dev-aca (Container Apps env + GPU workload profile — MedGemma/vLLM)
├── carditrack-dev-notif (Notification Hubs — FCM/APNs routing)
└── carditrack-dev-blob (Blob Storage — per-user LSTM models, report files, audit archive)
```

### Cost Estimates

#### MVP Phase (0-100 users, pre-AI rollout)
- **App Service** (Basic B1 — API): $13/month
- **Azure SQL** (Basic): $5/month
- **Worker** (App Service Basic B1): $13/month
- **Key Vault**: Free tier
- **Total**: ~$31-35/month

#### Growth Phase (1,000-10,000 users, AI pipeline live)
- **App Service** (Premium P1V2 — API): $146/month
- **Azure SQL** (Standard S2): $75/month
- **Worker** (Container App): ~$30-50/month
- **SignalR** (Standard): $50/month
- **ACA T4 GPU** (1 replica always-on, MedGemma inference): ~$430/month
- **Event Hubs** (Standard, 1 TU): ~$11/month
- **Cosmos DB** (serverless): ~$25/month
- **Notification Hubs** (Basic): ~$6/month
- **Azure Functions** (consumption): near-zero at this scale
- **Total**: ~$775-795/month + third-party services

---

## Terraform Configuration

### Project Structure

```
infrastructure/
├── main.tf
├── variables.tf
├── outputs.tf
├── providers.tf
├── versions.tf
├── backend.tf
├── environments/
│   ├── dev.tfvars
│   ├── staging.tfvars
│   └── prod.tfvars
└── deployments/
    ├── app_service.tf
    ├── azure_sql.tf
    ├── worker.tf
    ├── key_vault.tf
    ├── monitoring.tf
    ├── signalr.tf
    ├── event_hubs.tf        # AI pipeline: ingestion buffer
    ├── cosmos_db.tf         # AI pipeline: results store
    ├── functions.tf         # AI pipeline: dotnet-isolated Function App
    ├── container_apps.tf    # AI pipeline: ACA env + GPU workload profile (MedGemma/vLLM)
    └── notification_hubs.tf # AI pipeline: push routing
```

### Example: App Service

```hcl
resource "azurerm_service_plan" "main" {
  name                = "${var.prefix}-plan"
  resource_group_name = azurerm_resource_group.main.name
  location            = var.location
  os_type             = "Linux"
  sku_name            = var.app_service_sku
}

resource "azurerm_linux_web_app" "api" {
  name                = "${var.prefix}-api"
  resource_group_name = azurerm_resource_group.main.name
  location            = var.location
  service_plan_id     = azurerm_service_plan.main.id

  site_config {
    always_on = true
    application_stack {
      dotnet_version = "10.0"
    }
  }

  app_settings = {
    "ASPNETCORE_ENVIRONMENT" = var.environment
    "KeyVault__VaultUri"     = azurerm_key_vault.main.vault_uri
  }

  identity {
    type = "SystemAssigned"
  }
}
```

### Deploying Infrastructure

```bash
# Initialize Terraform
terraform init

# Plan changes
terraform plan -var-file="environments/dev.tfvars"

# Apply infrastructure
terraform apply -var-file="environments/dev.tfvars"
```

---

## CI/CD Pipeline

### GitHub Actions Workflow

```yaml
name: Deploy CardiTrack

on:
  push:
    branches: [main]

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v3

      - name: Setup .NET
        uses: actions/setup-dotnet@v3
        with:
          dotnet-version: '10.0.x'

      - name: Restore dependencies
        run: dotnet restore

      - name: Build
        run: dotnet build --configuration Release

      - name: Test
        run: dotnet test --no-build --verbosity normal

      - name: Publish API
        run: dotnet publish src/Presentation/CardiTrack.API -c Release -o ./publish/api

      - name: Deploy to Azure
        uses: azure/webapps-deploy@v2
        with:
          app-name: carditrack-dev-api
          publish-profile: ${{ secrets.AZURE_WEBAPP_PUBLISH_PROFILE }}
          package: ./publish/api
```

---

## Monitoring & Observability

### Application Insights

```csharp
services.AddApplicationInsightsTelemetry(options =>
{
    options.ConnectionString = configuration["ApplicationInsights:ConnectionString"];
});
```

### Key Metrics to Track

- **API Performance**: Request duration, failure rate
- **Device Sync Success**: % successful syncs per device type
- **Alert Accuracy**: False positive rate
- **Database Performance**: Query duration, deadlocks
- **Token Refresh**: Success rate per device

### Alerts Configuration

```hcl
resource "azurerm_monitor_metric_alert" "api_errors" {
  name                = "api-error-rate"
  resource_group_name = azurerm_resource_group.main.name
  scopes              = [azurerm_linux_web_app.api.id]

  criteria {
    metric_namespace = "Microsoft.Web/sites"
    metric_name      = "Http5xx"
    aggregation      = "Total"
    operator         = "GreaterThan"
    threshold        = 10
  }

  action {
    action_group_id = azurerm_monitor_action_group.main.id
  }
}
```

---

## Scaling Strategy

### Horizontal Scaling

**API & Web:**
- Auto-scale based on CPU (>70%)
- Auto-scale based on memory (>80%)
- Max instances: 10

**Background Jobs:**
- `CardiTrack.Worker` (non-AI jobs: token refresh, baseline recalculation, cleanup) deploys as an Azure Container App or App Service; scale out by running multiple replicas — each instance claims a DI scope per cron tick
- AI pipeline Functions scale automatically on the consumption plan; MedGemma inference scales 1→5 ACA GPU replicas on HTTP concurrency (see [llm_design.md](./llm_design.md))

### Database Scaling

**Read Replicas:**
- Create read-only replica for dashboard queries
- Use read replica for reporting

**Partitioning:**
- Partition `ActivityLogs` by CardiMemberId
- Archive logs older than 2 years to cold storage

### Caching Strategy

- **Redis Cache**: User sessions, dashboard data
- **In-Memory Cache**: Reference data (devices, capabilities)
- **CDN**: Static assets

---

## Backup & Disaster Recovery

### Database Backups

- **Automated backups**: Enabled (7-day retention)
- **Long-term retention**: Monthly backups (1 year)
- **Geo-redundant**: Enabled for production

### Recovery Procedures

**RTO (Recovery Time Objective):** 4 hours
**RPO (Recovery Point Objective):** 1 hour

```bash
# Restore database from backup
az sql db restore \
  --resource-group carditrack-prod-rg \
  --server carditrack-prod-sql \
  --name carditrack-db \
  --dest-name carditrack-db-restored \
  --time "2026-01-08T10:00:00Z"
```

---

## Security Best Practices

### Network Security

- **VNet Integration**: Isolate App Service
- **Private Endpoints**: SQL Database accessible only from VNet
- **NSG Rules**: Restrict inbound traffic
- **DDoS Protection**: Standard tier for production

### Access Control

- **Managed Identities**: For Azure service authentication
- **RBAC**: Least privilege access
- **Key Vault**: Centralized secret management
- **MFA**: Required for admin access

### Compliance

- **HIPAA**: Business Associate Agreement with Microsoft
- **SOC 2**: Azure compliance inherited
- **Encryption**: TLS 1.2+ enforced
- **Audit Logging**: All PHI access tracked

---

## Troubleshooting

### Common Issues

**Migration Failures:**
```bash
# Drop and recreate database
dotnet ef database drop --force
dotnet ef database update
```

**Connection Issues:**
```bash
# Test SQL connection
sqlcmd -S carditrack-dev-sql.database.windows.net -U admin -P <password>
```

**Terraform State Lock:**
```bash
# Force unlock (use with caution)
terraform force-unlock <lock-id>
```

---

## References

- [Entity Framework Core Documentation](https://docs.microsoft.com/ef/core/)
- [Azure SQL Database](https://docs.microsoft.com/azure/azure-sql/)
- [Terraform Azure Provider](https://registry.terraform.io/providers/hashicorp/azurerm/latest/docs)
- [HIPAA Compliance on Azure](https://docs.microsoft.com/azure/compliance/offerings/offering-hipaa-us)

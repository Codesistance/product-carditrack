# CardiTrack Entity Summary

This document provides an overview of all domain entities in the CardiTrack system. All entities live in **Azure SQL**, the transactional system of record (see [storage boundary](../infrastructure.md#storage-boundary)); AI pipeline outputs live in Cosmos DB and are documented in [llm_design.md](../llm_design.md).

## Entity Overview

### Core Entities

#### 1. **Organization**
- Represents either a Family account or Business (care home)
- Contains: Name, Type (Family/Business), IsActive
- **No FK constraints** - uses Guid references only

#### 2. **User**
- Login account for family members or care home staff
- Contains: Auth0UserId (unique — Auth0 owns credentials, no local passwords), Email, Name, Phone, Role, OrganizationId
- Role hidden in UI for Family type organizations

#### 3. **CardiMember**
- Person being monitored (can be the User themselves)
- Contains: Name, Email, Phone, DateOfBirth, Gender, OrganizationId
- Monitoring pause state: MonitoringPausedUntil, MonitoringPauseReason
- Medical notes stored encrypted
- Links to devices, activity logs, alerts, and pattern baselines

#### 4. **UserCardiMember** (Join Table)
- Many-to-many relationship between Users and CardiMembers
- Contains: RelationshipType, IsPrimaryCaregiver, permissions
- Enables multiple users to monitor same CardiMember (care home scenario)
- Also scopes AI-pipeline family reads (see [llm_design.md](../llm_design.md) privacy guardrails)

### Device & Health Data Entities

#### 5. **DeviceConnection**
- Stores OAuth tokens for connected wearable devices
- **Device-agnostic design** - supports Fitbit, Apple Watch, Garmin, Samsung, etc.
- Contains: DeviceType, ConnectionStatus, AccessToken (encrypted), RefreshToken (encrypted)
- On-device-bridge providers (Apple Health) have connection records without tokens
- No FK constraints - uses CardiMemberId (Guid)

#### 6. **ActivityLog**
- Normalized health data from all device types
- Contains: Steps, Heart Rate, Sleep metrics, SpO2, VO2Max, etc.
- Tracks DataSource (which device provided the data)
- No FK constraints - uses CardiMemberId and DeviceConnectionId (Guid)

#### 7. **Alert**
- AI-generated health alerts
- AlertType: ActivityDecline, ElevatedHeartRate, NoMorningActivity, IrregularSleep, DeviceDisconnected, LongTermTrend
- Alert severity levels: Yellow, Orange, Red (Green is a *health status*, not an alert severity — no alert exists for normal states)
- Tracks acknowledgment by users; lifecycle `New → Acknowledged → Resolved`

#### 8. **PatternBaseline**
- AI-learned normal patterns for each CardiMember
- Calculated over 30, 60, or 90 day periods
- Contains: Average steps, heart rate baselines, sleep patterns
- Includes day-of-week variations (JSON)

### Business Entities

#### 9. **Subscription**
- Billing and subscription management (Stripe-backed)
- Contains: Tier (Basic, Complete, Plus), Status, Price, BillingCycle
- MaxCardiMembers and MaxUsers (tier limits — Basic: 2/5, Complete: 5/20)
- TrialEndsAt (30-day free trial), Stripe customer/subscription IDs
- Features stored as JSON for flexibility

#### 10. **Device** (Catalog)
- Reference data for supported wearable devices
- Contains: DeviceType, Manufacturer, ModelName, Capabilities (JSON)
- IntegrationMode: ServerOAuth or OnDeviceBridge (Apple Health)
- OAuth configuration stored as JSON (null for on-device providers)
- Used for UI display and capability checking

### Compliance Entities

#### 11. **AuditLog**
- HIPAA compliance audit trail
- Tracks all PHI (Protected Health Information) access
- Contains: UserId, CardiMemberId, Action, EntityType, Timestamp
- IP address, user agent, request details
- **6-year retention** (1 year hot in SQL, then archive tier)

### Feature Entities

These back the API features in [/execution/backend/api/](../execution/backend/api/readme.md); table definitions are in [infrastructure.md](../infrastructure.md).

#### 12. **EmergencyContact** — up to 5 per CardiMember (name, phone, relationship)
#### 13. **ConsentRecord** — append-only per-metric consent history (activity/heart rate/sleep, method, consented-by); latest row is current
#### 14. **FamilyInvitation** — email invitations with role, 7-day expiry, Pending/Accepted/Revoked/Expired status
#### 15. **SharedNote** — care-coordination notes per CardiMember with @mentions (JSON) and view receipts (JSON)
#### 16. **CardiMemberNote** — self-authored notes by the monitored person (max 1000 chars)
#### 17. **AlertNote** — follow-up notes on an alert, with optional actionTaken analytics key
#### 18. **AlertPhoto** — photo attachments on alerts (blob URL, caption)
#### 19. **AlertPreference** — one per CardiMember: sensitivity (low/medium/high), channels, quiet hours, per-type settings, family routing rules (JSON columns)
#### 20. **PushNotificationToken** — APNS/FCM tokens per user device (upsert by user+device)
#### 21. **NotificationPreference** — one per User: global channels and weekly digest settings (JSON)
#### 22. **Report** — async report generation state (format, parameters, status, blob URL, 24h download expiry)

> **Biometric credentials have no entity** — biometrics are a local device gate over the Auth0 refresh token (see [auth.md](../execution/backend/api/auth.md)).

## Design Principles

### 1. No Foreign Key Constraints
- All relationships use Guid references without FK constraints
- Prevents orphan record issues during deletions
- Application-level referential integrity via repositories
- More flexible for soft deletes and data archival

### 2. Guid Primary Keys
- All entities use Guid for Id (not int)
- Better for distributed systems
- No sequential ID enumeration security risk
- Easier cross-database/cross-service references

### 3. Device-Agnostic Architecture
- DeviceType enum supports all wearables (Fitbit, Apple Watch, Garmin, Samsung, Withings, Oura, Whoop)
- ActivityLog.DataSource tracks which device provided data
- Normalized data schema works with any device
- Device catalog table for device capabilities and integration mode

### 4. Soft Deletes
- Entities implement ISoftDeletable interface
- IsActive flag instead of hard deletes
- Maintains data integrity and audit trail
- HIPAA compliance for data retention

### 5. JSON for Flexibility
- NotificationPreferences, Metadata, Features stored as JSON
- Allows schema evolution without migrations
- Device-specific data stored flexibly
- Pattern baselines store day-of-week arrays

### 6. Security & Encryption
- OAuth tokens (AccessToken, RefreshToken) encrypted in database
- MedicalNotes encrypted
- **No password storage** — authentication is Auth0-hosted; Users carry only Auth0UserId
- Audit logging for all PHI access

## Entity Relationships

```
Organization (1) ──→ (N) User
Organization (1) ──→ (N) CardiMember
Organization (1) ──→ (1) Subscription
Organization (1) ──→ (N) FamilyInvitation

User (M) ←──→ (N) CardiMember (via UserCardiMember join table)
User (1) ──→ (N) PushNotificationToken
User (1) ──→ (1) NotificationPreference
User (1) ──→ (N) Report

CardiMember (1) ──→ (N) DeviceConnection
CardiMember (1) ──→ (N) ActivityLog
CardiMember (1) ──→ (N) Alert
CardiMember (1) ──→ (N) PatternBaseline
CardiMember (1) ──→ (N) EmergencyContact
CardiMember (1) ──→ (N) ConsentRecord
CardiMember (1) ──→ (N) SharedNote / CardiMemberNote
CardiMember (1) ──→ (1) AlertPreference

Alert (1) ──→ (N) AlertNote / AlertPhoto
DeviceConnection (1) ──→ (N) ActivityLog
```

## Enums

- **OrganizationType**: Family, Business
- **UserRole**: Admin, Staff, Viewer
- **RelationshipType**: Self, Parent, Spouse, Grandparent, Sibling, Child, Other
- **DeviceType**: Fitbit, AppleWatch, Garmin, Samsung, Withings, Oura, Whoop, Other
- **IntegrationMode**: ServerOAuth, OnDeviceBridge
- **ConnectionStatus**: Pending, Connected, Disconnected, TokenExpired, AuthError, SyncError
- **AlertType**: ActivityDecline, ElevatedHeartRate, NoMorningActivity, IrregularSleep, DeviceDisconnected, LongTermTrend
- **AlertSeverity**: Yellow, Orange, Red
- **HealthStatus**: Green, Yellow, Orange, Red, Unknown
- **AlertStatus**: New, Acknowledged, Resolved
- **SubscriptionTier**: Basic, Complete, Plus
- **SubscriptionStatus**: Trialing, Active, PastDue, Cancelled, Suspended
- **BillingCycle**: Monthly, Annual
- **InvitationStatus**: Pending, Accepted, Revoked, Expired
- **ReportStatus**: Pending, Ready, Failed, Expired
- **Gender**: Male, Female, Other, PreferNotToSay

> API surfaces serialize enum values in `snake_case`/lowercase (e.g. `activity_decline`, `token_expired`, `yellow`) per the [API spec](../execution/backend/api/readme.md); the PascalCase names above are the C# domain enums.

## File Structure

```
CardiTrack.Domain/
├── Common/
│   └── BaseEntity.cs
├── Interfaces/
│   ├── IEntity.cs
│   └── ISoftDeletable.cs
├── Enums/
│   ├── OrganizationType.cs
│   ├── UserRole.cs
│   ├── RelationshipType.cs
│   ├── DeviceType.cs
│   ├── IntegrationMode.cs
│   ├── ConnectionStatus.cs
│   ├── AlertType.cs
│   ├── AlertSeverity.cs
│   ├── HealthStatus.cs
│   ├── AlertStatus.cs
│   ├── SubscriptionTier.cs
│   ├── SubscriptionStatus.cs
│   ├── BillingCycle.cs
│   ├── InvitationStatus.cs
│   ├── ReportStatus.cs
│   └── Gender.cs
└── Entities/
    ├── Organization.cs
    ├── User.cs
    ├── CardiMember.cs
    ├── UserCardiMember.cs
    ├── DeviceConnection.cs
    ├── ActivityLog.cs
    ├── Alert.cs
    ├── AlertNote.cs
    ├── AlertPhoto.cs
    ├── AlertPreference.cs
    ├── PatternBaseline.cs
    ├── Subscription.cs
    ├── Device.cs
    ├── EmergencyContact.cs
    ├── ConsentRecord.cs
    ├── FamilyInvitation.cs
    ├── SharedNote.cs
    ├── CardiMemberNote.cs
    ├── PushNotificationToken.cs
    ├── NotificationPreference.cs
    ├── Report.cs
    └── AuditLog.cs
```

## Next Steps

1. Create EF Core DbContext and entity configurations (CardiTrack.Infrastructure)
2. Configure entity mappings (FluentAPI)
3. Set up encryption for sensitive fields (tokens, medical notes)
4. Create migrations for the feature entities (#12–#22)
5. Implement repositories with Guid-based queries
6. Add indexes for performance (CardiMemberId, UserId, Date fields)
7. Configure JSON column serialization
8. Set up audit logging middleware and archive tiering job

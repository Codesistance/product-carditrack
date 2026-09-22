# Family Collaboration API

> **Status: partly implemented (2026-09-22).** A family is now a thing people join, leave and are
> admitted to, and the endpoints for that are live — see "Implemented today". The shared-notes and
> audit-log read endpoints below remain **planned**, and the `/api/v1/family-members/*` routes in
> this doc were never built: the shipped surface uses `/api/v1/families/*` and
> `/api/v1/cardimembers/{id}/caregiver-invites`, described below. The planned contracts are kept
> as design intent, and where the shipped one differs it is the shipped one that is right.

Manages family member accounts, role-based access, email invitations, shared care notes with @mention support, and the HIPAA-compliant activity audit log.

**User Stories:** 4.1 (Inviting Family Members), 4.2 (Coordinating Care), 8.3 (Family Communication / Facility Portal)

---

## Implemented today

### Membership is a relation, not a column

Until 2026-09-22 a user's family was a column on their row (`User.OrganizationId`), which made "belongs to a family" and "has a role" the same fact and allowed exactly one of each. Membership now lives in **`UserOrganization`** (`UserId`, `OrganizationId`, `Role`, `JoinedDate`, `IsActive`), and the role lives there with it — a role is held *in* a family, not by a person, so somebody can be the admin of their own and an ordinary member of their mother's.

`User.OrganizationId` survives as the **home family**: the one a person's own members and subscription belong to. It is nullable, because somebody who signed up to join a family somebody else runs has no family of their own until they add their first member.

### Joining, and being let in

| What | How |
|------|-----|
| **Family ID** | Eight characters from a 31-letter alphabet (`KTR7-M2Q9`), minted per family, read aloud or auto-filled from a link. Stored unseparated; the hyphen is for reading. It is **not a secret** and is not sized as one — knowing it buys the right to *ask*, which is worth nothing on its own |
| **Join request** | `POST /api/v1/families/join-requests` with a Family ID. Returns an identical empty receipt for an unknown code, a malformed one, and a family the caller is already in — so the endpoint cannot be used to discover which families exist. Rate-limited |
| **Admin approval** | `GET /api/v1/families/{id}/join-requests`, then `POST .../approve` or `POST .../decline`. Mandatory: nothing admits anybody without it |
| **Caregiver invitation** | `POST /api/v1/cardimembers/{id}/caregiver-invites` — the other direction, where an admin offers a specific person a share of watching a specific member. Token-based, and redemption claims the invitation before writing any grant. **Always admits as `Member`**: an invitation says "come and help me watch Mum", and handing the family and its billing to somebody is its own deliberate act (`PUT /api/v1/families/{id}/admin`), not a field on a message sent a week earlier |

### Roles, and the one admin

**`UserRole` is `Member` (1), `Admin` (2), `Staff` (3)** — integers on the wire, and **there is no `viewer` role**, here or anywhere. The JSON examples further down this document show `"role": "viewer"` as strings; both are wrong and are kept only because the surrounding contract is still design intent.

Leaving a family clears the home pointer too. `User.OrganizationId` is what `UserContextMiddleware` serves as the caller's organization, and organization-scoped reads trust it; while it *was* membership the two ended together, so removal now clears it explicitly (or repoints it at another family they are still in). Otherwise a removed caregiver keeps reading the family's members.

A family has exactly one admin, and only the admin pays. An admin cannot simply leave: `PUT /api/v1/families/{id}/admin` hands the family and its plan to somebody else and demotes the caller in the same save, and only then can they go. `Staff` stays unused by Family organizations — it belongs to the Enterprise offering.

### The shipped routes

| Route | What it does |
|-------|--------------|
| `GET /api/v1/families/mine` | Every family this user belongs to, with their role in each, and each family's Family ID |
| `GET /api/v1/families/{id}/members` | The roster |
| `PUT /api/v1/families/{id}/admin` | Hands over the family and its plan; promote and demote in one save |
| `DELETE /api/v1/families/{id}/members/{userId}` | Admin removes somebody |
| `DELETE /api/v1/families/{id}/members/me` | Leave. Refused for the last admin, who must hand over first |
| `POST /api/v1/families/join-requests` | Ask to join, by Family ID |
| `GET /api/v1/families/join-requests/mine` | What the caller has asked for |
| `DELETE /api/v1/families/join-requests/{id}` | Withdraw an ask |
| `GET /api/v1/families/{id}/join-requests` | Admin: who is asking |
| `POST /api/v1/families/{id}/join-requests/{requestId}/approve` \| `.../decline` | Admin decides, and chooses what the joiner gets |
| `POST` \| `GET` \| `DELETE /api/v1/cardimembers/{id}/caregiver-invites[/{inviteId}]` | Issue, list and revoke invitations to watch one member |
| `GET /api/v1/caregiver-invites/{token}` | The landing page's view of an invitation |
| `POST /api/v1/caregiver-invites/{token}/accept` \| `.../decline` | The invitee's answer |

**The Family ID travels with the summary.** `FamilySummary.familyId` carries the stored eight
characters, for every member of the family rather than only its admin: it identifies the family and
authorizes nothing (D-11), and the client that shows it is the one place a caregiver can read it
out from. Added 2026-09-22 with the mobile Family tab, which is where it is displayed.

### Still the per-member grant underneath

Belonging to a family is not the same as being able to see a member's health data. Every read is still authorized against the **`UserCardiMember` link entity** — the per-user, per-member access grant, which is what an approval or an accepted invitation actually writes:

| Field | Purpose |
|-------|---------|
| `UserId` / `CardiMemberId` | Which user may access which member |
| `RelationshipType` | Caregiver's relationship (integer enum) |
| `IsPrimaryCaregiver` | Primary caregiver flag |
| `CanViewHealthData` | Gates the dashboard (`GET .../dashboard` requires it) |
| `ReceiveAlerts` | The live recipient-resolution predicate: `IDispatchService.EnqueueForAlertAsync` filters links on `IsActive && ReceiveAlerts` to decide who gets pushed for an alert |
| `IsActive` | Soft enable/disable of the link |

Links are no longer created only by onboarding: an approved join request and an accepted caregiver invitation both write one, with exactly the `CanViewHealthData` / `ReceiveAlerts` the admin chose.

**Naming collision warning:** this doc's "activity log" means the HIPAA **audit trail**. The write side of it is live: `AuditLoggingMiddleware` writes an `AuditLog` row (user, member, action, path, method, IP, user-agent, response status) for every endpoint carrying `[AuditHealthDataAccess]` — applied across Alerts, CardiMembers, Dashboard, Devices, Insights, MemberChat, MetricAlarms, Onboarding (member creation only), Questionnaires, Reports as of 2026-09-13. Repeated GETs coalesce for 15 minutes. What remains **planned** is the read/query endpoint below. The codebase's `ActivityLog` entity is something else entirely: **daily health metrics** (steps, heart rate, sleep) synced from wearables.

Everything below is the **planned** contract, kept as design intent.

---

## GET `/api/v1/family-members`

List all family members who have access to the authenticated user's CardiTrack account.

**Priority:** P1 | **Auth Required:** Yes | **Required Role:** Admin

### Response `200 OK`

```json
{
  "familyMembers": [
    {
      "userId": "usr_sibling123",
      "name": "Tom Doe",
      "email": "tom@example.com",
      "role": "viewer",
      "status": "active",
      "lastActiveAt": "2026-03-09T08:00:00Z",
      "joinedAt": "2026-02-01T10:00:00Z"
    }
  ],
  "total": 1
}
```

**Role Values** (per the implemented `UserRole` enum — integers on the wire; there is no `viewer` role):

| Role | Enum value | Description |
|------|-----------|-------------|
| `Member` | 1 | Default for Family accounts — can view dashboards and alerts for linked CardiMembers |
| `Admin` | 2 | Full access — can invite/remove members, edit CardiMember settings |
| `Staff` | 3 | Business staff — can view and acknowledge alerts, edit CardiMember details |

---

## POST `/api/v1/family-members/invite`

Send an email invitation to a new family member. The invitation includes a role assignment and expires after 7 days.

**Priority:** P1 | **Auth Required:** Yes | **Required Role:** Admin

### Request Body

```json
{
  "email": "sarah@example.com",
  "role": "viewer",
  "message": "Hi Sarah, I've invited you to help monitor Mom's health on CardiTrack."
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `email` | string | Yes | Invitee's email address |
| `role` | string | Yes | `Member`, `Admin`, or `Staff` (see Role Values above) |
| `message` | string | No | Personal message included in invitation email (max 500 chars) |

### Response `201 Created`

```json
{
  "invitationId": "inv_abc123",
  "email": "sarah@example.com",
  "role": "viewer",
  "status": "pending",
  "expiresAt": "2026-03-16T10:00:00Z",
  "sentAt": "2026-03-09T10:00:00Z"
}
```

### Errors

| Code | Status | Description |
|------|--------|-------------|
| `INVITATION_ALREADY_PENDING` | 409 | An active invitation already exists for this email |
| `USER_ALREADY_MEMBER` | 409 | Email is already a member of this account |
| `MEMBER_LIMIT_REACHED` | 422 | Plan tier family member limit exceeded |

---

## GET `/api/v1/family-members/invitations`

List all pending invitations for the account.

**Priority:** P1 | **Auth Required:** Yes | **Required Role:** Admin

### Response `200 OK`

```json
{
  "invitations": [
    {
      "invitationId": "inv_abc123",
      "email": "sarah@example.com",
      "role": "viewer",
      "status": "pending",
      "expiresAt": "2026-03-16T10:00:00Z",
      "sentAt": "2026-03-09T10:00:00Z"
    }
  ]
}
```

---

## POST `/api/v1/family-members/invitations/{token}/resend`

Resend an existing invitation email. Resets the 7-day expiry window.

**Priority:** P1 | **Auth Required:** Yes | **Required Role:** Admin

### Path Parameters

| Parameter | Description |
|-----------|-------------|
| `token` | Invitation token (from `invitationId`) |

### Response `200 OK`

```json
{
  "invitationId": "inv_abc123",
  "email": "sarah@example.com",
  "status": "pending",
  "expiresAt": "2026-03-16T11:00:00Z",
  "resentAt": "2026-03-09T11:00:00Z"
}
```

### Errors

| Code | Status | Description |
|------|--------|-------------|
| `INVITATION_NOT_FOUND` | 404 | Invitation token not found |
| `INVITATION_ALREADY_ACCEPTED` | 409 | Invitation was already accepted |

---

## DELETE `/api/v1/family-members/invitations/{token}`

Revoke a pending invitation before it is accepted.

**Priority:** P1 | **Auth Required:** Yes | **Required Role:** Admin

### Response `204 No Content`

### Errors

| Code | Status | Description |
|------|--------|-------------|
| `INVITATION_NOT_FOUND` | 404 | Invitation token not found |

---

## PUT `/api/v1/family-members/{userId}/role`

Change the role of an existing family member.

**Priority:** P1 | **Auth Required:** Yes | **Required Role:** Admin

### Request Body

```json
{
  "role": "staff"
}
```

### Response `200 OK`

```json
{
  "userId": "usr_sibling123",
  "name": "Tom Doe",
  "role": "staff",
  "updatedAt": "2026-03-09T12:00:00Z"
}
```

### Errors

| Code | Status | Description |
|------|--------|-------------|
| `CANNOT_DEMOTE_LAST_ADMIN` | 422 | At least one Admin must remain on the account |
| `FAMILY_MEMBER_NOT_FOUND` | 404 | User ID not found on this account |

---

## DELETE `/api/v1/family-members/{userId}`

Remove a family member from the account. Their access is revoked immediately.

**Priority:** P1 | **Auth Required:** Yes | **Required Role:** Admin

### Response `204 No Content`

### Errors

| Code | Status | Description |
|------|--------|-------------|
| `CANNOT_REMOVE_LAST_ADMIN` | 422 | Cannot remove the only Admin on the account |
| `CANNOT_REMOVE_SELF` | 422 | Use account deletion flow instead |

---

## GET `/api/v1/cardimembers/{id}/shared-notes`

Get shared care coordination notes for a CardiMember, visible to all family members.

**Priority:** P1 | **Auth Required:** Yes

### Query Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `limit` | integer | Max results (default: 20, max: 100) |
| `offset` | integer | Pagination offset |

### Response `200 OK`

```json
{
  "notes": [
    {
      "noteId": "sn_abc123",
      "content": "Called, she had a cold but is fine. @Tom — no need to visit this week.",
      "mentions": [
        {
          "userId": "usr_sibling123",
          "name": "Tom Doe"
        }
      ],
      "author": {
        "userId": "usr_01J8K2...",
        "name": "Jane Doe"
      },
      "createdAt": "2026-03-09T11:30:00Z",
      "lastViewedBy": [
        {
          "userId": "usr_sibling123",
          "name": "Tom Doe",
          "viewedAt": "2026-03-09T12:00:00Z"
        }
      ]
    }
  ],
  "total": 1
}
```

---

## POST `/api/v1/cardimembers/{id}/shared-notes`

Add a shared care coordination note. Supports @mentions to notify specific family members.

**Priority:** P1 | **Auth Required:** Yes

### Request Body

```json
{
  "content": "Spoke to Mom this afternoon. She seemed a bit tired. @Tom can you check in tomorrow?",
  "mentionedUserIds": ["usr_sibling123"]
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `content` | string | Yes | Note content (max 2000 chars) |
| `mentionedUserIds` | array | No | User IDs to notify via push/email |

### Response `201 Created`

```json
{
  "noteId": "sn_xyz456",
  "content": "Spoke to Mom this afternoon...",
  "mentions": [...],
  "author": {
    "userId": "usr_01J8K2...",
    "name": "Jane Doe"
  },
  "createdAt": "2026-03-09T14:00:00Z",
  "mentionedUsersNotified": true
}
```

### Errors

| Code | Status | Description |
|------|--------|-------------|
| `NOTE_TOO_LONG` | 400 | Content exceeds 2000 characters |
| `INVALID_MENTION` | 400 | Mentioned user is not a member of this account |

---

## GET `/api/v1/activity-log`

> **Naming note:** despite the route name, this is the **audit trail**, not the codebase's `ActivityLog` entity (daily health metrics) — see the collision warning in "Implemented today".

HIPAA-compliant audit log of all access events — who viewed what data and when. Required for compliance. Audit records are retained for **6 years** (most recent year queryable via this endpoint; older records available from the archive tier on request — see [infrastructure.md](../../../infrastructure.md)).

**Priority:** P1 | **Auth Required:** Yes | **Required Role:** Admin

### Query Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `cardiMemberId` | string | Filter by specific CardiMember |
| `userId` | string | Filter by specific family member |
| `from` | string (ISO 8601) | Start date filter |
| `to` | string (ISO 8601) | End date filter |
| `limit` | integer | Max results (default: 50, max: 500) |

### Response `200 OK`

```json
{
  "events": [
    {
      "eventId": "evt_001",
      "userId": "usr_sibling123",
      "userName": "Tom Doe",
      "action": "viewed_dashboard",
      "cardiMemberId": "cm_01J8K2...",
      "cardiMemberName": "Margaret Doe",
      "resourceType": "health_summary",
      "occurredAt": "2026-03-09T08:05:00Z",
      "ipAddress": "192.168.1.1",
      "userAgent": "CardiTrack/2.0 iOS/17.0"
    }
  ],
  "total": 1
}
```

**Tracked Event Types:**

| Action | Description |
|--------|-------------|
| `viewed_dashboard` | Opened CardiMember health summary |
| `viewed_trends` | Opened trend chart |
| `acknowledged_alert` | Acknowledged or resolved an alert |
| `added_shared_note` | Posted a shared care note |
| `exported_data` | Downloaded health data export |
| `modified_settings` | Changed CardiMember or account settings |
| `invited_family_member` | Sent family invitation |

---

**Related:** [readme.md](readme.md) | [alerts.md](alerts.md) | [notifications.md](notifications.md) | [User Stories 4.1, 4.2, 8.3](../../ui/mobile/user_stories.md)

**Last Updated:** September 22, 2026

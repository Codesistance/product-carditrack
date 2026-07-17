# Family Collaboration API

Manages family member accounts, role-based access, email invitations, shared care notes with @mention support, and the HIPAA-compliant activity audit log.

**User Stories:** 4.1 (Inviting Family Members), 4.2 (Coordinating Care), 8.3 (Family Communication / Facility Portal)

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

**Role Values:**

| Role | Description |
|------|-------------|
| `admin` | Full access — can invite/remove members, edit CardiMember settings |
| `staff` | Can view and acknowledge alerts, edit CardiMember details |
| `viewer` | Read-only — can view dashboard and alerts, cannot modify settings |

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
| `role` | string | Yes | `admin`, `staff`, or `viewer` |
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

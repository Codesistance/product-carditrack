# Subscription Management API

> **Status: Planned — not yet implemented.** None of the endpoints below exist yet. See "Implemented today" for current coverage.

Handles plan retrieval, upgrades/downgrades, billing history, and payment method updates. The Guardian Plus business tier is out of scope for MVP and handled via a dedicated business account flow.

> **Release note:** MVP 1 runs trial-only (every account starts a 30-day trial at signup; no billing UI). The subscription endpoints below ship with **MVP 2**, before the first trials require payment — priorities in this file are relative to that release. See the [release matrix](../../../release_matrix.md).

**User Stories:** 6.1 (Subscription Management)

---

## Implemented today

The trial-only model is real: a trial subscription is **auto-created inside the onboarding transaction** (`SubscriptionService.CreateTrialSubscriptionAsync`, called from `POST /api/Onboarding/setup` / `POST /api/Onboarding/organization`). There is no standalone subscription endpoint — the subscription is returned **nested in the onboarding response** at `data.organization.subscription`:

```json
{
  "id": "5c2f5f64-5717-4562-b3fc-2c963f66afa6",
  "tier": 2,
  "status": 1,
  "startDate": "2026-08-07T10:00:00Z",
  "trialEndDate": "2026-09-06T10:00:00Z",
  "maxCardiMembers": 5,
  "maxUsers": 1
}
```

Trial parameters (fixed in code):

| Parameter | Value |
|-----------|-------|
| Tier | `Complete` (2) |
| Status | `Trial` (1) |
| Duration | 30 days from signup |
| Price | 0 USD, monthly billing cycle |
| Limits — Family org | 5 CardiMembers / 1 user ⚠️ |
| Limits — Business org | 50 CardiMembers / 20 users |

> ⚠️ **The trial's hardcoded 5-CardiMember limit no longer matches any tier.** `CreateTrialSubscriptionAsync` provisions the `Complete` tier with 5 CardiMembers, which was correct when Complete Care allowed 5. After the 2026-08-18 repricing Complete Care allows **3**, so a trial currently grants more headroom than the tier it is a trial of. Harmless today — limits are not enforced anywhere — but it becomes a real downgrade cliff the moment enforcement or billing lands, because a triallist could add 5 members and then be unable to convert without removing 2. Fix alongside the enforcement work, not before.

- `tier` and `status` are **integer enums**: `SubscriptionTier` Basic=1, Complete=2, Plus=3; `SubscriptionStatus` Trial=1, Active=2, PastDue=3, Cancelled=4, Suspended=5. (Note: `Trial` is the first enum member — there is no `trialing` string status.)
- **Limits are not enforced anywhere** — `MaxCardiMembers`/`MaxUsers` exist on the entity but no endpoint checks them.
- **No Stripe or billing code exists** — no payment methods, invoices, proration, or plan catalog.

Everything below is the **planned** contract, kept as design intent.

---

## GET `/api/v1/subscription`

Get the authenticated user's current subscription plan, usage metrics, and next billing date.

**Priority:** P0 | **Auth Required:** Yes

### Response `200 OK`

```json
{
  "subscription": {
    "planId": "plan_basic",
    "planName": "Basic",
    "status": "active",
    "billingCycle": "monthly",
    "pricePerMonth": 8.00,
    "currency": "USD",
    "currentPeriodStart": "2026-03-01",
    "currentPeriodEnd": "2026-04-01",
    "cancelAtPeriodEnd": false,
    "trialEndsAt": null
  },
  "usage": {
    "cardiMembersUsed": 2,
    "cardiMembersLimit": 2,
    "familyMembersUsed": 3,
    "familyMembersLimit": 5
  },
  "paymentMethod": {
    "brand": "visa",
    "last4": "4242",
    "expiryMonth": 12,
    "expiryYear": 2027
  },
  "annualSavingsAvailable": {
    "savingsPercent": 15,
    "annualPrice": 71.40
  }
}
```

**Plan Status Values:**

| Status | Description |
|--------|-------------|
| `trialing` | Within the 30-day free trial |
| `active` | Paid and current |
| `past_due` | Payment failed, grace period |
| `canceled` | Subscription ended |

---

## GET `/api/v1/subscription/plans`

List all available subscription plans with feature comparison. Used to render the upgrade/downgrade UI.

**Priority:** P0 | **Auth Required:** Yes

### Response `200 OK`

```json
{
  "plans": [
    {
      "planId": "plan_basic",
      "name": "Basic",
      "monthlyPrice": 7.00,
      "annualPrice": 71.40,
      "currency": "USD",
      "isCurrentPlan": true,
      "features": [
        { "key": "cardiMemberLimit", "value": 1, "label": "1 CardiMember" },
        { "key": "familyMemberLimit", "value": 5, "label": "Up to 5 family members" },
        { "key": "alertTypes", "value": "standard", "label": "Standard alert types" },
        { "key": "dataRetention", "value": 30, "label": "30 days data history" },
        { "key": "export", "value": false, "label": "Data export" }
      ]
    },
    {
      "planId": "plan_complete_care",
      "name": "Complete Care",
      "monthlyPrice": 10.00,
      "annualPrice": 102.00,
      "currency": "USD",
      "isCurrentPlan": false,
      "features": [
        { "key": "cardiMemberLimit", "value": 3, "label": "Up to 3 CardiMembers" },
        { "key": "familyMemberLimit", "value": 20, "label": "Up to 20 family members" },
        { "key": "alertTypes", "value": "advanced", "label": "Advanced AI alert types" },
        { "key": "dataRetention", "value": 90, "label": "90 days data history" },
        { "key": "export", "value": true, "label": "PDF & CSV data export" }
      ]
    },
    {
      "planId": "plan_guardian_plus",
      "name": "Guardian Plus",
      "monthlyPrice": 15.00,
      "annualPrice": 153.00,
      "currency": "USD",
      "isCurrentPlan": false,
      "features": [
        { "key": "cardiMemberLimit", "value": 6, "label": "Up to 6 CardiMembers" },
        { "key": "familyMemberLimit", "value": 20, "label": "Up to 20 family members" },
        { "key": "alertTypes", "value": "advanced", "label": "Advanced AI alert types" },
        { "key": "dataRetention", "value": 180, "label": "180 days data history" },
        { "key": "export", "value": true, "label": "PDF & CSV data export" }
      ]
    }
  ]
}
```

> **Guardian Plus is now a consumer tier.** It was previously specified as a post-MVP business tier and was absent from this catalog; the published pricing page sells it to families alongside Basic and Complete Care, so it belongs here. Its differentiators over Complete Care are the member limit, the CardiJournal's monthly Monthbook, priority support and the longer history window.

---

## POST `/api/v1/subscription/upgrade`

Upgrade the current plan. Takes effect immediately (prorated billing). If upgrading from trial, billing starts from the upgrade date.

**Priority:** P0 | **Auth Required:** Yes

### Request Body

```json
{
  "planId": "plan_complete_care",
  "billingCycle": "annual"
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `planId` | string | Yes | Target plan ID |
| `billingCycle` | string | Yes | `"monthly"` or `"annual"` |

### Response `200 OK`

```json
{
  "subscription": {
    "planId": "plan_complete_care",
    "planName": "Complete Care",
    "status": "active",
    "billingCycle": "annual",
    "pricePerMonth": 12.75,
    "effectiveAt": "2026-03-09T10:00:00Z",
    "nextBillingDate": "2027-03-09"
  },
  "proratedCharge": {
    "amount": 7.23,
    "description": "Prorated charge for remainder of current billing period"
  }
}
```

### Errors

| Code | Status | Description |
|------|--------|-------------|
| `ALREADY_ON_PLAN` | 409 | User is already subscribed to this plan |
| `PAYMENT_METHOD_REQUIRED` | 422 | No payment method on file |
| `PAYMENT_FAILED` | 402 | Charge to payment method failed |

---

## POST `/api/v1/subscription/downgrade`

Downgrade to a lower plan. Takes effect at the end of the current billing period.

**Priority:** P1 | **Auth Required:** Yes

### Request Body

```json
{
  "planId": "plan_basic",
  "billingCycle": "monthly"
}
```

### Response `200 OK`

```json
{
  "subscription": {
    "planId": "plan_complete_care",
    "planName": "Complete Care",
    "status": "active"
  },
  "scheduledDowngrade": {
    "planId": "plan_basic",
    "planName": "Basic",
    "effectiveAt": "2026-04-01T00:00:00Z",
    "note": "Your Complete Care plan remains active until April 1, 2026."
  },
  "warnings": [
    "You currently have 4 CardiMembers. Basic plan supports 1. You will need to remove 3 CardiMembers before the downgrade takes effect."
  ]
}
```

### Errors

| Code | Status | Description |
|------|--------|-------------|
| `DOWNGRADE_LIMIT_CONFLICT` | 422 | Current usage exceeds target plan limits — details in response |

---

## GET `/api/v1/subscription/billing`

Get billing history (invoices) for the authenticated user.

**Priority:** P1 | **Auth Required:** Yes

### Query Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `limit` | integer | Max results (default: 12, max: 100) |
| `offset` | integer | Pagination offset |

### Response `200 OK`

```json
{
  "invoices": [
    {
      "invoiceId": "inv_stripe_001",
      "amount": 8.00,
      "currency": "USD",
      "status": "paid",
      "description": "CardiTrack Basic — March 2026",
      "billedAt": "2026-03-01T00:00:00Z",
      "pdfUrl": "https://billing.carditrack.com/invoices/inv_stripe_001.pdf"
    }
  ],
  "total": 3
}
```

---

## PUT `/api/v1/subscription/billing/payment-method`

Update the payment method on file. Uses a Stripe SetupIntent token collected client-side.

**Priority:** P1 | **Auth Required:** Yes

### Request Body

```json
{
  "setupIntentId": "seti_1234abcd..."
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `setupIntentId` | string | Yes | Stripe SetupIntent ID (created client-side via Stripe.js) |

### Response `200 OK`

```json
{
  "paymentMethod": {
    "brand": "mastercard",
    "last4": "1234",
    "expiryMonth": 8,
    "expiryYear": 2028
  },
  "updatedAt": "2026-03-09T10:00:00Z"
}
```

### Errors

| Code | Status | Description |
|------|--------|-------------|
| `INVALID_SETUP_INTENT` | 400 | SetupIntent ID is invalid or already consumed |

---

**Related:** [readme.md](readme.md) | [User Story 6.1](../../ui/mobile/user_stories.md)

**Last Updated:** August 7, 2026

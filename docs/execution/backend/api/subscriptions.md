# Subscription Management API

Handles plan retrieval, upgrades/downgrades, billing history, and payment method updates. The Guardian Plus business tier is out of scope for MVP and handled via a dedicated business account flow.

> **Release note:** MVP 1 runs trial-only (every account starts a 30-day trial at signup; no billing UI). The subscription endpoints below ship with **MVP 2**, before the first trials require payment — priorities in this file are relative to that release. See the [release matrix](../../../release_matrix.md).

**User Stories:** 6.1 (Subscription Management)

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
    "annualPrice": 81.60
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
      "monthlyPrice": 8.00,
      "annualPrice": 81.60,
      "currency": "USD",
      "isCurrentPlan": true,
      "features": [
        { "key": "cardiMemberLimit", "value": 2, "label": "Up to 2 CardiMembers" },
        { "key": "familyMemberLimit", "value": 5, "label": "Up to 5 family members" },
        { "key": "alertTypes", "value": "standard", "label": "Standard alert types" },
        { "key": "dataRetention", "value": 90, "label": "90 days data history" },
        { "key": "export", "value": false, "label": "Data export" }
      ]
    },
    {
      "planId": "plan_complete_care",
      "name": "Complete Care",
      "monthlyPrice": 15.00,
      "annualPrice": 153.00,
      "currency": "USD",
      "isCurrentPlan": false,
      "features": [
        { "key": "cardiMemberLimit", "value": 5, "label": "Up to 5 CardiMembers" },
        { "key": "familyMemberLimit", "value": 20, "label": "Up to 20 family members" },
        { "key": "alertTypes", "value": "advanced", "label": "Advanced AI alert types" },
        { "key": "dataRetention", "value": 365, "label": "365 days data history" },
        { "key": "export", "value": true, "label": "PDF & CSV data export" }
      ]
    }
  ]
}
```

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
    "You currently have 4 CardiMembers. Basic plan supports 2. You will need to remove 2 CardiMembers before the downgrade takes effect."
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

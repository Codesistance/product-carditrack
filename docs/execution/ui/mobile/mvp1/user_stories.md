# CardiTrack - MVP 1 User Stories

> **Extract — do not edit directly.** This file is extracted from the canonical [user_stories.md](../user_stories.md); make changes there and re-extract. Release sequencing is governed by the [release matrix](../../../../release_matrix.md).

> **Build status (August 9, 2026):** 13 of 17 Figma M1 screens are built (M1-01 through M1-10, plus M1-13 CardiMember Detail, M1-14 Edit CardiMember, M1-15 Device Management); the unbuilt screens are alert detail (M1-11/M1-12/M1-16) and M1-17 Health Data Export. Four shipped screens have **no Figma M1 frame — needs design sync**: SignInPage, ForgotPasswordPage, VerifyEmailPage, Onboarding/AccountSetupPage (Stories 1.5–1.8).

Stories mapped to MVP 1 screens (M1-01 through M1-17), plus the four shipped screens without Figma M1 frames.

**Platform Requirements**
- **Minimum iOS:** 17.0 — required for modern platform APIs and reliable background push delivery
- **Minimum Android:** 12 (API 31) — raised for the Android 12 SplashScreen API, so one splash design matches the OS handover on every supported device
- **Target iOS:** 18 (latest stable)
- **Target Android:** 15 / API 35 (latest stable)

---

## Onboarding & Setup

**Story 1.1: First-Time User Registration** _(P0 — Must Have)_
- **As a** concerned family caregiver
- **I want to** quickly create an account and understand what CardiTrack does
- **So that** I can start monitoring my elderly parent's health within minutes
- **Acceptance Criteria:**
  - Simple signup flow (email/password or social login via Auth0)
  - Clear value proposition on landing page
  - 30-day free trial messaging prominent
  - Account creation leads into email verification (Story 1.7) — the user cannot enter the app until their email is verified
- **Screens:** M1-02 (Welcome), M1-03 (Sign Up)

**Story 1.2: Adding First CardiMember** _(P0 — Must Have)_
- **As a** new CardiTrack user
- **I want to** easily add my parent as a CardiMember with minimal information
- **So that** I don't abandon the setup process due to complexity
- **Acceptance Criteria:**
  - Progressive disclosure (collect basic info first, details later)
  - Required fields: Name, Date of Birth, Sex (Male/Female — the Sex picker is a **deliberate divergence from the Figma comps**; it sets the reference range readings are judged against)
  - Optional fields: Relationship (falls back to Other), Photo, medical notes (encrypted), emergency contacts
  - Clear privacy messaging ("Your parent will be notified")
  - Visual progress indicator (Step 2 of 4)
  - Emergency-phone placeholder localized by device region (PR #8): US/CA "+1 555 000 0000", GB "+44 7700 900000" — **limitation:** all other regions fall back to the US format, notable given the US + EU target market
- **Screens:** M1-04 (Add First CardiMember)

**Story 1.3: Device Connection Wizard** _(P0 — Must Have)_
- **As a** caregiver setting up monitoring
- **I want to** connect my parent's wearable device through a guided wizard
- **So that** I understand what permissions are needed and why
- **Acceptance Criteria:**
  - Device selection screen with icons (Fitbit and Google Pixel Watch — live in MVP 1, both via the Google Health API; Apple Watch, Garmin, Samsung — Coming Soon)
  - OAuth flow with clear permission explanations
  - "Why we need this" tooltips for each permission
  - Success confirmation with sample data preview
  - Troubleshooting tips if connection fails
  - Support for multiple devices per CardiMember
- **Screens:** M1-05 (Device Selection), M1-06 (OAuth), M1-07 (Success), M1-08 (Baseline)

**Story 1.4: CardiMember Profile Management** _(P0 — Must Have)_
- **As a** caregiver
- **I want to** view and edit a CardiMember's profile (photo, medical notes, emergency contact, monitoring settings)
- **So that** their information stays accurate and I can quickly act in an emergency
- **Acceptance Criteria:**
  - View profile summary: name, DOB, relationship, photo, emergency contact
  - Encrypted medical notes (biometric gating deferred to R4 per the release matrix — not an MVP 1 criterion)
  - Enable/disable monitoring toggle with confirmation
  - Alert sensitivity control (Low / Medium / High)
  - Quick-action buttons: View Dashboard, View Alerts, Manage Devices
  - Danger-zone actions: Pause Monitoring, Remove CardiMember (with confirmation dialogs)
- **Screens:** M1-13 (CardiMember Detail), M1-14 (Edit CardiMember)

**Story 1.5: Sign In** _(P0 — shipped; no Figma M1 frame, needs design sync)_
- **As a** returning caregiver
- **I want to** sign in quickly with my email and password
- **So that** I can get back to monitoring without friction
- **Acceptance Criteria:**
  - Email + password form with password show/hide toggle
  - **"Remember me" checkbox**
  - "Forgot password" link → Story 1.6
  - Inline error message for failed sign-in (no banner)
  - Social sign-in options (Google / Apple) presented consistently with sign-up
  - Link back to sign-up for users without an account
- **Screens:** SignInPage (no Figma M1 frame)

**Story 1.6: Forgot Password** _(P0 — shipped; no Figma M1 frame, needs design sync)_
- **As a** caregiver who forgot my password
- **I want to** request a reset link by email
- **So that** I can regain access without contacting support
- **Acceptance Criteria:**
  - Request state: email input + send-reset-link CTA
  - Confirmation state: "Check your email" with resend option (cooldown to prevent spamming)
  - "Back to sign in" path from both states
- **Screens:** ForgotPasswordPage (no Figma M1 frame)

**Story 1.7: Verify Email** _(P0 — shipped; no Figma M1 frame, needs design sync)_
- **As a** newly registered caregiver
- **I want to** verify my email address right after signing up
- **So that** my account is secured and I can proceed into the app
- **Acceptance Criteria:**
  - **Hard tenant gate:** account creation does not auto-login; the user lands on the verification screen and cannot proceed until verified (threads between Story 1.1 and Story 1.2)
  - "I've verified — continue" action with a checking state
  - "Open mail app" shortcut
  - Resend verification email (cooldown; confirmation message)
  - Clear error state when the address is still unverified
- **Screens:** VerifyEmailPage (no Figma M1 frame)

**Story 1.8: Account-Type Setup** _(P0 — shipped; no Figma M1 frame, needs design sync)_
- **As a** first-time user after verification
- **I want to** say whether I'm caring for my family or providing care professionally
- **So that** CardiTrack can tailor my account
- **Acceptance Criteria:**
  - Radio-cards: "My Family" (personal) vs "My Organization" (professional care)
  - Selecting "My Organization" reveals a required Organization Name field
  - Continue disabled until a type is chosen
  - **Flagged scope question (not a resolution):** the Organization option surfaces business onboarding in MVP 1 while the Guardian Plus business tier is post-R4 in the release matrix — needs a product decision
- **Screens:** Onboarding/AccountSetupPage (no Figma M1 frame)

---

## Dashboard & Monitoring

**Story 2.1: Daily Health Overview** _(P0 — Must Have)_
- **As a** busy family caregiver checking in daily
- **I want to** see a quick visual summary of my parent's health status
- **So that** I know if everything is okay without reading detailed reports
- **Acceptance Criteria:**
  - Traffic light status indicators (Green/Yellow/Orange/Red)
  - Key metrics at-a-glance: Steps, Heart Rate, Sleep Quality
  - "Last synced" timestamp
  - Comparison to baseline ("20% below normal activity")
  - Quick action buttons ("Call Mom", "View Details", "Acknowledge Alert")
- **Screens:** M1-09 (Main Dashboard)

---

## Alert Management

**Story 3.1: Receiving Critical Alerts** _(P0 — Must Have)_
- **As a** caregiver receiving an urgent alert
- **I want to** immediately understand what's wrong and what action to take
- **So that** I can respond appropriately without panic
- **Acceptance Criteria:**
  - Alert severity clearly visible (color-coded, icon)
  - Plain language description ("Dad hasn't moved this morning. Typical wake time: 7am. Current time: 11am")
  - Recommended actions ("Call to check in", "Contact emergency services")
  - One-tap actions (Call, SMS, Acknowledge)
  - Alert history visible ("This is the first time this month")
- **Screens:** M1-10 (Alerts List), M1-11 (Activity Detail), M1-12 (Critical Detail), M1-16 (Heart Rate Detail)

**Story 3.3: Alert Acknowledgment & Notes** _(P1 — Should Have)_
- **As a** caregiver following up on an alert
- **I want to** mark it as acknowledged and add notes about my action
- **So that** other family members know it's been handled
- **Acceptance Criteria:**
  - Quick acknowledgment button with timestamp
  - Notes field ("Called, he had a cold but is fine")
  - Photos upload option (if doctor visit occurred)
  - Alert status: New → Acknowledged → Resolved
  - Notification to other family members when acknowledged
- **Screens:** M1-10 (Alerts List), M1-11 (Activity Detail), M1-12 (Critical Detail)

---

## Settings & Preferences

**Story 6.2: Device Management** _(P1 — Should Have)_
- **As a** caregiver whose parent switched devices
- **I want to** disconnect old device and connect new one easily
- **So that** data continues flowing without interruption
- **Acceptance Criteria:**
  - List of connected devices with status (Active, Disconnected, Token Expired)
  - Refresh/reconnect button for expired OAuth tokens
  - Primary device designation (when multiple devices connected)
  - Device removal with confirmation ("This will delete connection but keep historical data")
  - Data source indicator on charts (which device provided this data)
- **Screens:** M1-15 (Device Management)

**Story 6.3: Health Data Export** _(P0 — Must Have; **status: M1-17 not built yet** — entry points show "Coming soon")_
- **As a** caregiver preparing for a doctor's visit or needing records
- **I want to** export a CardiMember's health data in standard medical formats
- **So that** I can share it with healthcare providers or keep it for my records
- **Acceptance Criteria:**
  - Date range selector for the export window
  - Format options: PDF, CSV, FHIR R4 (**MVP 1**); HL7 v2 (MVP 2); LOINC/CCD (MVP 2); SNOMED CT (MVP 3)
  - Delivery options: save to device, share via system share sheet, email to self
  - Clear format explanations ("FHIR R4 is accepted by most US patient portals and EHR systems")
  - Export confirmation with file size estimate
- **Screens:** M1-17 (Health Data Export)

---

## Alert Type Stories

### Alert Type 1: Activity Alerts (Yellow Severity)
**Story 11.1: Gradual Activity Decline**
- **Display:**
  - Chart showing 2-week trend (declining line)
  - Comparison: "Dad's steps: 2,500/day. Normal: 5,000/day (-50%)"
  - Context: "This could indicate illness, pain, or low mood"
- **Actions:**
  - "Call to check in" (primary button)
  - "Acknowledge" (secondary)
  - "Adjust baseline" (if this is new normal)
- **Screen:** M1-11 (Alert Detail - Activity)

### Alert Type 2: Heart Rate Alerts (Orange Severity)
**Story 11.2: Elevated Resting Heart Rate**
- **Display:**
  - Heart rate chart with baseline range shaded
  - "Mom's resting HR: 88 bpm. Normal: 68 bpm (+29%)"
  - Context: "Elevated for 3 consecutive days. May indicate infection or stress"
- **Actions:**
  - "Recommend doctor visit" (primary)
  - "Monitor for 2 more days" (secondary)
  - "View detailed HR data"
- **Screen:** M1-16 (Alert Detail - Heart Rate)

### Alert Type 3: Pattern Break (Red Severity)
**Story 11.3: No Morning Activity**
- **Display:**
  - Large red alert banner
  - "Dad hasn't moved today. Typical wake time: 7am. Current: 11am"
  - Last known activity timestamp
- **Actions:**
  - "Call now" (one-tap phone call, primary)
  - "I'm checking in person"
  - "He told me he'd sleep in today" (dismiss with note)
- **Screen:** M1-12 (Alert Detail - Critical)

---

## Onboarding Flow UX

### Step 1: Value Proposition (30 seconds)
- Hero image: Happy elderly person with Fitbit, smiling family on phone
- Headline: "Peace of Mind for Your Family. $8/month."
- 3 key benefits with icons:
  - Works with devices they already own (Fitbit, Apple Watch, etc.)
  - AI alerts you BEFORE health issues become emergencies
  - 70% cheaper than medical alert systems ($8 vs $47/month)
- CTA: "Start Free 30-Day Trial"

### Step 2: Account Creation (1 minute)
- Email/password or "Continue with Google/Apple" (social buttons wired — Auth0 PKCE authorization-code flow in the system browser on Android/iOS)
- Checkbox: "I agree to Terms & Privacy Policy" (with links)
- **No auto-login after creation** — the user must verify their email (VerifyEmailPage, Story 1.7) before entering the app; PostLoginRouter then routes to account-type setup (Story 1.8) or Add CardiMember

### Step 3: Add CardiMember (2 minutes)
- "Who would you like to monitor?"
- Form: Name, DOB, Relationship, Photo (optional)
- Tone: "We'll help you set up monitoring in 4 simple steps" (the wizard is a 4-step flow: Create Account → Add CardiMember → Connect Device → Baseline)

### Step 4: Device Connection (3 minutes)
- "What wearable device does [Name] use?"
- Device icons with brands (Fitbit and Google Pixel Watch active; others Coming Soon in MVP 1)
- Click → OAuth flow → Success
- "Great! We're syncing [Name]'s data. This may take a few minutes."

### Step 5: Baseline Learning (Info screen)
- "CardiTrack is learning [Name]'s normal patterns..."
- Progress indicator: "Day 3 of 30"
- "You'll start receiving alerts after we establish a baseline (30 days)"
- Option: "Use statistical alerts in the meantime" (basic threshold alerts)

### Step 6: First Dashboard View
- Celebratory tone: "You're all set! Here's [Name]'s health overview."
- The baseline screen's "Invite Family Member First" link **ships in MVP 1** but the invite flow (M3-02) is MVP 2 — the link is currently a dead end
- Guided tour overlay (5 tooltips) — **planned, not shipped** (no guided tour exists in the current app):
  1. "This shows overall health status"
  2. "View detailed trends here"
  3. "Alerts appear in this section"
  4. "Invite family members here"
  5. "Need help? Check our support docs"

---

## MVP 1 Priority Summary

| Story | Title | Priority | Build Status |
|-------|-------|----------|--------------|
| 1.1 | First-Time User Registration | P0 | Built (M1-02, M1-03) |
| 1.2 | Adding First CardiMember | P0 | Built (M1-04) |
| 1.3 | Device Connection Wizard | P0 | Built (M1-05–M1-08) |
| 1.4 | CardiMember Profile Management | P0 | Built (M1-13, M1-14) |
| 1.5 | Sign In | P0 | Built — no Figma M1 frame |
| 1.6 | Forgot Password | P0 | Built — no Figma M1 frame |
| 1.7 | Verify Email | P0 | Built — no Figma M1 frame |
| 1.8 | Account-Type Setup | P0 | Built — no Figma M1 frame |
| 2.1 | Daily Health Overview | P0 | Built (M1-09) |
| 3.1 | Receiving Critical Alerts | P0 | Partial — M1-10 alerts list built; detail screens (M1-11, M1-12, M1-16) not built |
| 6.3 | Health Data Export | P0 | **Not built** (M1-17 — P0 but unbuilt) |
| 3.3 | Alert Acknowledgment & Notes | P1 | Partial — inline acknowledge shipped on M1-10 (`POST /api/v1/alerts/{id}/acknowledge`); notes await the detail screens (M1-11/M1-12/M1-16) |
| 6.2 | Device Management | P1 | Built (M1-15) |
| 11.1 | Gradual Activity Decline | — | Not built (M1-11) |
| 11.2 | Elevated Resting Heart Rate | — | Not built (M1-16) |
| 11.3 | No Morning Activity | — | Not built (M1-12) |

> **Telemetry-consent gap (product follow-up):** Datadog telemetry is logs + traces only — RUM was removed in PR #185, and with it Datadog crash reporting (`NativeCrashReportEnabled=false`); crashes/ANRs come from Play Console vitals. `TrackingConsent.Granted` is still hardcoded — consent is granted by default, with no in-app opt-out and no diagnostics screen. **There is no in-app telemetry control in MVP 1.** This is in tension with the consent-first principle (Principle 4) — see Story 7.4 in the canonical [user_stories.md](../user_stories.md).

---

## UI/UX Design Principles (MVP 1)

### Principle 1: Trust Through Transparency
- Show data source and reasoning for every alert
- Use warm, caring language ("Your mom's activity is lower than usual. Might be worth a check-in call")
- Avoid medical jargon and alarmist language

### Principle 2: Simplicity Over Features
- Information hierarchy: Status → Alert → Action
- Progressive disclosure (advanced features hidden until needed)
- Mobile-first design

### Principle 3: Peace of Mind, Not Panic
- Green status should be prominent when all is well
- Alerts provide context, not just warnings
- Success messaging ("Your dad had his most active week this month!")

### Principle 4: Respect for Elderly Dignity
- Never use patronizing language or imagery
- Focus on independence and wellness, not decline
- Consent-first approach to all monitoring

### Principle 5: Multi-Generational Accessibility
- WCAG AA compliance minimum (AAA preferred)
- Font size options (small/medium/large)
- High contrast mode
- Keyboard navigation support
- Screen reader optimization

---

**Source:** Extracted from [user_stories.md](../user_stories.md) v1.1 (manually re-synced August 9, 2026)
**Screens covered:** M1-01 through M1-17, plus SignInPage, ForgotPasswordPage, VerifyEmailPage, and Onboarding/AccountSetupPage (no Figma M1 frames — need design sync)

# CardiTrack User Stories for UI/UX Design

> **Build status (August 15, 2026):** 16 of 17 Figma M1 screens are built in `CardiTrack.Mobile` (M1-01 through M1-16). Alert detail is one page (`AlertDetailPage`) covering M1-11/12/16, charted from the single series that caused the alert. Unbuilt: M1-17 Health Data Export. **Seven** shipped surfaces have **no Figma M1 frame — needs design sync**: SignInPage, ForgotPasswordPage, VerifyEmailPage, Onboarding/AccountSetupPage (see Stories 1.5–1.8), plus NotificationsPage, QuestionnairesPage, and QuestionCard. `AskCard` on M1-13 is a further as-built component without a frame (Story 2.4). Release waves re-baselined: MVP 1 (R1) → Q4 2026, MVP 2 (R2) → Q1 2027, MVP 3 (R3) → Q2 2027. Release sequencing is governed by the [release matrix](../../../release_matrix.md).

Based on the solution manifest, market analysis, and README, here are comprehensive user stories organized by user persona and platform:

## 👨‍👩‍👧 Primary Persona: Family Caregiver (Ages 45-65)

### Onboarding & Setup

**Story 1.1: First-Time User Registration**
- **As a** concerned family caregiver
- **I want to** quickly create an account and understand what CardiTrack does
- **So that** I can start monitoring my elderly parent's health within minutes
- **Acceptance Criteria:**
  - Simple signup flow (email/password or social login via Auth0)
  - Clear value proposition on landing page
  - 30-day free trial messaging prominent
  - Account creation leads into email verification (Story 1.7) — the user cannot enter the app until their email is verified

**Story 1.2: Adding First CardiMember**
- **As a** new CardiTrack user
- **I want to** easily add my parent as a CardiMember with minimal information
- **So that** I don't abandon the setup process due to complexity
- **Acceptance Criteria:**
  - Progressive disclosure (collect basic info first, details later)
  - Required fields: Name and Sex (Male/Female) — the Sex picker is a **deliberate divergence from the Figma comps** (it sets the reference range readings are judged against); Date of Birth defaults to today
  - Optional fields: Relationship (falls back to Other), Photo, medical notes (encrypted), emergency contacts
  - Privacy card **not built** (the "Your parent will be notified" AC is unmet)
  - Visual progress indicator (Step 2 of 4)
  - Emergency-phone placeholder localized by device region (PR #8): US/CA "+1 555 000 0000", GB "+44 7700 900000" — **limitation:** all other regions fall back to the US format, notable given the US + EU target market

**Story 1.3: Device Connection Wizard**
- **As a** caregiver setting up monitoring
- **I want to** connect my parent's wearable device through a guided wizard
- **So that** I understand what permissions are needed and why
- **Acceptance Criteria:**
  - Device selection screen with icons — **Fitbit and Google Pixel Watch in MVP 1** (both ride the Google Health API); Apple Watch, Garmin, Samsung, Withings, Other shown as "Coming Soon"
  - OAuth flow with clear permission explanations
  - "Why we need this" tooltips for each permission
  - Success confirmation with sample data preview
  - Troubleshooting tips if connection fails
  - Support for multiple devices per CardiMember

**Story 1.4: CardiMember Profile Management**
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
  - Screens: M1-13 (CardiMember Detail), M1-14 (Edit CardiMember)

**Story 1.5: Sign In** _(shipped — no Figma M1 frame, needs design sync)_
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
  - Screen: SignInPage (no Figma M1 frame)

**Story 1.6: Forgot Password** _(shipped — no Figma M1 frame, needs design sync)_
- **As a** caregiver who forgot my password
- **I want to** request a reset link by email
- **So that** I can regain access without contacting support
- **Acceptance Criteria:**
  - Request state: email input + send-reset-link CTA
  - Confirmation state: "Check your email" with resend option (cooldown to prevent spamming)
  - "Back to sign in" path from both states
  - Screen: ForgotPasswordPage (no Figma M1 frame)

**Story 1.7: Verify Email** _(shipped — no Figma M1 frame, needs design sync)_
- **As a** newly registered caregiver
- **I want to** verify my email address right after signing up
- **So that** my account is secured and I can proceed into the app
- **Acceptance Criteria:**
  - **Hard tenant gate:** account creation does not auto-login; the user lands on the verification screen and cannot proceed until verified (threads between Story 1.1 and Story 1.2)
  - "I've verified — continue" action with a checking state
  - "Open mail app" shortcut
  - Resend verification email (cooldown; confirmation message)
  - Clear error state when the address is still unverified
  - Screen: VerifyEmailPage (no Figma M1 frame)

**Story 1.8: Account-Type Setup** _(shipped — no Figma M1 frame, needs design sync)_
- **As a** first-time user after verification
- **I want to** say whether I'm caring for my family or providing care professionally
- **So that** CardiTrack can tailor my account
- **Acceptance Criteria:**
  - Radio-cards: "My Family" (personal) vs "My Organization" (professional care)
  - Selecting "My Organization" reveals a required Organization Name field
  - Continue disabled until a type is chosen
  - Screen: Onboarding/AccountSetupPage (no Figma M1 frame)
  - **Flagged scope question (not a resolution):** the Organization option surfaces business onboarding in MVP 1 while the Guardian Plus business tier is post-R4 in the release matrix — needs a product decision

### Dashboard & Monitoring

**Story 2.1: Daily Health Overview**
- **As a** busy family caregiver checking in daily
- **I want to** see a quick visual summary of my parent's health status
- **So that** I know if everything is okay without reading detailed reports
- **Acceptance Criteria:**
  - Traffic light status indicators (Green/Yellow/Orange/Red)
  - Key metrics at-a-glance: Steps, Heart Rate, Sleep Quality
  - "Last synced" timestamp
  - Comparison to baseline ("20% below normal activity")
  - Quick action buttons on the dashboard: SOS / Call / Message / Details. Acknowledge lives on M1-10 and `AlertDetailPage`, not on the dashboard row

**Story 2.2: Multi-Member Dashboard**
- **As a** caregiver monitoring both parents
- **I want to** view side-by-side health summaries for multiple CardiMembers
- **So that** I can quickly compare their health status
- **Acceptance Criteria:**
  - Card-based layout (one card per CardiMember)
  - Sortable by status (alerts first) or name
  - Filter options (show only alerts, specific member)
  - Responsive grid (mobile: stacked, tablet: 2 columns, desktop: 3+ columns)

**Story 2.3: Trend Charts & Historical Data**
- **As a** caregiver wanting to understand long-term patterns
- **I want to** view interactive charts showing activity, heart rate, and sleep trends
- **So that** I can spot gradual declines or improvements over time
- **Acceptance Criteria:**
  - Time range selector (7 days, 30 days, 90 days, custom)
  - Baseline overlay (show normal range as shaded area)
  - Hover tooltips with exact values and dates
  - Export to PDF/CSV for doctor visits
  - Annotations for alerts and significant events

**Story 2.4: Ask about this CardiMember** _(P1 — Should Have)_
- **As a** caregiver who has a specific question about today's readings
- **I want to** type it on the member's page and get a plain-language answer from the readings we already have
- **So that** I do not have to infer from the summary card or wait for the next digest
- **Acceptance Criteria:**
  - Composer on M1-13 (as-built `AskCard`; no Figma frame — needs design sync), not a new chat page
  - Answered by in-project MedGemma from last 7 days + baseline + member context — not Gemini chat
  - Cannot set or change alerts, look anything else up, or invent a number
  - Question is never stored; last exchange stays on screen for this visit
  - Empty/whitespace question is rejected; over-long question (500 chars) is rejected
  - "Not medical advice" line visible on the card
  - 404 when the caller cannot view the member (same non-disclosure as other insights)
- **Screens:** M1-13 (CardiMember Detail) — as-built card
- **Out of Scope:** speech-to-text, search beyond injected readings, custom alert creation, conversation history, web UI (web is still template-stage)

### Alert Management

**Story 3.1: Receiving Critical Alerts**
- **As a** caregiver receiving an urgent alert
- **I want to** immediately understand what's wrong and what action to take
- **So that** I can respond appropriately without panic
- **Acceptance Criteria:**
  - Alert severity clearly visible (color-coded, icon)
  - Plain language description ("Dad hasn't moved this morning. Typical wake time: 7am. Current time: 11am")
  - Recommended actions ("Call to check in", "Contact emergency services")
  - One-tap actions (Call, SMS, Acknowledge)
  - Alert history visible ("This is the first time this month")

**Story 3.2: Managing Alert Notifications**
- **As a** caregiver who receives too many notifications
- **I want to** customize which alerts I receive and how
- **So that** I only get notified about truly important issues
- **Acceptance Criteria:**
  - Granular notification settings by alert type
  - Channel preferences (Email, SMS, Push, All)
  - Quiet hours configuration ("Don't alert me 10pm-7am unless Red")
  - Sensitivity adjustment per CardiMember
  - Multi-user alert routing (alert siblings on high-severity only)

**Story 3.3: Alert Acknowledgment & Notes**
- **As a** caregiver following up on an alert
- **I want to** mark it as acknowledged and add notes about my action
- **So that** other family members know it's been handled
- **Acceptance Criteria:**
  - Quick acknowledgment button with timestamp
  - Notes field ("Called, he had a cold but is fine")
  - Photos upload option (if doctor visit occurred)
  - Alert status: New → Acknowledged → Resolved
  - Notification to other family members when acknowledged

### Family Collaboration

**Story 4.1: Inviting Family Members**
- **As an** account admin
- **I want to** invite my siblings to view our parent's health data
- **So that** we can share caregiving responsibilities
- **Acceptance Criteria:**
  - Email invitation with role selection (Admin, Staff, Member)
  - Permission matrix clearly explained (who can see/do what)
  - Pending invitations list with resend/revoke options
  - Activity log showing who accessed what and when (HIPAA compliance)

**Story 4.2: Coordinating Care**
- **As a** family member collaborating with siblings
- **I want to** see who last checked on our parent and add shared notes
- **So that** we avoid duplicate calls and coordinate better
- **Acceptance Criteria:**
  - "Last viewed by" indicator on dashboard
  - Shared notes section visible to all family members
  - @mention functionality to notify specific family members
  - Weekly digest email summarizing activity and alerts

### Mobile Experience

**Story 5.1: Mobile Push Notifications**
- **As a** caregiver on the go
- **I want to** receive push notifications for critical alerts on my phone
- **So that** I can respond quickly even when not using the app
- **Acceptance Criteria:**
  - **Shipped:** FCM registration, content-free payload, deep link to `alertdetail`, Safety-class nudge push (`DEVICE_AUTH_BROKEN`, three-tier `DEVICE_BATTERY_LOW`)
  - **Not shipped (remain M4-05 / R4):** rich lock-screen action buttons, notification grouping, iOS notification service extension
  - Badge count on app icon
  - Deep linking to specific alert or CardiMember

**Story 5.2: Quick Check-In (Mobile Widget)**
- **As a** busy caregiver checking my phone frequently
- **I want to** see parent's health status without opening the app
- **So that** I get instant peace of mind throughout the day
- **Acceptance Criteria:**
  - Home screen widget showing status for all CardiMembers
  - Traffic light indicators (Green = all good)
  - Last sync time
  - Tap to open full app

**Story 5.3: Native Sharing**
- **As a** caregiver who wants to share health data with a sibling, doctor, or save it locally
- **I want to** use the device's native share sheet from any relevant screen
- **So that** I can send information through any app already on my phone without extra steps
- **Acceptance Criteria:**
  - Native OS share sheet triggered from export, chart, and test results screens
  - Share targets: email, messages, AirDrop, health apps, save to Files
  - Screenshot capture for sharing individual charts or alert summaries
  - PDF/CSV output formats available
  - Screen: M4-07 (Share Sheet Integration)

### Settings & Preferences

**Story 6.1: Subscription Management** _(P1 — Should Have, R2/MVP 2 per the release matrix)_
- **As a** paying customer
- **I want to** easily understand my current plan and upgrade/downgrade options
- **So that** I can make informed decisions about features vs cost
- **Acceptance Criteria:**
  - Current tier highlighted (Basic $7, Complete Care $10, Guardian Plus $15)
  - Feature comparison table (what I get with each tier)
  - Usage metrics ("You're monitoring 2 CardiMembers, upgrade to add more")
  - One-click upgrade/downgrade
  - Annual discount option (15% savings)
  - Clear billing date and payment method
  - _Note: Guardian Plus (business tier) is out of scope for MVP — handled via a dedicated business account flow_

**Story 6.3: Health Data Export**
- **As a** caregiver preparing for a doctor's visit or needing records
- **I want to** export a CardiMember's health data in standard medical formats
- **So that** I can share it with healthcare providers or keep it for my records
- **Acceptance Criteria:**
  - Date range selector for the export window
  - Format options: PDF, CSV, FHIR R4 (**MVP 1**); HL7 v2 (MVP 2); LOINC/CCD (MVP 2); SNOMED CT (MVP 3)
  - Delivery options: save to device, share via system share sheet, email to self
  - Clear format explanations ("FHIR R4 is accepted by most US patient portals and EHR systems")
  - Export confirmation with file size estimate
  - Screen: M1-17 (Health Data Export)

**Story 6.2: Device Management**
- **As a** caregiver whose parent switched devices
- **I want to** disconnect old device and connect new one easily
- **So that** data continues flowing without interruption
- **Acceptance Criteria:**
  - List of connected devices with status (Active, Disconnected, Token Expired)
  - Refresh/reconnect button for expired OAuth tokens
  - Primary device designation (when multiple devices connected)
  - Device removal with confirmation ("This will delete connection but keep historical data")
  - Data source indicator on charts (which device provided this data)

---

## 👵 Secondary Persona: Elderly CardiMember (Ages 70-85)

### Consent & Transparency

**Story 7.1: Understanding Monitoring** _(Descoped — product decision: wearers never log in; all wearer-facing auth/screens permanently descoped)_
- **As an** elderly person being monitored
- **I want to** clearly understand what data is being shared and who can see it
- **So that** I can give informed consent and maintain dignity
- **Acceptance Criteria:**
  - Simple consent screen in large, readable font
  - "What your family will see" with examples
  - "What they won't see" (privacy reassurance)
  - Option to decline specific data types (e.g., share activity but not sleep)
  - Easy-to-understand video explanation

**Story 7.2: Viewing My Own Data** _(Descoped — product decision: wearers never log in; all wearer-facing auth/screens permanently descoped)_
- **As an** elderly CardiMember
- **I want to** access my own health dashboard if I choose
- **So that** I can see what my family sees and feel included
- **Acceptance Criteria:**
  - Optional CardiMember login (not required)
  - Simplified view with larger fonts and fewer options
  - "Your family was notified about..." transparency
  - Ability to add notes ("I was sick this week, that's why activity is low")

**Story 7.3: Pausing Monitoring Temporarily** _(P0 — shipped on M1-13 for the caregiver; wearer-self-pause remains descoped)_
- **As an** independent elderly person
- **I want to** temporarily pause monitoring when I don't need it
- **So that** I maintain autonomy and privacy when desired
- **Acceptance Criteria:**
  - "Pause for X hours/days" option
  - Family notification when paused ("Dad paused monitoring for 24 hours")
  - Auto-resume with reminder
  - Easy reactivation

**Story 7.4: Telemetry Consent** _(GAP — product follow-up, not yet designed or built)_
- **As an** app user
- **I want to** control whether crash reports and usage telemetry are collected from my device
- **So that** monitoring my family doesn't mean being monitored myself without consent
- **Current state (shipped):** Datadog telemetry is **logs + traces only** — RUM was removed in PR #185, and with it Datadog crash reporting (`NativeCrashReportEnabled=false`); crashes/ANRs come from Play Console vitals. `TrackingConsent.Granted` is still hardcoded — consent is granted by default, there is no in-app opt-out and no diagnostics screen. **There is no in-app telemetry control in MVP 1.**
- **Why it matters:** in tension with the "consent-first" design principle (Principle 4) and the transparency framing of Story 7.1
- **Acceptance Criteria (proposed):**
  - Telemetry disclosure during onboarding or first run
  - Settings toggle to opt out of non-essential telemetry
  - Log and trace shipping respects the stored consent state

---

## 🧪 Test Results & Medical Documents (MVP 2)

**Story 12.1: Lab Results Capture**
- **As a** caregiver who received physical lab results or a discharge summary for my parent
- **I want to** scan or upload the document into CardiTrack
- **So that** all health records are in one place and can be analysed alongside wearable data
- **Acceptance Criteria:**
  - Camera scan with auto-crop and quality guidance (blur/lighting feedback)
  - File upload fallback (PDF, JPG, PNG)
  - Multi-page document support
  - OCR processing with progress indicator
  - Clear error state if document is unreadable with retry / manual entry option
  - Screen: M3-06 (Test Results Scanner)

**Story 12.2: Medical Insights from Lab Results**
- **As a** caregiver viewing parsed lab results
- **I want to** see AI-generated insights that explain values in plain language and correlate them with wearable trends
- **So that** I understand what the results mean and can decide if a doctor follow-up is needed
- **Acceptance Criteria:**
  - Parsed results table with reference ranges and out-of-range highlights
  - CardiTrack Insights section with plain-language explanation
  - Trend comparison: current result vs. previous results (if available)
  - Ability to correct/verify OCR-extracted values before saving
  - Export options (LOINC/CCD in MVP 2, SNOMED CT in MVP 3)
  - Share via native share sheet (Story 5.3)
  - Screen: M3-07 (Test Results Detail)

---

## 🏥 Tertiary Persona: Assisted Living Facility Staff

### Enterprise Dashboard

**Story 8.1: Multi-Resident Overview**
- **As a** facility healthcare director
- **I want to** monitor 50+ residents from one dashboard
- **So that** I can efficiently allocate staff attention to those who need it
- **Acceptance Criteria:**
  - List view with sortable columns (Name, Room, Status, Last Alert)
  - Filter by floor/wing/care level
  - Bulk actions (acknowledge all green status)
  - Search by name or room number
  - Export resident health summary for compliance

**Story 8.2: Staff Assignment & Handoffs**
- **As a** facility manager doing shift change
- **I want to** assign residents to specific staff and log handoff notes
- **So that** the next shift knows who needs attention
- **Acceptance Criteria:**
  - Drag-and-drop staff assignment
  - Shift notes section ("Mrs. Johnson had elevated HR, checked at 2pm, normal")
  - Outstanding alerts highlighted for incoming shift
  - Staff activity log (who checked on whom)

**Story 8.3: Family Communication**
- **As a** facility administrator
- **I want to** grant family members view-only access to their loved one's data
- **So that** families feel connected and we reduce "how is my mom" calls
- **Acceptance Criteria:**
  - Family portal link generation
  - View-only permissions (cannot modify settings)
  - Privacy controls (show only their relative's data)
  - Opt-in from resident or POA required

---

## 🎨 UI/UX Design Principles from Market Analysis

### Principle 1: Trust Through Transparency
**Insight:** Caregivers worried about "Big Brother" surveillance (Market Analysis Risk 2)
- Show data source and reasoning for every alert
- Use warm, caring language ("Your mom's activity is lower than usual. Might be worth a check-in call")
- Avoid medical jargon and alarmist language

### Principle 2: Simplicity Over Features
**Insight:** Primary users are 45-65, need quick insights during busy day
- Information hierarchy: Status → Alert → Action
- Progressive disclosure (advanced features hidden until needed)
- Mobile-first design (caregivers check phones 60+ times/day)

### Principle 3: Peace of Mind, Not Panic
**Insight:** Product sells "peace of mind" not "emergency response"
- Green status should be prominent when all is well
- Alerts provide context, not just warnings
- Success messaging ("Your dad had his most active week this month!")

### Principle 4: Respect for Elderly Dignity
**Insight:** Elderly won't wear "ugly medical devices" (Market Analysis)
- Never use patronizing language or imagery
- Focus on independence and wellness, not decline
- Consent-first approach to all monitoring

### Principle 5: Multi-Generational Accessibility
**Insight:** Users range from 45-85+ years old
- WCAG AA compliance minimum (AAA preferred)
- Font size options (small/medium/large)
- High contrast mode
- Keyboard navigation support
- Screen reader optimization

---

## 📱 Platform-Specific Stories

### Blazor Web Dashboard

**Story 9.1: Real-Time Updates (SignalR)**
- **As a** caregiver with dashboard open
- **I want to** see health data update in real-time without refreshing
- **So that** I have the most current information
- **Acceptance Criteria:**
  - Live data updates every 10 minutes (when device syncs)
  - Visual indicator when new data arrives ("Just updated")
  - No page refresh required
  - Offline indicator if connection lost

**Story 9.2: Printable Reports**
- **As a** caregiver preparing for doctor's appointment
- **I want to** generate a printable health summary for the past 30 days
- **So that** I can share it with healthcare providers
- **Acceptance Criteria:**
  - Print-optimized layout (charts, tables, key metrics)
  - Date range selection
  - Include/exclude sections (alerts, notes, trends)
  - PDF download option
  - HIPAA-compliant footer ("Confidential Health Information")

### .NET MAUI Mobile App

**Platform Requirements**
- **Minimum iOS:** 17.0 — required for modern platform APIs and reliable background push delivery
- **Minimum Android:** 12 (API 31) — raised for the Android 12 SplashScreen API, so one splash design matches the OS handover on every supported device
- **Target iOS:** 18 (latest stable)
- **Target Android:** 15 / API 35 (latest stable)

**Story 10.1: Offline Support**
- **As a** mobile user with spotty connectivity
- **I want to** view recent health data even when offline
- **So that** I can check on my parent anywhere
- **Acceptance Criteria:**
  - Local SQLite cache of last 7 days
  - Clear "Offline" indicator
  - Data syncs when connection restored
  - Offline alert queue (show pending alerts)

**Story 10.2: Biometric Login**
- **As a** mobile user accessing health data frequently
- **I want to** use Face ID/Touch ID to login quickly
- **So that** I save time while maintaining security
- **Acceptance Criteria:**
  - Biometric auth setup during onboarding
  - Fallback to password if biometric fails
  - Re-authenticate every 7 days for security
  - Option to require biometric for sensitive actions

---

## 🔔 Alert-Specific UI/UX Stories

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

---

## 📖 The CardiJournal (Daybook entries) & Alert Housekeeping

**Story 12.1: Read back the whole of yesterday** _(P1 — Should Have)_
- **As a** caregiver who was busy all day
- **I want to** read one full account of how yesterday actually went
- **So that** I can catch what a glance at the dashboard missed, and take something concrete to their next appointment
- **Acceptance Criteria:**
  - Given the member's local day has ended and they have readings for it, when I open the **Journal** tab, then I see one Daybook card for that day, newest first
  - Each card shows the day ("Yesterday", then the weekday, then the date), the review's own headline, an urgency pill, and the first three lines; tapping expands it in place to the full account and the suggestion
  - The account covers sleep, heart, oxygen and breathing, and movement — each against what is usual **for them** and against the published band where one exists, with the publisher named
  - A precise clinical term may be used **only if the same sentence explains what it measures**; a review that names a condition, calls a reading a sign of one, or proposes a treatment is never shown at all
  - **Edge — no reviews yet:** a member in their first days sees "No Daybook entries yet" and a line saying the first review is written after their first full day, not a bare empty state
  - **Edge — a reading was not measured:** the review says so plainly. Silence must never read as "healthy"
  - **Edge — refresh fails with a list already on screen:** the list stays and the failure is shown over it; finished days do not go stale, so they are still worth reading
  - **Screens:** JournalPage, JournalEntryPage (no Figma M1 frames — need design sync)

**Story 12.6: Read the week, not just the days** _(P1 — Should Have)_
- **As a** caregiver who checks in most days
- **I want to** step back and read how a whole week went
- **So that** I can see a drift that no single day made obvious
- **Acceptance Criteria:**
  - Given the member's journal week has turned and most of that week carried readings, when I switch the Journal tab to **Weeks**, then I see one card per finished week, newest first, each labelled by its span ("Mon 3 – Sun 9 August")
  - The account is about the week as a whole — what moved against their usual, what held steady, and which day stood apart — not a list of the seven days
  - Opening a week shows it in full, with the trend charts running over that week and the one before it
  - **Edge — a week too thin to account for:** a week with fewer than four days of readings gets no entry at all, and the empty state says the first is written when the week turns and needs most of its days measured — never a partial week presented as a whole one
  - **Edge — Daybooks discarded that week:** the Weekbook is still written; it is read from the week's measurements, not from its Daybooks
  - **Edge — switching cadence mid-load:** the list that arrives is the one for the cadence now selected, never the previous series painted over it
  - **Screens:** JournalPage, JournalEntryPage (no Figma M1 frames — need design sync)

**Story 12.4: Find the day I half-remember** _(P1 — Should Have)_
- **As a** caregiver who remembers "there was a day his oxygen dipped"
- **I want to** search the reviews by words, narrow by urgency, and bound by date
- **So that** I can find that day without re-reading a month of cards
- **Acceptance Criteria:**
  - Given reviews exist, when I type in the search box, then after a pause the list shows only reviews whose text, headline or suggestion contains my words — searched over the whole history, not just the loaded page
  - The urgency chip narrows to one tier; the window chip narrows to the last 7/30/90 days; the filters combine
  - **Edge — nothing matches:** the empty state says "No reviews match … clear one and look again", and the filter controls stay on screen so I can
  - **Edge — I type a % or _:** it is searched as the character I typed, not as a wildcard

**Story 12.5: See the trend behind the words** _(P1 — Should Have)_
- **As a** caregiver reading a review that says "fewer steps than usual"
- **I want to** see the last fortnight charted, with what "usual" and "recommended" mean marked and sourced
- **So that** I have awareness of the direction — without anyone scoring or diagnosing
- **Acceptance Criteria:**
  - Given I open a review, when it loads, then Sleep, Resting heart rate and Steps are charted for the last 14 days, with the member's own usual dashed and any published band shaded
  - Every chart's key names its source ("recommended 7–8 (NSF)"); a chart with no published band shows none rather than an unattributed one
  - Under each chart a counted line — "Under their usual on 10 of the last 14 nights" — counting only finished, measured days; no line at all without a baseline or under 7 measured days
  - The screen states plainly: for awareness, not medical advice; CardiTrack never diagnoses
  - **Edge — the charts fetch fails:** the review still shows; the trends section hides rather than costing the caregiver the account they came to read

**Story 12.2: Stop being paged about a night that was already better** _(P0 — Must Have)_
- **As a** caregiver
- **I want to** not be alerted when my relative simply slept *longer* than usual
- **So that** the alerts I do get keep meaning something
- **Acceptance Criteria:**
  - Given last night was longer than their usual but did not pass the recommended hours for their age, when the rules run, then **no alert is raised** — the fact appears in that day's review instead
  - A night past the recommended ceiling still alerts (yellow), and a much *shorter* night still alerts (yellow) whatever the absolute figure
  - **Edge — alerts already raised by the retired branch:** these are marked resolved rather than deleted, so they leave the dashboard strip and the unread count but stay in the archive as history

**Story 12.3: Remove an alert I have finished with** _(P1 — Should Have)_
- **As a** caregiver who opened an alert to decide about it
- **I want to** remove it from that same screen
- **So that** I do not have to go back and find the row again to act on the decision I just made
- **Acceptance Criteria:**
  - Given I am on an alert's detail screen, when I tap "Remove this alert" and confirm, then it is removed and I am returned to the Alerts list
  - The confirm says plainly that it cannot be undone — unlike acknowledging, which the same screen lets me take back
  - **Edge — already removed elsewhere:** if the alert is already gone (another caregiver, or a lost response to an earlier attempt), the removal is treated as successful rather than as an error
  - **Edge — removal fails:** I stay on the screen with the alert intact and am told why; a card that vanished on an error I never saw would be the worse outcome
  - **Screens:** AlertDetailPage (M1-11 / M1-12 / M1-16)

---

## 🧪 Onboarding Flow UX

### Step 1: Value Proposition (30 seconds)
- Hero image: Happy elderly person with Fitbit, smiling family on phone
- Headline: "Peace of Mind for Your Family. From $7/month."
- 3 key benefits with icons:
  - ✅ Works with devices they already own (Fitbit, Apple Watch, etc.)
  - ✅ AI alerts you BEFORE health issues become emergencies
  - ✅ 65-85% cheaper than medical alert systems ($7-15 vs $47/month)
- CTA: "Start Free 30-Day Trial"

### Step 2: Account Creation (1 minute)
- Email/password or "Continue with Google/Apple" (social buttons wired — Auth0 PKCE authorization-code flow in the system browser on Android/iOS)
- Checkbox: "I agree to Terms & Privacy Policy" (with links)
- **No auto-login after creation** — the user must verify their email (VerifyEmailPage, Story 1.7) before entering the app; PostLoginRouter then routes to account-type setup (Story 1.8) or Add CardiMember

### Step 3: Add CardiMember (2 minutes)
- "Who would you like to monitor?"
- Form: Name, Sex (Male/Female, required), DOB, Relationship, Photo (optional)
- Tone: "We'll help you set up monitoring in 4 simple steps" (the wizard is a 4-step flow: Create Account → Add CardiMember → Connect Device → Baseline)

### Step 4: Device Connection (3 minutes)
- "What wearable device does [Name] use?"
- Device icons with brands — **Fitbit and Google Pixel Watch active in MVP 1**; Apple Watch, Garmin, Samsung, Withings, Other shown as "Coming Soon"
- Tap "Continue with [Device]" (CTA names the selected device) → OAuth flow → Success
- "Great! We're syncing [Name]'s data. This may take a few minutes."

### Step 5: Baseline Learning (Info screen)
- "CardiTrack is learning [Name]'s normal patterns..."
- Progress indicator: "Day 3 of 30"
- "You'll start receiving alerts after we establish a baseline (30 days)"
- **As built:** every statistical rule stays silent until that 30-day baseline exists. The learning-screen toggle does not persist and does not enable "alerts in the meantime".

### Step 6: Invite Family (Optional)
- "Want to share monitoring with family members?"
- The "Invite Family Member First" link **ships in MVP 1** on the baseline screen, but the invite flow (M3-02) is MVP 2 — the link is currently a dead end
- Email invite form with role selection _(MVP 2)_
- Skip option: "I'll do this later"

### Step 7: First Dashboard View
- Celebratory tone: "You're all set! Here's [Name]'s health overview."
- Guided tour overlay (5 tooltips) — **planned, not shipped** (no guided tour exists in the current app):
  1. "This shows overall health status"
  2. "View detailed trends here"
  3. "Alerts appear in this section"
  4. "Invite family members here"
  5. "Need help? Check our support docs"

---

## 📊 Key Metrics to Track (for iterative UX improvements)

### Onboarding Metrics
- Time to first device connection (target: <5 minutes)
- Onboarding completion rate (target: >60%)
- Drop-off points in funnel

### Engagement Metrics
- Daily active users (DAU) / Monthly active users (MAU)
- Average session duration (target: 2-3 minutes for quick check)
- Alert acknowledgment time (target: <15 minutes)
- Feature adoption (what % use trends, multi-member, etc.)

### Satisfaction Metrics
- Net Promoter Score (NPS) - target: >50
- Alert usefulness rating (5-star after each alert)
- Support ticket volume by category (identifies UX pain points)

---

## 🎯 Priority Matrix for MVP 1 (Q4 2026)

### Must Have (P0)
- [x] Story 1.1-1.3: Onboarding flow
- [x] Story 1.4: CardiMember profile management
- [x] Story 1.5-1.8: Sign In, Forgot Password, Verify Email, Account-Type Setup (shipped; need Figma frames)
- [x] Story 2.1: Daily health overview
- [x] Story 2.4: Ask about this CardiMember (as-built on M1-13; needs Figma frame)
- [x] Story 3.1: Critical alert display (`AlertDetailPage`)
- [ ] Story 6.3: Health data export (PDF, CSV, FHIR R4)

### Should Have (P1)
- [ ] Story 2.3: Trend charts
- [ ] Story 3.2: Alert notification preferences
- [ ] Story 3.3: Alert acknowledgment & notes
- [ ] Story 4.1: Family member invitations
- [ ] Story 6.1: Subscription management _(moved from P0 — R2/MVP 2 per the release matrix)_
- [ ] Story 10.1: Mobile offline support
- [ ] Story 12.1: Lab results capture
- [ ] Story 12.2: Medical insights from lab results

### Nice to Have (P2)
- [ ] Story 2.2: Multi-member dashboard
- [ ] Story 5.2: Mobile widget
- [ ] Story 5.3: Native sharing
- [ ] Story 9.2: Printable reports
- [ ] Story 7.2: CardiMember self-view _(Descoped — wearers never log in; permanent product decision)_

### Future (Post-MVP)
- [ ] Story 8.1-8.3: Enterprise features
- [ ] Story 7.3: Pause monitoring
- [ ] Story 7.4: Telemetry consent (flagged gap — currently granted by default with no UI)
- [ ] Advanced ML features

---

## 📋 User Story Summary by Category

| Category | Total Stories | Must Have (P0) | Should Have (P1) | Nice to Have (P2) | Future |
|----------|---------------|----------------|------------------|-------------------|---------|
| Onboarding & Setup | 8 | 8 | 0 | 0 | 0 |
| Dashboard & Monitoring | 3 | 1 | 1 | 1 | 0 |
| Alert Management | 6 | 1 | 2 | 0 | 0 |
| Family Collaboration | 2 | 0 | 1 | 0 | 1 |
| Mobile Experience | 3 | 0 | 0 | 2 | 1 |
| Settings & Preferences | 3 | 1 | 1 | 0 | 1 |
| Elderly CardiMember | 4 | 0 | 0 | 1 | 3 |
| Enterprise Features | 3 | 0 | 0 | 0 | 3 |
| Platform-Specific | 4 | 0 | 1 | 1 | 2 |
| Test Results & Medical Documents | 2 | 0 | 2 | 0 | 0 |
| **TOTAL** | **38** | **11** | **8** | **6** | **11** |

---

## 🔗 Related Documentation

- [Solution Manifest](../../../solution_manifest.md) - Technical architecture and business model
- [Market Analysis](../../../market_analysis.md) - Competitive landscape and positioning
- [README](../../../readme.md) - Project overview and getting started
- [Entity Summary](../../../technical/entity_summary.md) - Database entities and relationships
- [User Onboarding Process](../../../technical/user_onboarding_process.md) - Technical onboarding flow

---

**Document Version:** 1.2
**Last Updated:** August 14, 2026
**Next Review:** Q4 2026 (MVP 1 / R1 wave — post-beta feedback)
**Owner:** Product & UX Team

---

This comprehensive set of user stories provides the foundation for designing an intuitive, trustworthy, and effective UI/UX for CardiTrack across web and mobile platforms. The stories are grounded in real user needs identified in the market analysis and aligned with the technical capabilities outlined in the solution manifest.
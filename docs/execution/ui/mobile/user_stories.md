# CardiTrack User Stories for UI/UX Design

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
  - "How it works" video or interactive tutorial
  - Mobile-responsive design

**Story 1.2: Adding First CardiMember**
- **As a** new CardiTrack user
- **I want to** easily add my parent as a CardiMember with minimal information
- **So that** I don't abandon the setup process due to complexity
- **Acceptance Criteria:**
  - Progressive disclosure (collect basic info first, details later)
  - Required fields: Name, Date of Birth, Relationship
  - Optional fields: Photo, medical notes (encrypted), emergency contacts
  - Clear privacy messaging ("Your parent will be notified")
  - Visual progress indicator (Step 1 of 3)

**Story 1.3: Device Connection Wizard**
- **As a** caregiver setting up monitoring
- **I want to** connect my parent's wearable device through a guided wizard
- **So that** I understand what permissions are needed and why
- **Acceptance Criteria:**
  - Device selection screen with icons (Fitbit, Apple Watch, Garmin, Samsung)
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
  - Encrypted medical notes (require biometric auth to view/edit)
  - Enable/disable monitoring toggle with confirmation
  - Alert sensitivity control (Low / Medium / High)
  - Quick-action buttons: View Dashboard, View Alerts, Manage Devices
  - Danger-zone actions: Pause Monitoring, Remove CardiMember (with confirmation dialogs)
  - Screens: M1-13 (CardiMember Detail), M1-14 (Edit CardiMember)

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
  - Quick action buttons ("Call Mom", "View Details", "Acknowledge Alert")

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
  - Email invitation with role selection (Admin, Staff, Viewer)
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
  - Rich notifications with action buttons (Call, View, Acknowledge)
  - Lock screen visibility for critical alerts
  - Notification grouping (multiple alerts from same CardiMember)
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

**Story 6.1: Subscription Management**
- **As a** paying customer
- **I want to** easily understand my current plan and upgrade/downgrade options
- **So that** I can make informed decisions about features vs cost
- **Acceptance Criteria:**
  - Current tier highlighted (Basic $8, Complete Care $15)
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
  - Format options: PDF, CSV, FHIR R4 (**MVP 1**); HL7 v2 (MVP 2); LOINC/CCD (MVP 3); SNOMED CT (MVP 4)
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

**Story 7.1: Understanding Monitoring**
- **As an** elderly person being monitored
- **I want to** clearly understand what data is being shared and who can see it
- **So that** I can give informed consent and maintain dignity
- **Acceptance Criteria:**
  - Simple consent screen in large, readable font
  - "What your family will see" with examples
  - "What they won't see" (privacy reassurance)
  - Option to decline specific data types (e.g., share activity but not sleep)
  - Easy-to-understand video explanation

**Story 7.2: Viewing My Own Data**
- **As an** elderly CardiMember
- **I want to** access my own health dashboard if I choose
- **So that** I can see what my family sees and feel included
- **Acceptance Criteria:**
  - Optional CardiMember login (not required)
  - Simplified view with larger fonts and fewer options
  - "Your family was notified about..." transparency
  - Ability to add notes ("I was sick this week, that's why activity is low")

**Story 7.3: Pausing Monitoring Temporarily**
- **As an** independent elderly person
- **I want to** temporarily pause monitoring when I don't need it
- **So that** I maintain autonomy and privacy when desired
- **Acceptance Criteria:**
  - "Pause for X hours/days" option
  - Family notification when paused ("Dad paused monitoring for 24 hours")
  - Auto-resume with reminder
  - Easy reactivation

---

## 🧪 Test Results & Medical Documents (MVP 3)

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
  - Export options (LOINC/CCD in MVP 3, SNOMED CT in MVP 4)
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
  - Live data updates every 30 minutes (when device syncs)
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
- **Minimum iOS:** 16.0 — covers ~90%+ of active iPhones; required for modern platform APIs and reliable background push delivery
- **Minimum Android:** 10 (API 29) — covers ~85–90% of active Android devices; required for scoped storage and modern permission model
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

## 🧪 Onboarding Flow UX

### Step 1: Value Proposition (30 seconds)
- Hero image: Happy elderly person with Fitbit, smiling family on phone
- Headline: "Peace of Mind for Your Family. $8/month."
- 3 key benefits with icons:
  - ✅ Works with devices they already own (Fitbit, Apple Watch, etc.)
  - ✅ AI alerts you BEFORE health issues become emergencies
  - ✅ 70% cheaper than medical alert systems ($8 vs $47/month)
- CTA: "Start Free 30-Day Trial"

### Step 2: Account Creation (1 minute)
- Email/password or "Continue with Google/Apple"
- Checkbox: "I agree to Terms & Privacy Policy" (with links)
- Auto-login after creation

### Step 3: Add CardiMember (2 minutes)
- "Who would you like to monitor?"
- Form: Name, DOB, Relationship, Photo (optional)
- Tone: "We'll help you set up monitoring in 3 simple steps"

### Step 4: Device Connection (3 minutes)
- "What wearable device does [Name] use?"
- Device icons with brands (Fitbit, Apple, Garmin, Samsung, Withings)
- Click → OAuth flow → Success
- "Great! We're syncing [Name]'s data. This may take a few minutes."

### Step 5: Baseline Learning (Info screen)
- "CardiTrack is learning [Name]'s normal patterns..."
- Progress indicator: "Day 3 of 30"
- "You'll start receiving alerts after we establish a baseline (30 days)"
- Option: "Use statistical alerts in the meantime" (basic threshold alerts)

### Step 6: Invite Family (Optional)
- "Want to share monitoring with family members?"
- Email invite form with role selection
- Skip option: "I'll do this later"

### Step 7: First Dashboard View
- Celebratory tone: "You're all set! Here's [Name]'s health overview."
- Guided tour overlay (5 tooltips):
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

## 🎯 Priority Matrix for MVP (Q1 2026)

### Must Have (P0)
- [ ] Story 1.1-1.3: Onboarding flow
- [ ] Story 1.4: CardiMember profile management
- [ ] Story 2.1: Daily health overview
- [ ] Story 3.1: Critical alert display
- [ ] Story 6.1: Subscription management
- [ ] Story 6.3: Health data export (PDF, CSV, FHIR R4)

### Should Have (P1)
- [ ] Story 2.3: Trend charts
- [ ] Story 3.2: Alert notification preferences
- [ ] Story 3.3: Alert acknowledgment & notes
- [ ] Story 4.1: Family member invitations
- [ ] Story 10.1: Mobile offline support
- [ ] Story 12.1: Lab results capture
- [ ] Story 12.2: Medical insights from lab results

### Nice to Have (P2)
- [ ] Story 2.2: Multi-member dashboard
- [ ] Story 5.2: Mobile widget
- [ ] Story 5.3: Native sharing
- [ ] Story 9.2: Printable reports
- [ ] Story 7.2: CardiMember self-view

### Future (Post-MVP)
- [ ] Story 8.1-8.3: Enterprise features
- [ ] Story 7.3: Pause monitoring
- [ ] Advanced ML features

---

## 📋 User Story Summary by Category

| Category | Total Stories | Must Have (P0) | Should Have (P1) | Nice to Have (P2) | Future |
|----------|---------------|----------------|------------------|-------------------|---------|
| Onboarding & Setup | 4 | 4 | 0 | 0 | 0 |
| Dashboard & Monitoring | 3 | 1 | 1 | 1 | 0 |
| Alert Management | 6 | 1 | 2 | 0 | 0 |
| Family Collaboration | 2 | 0 | 1 | 0 | 1 |
| Mobile Experience | 3 | 0 | 0 | 2 | 1 |
| Settings & Preferences | 3 | 2 | 0 | 0 | 1 |
| Elderly CardiMember | 3 | 0 | 0 | 1 | 2 |
| Enterprise Features | 3 | 0 | 0 | 0 | 3 |
| Platform-Specific | 4 | 0 | 1 | 1 | 2 |
| Test Results & Medical Documents | 2 | 0 | 2 | 0 | 0 |
| **TOTAL** | **33** | **8** | **7** | **6** | **10** |

---

## 🔗 Related Documentation

- [Solution Manifest](../../../solution_manifest.md) - Technical architecture and business model
- [Market Analysis](../../../market_analysis.md) - Competitive landscape and positioning
- [README](../../../readme.md) - Project overview and getting started
- [Entity Summary](../../../technical/entity_summary.md) - Database entities and relationships
- [User Onboarding Process](../../../technical/user_onboarding_process.md) - Technical onboarding flow

---

**Document Version:** 1.0
**Last Updated:** February 24, 2026
**Next Review:** February 2026 (post-MVP beta feedback)
**Owner:** Product & UX Team

---

This comprehensive set of user stories provides the foundation for designing an intuitive, trustworthy, and effective UI/UX for CardiTrack across web and mobile platforms. The stories are grounded in real user needs identified in the market analysis and aligned with the technical capabilities outlined in the solution manifest.
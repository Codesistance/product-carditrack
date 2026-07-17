# CardiTrack Web App — User Stories

Based on the web screen specifications, solution manifest, and market analysis, here are comprehensive user stories for the CardiTrack Blazor web application, organised by persona and MVP.

---

## 👨‍👩‍👧 Primary Persona: Family Caregiver (Ages 45-65)

The primary web user checks in from a laptop or desktop at home or work. They want quick, at-a-glance status checks and the ability to dig into detail when something looks off. Speed and clarity matter — they are typically busy adults fitting monitoring into their daily routine.

---

### Onboarding & Setup

**Story 1.1: Landing Page & Value Discovery**
- **As a** family caregiver who just heard about CardiTrack
- **I want to** quickly understand what the product does and why it's worth trying
- **So that** I can decide whether to sign up within a single page visit
- **Screen:** W1-01
- **MVP:** 1
- **Acceptance Criteria:**
  - Hero section answers "what it does" and "who it's for" above the fold
  - Value proof points are concrete: compatible devices, price vs. alternatives, AI-powered alerts
  - "Start Free 30-Day Trial" CTA is prominent — no credit card required
  - "How It Works" section shows the 3-step flow (Connect → Learn → Alert)
  - Testimonials from real caregiver archetypes
  - Pricing cards give a clear sense of cost before sign-up
  - Page header becomes sticky and solid on scroll so CTA remains accessible
  - No login required to view any part of the landing page

**Story 1.2: Account Creation**
- **As a** new visitor who decided to try CardiTrack
- **I want to** create an account in under 2 minutes
- **So that** I can get to setup without friction
- **Screen:** W1-02
- **MVP:** 1
- **Acceptance Criteria:**
  - Email/password or social login (Google, Apple) — user's choice
  - Password strength indicator updates in real-time as user types
  - Inline field validation — errors appear beneath the field, not on submit
  - "Create Account" button is disabled until all fields are valid and terms accepted
  - Terms and Privacy Policy links open in a new tab (do not navigate away)
  - Error banner for duplicate email is clear and actionable ("Sign in instead?")
  - On success: redirect directly to onboarding, no email verification gate

**Story 1.3: Adding First CardiMember**
- **As a** new CardiTrack user just signed up
- **I want to** add my elderly parent with only essential information
- **So that** I don't abandon setup due to a long form
- **Screen:** W1-03
- **MVP:** 1
- **Acceptance Criteria:**
  - Only 3 required fields: Full Name, Date of Birth, Relationship
  - Photo upload supports drag-and-drop onto the circle area as well as click-to-browse
  - Optional fields (medical notes, emergency contact) are hidden behind a collapsible — not shown by default
  - Medical notes field shows an encryption indicator (🔒) to reassure about privacy
  - Privacy notice explains what the CardiMember will know in plain language
  - "Skip for Now" link goes directly to dashboard without blocking progress
  - Progress bar shows "Step 1 of 4" to set expectations for the full onboarding

**Story 1.4: Device Connection Wizard**
- **As a** caregiver setting up monitoring for the first time
- **I want to** connect my parent's wearable device through a clear, guided process
- **So that** I understand what permissions I'm granting and why
- **Screens:** W1-04, W1-05, W1-06
- **MVP:** 1
- **Acceptance Criteria:**
  - Device selection grid shows all supported brands with logos (Fitbit, Apple Watch, Garmin, Samsung, Withings)
  - Clicking a device card automatically advances to the OAuth step — no separate "Next" button
  - Keyboard users can navigate device cards with arrow keys and select with Enter
  - OAuth permission list uses plain language ("So we can spot if something's off"), not technical scopes
  - Each permission row has an (i) tooltip on hover with a one-line explanation
  - Privacy pledge is visible before the user taps "Authorize" — not buried in small print
  - OAuth flow opens in a popup or new tab — does not destroy the onboarding context
  - On success: data preview card shows real data (or skeleton if still syncing)
  - "Add Another Device" option visible before proceeding

**Story 1.5: Baseline Learning Info**
- **As a** caregiver who just connected a device
- **I want to** understand what "baseline learning" means and when I'll get alerts
- **So that** I'm not confused if I don't receive alerts immediately
- **Screen:** W1-07
- **MVP:** 1
- **Acceptance Criteria:**
  - Explanation uses plain language: what CardiTrack learns, why 30 days, what to expect
  - Shows "Day 1 of 30" progress bar so the timeline is tangible
  - Toggle for "basic alerts in the meantime" (e.g., heart rate over 100) — enabled by default
  - "Go to Dashboard" CTA is the primary action
  - MVP 3 addition: "Invite Family Members First" link available

---

### Dashboard & Monitoring

**Story 2.1: Daily Status Check (Desktop)**
- **As a** caregiver opening the app at the start of their day
- **I want to** see my parent's overnight and morning health status at a glance
- **So that** I know if everything is okay without reading detailed data
- **Screen:** W1-08
- **MVP:** 1
- **Acceptance Criteria:**
  - Status hero card uses color + icon + plain-language label to communicate status (never color alone)
  - Four status levels are visually distinct: Normal / Caution / Urgent / Critical
  - "Last synced" timestamp is always visible and accurate
  - 3 metric cards (Activity, Heart Rate, Sleep) show current value, baseline comparison, and mini sparkline
  - Quick action buttons (Call, Message, View Details) are reachable with a single click
  - Desktop layout uses a 2-column split: primary status + metrics left; alerts feed and quick links right
  - SignalR pushes updates to the page in real-time — no manual refresh needed
  - "Updated just now" toast appears when new data arrives via SignalR

**Story 2.2: Real-Time Updates Without Refresh**
- **As a** caregiver with the dashboard open in a browser tab
- **I want to** see health data update automatically when new data arrives
- **So that** I always see current information without having to reload the page
- **Screen:** W1-08
- **MVP:** 1
- **Acceptance Criteria:**
  - SignalR connection established on page load — connection state is visible in the UI (subtle indicator)
  - Metric cards, status hero, and recent alerts section update without page reload
  - "Just updated" indicator appears briefly after each data push
  - If SignalR connection drops: show "Reconnecting..." state, then "Reconnected" toast on recovery
  - If offline: show W4-03 offline banner, freeze data with last-updated timestamp

**Story 2.3: Multi-Member Overview (Desktop Grid)**
- **As a** caregiver monitoring both parents
- **I want to** see a side-by-side grid of all my CardiMembers' statuses
- **So that** I can quickly identify who needs attention without clicking into each profile
- **Screen:** W3-03
- **MVP:** 3
- **Acceptance Criteria:**
  - Desktop layout: 3-column grid; tablet: 2-column; mobile: single column stacked
  - Each card shows: photo, name, age, status (color + icon + label), last synced, alert count badge
  - Cards sortable by status (critical first) or name
  - Filter chips: [All] [Alerts Only] [Good Status]
  - Hovering a card reveals "Call" and "View Dashboard" quick actions
  - Clicking anywhere on a card navigates to W1-08 for that member
  - "+ Add CardiMember" button always visible (respects plan limits)

**Story 2.4: Trend Charts & Historical Data**
- **As a** caregiver preparing for a parent's doctor appointment
- **I want to** view interactive charts of activity, heart rate, and sleep over the past 30 days
- **So that** I can spot gradual trends and bring concrete data to the consultation
- **Screen:** W2-03
- **MVP:** 2
- **Acceptance Criteria:**
  - Time range selector: [7D] [30D] [90D] [Custom date range via date picker popover]
  - Metric tabs: [Activity] [Heart Rate] [Sleep] [All]
  - Baseline/normal range shown as shaded area behind the line chart
  - Alert events shown as markers on the timeline — hoverable for alert summary
  - Hover on any data point: tooltip shows date, value, % vs baseline
  - Scroll to zoom in; double-click to reset zoom
  - Keyboard: arrow keys step through data points one day at a time
  - Summary stats card below chart: Average, High (date), Low (date), Trend direction
  - Export button opens PDF / CSV / email options

---

### Alert Management

**Story 3.1: Understanding a Critical Alert**
- **As a** caregiver who just received a critical alert
- **I want to** immediately understand what's wrong and what I should do
- **So that** I can take the right action without delay or panic
- **Screens:** W1-11, W1-15
- **MVP:** 1
- **Acceptance Criteria:**
  - Critical banner uses pulsing animation to convey urgency — cannot be missed
  - Plain language title and description — no medical jargon
  - Event timeline shows: last movement, expected wake time, time of alert, time I was notified
  - "CALL NOW" button is the most prominent element on the page — one click to dial
  - "I'm on my way" button updates status and notifies other family members
  - Dismissal ("He told me he'd sleep in") prompts for a note before resolving
  - Family notification card confirms who else was alerted and when

**Story 3.2: Browsing Alerts with Split-Pane View**
- **As a** caregiver reviewing multiple alerts
- **I want to** see the alert list and alert detail side by side
- **So that** I can quickly scan alerts and read details without losing my place in the list
- **Screen:** W1-09 (split-pane with W1-10 / W1-11 / W1-15)
- **MVP:** 1
- **Acceptance Criteria:**
  - Desktop layout: list on the left, detail panel on the right — clicking a row loads detail in-panel without navigation
  - Keyboard: `J` / `K` navigates between alerts; `O` opens detail; `E` acknowledges
  - Tablet/mobile: clicking an alert navigates to full-page detail (no split-pane)
  - Alert rows highlight on hover; action buttons (Acknowledge ✓, expand →) appear on hover
  - Unread alerts have a visible unread dot that disappears on open
  - Selected row remains highlighted while detail is open in the right panel

**Story 3.3: Filtering and Searching Alerts**
- **As a** caregiver with a long alert history
- **I want to** filter by severity and search by keyword
- **So that** I can find a specific alert quickly without scrolling through everything
- **Screen:** W1-09
- **MVP:** 1
- **Acceptance Criteria:**
  - Filter chips: [All] [Unread] [Critical] [Today] [This Week]
  - Sort dropdown: Newest First / Severity
  - Inline search filters alert list in real-time as I type
  - "No alerts match this filter" empty state is clear and suggests removing filters
  - Applied filters are visible as removable chips

**Story 3.4: Acknowledging an Alert with Notes**
- **As a** caregiver who has followed up on an alert
- **I want to** mark it as acknowledged and record what I did
- **So that** my family members know it's been handled and why
- **Screen:** W1-10, W1-11
- **MVP:** 1
- **Acceptance Criteria:**
  - "Mark as Acknowledged" button is visible on every unread alert detail
  - Acknowledgment records: who acknowledged, timestamp, and optional note
  - Once acknowledged: shows "Acknowledged by [Name], [X] min ago" with note if present
  - Other family members see the acknowledgment in real-time via SignalR
  - Alert status flow: New → Acknowledged → Resolved

**Story 3.5: Customising Alert Notification Preferences**
- **As a** caregiver who gets too many low-priority notifications
- **I want to** configure which alert types trigger which notification channels
- **So that** I only get interrupted for genuinely important issues
- **Screen:** W2-04
- **MVP:** 2
- **Acceptance Criteria:**
  - Separate settings per alert type (Activity, Heart Rate, Sleep, Pattern Break)
  - Per-type sensitivity slider: Low / Medium / High
  - Per-type notification channels: Email / SMS / Browser Push / All
  - Quiet Hours: time-range picker with override for critical-only emergencies
  - Pattern Break alerts cannot be disabled (always-on, labelled clearly)
  - "Send Test" buttons for each channel to verify delivery
  - CardiMember selector if user is managing multiple members

---

### Family Collaboration

**Story 4.1: Inviting a Sibling to Co-Monitor**
- **As an** account admin caregiver
- **I want to** invite my sibling to view our parent's health data
- **So that** we can share the emotional and practical caregiving load
- **Screen:** W3-02
- **MVP:** 3
- **Acceptance Criteria:**
  - Modal overlay for invite — does not leave current page
  - Role selector with plain-language description of each role (Admin / Staff / Viewer)
  - Optional personal message field with a helpful placeholder
  - Invitation sent via email with a deep link to accept
  - Pending invitations visible in a separate tab (W3-01) with Resend / Revoke options
  - New member appears in active list as soon as they accept (live via SignalR)

**Story 4.2: Managing Family Member Roles**
- **As an** account admin
- **I want to** change a family member's access level or remove them
- **So that** permissions stay appropriate as caregiving arrangements change
- **Screen:** W3-01
- **MVP:** 3
- **Acceptance Criteria:**
  - Role change available from the table row on hover (no separate settings page needed)
  - Role change takes effect immediately
  - "Remove Access" shows a confirmation dialog before executing
  - Activity log per member: who accessed what and when (HIPAA audit trail)
  - Admin cannot remove themselves if they are the only admin (error state with guidance)

**Story 4.3: Sharing Notes with the Family**
- **As a** family member who just called my parent after an alert
- **I want to** log a note for the rest of the family explaining what happened
- **So that** nobody calls twice and everyone stays informed
- **Screens:** W3-04, W3-05
- **MVP:** 3
- **Acceptance Criteria:**
  - Note composer is accessible directly from the notes feed — no deep navigation
  - @mention dropdown appears when typing "@" — shows family member names
  - Notes can be tagged to a specific CardiMember (or left as general)
  - Photo attachments supported (max 3, drag-and-drop or click-to-browse)
  - Posted note appears in the feed immediately for all family members via SignalR
  - Threaded replies keep conversations in context without cluttering the main feed
  - Author can edit or delete their own note from the three-dot menu

---

### Health Records

**Story 7.1: Uploading Lab Test Results**
- **As a** caregiver who just received Dad's blood test results
- **I want to** upload the PDF and let CardiTrack extract and explain the values
- **So that** I understand what the results mean without waiting for a GP appointment
- **Screen:** W3-06
- **MVP:** 3
- **Acceptance Criteria:**
  - Large drag-and-drop upload zone — supports PDF, JPG, PNG up to 10 MB
  - Camera capture available on mobile/tablet as an alternative to file upload
  - Processing steps (OCR → identify values → cross-reference standards) shown as animated progress
  - Partial OCR reads are handled gracefully: "We got most of it — you can fix the rest"
  - Multi-page documents supported (add more pages after first)
  - Cancel button available during processing

**Story 7.2: Reviewing and Correcting Parsed Results**
- **As a** caregiver reviewing an uploaded lab report
- **I want to** correct any OCR errors and see CardiTrack's plain-language interpretation
- **So that** I have accurate data and understand what the numbers mean
- **Screen:** W3-07
- **MVP:** 3
- **Acceptance Criteria:**
  - Parsed results table shows test name, value, reference range, and status (High / Normal / Low) per row
  - Each value row has an edit icon — clicking opens an inline editable field
  - "Mark as Verified" button once corrections are done
  - CardiTrack Insights panel (right column on desktop) explains each out-of-range value in plain language
  - Medical disclaimer is clearly visible above insights
  - "Learn More" links open relevant health information (new tab)
  - Trend comparison shown if previous test results exist for this CardiMember
  - Export to LOINC/CCD formats available via W2-05

---

### Settings & Account

**Story 6.1: Subscription Management**
- **As a** paying customer
- **I want to** understand my current plan, usage, and upgrade options clearly
- **So that** I can make an informed decision about my subscription
- **Screen:** W2-02
- **MVP:** 2
- **Acceptance Criteria:**
  - Current plan prominently displayed with renewal date
  - Usage progress bars (CardiMembers used / available, data retention days)
  - Side-by-side plan comparison (Basic vs Complete Care)
  - "Upgrade" / "Downgrade" buttons clearly labelled — "Current Plan" shown as disabled on active tier
  - Annual billing banner shows exact savings amount (not just "15% off")
  - Payment method shown with last 4 digits; "Change" and "Billing History" easily accessible
  - _Note: Guardian Plus business tier is out of scope for MVP_

**Story 6.2: Device Management**
- **As a** caregiver whose parent got a new smartwatch
- **I want to** disconnect the old device and connect the new one easily
- **So that** health data keeps flowing without any gap
- **Screen:** W1-14
- **MVP:** 1
- **Acceptance Criteria:**
  - Devices listed grouped by CardiMember with status badges (Active / Token Expiring / Disconnected)
  - Expanding a device row shows last sync time, next sync time, and battery (if available)
  - Hover on a row reveals: Refresh Connection / Set as Primary / Remove (destructive)
  - "Remove Device" shows a confirmation: "Historical data is kept — only the connection is removed"
  - "+ Add Device" button at top of page navigates to W1-04 flow
  - Troubleshooting section at bottom covers common issues

**Story 6.3: Health Data Export**
- **As a** caregiver preparing for a doctor's appointment
- **I want to** export my parent's health data in the format the doctor's system accepts
- **So that** the GP can review the data without manual transcription
- **Screen:** W2-05
- **MVP:** 2
- **Acceptance Criteria:**
  - Format options: PDF Report / CSV / HL7 v2 / FHIR (R4) — with plain-language description of each
  - Format info icon (expandable) explains use case and typical recipient for clinical formats
  - Date range picker with quick presets (Last 7 Days / 30 Days / 90 Days / All Data)
  - Data type checkboxes: Activity / Heart Rate / Sleep / Alerts / Notes
  - Delivery options: Download to computer / Email to address / Print (MVP 4)
  - "Preview Export" generates a sample before committing to the full export
  - Progress bar shown while export generates; cancel option available

---

## 🌐 Web Platform-Specific Stories

### Real-Time & Connectivity

**Story 9.1: SignalR Real-Time Dashboard Updates**
- **As a** caregiver with the CardiTrack tab open while working
- **I want to** see alerts and data updates appear automatically without refreshing
- **So that** I never miss an alert because I forgot to reload
- **Screen:** W1-08, W1-09
- **MVP:** 1
- **Acceptance Criteria:**
  - SignalR hub connected on authenticated page load
  - New alerts trigger: alert badge count increments, recent alerts section updates, toast notification appears
  - Dashboard metrics update in real-time when device syncs
  - Connection state change (disconnect / reconnect) is surfaced unobtrusively
  - No duplicate alerts shown if the same event fires multiple times

**Story 9.2: Browser Notification Permission**
- **As a** caregiver who doesn't always have CardiTrack as the active tab
- **I want to** grant browser notification permission so critical alerts reach me regardless
- **So that** I'm not reliant on checking the tab for time-sensitive situations
- **Screen:** W4-02
- **MVP:** 4
- **Acceptance Criteria:**
  - Permission request is presented as an in-app card — not a bare browser dialog cold-shown on first load
  - Card explains the benefit clearly before triggering the browser dialog
  - If permission denied: guidance on how to re-enable in browser settings is provided
  - If granted: "Send Test Notification" button lets user verify it works immediately
  - Setting is reflected in W2-04 Notification Preferences

**Story 9.3: Rich Browser Notifications**
- **As a** caregiver with browser notifications enabled
- **I want to** act on critical alerts directly from the browser notification
- **So that** I don't need to switch tabs to acknowledge or call
- **Screen:** W4-05
- **MVP:** 4
- **Acceptance Criteria:**
  - Standard notification: app icon + CardiMember name + alert summary + timestamp
  - Critical alert (expanded, on long press or expand button): full alert body + action buttons (Call, Acknowledge, View)
  - Clicking "View" deep-links directly to the correct alert detail page
  - Clicking "Acknowledge" acknowledges the alert without opening the app
  - Notifications are grouped by CardiMember in the browser notification center
  - In-app banner appears when the tab is active — does not stack with browser notification

### Keyboard & Power Users

**Story 10.1: Keyboard Navigation Throughout**
- **As a** caregiver who prefers keeping hands on the keyboard
- **I want to** navigate CardiTrack entirely without a mouse
- **So that** I can check on my parent faster during a workday
- **Screens:** All
- **MVP:** 1
- **Acceptance Criteria:**
  - All interactive elements are reachable via Tab key
  - Focus indicator is clearly visible on every focused element (WCAG AA minimum)
  - Dropdown menus close on Escape; modals close on Escape
  - Forms submit on Enter where appropriate
  - No keyboard traps anywhere in the application

**Story 10.2: Command Palette & Quick Search**
- **As a** power user monitoring multiple CardiMembers
- **I want to** quickly navigate to any member, alert, or section using a keyboard shortcut
- **So that** I don't have to click through multiple levels of navigation
- **Screen:** All (⌘K / Ctrl+K trigger)
- **MVP:** 4
- **Acceptance Criteria:**
  - `⌘K` / `Ctrl+K` opens command palette from any page
  - Palette searches: CardiMember names, alert titles, settings pages, screen names
  - Results appear within 200ms as user types (fuzzy match)
  - Arrow keys navigate results; Enter navigates to selection; Escape dismisses
  - Most recently visited items shown as default (before typing)

**Story 10.3: Alert List Keyboard Shortcuts**
- **As a** caregiver reviewing the daily alert list
- **I want to** use keyboard shortcuts to triage alerts quickly
- **So that** I can process 5 alerts in under a minute without touching the mouse
- **Screen:** W1-09
- **MVP:** 4
- **Acceptance Criteria:**
  - `J` / `K` moves focus to next / previous alert row and loads detail in the right panel
  - `E` acknowledges the currently selected alert
  - `O` opens the full-page alert detail
  - `C` triggers a phone call to the alert's CardiMember
  - Keyboard shortcut hints visible in the UI (tooltip on hover for each action button)
  - `?` opens W4-06 Keyboard Shortcuts Reference from any page

**Story 10.4: Keyboard Shortcuts Reference**
- **As a** new CardiTrack user who wants to use the app efficiently
- **I want to** discover all keyboard shortcuts in one place
- **So that** I can learn the power user workflow progressively
- **Screen:** W4-06
- **MVP:** 4
- **Acceptance Criteria:**
  - `?` opens the shortcuts reference modal from any authenticated page
  - Modal lists shortcuts in groups: Navigation / Alerts / General
  - Shortcuts shown as styled keyboard badges (e.g., `G` `D`)
  - Modal closes on Escape
  - Link to this modal visible in the footer and in W2-01 Settings

### Offline & PWA

**Story 10.5: Offline Mode**
- **As a** caregiver with unreliable internet (travelling, poor Wi-Fi)
- **I want to** still see my parent's last-known health data when offline
- **So that** I'm not left with a blank screen at a worrying moment
- **Screen:** W4-03
- **MVP:** 4
- **Acceptance Criteria:**
  - Persistent offline banner at the top of every page when connection is lost
  - Dashboard, alerts list, and CardiMember profiles show cached data with last-updated timestamp
  - Refresh/sync buttons are greyed out with a tooltip: "Not available offline"
  - Any actions taken offline (acknowledge alert, add note) are queued and shown with "Pending sync" badge
  - On reconnect: "Back online" toast appears; queued actions sync automatically; success confirmation shown
  - Offline cache stores at least 7 days of data by default

**Story 10.6: PWA Installation**
- **As a** caregiver who checks CardiTrack multiple times per day
- **I want to** install it to my device like a native app
- **So that** I can open it faster without going through the browser each time
- **Screen:** W4-01
- **MVP:** 4
- **Acceptance Criteria:**
  - Install prompt appears as a non-intrusive sticky banner (bottom of page) — not a blocking popup
  - Banner includes app icon, name, and one-line benefit: "Add to home screen — works offline too"
  - "Install" button triggers the native browser install dialog
  - "Not now" dismisses the banner for the session without penalising the user
  - Post-install: toast confirms installation and points user to their home screen
  - Installed app opens in standalone mode (no browser chrome)

### Print & Export

**Story 9.3: Print-Optimised Health Report**
- **As a** caregiver going to a doctor's appointment
- **I want to** print a clean, readable health summary for the GP
- **So that** the doctor can review the last 30 days quickly without needing to log in
- **Screen:** W4-07
- **MVP:** 4
- **Acceptance Criteria:**
  - Print view removes all navigation chrome (sidebar, top bar, browser UI)
  - Report includes: CardiMember name, date range, key stats table, trend charts (rendered as static images), alerts summary, notes (optional)
  - "Customize Report" dropdown lets user include/exclude sections before printing
  - "Print / Save as PDF" button triggers `window.print()` — no server-side rendering required
  - HIPAA-appropriate disclaimer footer on every printed page: "Confidential Health Information — for authorised recipients only"
  - Print preview renders correctly on standard A4 and US Letter paper sizes

---

## 👵 Secondary Persona: Elderly CardiMember (Ages 70-85)

The elderly CardiMember may occasionally access CardiTrack on a web browser — typically via a link sent by a family member. The experience must be accessible, low-friction, and non-alarming.

**Story 7.3: Understanding What Is Being Shared**
- **As an** elderly person whose family set up CardiTrack monitoring
- **I want to** read a clear explanation of what data my family can see
- **So that** I can give genuine, informed consent to being monitored
- **Acceptance Criteria:**
  - Consent screen uses large font (minimum 16px body), plain language, short paragraphs
  - "What your family will see" with concrete examples
  - "What they won't see" section addresses common dignity concerns
  - Option to restrict specific data types (e.g., share activity but not sleep)
  - Short explainer video or animated walkthrough available

**Story 7.4: Viewing My Own Health Data**
- **As an** elderly CardiMember who wants to feel included
- **I want to** see my own health dashboard if I choose to
- **So that** I know what my family sees and can add my own context
- **Acceptance Criteria:**
  - CardiMember can optionally log in to a simplified view (not the full caregiver dashboard)
  - Larger font sizes and simpler layout than the caregiver view
  - "Your family was notified about..." transparency section
  - Text note field: "Add a note for your family" (e.g., "I was sick this week")
  - No ability to modify settings or alert preferences from this view

---

## 🏥 Tertiary Persona: Care Facility Staff (Business Account)

Business account features are post-MVP but user stories are defined here for planning purposes.

**Story 8.1: Multi-Resident Overview**
- **As a** healthcare director at an assisted living facility
- **I want to** monitor all residents from a single dashboard
- **So that** I can allocate staff attention to those who need it most
- **Acceptance Criteria:**
  - Table/list view: Name, Room, Status, Last Alert, Assigned Staff
  - Sortable columns; filter by floor, wing, care level, or status
  - Bulk acknowledge for all "Normal" status residents
  - Search by name or room number
  - Export resident health summary in CSV for compliance reporting

**Story 8.2: Staff Assignment & Shift Handoffs**
- **As a** facility manager doing a shift handover
- **I want to** log handoff notes and flag residents needing attention for the incoming shift
- **So that** continuity of care is maintained and nothing is missed
- **Acceptance Criteria:**
  - Staff assignment panel per resident
  - Shift notes section visible to all staff
  - Outstanding unacknowledged alerts highlighted for incoming shift
  - Staff activity log (who checked on whom, and when)

**Story 8.3: Family Portal for Facility Residents**
- **As a** facility administrator
- **I want to** grant families view-only access to their loved one's monitoring data
- **So that** families feel informed and we reduce "how is my mum" phone calls
- **Acceptance Criteria:**
  - Shareable family portal link per resident (no account required for family to view)
  - View-only: family cannot modify settings or acknowledge alerts
  - Shows only that resident's data — no access to other residents
  - Opt-in consent from resident or power of attorney required before link is issued

---

## 🎨 Web UI/UX Design Principles

### Principle 1: Desktop-First, Mobile-Ready
**Context:** Primary web users check in from a laptop during the workday
- Sidebar + top bar navigation optimised for large screens
- Responsive breakpoints (1024px / 768px / 375px) ensure mobile web is usable
- 2-column split layouts (list + detail) make efficient use of screen space on desktop
- Single-column stacked layouts on mobile — no horizontal scrolling

### Principle 2: Speed Above All
**Context:** Caregivers open the tab briefly during a busy day — every extra click is a barrier
- Dashboard is the default landing page after login — no intermediate screens
- Most common actions (acknowledge, call, view detail) available with one click or one keyboard shortcut
- Command palette (`⌘K`) enables instant navigation to any page
- SignalR eliminates the need to manually refresh

### Principle 3: Trust Through Clarity
**Context:** Health data is sensitive — users need to feel in control
- Data provenance shown on every metric (which device, when synced)
- Plain language on all alerts — no medical jargon, no ambiguous numbers without context
- Acknowledge + note flow creates a shared audit trail that builds family trust
- Privacy messaging on any encrypted or sensitive field

### Principle 4: Calm When Calm, Urgent When Urgent
**Context:** 80% of sessions should feel reassuring; 20% require immediate action
- Green / Normal state is visually dominant — makes calm status unmissable
- Critical states use animation (pulsing border) to demand attention appropriately
- Caution and Urgent states are clearly differentiated from each other
- Alerts provide context and suggested actions — not just warnings

### Principle 5: Accessibility Is Non-Negotiable
**Context:** Users range from 30–85 years old; some sessions under stress
- WCAG AA minimum contrast (4.5:1 text, 3:1 large text)
- Full keyboard navigation across every page
- Focus indicators clearly visible — never hidden or too subtle
- Status never conveyed by colour alone — always paired with icon and text
- Screen reader labels on all interactive elements and data visualisations

---

## 📱 Platform-Specific Web Stories vs Mobile

| Category | Web (Blazor) | Mobile (MAUI) |
|----------|-------------|---------------|
| Notifications | Browser Push (W4-05) + in-app SignalR banner | Native Push + lock screen |
| Offline | PWA Service Worker + sync queue (W4-03) | SQLite cache + sync queue |
| Install | PWA manifest (W4-01) | App Store / Play Store |
| Navigation | Sidebar + top bar + keyboard shortcuts | Bottom tab bar + flyout |
| Alert triage | Split-pane list + keyboard J/K/E | Swipe left/right on rows |
| Data entry | Desktop keyboard + drag-and-drop upload | Camera OCR + native pickers |
| Export | Download / Email / Print (W4-07) | Share sheet / Save to Files |
| Quick glance | Browser tab badge count | Home screen widget (M4-06) |
| Login security | 2FA (settings toggle) | Biometric (Face ID / fingerprint) |

---

## 🎯 Priority Matrix for Web MVP

### Must Have — MVP 1 (P0)
- [x] Story 1.1: Landing page & value discovery
- [x] Story 1.2: Account creation
- [x] Story 1.3: Adding first CardiMember
- [x] Story 1.4: Device connection wizard
- [x] Story 2.1: Daily status check (desktop)
- [x] Story 2.2: Real-time SignalR updates
- [x] Story 3.1: Understanding a critical alert
- [x] Story 3.2: Split-pane alert browsing
- [x] Story 3.4: Acknowledging an alert with notes
- [x] Story 6.2: Device management
- [x] Story 10.1: Keyboard navigation throughout

### Should Have — MVP 2 (P1)
- [ ] Story 2.4: Trend charts & historical data
- [ ] Story 3.5: Alert notification preferences
- [ ] Story 6.1: Subscription management
- [ ] Story 6.3: Health data export

### Should Have — MVP 3 (P2)
- [ ] Story 2.3: Multi-member overview
- [ ] Story 4.1: Inviting a sibling
- [ ] Story 4.2: Managing family roles
- [ ] Story 4.3: Sharing family notes
- [ ] Story 7.1: Uploading lab test results
- [ ] Story 7.2: Reviewing parsed results

### Nice to Have — MVP 4 (P3)
- [ ] Story 9.2: Browser notification permission
- [ ] Story 9.3 (duplicate ref): Rich browser notifications
- [ ] Story 9.3: Print-optimised health report
- [ ] Story 10.2: Command palette & quick search
- [ ] Story 10.3: Alert list keyboard shortcuts
- [ ] Story 10.4: Keyboard shortcuts reference
- [ ] Story 10.5: Offline mode
- [ ] Story 10.6: PWA installation

### Post-MVP (Future)
- [ ] Story 7.3: Elderly CardiMember consent screen
- [ ] Story 7.4: CardiMember self-view
- [ ] Story 8.1–8.3: Business account / care facility features

---

## 📊 User Story Summary by Category

| Category | Total Stories | MVP 1 (P0) | MVP 2 (P1) | MVP 3 (P2) | MVP 4 (P3) | Future |
|----------|---------------|-----------|-----------|-----------|-----------|--------|
| Onboarding & Setup | 5 | 5 | 0 | 0 | 0 | 0 |
| Dashboard & Monitoring | 4 | 2 | 1 | 1 | 0 | 0 |
| Alert Management | 5 | 3 | 1 | 0 | 1 | 0 |
| Family Collaboration | 3 | 0 | 0 | 3 | 0 | 0 |
| Health Records | 2 | 0 | 0 | 2 | 0 | 0 |
| Settings & Account | 3 | 1 | 2 | 0 | 0 | 0 |
| Real-Time & Connectivity | 2 | 1 | 0 | 0 | 1 | 0 |
| Keyboard & Power Users | 4 | 1 | 0 | 0 | 3 | 0 |
| Offline & PWA | 2 | 0 | 0 | 0 | 2 | 0 |
| Print & Export | 1 | 0 | 0 | 0 | 1 | 0 |
| Elderly CardiMember | 2 | 0 | 0 | 0 | 0 | 2 |
| Enterprise (Business) | 3 | 0 | 0 | 0 | 0 | 3 |
| **TOTAL** | **36** | **13** | **4** | **6** | **8** | **5** |

---

## 🔗 Related Documentation

- [Web Screen Specifications](./ui_screens_blazor_web.md) — Screen-by-screen layout, states, and interactions
- [Mobile User Stories](../MOBILE/USER_STORIES.md) — Mobile-specific stories and shared design principles
- [Mobile Screen Specifications](../mobile/ui_screens_maui_mobile.md) — MAUI mobile screen specs
- [Solution Manifest](../../../SOLUTION_MANIFEST.md) — Technical architecture and business model
- [Market Analysis](../../../MARKET_ANALYSIS.md) — Competitive landscape and positioning

---

**Document Version:** 1.0
**Last Updated:** February 24, 2026
**Next Review:** Post-MVP 1 beta feedback
**Owner:** Product & UX Team

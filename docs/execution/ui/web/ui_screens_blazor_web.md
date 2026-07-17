# CardiTrack - Web App Screen Specifications

## Project Overview

**Product:** CardiTrack - Remote health monitoring for elderly family members
**Platform:** Blazor Server (.NET 10) — Chrome, Firefox, Safari, Edge (latest 2 versions)
**Responsive Breakpoints:** Mobile (< 768px), Tablet (768–1024px), Desktop (> 1024px)
**Target Users:** Family caregivers across the US & EU monitoring elderly relatives' wearable health data
**Document Version:** 3.0
**Last Updated:** February 24, 2026

---

## Release Strategy

68 screens across 4 MVPs (counting each state as a screen). Each MVP is a fully functional, shippable release.

| Release | Screens | Theme | User Gets |
|---------|---------|-------|-----------|
| **MVP 1** | 33 | Core Monitoring | Public landing page, sign up, connect and manage device (Fitbit), monitor one to n parent(s), CardiMember profile, device management, view dashboard, receive and manage all alert types |
| **MVP 2** | 8 | Management & Settings | Trend charts, notification preferences, personal subscription (Basic & Complete Care), health data export (HL7, FHIR), connect and manage device (Garmin) |
| **MVP 3** | 14 | Family & Multi-Member | Invite family, share notes, manage multiple CardiMembers, scan test results with CardiTrack medical insights, export data in LOINC/CCD |
| **MVP 4** | 13 | Web Native & Offline | PWA install, browser notifications with inline actions, offline support with sync queue, keyboard shortcuts, print/PDF export view, SNOMED CT export |

---

## Screen Index

| ID | Screen | Release | Variations |
|----|--------|---------|------------|
| W1-01 | Landing Page (Public) | MVP 1 | 2 (a–b) |
| W1-02 | Sign Up | MVP 1 | 4 (a–d) |
| W1-03 | Add First CardiMember | MVP 1 | 3 (a–c) |
| W1-04 | Device Connection - Selection | MVP 1 | 1 |
| W1-05 | Device Connection - OAuth | MVP 1 | 3 (a–c) |
| W1-06 | Device Connection - Success | MVP 1 | 3 (a–c) |
| W1-07 | Baseline Learning Info | MVP 1 | 1 |
| W1-08 | Main Dashboard | MVP 1 | 5 (a–e) |
| W1-09 | Alerts List | MVP 1 | 4 (a–d) |
| W1-10 | Alert Detail - Activity | MVP 1 | 1 |
| W1-11 | Alert Detail - Critical | MVP 1 | 1 |
| W1-12 | CardiMember Detail | MVP 1 | 1 |
| W1-13 | Edit CardiMember | MVP 1 | 1 |
| W1-14 | Device Management | MVP 1 | 1 |
| W1-15 | Alert Detail - Heart Rate | MVP 1 | 1 |
| W2-01 | Settings Main | MVP 2 | 1 |
| W2-02 | Subscription Management | MVP 2 | 1 |
| W2-03 | Trend Charts | MVP 2 | 1 |
| W2-04 | Notification Settings | MVP 2 | 1 |
| W2-05 | Health Data Export | MVP 2 | 4 (a–d) |
| W3-01 | Family Members List | MVP 3 | 1 |
| W3-02 | Invite Family Modal | MVP 3 | 1 |
| W3-03 | Multi-Member Dashboard | MVP 3 | 2 (a–b) |
| W3-04 | Shared Notes Feed | MVP 3 | 1 |
| W3-05 | Add / Edit Note | MVP 3 | 1 |
| W3-06 | Test Results Scanner | MVP 3 | 4 (a–d) |
| W3-07 | Test Results Detail | MVP 3 | 4 (a–d) |
| W4-01 | PWA Install Prompt | MVP 4 | 1 |
| W4-02 | Browser Notification Permission | MVP 4 | 1 |
| W4-03 | Offline Mode Indicator | MVP 4 | 2 (a–b) |
| W4-04 | Offline Data Cache Settings | MVP 4 | 1 |
| W4-05 | Browser Notifications | MVP 4 | 4 (a–d) |
| W4-06 | Keyboard Shortcuts Reference | MVP 4 | 1 |
| W4-07 | Print / PDF Export View | MVP 4 | 3 (a–c) |

---

## User Flows

### Flow 1: First-Time Onboarding (MVP 1)

```
[W1-01 Landing] → [W1-02 Sign Up] → [W1-03 Add CardiMember]
      │                                        │
      │ "Sign In"                              │ "Skip"
      ▼                                        ▼
 (Login page)                           [W1-08 Dashboard]
                                         (empty state)

           [W1-03 Add CardiMember]
                    │
                    ▼
           [W1-04 Device Selection]
                    │
                    ▼
           [W1-05 OAuth Permission]
                    │
               ┌────┴────┐
               │ Success │ Failure
               ▼         ▼
           [W1-06 Success] → Retry / Help
               │
               ▼
           [W1-07 Baseline Info]
               │
               ▼
           [W1-08 Dashboard]
```

### Flow 2: Daily Monitoring (MVP 1)

```
[Browser tab] → [W1-08 Dashboard]
                      │
            ┌─────────┼───────────┐
            ▼         ▼           ▼
       [Call/SMS] [W1-09 Alerts] [W2-03 Trends]
                      │
                 ┌────┴────┐
                 ▼         ▼
            [W1-10]    [W1-11]
            Activity   Critical
                │         │
                ▼         ▼
          [Acknowledge] [Call Now]
```

### Flow 3: Settings & Management (MVP 2)

```
[Sidebar: Settings] → [W2-01 Settings Main]
                              │
                ┌─────────────┼──────────────┬──────────┐
                ▼             ▼              ▼          ▼
         [W2-02 Sub]  [W2-04 Notif]  [W1-12 Detail] [W1-14 Devices]
                                          │
                                          ▼
                                     [W1-13 Edit]
```

### Flow 3b: Data Export (MVP 2)

```
[W2-03 Trends] → Export button → [W2-05 Health Data Export]
[W2-01 Settings] → "Export Health Data" → [W2-05 Health Data Export]
[W1-12 Detail] → "Export Data" → [W2-05 Health Data Export]
                                        │
                                   ┌────┴────┐
                                   ▼         ▼
                             [Download]   [Email]
```

### Flow 4: Family Collaboration (MVP 3)

```
[Sidebar: Family] → [W3-01 Family List]
                           │
                     ┌─────┴─────┐
                     ▼           ▼
              [W3-02 Invite] [W3-04 Notes]
                                 │
                                 ▼
                            [W3-05 Add Note]
```

### Flow 5: Multi-Member (MVP 3)

```
[W3-03 Multi-Dashboard] → click member → [W1-08 Single Dashboard]
```

### Flow 6: Test Results (MVP 3)

```
[W3-06 Scanner] → Upload/Camera → OCR Processing
                                        │
                                        ▼
                                  [W3-07 Results Detail]
                                        │
                              ┌─────────┼──────────┐
                              ▼         ▼          ▼
                     [CardiTrack Insights] [Export] [Share]
                                        │
                                        ▼
                                  [W2-05 Export]
```

---

## Navigation Structure

### Application Shell (authenticated pages)

```
┌─────────────────────────────────────────────────────────┐
│  [CardiTrack Logo]  [Search ⌘K]  [🔔 3]  [User Avatar ▼] │  ← Top Bar (fixed)
├──────────┬──────────────────────────────────────────────┤
│          │                                              │
│  Dashboard│           Main Content Area                 │
│  Alerts  │           (Blazor Page Content)              │
│  Family  │                                              │
│  Settings│                                              │
│          │                                              │
│ ─────── │                                              │
│  Sign Out│                                              │
└──────────┴──────────────────────────────────────────────┘
  Sidebar    Content (1024px+ always visible; tablet/mobile collapsible)
```

### Sidebar Navigation

- **Logo** (top) — links to Dashboard
- **Dashboard** — W1-08 (badge: member count if >1)
- **Alerts** (badge: unread count) — W1-09
- **CardiMembers** — W1-12 (if single member) / W3-03 (if multiple)
- **Family & Sharing** (MVP 3) — W3-01
- **Settings** — W2-01
- **Help & Support**
- **Sign Out** (bottom, separated)

### Top Bar

- **CardiTrack logo** (left)
- **Search bar** (`⌘K` / `Ctrl+K`) — global search across members, alerts, notes
- **Notifications bell** (badge count) — dropdown with latest 5 alerts
- **User avatar** (right) — dropdown: My Profile, Subscription, Sign Out

### Responsive Behavior

| Breakpoint | Sidebar | Layout |
|------------|---------|--------|
| Desktop (> 1024px) | Always visible, 240px wide | Multi-column |
| Tablet (768–1024px) | Collapsible (hamburger), 240px on open | 2-column |
| Mobile (< 768px) | Hidden (hamburger), full-width overlay | Single column |

### Keyboard Shortcuts (MVP 4 — available everywhere)

| Shortcut | Action |
|----------|--------|
| `⌘K` / `Ctrl+K` | Open command palette / search |
| `G D` | Go to Dashboard |
| `G A` | Go to Alerts |
| `G S` | Go to Settings |
| `?` | Show keyboard shortcut reference (W4-06) |

---

## MVP 1 — Core Monitoring (33 screens)

A user can visit the public landing page, sign up, add one or more CardiMembers, connect devices, manage CardiMember profiles, view the health dashboard, and receive and manage all alert types. This is the essential monitoring loop — everything needed for the web app to be useful from day one.

---

### W1-01: Landing Page (Public)
**URL:** `/`
**User Story:** 1.1 First-Time Registration
**Entry:** Direct URL / organic / referral
**Exit:** → W1-02 Sign Up ("Start Free Trial") | → Login page ("Sign In") | → `/pricing`

**Header (sticky on scroll):**
- CardiTrack logo (left)
- Nav links: "How It Works" / "Pricing" / "Sign In"
- CTA button: "Start Free 30-Day Trial"

**Hero Section (full viewport height, 2-column):**
- Left (50%):
  - Headline: "Know They're Okay"
  - Subheadline: "Monitor your elderly loved one's health from anywhere, using the device they already wear"
  - Value proof: ✓ Works with Fitbit, Apple Watch, Garmin · ✓ AI-powered health alerts · ✓ $8/month — 70% less than medical alert systems
  - Primary CTA: "Start Free 30-Day Trial" (large, brand color)
  - Trust line: "No credit card required. Cancel anytime."
- Right (50%):
  - Hero illustration: elderly parent with smartwatch + caring family member on laptop

**How It Works (3-column):**
1. Connect — "We link to their wearable in minutes"
2. Learn — "CardiTrack learns their normal patterns over 30 days"
3. Alert — "You get notified the moment something looks off"

**Social Proof:**
- Quote: "CardiTrack gave me peace of mind knowing my mum is safe, even from 500 miles away." — Sarah J., Family Caregiver
- Testimonials carousel (3 quotes)

**Pricing Preview (3 cards):**

| Basic | Complete Care | Guardian Plus |
|-------|--------------|---------------|
| $8/mo | $15/mo ⭐ Popular | $30/mo |
- CTA under each: "Start Free Trial"

**Footer:**
- Links: About / Privacy / Terms / Contact / Blog
- © 2026 CardiTrack

**States:**
- **W1-01a — Default:** Header transparent, hero visible
- **W1-01b — Scrolled:** Header turns solid/opaque (sticky), shadow appears

---

### W1-02: Sign Up
**URL:** `/signup`
**User Story:** 1.1 Account Creation
**Entry:** ← W1-01 Landing ("Start Free Trial") | any CTA on landing page
**Exit:** → W1-03 Add CardiMember (success) | → Login page ("Already have an account?")

**Layout:** Centered card (max-width 480px), full-page background

**Card Contents:**

**Logo + Heading:**
- CardiTrack logo (centered)
- Title: "Create Your Account"
- Subtitle: "Start your free 30-day trial"

**Email & Password Section:**
- Label: "Email Address" — text input, autofocus
- Label: "Password" — password input
  - Inline strength bar: Weak → Medium → Strong
- Label: "Confirm Password" — password input

**Divider:** `────── OR ──────`

**Social Login:**
- "Continue with Google" (Google brand button)
- "Continue with Apple" (Apple brand button)

**Terms:**
- Checkbox: "I agree to Terms of Service and Privacy Policy" (links open in new tab)

**CTA:**
- Primary button: "Create Account" (full width, disabled until valid)
- Error banner (hidden until needed)

**Bottom link:** "Already have an account? Sign In"

**Validation Rules:**
- Email: valid format, real-time feedback
- Password: min 8 chars, 1 uppercase, 1 number
- Confirm password: must match
- Terms checkbox: must be checked

**States:**
- **W1-02a — Default:** Empty form
- **W1-02b — Validating:** Inline error messages beneath invalid fields
- **W1-02c — Loading:** Button shows spinner, form disabled
- **W1-02d — Error:** Red error banner at top (e.g., "An account with this email already exists")

---

### W1-03: Add First CardiMember
**URL:** `/onboarding/add-member`
**User Story:** 1.2 Adding First CardiMember
**Entry:** ← W1-02 Sign Up (success)
**Exit:** → W1-04 Device Selection ("Continue") | → W1-08 Dashboard ("Skip for Now")

**Layout:** Centered card (max-width 600px), progress bar at top

**Header:**
- Progress bar: Step 1 of 4
- Title: "Add CardiMember"

**Introduction:**
- Icon: person silhouette
- Text: "Who would you like to look after?"
- Subtext: "Tell us about your loved one — we'll take it from there"

**Photo Section:**
- Circular drag-and-drop upload area (or click to browse)
- "Add Photo" label
- Accepted formats: JPG, PNG, WEBP

**Required Fields:**
- "Full Name *" — text input
- "Date of Birth *" — date picker
- "Relationship *" — select dropdown: Parent / Grandparent / Spouse / Sibling / Other

**Optional Fields (collapsible accordion):**
- "Add More Details (Optional)" toggle
- When expanded:
  - "Medical Notes" — textarea (max 500 chars), encrypted indicator (🔒)
  - "Emergency Contact Name" — text input
  - "Emergency Contact Phone" — tel input

**Privacy Notice:**
- Info card with lock icon
- Text: "[Name] will know you're looking out for them and can give their okay"

**CTA:**
- Primary button: "Continue"
- Text link: "Skip for Now"

**States:**
- **W1-03a — Default:** Empty form
- **W1-03b — Photo added:** Shows uploaded image in circle
- **W1-03c — Loading:** Button shows spinner on submit

---

### W1-04: Device Connection - Selection
**URL:** `/onboarding/connect-device`
**User Story:** 1.3 Device Connection Wizard
**Entry:** ← W1-03 Add CardiMember ("Continue")
**Exit:** → W1-05 OAuth Permission (device selected)

**Layout:** Centered card (max-width 720px), progress bar at top

**Header:**
- Progress bar: Step 2 of 4
- Title: "Connect Device"

**Introduction:**
- Text: "What does [Name] wear?"
- Subtext: "We'll connect with their device to keep you in the loop"

**Device Grid (3 columns on desktop, 2 on tablet, 1 on mobile):**

Each device card:
- Rounded card with hover shadow
- Device logo (medium)
- Device name (bold)
- Badge: "Supported" (checkmark) for active devices; "Coming Soon" (clock) for future devices
- Future device cards are visible but non-clickable (greyed out, cursor not-allowed)
- Entire active card is clickable, highlights on hover/selection

**Supported Devices (MVP 1 — Fitbit only; remaining devices shown as Coming Soon):**

| Device | Models | MVP Availability |
|--------|--------|-----------------|
| Fitbit | Charge, Versa, Sense series | **MVP 1** |
| Garmin | Venu, Forerunner, etc. | MVP 2 |
| Apple Watch | Series 4+ | Coming Soon |
| Samsung Galaxy Watch | All models | Coming Soon |
| Withings | ScanWatch, Move | Coming Soon |
| Other / Manual Entry | Limited features | Coming Soon |

**Bottom:**
- Link: "Don't see their device? We can help"

**Interactions:**
- Single selection — click to select, click again to deselect
- Only Fitbit cards are selectable in MVP 1; others are greyed out with "Coming Soon" badge
- Selected card shows highlighted border + checkmark overlay
- Keyboard: arrow keys navigate between cards, Enter selects
- Selecting a device automatically proceeds to W1-05

---

### W1-05: Device Connection - OAuth Permission
**URL:** `/onboarding/connect-device/authorize`
**User Story:** 1.3 OAuth Flow
**Entry:** ← W1-04 Device Selection (device chosen)
**Exit:** → W1-06 Success (authorization complete) | ← W1-04 Device Selection ("Cancel")

**Layout:** Centered card (max-width 560px)

**Visual Connection (centered):**
- Large device logo
- Animated arrow/connection icon
- Large CardiTrack logo

**Permission List:**
- Label: "To look after [Name], CardiTrack needs:"
- Each permission in its own row with info tooltip on hover:

| Icon | Permission | Tooltip |
|------|-----------|---------|
| Heart | Heart Rate Data | "So we can spot if something's off" |
| Shoe | Activity & Steps | "To make sure they're staying active" |
| Moon | Sleep Data | "To know they're resting well" |

**Privacy Notice:**
- Card with light background
- Lock icon + text: "Your family's health data stays private — always. We never sell or share it."

**CTA:**
- Primary button: "Authorize [Device Name]" — opens device OAuth in a new browser tab/popup
- Text link: "Cancel"

**States:**
- **W1-05a — Default:** Permission list visible
- **W1-05b — Authorizing:** Loading overlay with "Connecting to [Name]'s [Device]..." message
- **W1-05c — Error:** "We couldn't connect — let's try that again" with retry button

---

### W1-06: Device Connection - Success
**URL:** `/onboarding/connect-device/success`
**User Story:** 1.3 Connection Success
**Entry:** ← W1-05 OAuth (authorization complete)
**Exit:** → W1-07 Baseline Info ("Continue to Dashboard") | → W1-04 Device Selection ("Add Another Device")

**Layout:** Centered card (max-width 560px)

**Animation:**
- Animated checkmark (CSS or Lottie, plays once on load)

**Success Message:**
- Heading: "You're all set!"
- Text: "[Name]'s [Device] is now connected"
- Subtext: "We're pulling in their latest data — just a moment"

**Data Preview Card:**
- Title: "Latest Data"
- Rows: Steps Today: 4,250 | Last Synced: Just now | Heart Rate: 72 bpm

**Options:**
- Outlined button: "+ Add Another Device"
- Helper text: "Does [Name] have another device? The more data, the better we can watch over them"

**CTA:**
- Primary button: "Continue to Dashboard"

**States:**
- **W1-06a — Syncing:** Preview card shows skeleton loading
- **W1-06b — Synced:** Preview card shows real data
- **W1-06c — Partial sync:** Some values shown, others show "Syncing..."

---

### W1-07: Baseline Learning Info
**URL:** `/onboarding/baseline`
**User Story:** 1.3 Baseline Setup
**Entry:** ← W1-06 Device Success
**Exit:** → W1-08 Dashboard ("Go to Dashboard")

**Layout:** Centered card (max-width 560px)

**Header:**
- Progress bar: Step 4 of 4
- Title: "Learning Phase"

**Illustration:**
- Brain with gears concept (learning/AI)

**Explanation:**
- Heading: "Getting to Know [Name]"
- Body: "Over the next 30 days, CardiTrack will learn what a normal day looks like for [Name]:"
- Bullet list:
  - "When they usually wake up and go to sleep"
  - "How active they are day to day"
  - "What their resting heart rate looks like"

**Progress:**
- Progress bar: "Day 1 of 30" / "3% Complete"

**Options Card:**
- Toggle switch: "Keep me posted while CardiTrack is learning"
- Description: "You'll get basic alerts right away (like heart rate over 100)"

**CTA:**
- Primary button: "Go to Dashboard"

**MVP 3 addition:** Link "Invite Family Members First" → W3-02

---

### W1-08: Main Dashboard (Single CardiMember)
**URL:** `/dashboard`
**User Story:** 2.1 Daily Health Overview
**Entry:** Sidebar (Dashboard) | ← W1-07 Baseline Info (first time) | SignalR real-time push
**Exit:** → W1-09 Alerts List | → W2-03 Trend Charts | → W1-12 CardiMember Detail | → Phone call / SMS

**Layout (2-column on desktop):**

**Left column (primary content):**

**Page Header:**
- Greeting: "Good Morning, [User First Name]"
- Last synced timestamp: "Updated 10 minutes ago" + manual refresh button

**Status Hero Card:**
- Large card with gradient background colored by status
- CardiMember photo (circular) + Name and age: "[Name], 78"
- Large status indicator:

| Status | Label | Icon |
|--------|-------|------|
| Normal | "[Name] is doing well" | Checkmark |
| Caution | "Something looks a little different" | Warning triangle |
| Urgent | "You should check in" | Lightning bolt |
| Critical | "Reach out to [Name] now" | Siren |

- Quick Actions Row:
  - "Call [Name]" → initiates phone call (tel: link)
  - "Send Message" → opens mailto / SMS
  - "View Details" → W1-12

**Key Metrics (3-column grid):**

**Card 1: Activity**
- Icon: shoe | Value: "4,250 steps" | Progress bar (current vs. goal)
- Comparison: "85% of normal" + trend arrow | Mini 7-day sparkline

**Card 2: Heart Rate**
- Icon: heart | Value: "72 bpm" | Status: "Normal range"
- Range text: "68–75 bpm typical" | Mini sparkline

**Card 3: Sleep**
- Icon: moon | Value: "7.2 hours" | Quality: "Good"
- Comparison: "Better than average" | Mini sparkline

**Recent Alerts (conditional):**
- Section heading: "Recent Alerts"
- Horizontal scrollable alert cards
- Each card: icon, title, time, status — tap/click → W1-10 or W1-11

**Button:** "View Trends & History" → W2-03

**Right column (sidebar panel, desktop only):**
- CardiMember photo + status summary (compact)
- Mini alerts feed (last 3 alerts)
- Quick links: View Alerts / View Trends / Manage Devices

**Interactions:**
- Refresh button (top right) triggers data sync
- Hover on metric card → reveals "View Details" tooltip
- SignalR: status and metrics update in real-time without page reload

**States:**
- **W1-08a — Loading:** Skeleton/shimmer cards
- **W1-08b — Normal:** Full data displayed
- **W1-08c — Stale data:** Banner: "Last update was X hours ago — click here to refresh"
- **W1-08d — No device connected:** Prompt card: "Connect [Name]'s device so CardiTrack can start watching over them" → W1-04
- **W1-08e — Baseline learning:** Shows progress bar instead of "% of normal" comparisons

**MVP 3 change:** When user has multiple CardiMembers, Dashboard shows W3-03 instead

---

### W1-09: Alerts List
**URL:** `/alerts`
**User Story:** 3.1 Alert Management
**Entry:** Sidebar (Alerts) | ← W1-08 Dashboard (Recent Alerts section)
**Exit:** → W1-10 Alert Detail (Activity) | → W1-11 Alert Detail (Critical) | → W1-15 Alert Detail (Heart Rate)

**Layout (2-column on desktop):**

**Page Header:**
- Title: "Alerts"
- Filter button + Settings button → W2-04 Notification Settings

**Filter Bar:**
- Filter chips: [All] [Unread] [Critical] [Today] [This Week]
- Sort dropdown: "Newest First" / "Severity"
- Search input (inline): "Search alerts..."

**Alert List (grouped by date, left column):**

Section headers: "Today" / "Yesterday" / "This Week" / "Older"

**Alert Row Layout:**
- Left border colored by severity
- Top row: Severity badge ("CRITICAL" / "URGENT" / "INFO") + timestamp + unread dot
- Content: CardiMember avatar + name inline | Alert title (bold) | Preview text (2 lines)
- Bottom row: Status label ("New" / "Acknowledged" / "Resolved") + action buttons: Acknowledge (✓) | Expand (→)

**Hover actions (desktop):**
- Row highlights on hover
- Action buttons appear on row hover (right-aligned): Call | Acknowledge | View

**Right column (desktop — alert detail panel):**
- Clicking an alert loads the detail in-panel without leaving the list (split-pane)
- Falls back to full-page navigation on tablet/mobile

**Bottom:**
- Link: "View Archived Alerts"

**States:**
- **W1-09a — Default:** Grouped alert list with unread badges
- **W1-09b — Empty:** Bell icon (muted) + "Nothing to worry about" + "CardiTrack is keeping an eye on things — we'll let you know if anything comes up"
- **W1-09c — Filtered empty:** "No alerts match this filter"
- **W1-09d — Loading:** Skeleton rows

Heart rate alerts click → W1-15

---

### W1-10: Alert Detail - Activity
**URL:** `/alerts/{id}`
**User Story:** 11.1 Activity Decline
**Entry:** ← W1-09 Alerts List (click row) | Right-panel in split-pane view
**Exit:** ← W1-09 Alerts List (back / close panel) | → Phone call | → W2-03 Trend Charts

**Layout:** Full-page on tablet/mobile; right panel on desktop split-pane

**Alert Header Card:**
- Caution-level severity banner
- Warning icon
- Title: "Low Activity Alert"
- CardiMember photo + name + Timestamp: "January 10, 2026 at 11:30 AM"

**Description:**
- Large readable card: "Dad hasn't been as active as usual lately"

**Mini Trend Chart:**
- 2-week activity trend with declining line
- Baseline range shaded; current data overlaid
- Interactive: hover to see exact value tooltip

**Comparison Card (2-column grid):**

| Current | Normal |
|---------|--------|
| "Recent Average" | "Normal Average" |
| 2,500 steps/day | 5,000 steps/day |

- Full-width row: "-50% below normal"

**Context Card:**
- Lightbulb icon: "Here's what might be going on:"
  - They could be feeling under the weather
  - They might be in pain or uncomfortable
  - They may be feeling low or tired

**Recommended Actions:**
1. "Give Dad a Call" (primary, phone icon)
2. "Send a Quick Message" (secondary, email icon)
3. "Book a Doctor Visit" (secondary, calendar icon)

**More Options (dropdown/menu):**
- "Adjust Baseline" (if this is a new normal)
- "Add Note About This Alert"
- "Share with Family"

**Acknowledgment Section:**
- If unread: Button "Mark as Acknowledged"
- If acknowledged: "Acknowledged by Sarah, 30 min ago" + any notes

**Bottom:**
- Button: "View Detailed Activity Data" → W2-03

---

### W1-11: Alert Detail - Critical (No Movement)
**URL:** `/alerts/{id}` (critical severity)
**User Story:** 11.3 No Morning Activity
**Entry:** ← W1-09 Alerts List | Browser notification (direct link)
**Exit:** ← W1-09 Alerts List (back) | → Phone call | → Note input

This is the most safety-critical screen. Design for urgency and immediate action.

**Alert Header:**
- Full-width critical severity banner (pulsing animation via CSS)
- Large siren icon
- Title: "We haven't seen Dad move today"
- CardiMember photo + name + Timestamp

**Urgent Message Card (thick border, critical severity):**
- Large text: "Dad hasn't moved this morning"
- Details:
  - "He usually wakes up around 7:00 AM"
  - "It's now 11:00 AM"
  - "That's 4 hours with no movement"

**Last Known Activity Card:**
- "The last time we saw Dad move:"
- "Yesterday at 10:30 PM"
- "Bedroom area (based on his device)"

**Immediate Actions (large, prominent buttons):**
1. **"CALL NOW"** — critical severity, oversized, phone icon, one-click to dial, shows phone number
2. **"I'M ON MY WAY"** — urgent severity, large — updates status and lets family know

**Dismissal Option:**
- Button: "It's okay — he told me he'd sleep in"
  - Opens a text field for context
  - Dismisses alert with explanation logged

**Family Notification Card:**
- "Your family has been notified too:"
  - Sarah (via email) — timestamp
  - John (via push) — timestamp

**Event Timeline:**
- Vertical timeline:
  - 10:30 PM — Last movement
  - 7:00 AM — Expected wake time
  - 9:00 AM — Alert threshold reached
  - 11:30 AM — You were notified

---

### W1-12: CardiMember Detail
**URL:** `/members/{id}`
**Entry:** ← W1-08 Dashboard ("View Details") | ← W2-01 Settings ("Manage CardiMembers") | Sidebar
**Exit:** ← Previous page (browser back) | → W1-13 Edit CardiMember | → W1-08 Dashboard | → W1-09 Alerts

**Layout (2-column on desktop):**

**Left column:**

**Profile Section:**
- Large photo (prominent)
- Name (large heading)
- Age & relationship: "78 years old — Dad"

**Contact Info Card:**
- Emergency contact: name, phone (clickable tel: link), relationship

**Medical Info Card (encrypted):**
- Lock icon in card header
- Collapsible: "Medical Notes"
- Expand requires re-authentication (password confirm prompt)

**Monitoring Info Card:**
- Connected devices: "2 devices"
- Monitoring since: "Jan 1, 2026"
- Baseline status: "Learning (15 days)" or "Established"

**Action Buttons:**
- "View Dashboard" → W1-08
- "View Alerts" → W1-09
- "Manage Devices" → W1-14

**Danger Zone (separated, red border):**
- "Pause Monitoring" (warning style)
- "Remove CardiMember" (destructive style)

**Right column (desktop):**
- Recent alerts summary (last 3)
- Device status cards
- Quick stats: steps today, last heart rate, last sleep

---

### W1-13: Edit CardiMember
**URL:** `/members/{id}/edit`
**Entry:** ← W1-12 CardiMember Detail (Edit button)
**Exit:** ← W1-12 CardiMember Detail (cancel or save)

**Page Header:**
- Title: "Edit [Name]"
- "Cancel" button (top right) | "Save Changes" button (primary, top right, enabled when changes exist)

**Form (scrollable):**

**Photo:** Large circular image + "Change Photo" button (drag-and-drop or browse)

**Basic Info:**
- "Full Name" — text input
- "Date of Birth" — date picker
- "Relationship" — select dropdown

**Optional Info:**
- "Medical Notes" — textarea (encrypted)
- "Emergency Contact Name" — text input
- "Emergency Contact Phone" — tel input

**Monitoring Preferences:**
- Toggle: "Enable Monitoring"
- Dropdown: "Alert Sensitivity" — Low / Medium / High

**CTA:**
- Primary button: "Save Changes"

**Behavior:**
- Unsaved changes detected: browser `beforeunload` warning if navigating away without saving
- "Unsaved changes" banner appears when form is dirty

---

### W1-14: Device Management
**URL:** `/devices`
**User Story:** 6.2 Devices
**Entry:** ← W2-01 Settings ("Connected Devices") | ← W1-12 CardiMember Detail ("Manage Devices")
**Exit:** ← Previous page (back) | → W1-04 Device Selection ("Add Device")

**Page Header:**
- Title: "Connected Devices"
- "+ Add Device" button (top right)

**Devices Table (grouped by CardiMember):**

**Group Header:** CardiMember name + photo

**Device Row:**
- Device logo (small) | Device info: Name + Status badge + Data sources + Primary star
- Actions column (hover-revealed): Refresh | Set as Primary | View Sync History | Remove (destructive)

**Status badge:**
- "Active" (synced 10m ago) | "Token Expiring Soon" (caution) | "Disconnected" (critical)

**Expandable Row (click to expand):**
- Last sync: "10 minutes ago"
- Next sync: "In 20 minutes"
- Data synced today: "4 updates"
- Battery: "75%" (if available)

**Troubleshooting (bottom, collapsible):**
- "Having trouble?" — Make sure you're connected / Try reconnecting / Contact support

---

### W1-15: Alert Detail - Heart Rate
**URL:** `/alerts/{id}` (heart rate type)
**User Story:** 11.2 Elevated HR
**Entry:** ← W1-09 Alerts List
**Exit:** ← W1-09 Alerts List (back) | → Phone call | → W2-03 Trend Charts

**Alert Header:**
- Urgent severity banner
- Lightning bolt icon
- Title: "Elevated Heart Rate Alert"
- CardiMember photo + name + timestamp

**Description:**
- "Mom's heart rate has been running higher than usual for the past 3 days"

**Chart:**
- 7-day heart rate chart (interactive — hover for tooltips)
- Shaded normal range (68–75 bpm)
- Elevated portion highlighted with urgent severity treatment

**Comparison Grid:**

| Current | Normal | Difference |
|---------|--------|-----------|
| 88 bpm | 68 bpm | +29% above baseline |

**Context Card:**
- "Here's what might be going on:"
  - She could be fighting off an illness
  - She might be feeling stressed or anxious
  - She may not be drinking enough water
  - It could be a side effect of her medication

**Recommended Actions:**
1. "Suggest a Doctor Visit" (primary, urgent treatment)
2. "Keep watching for a couple more days" (secondary)
3. "Call Mom to ask how she's feeling" (secondary)

**Medical History (collapsible):**
- Shows medications and conditions from CardiMember profile

---

## MVP 2 — Management & Settings (8 screens)

Extends MVP 1 with account management: view trends and historical data, configure notification preferences, handle personal subscription (Basic & Complete Care), and export health data in HL7 and FHIR formats.

**Prerequisite:** MVP 1 must be complete.

---

### W2-01: Settings Main
**URL:** `/settings`
**User Story:** 6.1, 6.2 Settings
**Entry:** Sidebar (Settings) | Top-bar user menu
**Exit:** → W2-02 Subscription | → W2-04 Notification Settings | → W1-12 CardiMember Detail | → W1-14 Device Management | → W2-05 Health Data Export

**Layout (2-column on desktop: settings nav left, content right):**

**Left column — Settings Navigation:**
- Account
- CardiMembers
- Notifications
- Security
- Support
- About

**Right column — Settings Content:**

**User Profile Card (top):**
- Profile photo (large, click to change)
- Name: "[User Name]"
- Email: "[user@email.com]"
- "Edit Profile" button

**Settings Groups:**

**Account**
- My Profile →
- Subscription & Billing → (badge: current plan name)
- Family & Sharing → (MVP 3)

**CardiMembers**
- Manage CardiMembers →
- Connected Devices → W1-14
- Export Health Data → W2-05

**Health Records (MVP 3)**
- Scan Test Results → W3-06

**Notifications**
- Alert Settings →
- Notification Preferences →
- Quiet Hours →

**Security**
- Change Password →
- Two-Factor Authentication (inline toggle)
- Privacy Settings →

**Support**
- Help Center →
- Contact Support →
- Terms & Privacy →

**About**
- App Version (value: "1.0.0")
- Check for Updates

**Danger Zone (separated, red border):**
- "Sign Out" (destructive text)
- "Delete Account" (destructive text)

**MVP 3 addition:** Family & Sharing → W3-01

---

### W2-02: Subscription Management
**URL:** `/settings/subscription`
**User Story:** 6.1 Subscription
**Entry:** ← W2-01 Settings ("Subscription & Billing")
**Exit:** ← W2-01 Settings (back) | → Payment method change | → Plan change

> **Scope note:** MVP 2 covers personal tiers only (Basic and Complete Care). The Guardian Plus business tier is excluded from MVP and will be addressed in a dedicated business account flow post-MVP.

**Current Plan Card (gradient background):**
- Badge: "COMPLETE CARE"
- Price: "$15/month"
- Renewal date: "Renews Feb 10, 2026"
- Button: "Manage Subscription"

**Included Features (checklist):**
- Up to 3 CardiMembers
- Advanced ML Alerts
- Family Sharing
- 90-day data retention
- Priority support

**Usage Section:**
- Progress bars: CardiMembers: 2 of 3 | Data retention: 45 days of 90

**Plan Comparison (side-by-side cards, 2 plans):**
- Each card: Plan name + price/month + "Current Plan" badge (if active) + feature list
- Button: "Current Plan" (disabled) / "Upgrade" / "Downgrade"

**Annual Discount Banner:**
- "Save 15% with Annual Billing"
- "Switch to Annual" button

**Billing Section:**
- Payment method: "Visa ●●●● 1234" (with card icon)
- "Change" button | "Billing History" button

---

### W2-03: Trend Charts
**URL:** `/members/{id}/trends`
**User Story:** 2.3 Historical Data
**Entry:** ← W1-08 Dashboard ("View Trends") | ← W1-10 Alert Detail ("View Detailed Data")
**Exit:** ← Previous page (back) | → W2-05 Health Data Export

**Page Header:**
- Title: "[Name]'s Trends"
- Export button (top right)

**Layout (full-width content area):**

**CardiMember Selector (if multiple members):**
- Dropdown: "Viewing: [Dad]"

**Time Range Selector (segmented control):**
- [7D] [30D] [90D] [Custom]
- Custom opens a date range picker popover

**Metric Tabs:**
- [Activity] [Heart Rate] [Sleep] [All]

**Chart Area:**
- Line chart:
  - X-axis: dates | Y-axis: metric values
  - Shaded area: baseline/normal range | Line: actual data
  - Markers: alert events on timeline
- Zoom: scroll wheel | Click + drag to select range | Double-click to reset
- Keyboard: arrow keys to step through data points

**Interactive Tooltip (hover on data point):**
- Popup: Date/time | Exact value | "120% above baseline" | Note icon (if notes on that date)

**Timeline Annotations (below chart, horizontal scroll):**
- Alert markers with icons | Note markers with text preview
- Click to expand details

**Summary Stats Card:**
- Average: "4,500 steps" | High: "8,200 (Jan 5)" | Low: "1,200 (Jan 8)" | Trend: "Declining 15%" with down arrow

**Export Options (via Export button):**
- Export to PDF | Export to CSV | Copy chart as image | Send to email

---

### W2-04: Notification Settings
**URL:** `/settings/notifications`
**User Story:** 3.2 Alert Preferences
**Entry:** ← W2-01 Settings | ← W1-09 Alerts List (settings gear icon)
**Exit:** ← Previous page (back)

**CardiMember Selector (if multiple members):**
- Dropdown: "Settings for: [Dad]"

**Alert Type Groups (each with enable toggle):**

**Activity Alerts**
- Toggle: enabled/disabled
- Sensitivity slider: Low | Medium | High
- Description: "Let me know if they're moving around less than usual"

**Heart Rate Alerts**
- Toggle: enabled/disabled
- Sensitivity slider: Low | Medium | High

**Sleep Alerts**
- Toggle: enabled/disabled
- Checkboxes: Poor sleep quality | Unusual sleep patterns

**Pattern Break Alerts**
- Toggle: always on (cannot disable)
- Label: "Always on — this is how CardiTrack catches emergencies"

**Notification Channels (per alert type):**
- Multi-select chips: [Email] [SMS] [Browser Push] [All]

**Quiet Hours (collapsible):**
- Toggle: "Enable Quiet Hours"
- Time pickers: From 10:00 PM → To 7:00 AM
- Exception toggle: "Still notify me for emergencies"

**Family Routing (MVP 3):**
- "Also let these family members know:"
- Checkboxes with severity chips: Sarah Johnson — [High Severity] [Critical] | John Doe — [Critical Only]

**Test Section:**
- "Send Test Browser Notification" | "Send Test Email" | "Send Test SMS"

---

### W2-05: Health Data Export
**URL:** `/export`
**User Story:** 6.3 Data Export
**Entry:** ← W2-03 Trend Charts (Export button) | ← W2-01 Settings ("Export Health Data") | ← W1-12 CardiMember Detail ("Export Data")
**Exit:** ← Previous page (back) | → File download / email

**Page Header:**
- Title: "Export Health Data"

**CardiMember Selector:**
- Dropdown: "Export data for: [Dad]"

**Date Range:**
- "From" date picker | "To" date picker
- Quick presets: [Last 7 Days] [Last 30 Days] [Last 90 Days] [All Data]

**Data Selection (checkboxes):**
- Activity & Steps | Heart Rate | Sleep Data | Alerts & Events | Notes (if any)

**Export Format (radio buttons):**

| Format | Description | Use Case |
|--------|-------------|----------|
| PDF Report | Human-readable summary with charts | Sharing with family or personal records |
| CSV | Raw data spreadsheet | Personal analysis |
| HL7 v2 | Health Level Seven messaging format | Hospital system integration |
| FHIR (R4) | Fast Healthcare Interoperability Resources | Modern EHR integration, patient portals |

**MVP 3 addition:** LOINC and CCD formats added (see W3-07)

**MVP 4 addition:** SNOMED CT format added

**Format Info (expandable per format):**
- Click info icon next to HL7/FHIR → explains format and typical recipients

**Delivery Method:**
- "Download to computer" (default) | "Email to..." — email input with autocomplete | "Print" (MVP 4)

**Preview Section:**
- "Preview Export" button — opens modal with first page / sample
- Estimated file size: "~2.4 MB"

**CTA:**
- Primary button: "Export Data"

**States:**
- **W2-05a — Default:** Format and date selection
- **W2-05b — Generating:** Progress bar with cancel option
- **W2-05c — Complete:** Success message with download / share actions
- **W2-05d — Error:** "That didn't work — let's try again" with retry

---

## MVP 3 — Family & Multi-Member (14 screens)

Adds family collaboration: invite siblings to share caregiving, shared notes, manage multiple CardiMembers from a dedicated dashboard, test results scanning with CardiTrack medical insights, and expanded data export formats (LOINC, CCD).

**Prerequisite:** MVP 2 must be complete. MVP 3 extends existing screens (noted as "MVP 3 addition/change" in MVP 1–2 specs).

---

### W3-01: Family Members List
**URL:** `/family`
**User Story:** 4.1 Family Management
**Entry:** Sidebar (Family & Sharing) | ← W2-01 Settings ("Family & Sharing")
**Exit:** → W3-02 Invite Modal | → Role management | → W3-04 Shared Notes

**Page Header:**
- Title: "Family & Sharing"
- "+ Invite Member" button (top right)

**Tabs:**
- [Active Members] [Pending Invites]

**Active Members Table:**

| Column | Content |
|--------|---------|
| Member | Avatar + name + email |
| Role | Badge: ADMIN / STAFF / VIEWER |
| Last Active | "Active 2 hours ago" |
| Actions | Change Role / View Log / Remove |

**Row actions (hover-revealed):**
- "Change Role" → role picker dropdown
- "View Activity Log" → activity log modal
- "Remove Access" → confirmation dialog (destructive)

**Pending Invites Tab:**
- Email | Role | Sent date | "Resend" button | "Revoke" button

**Empty State (pending tab):** "No pending invitations"

---

### W3-02: Invite Family Modal
**URL:** `/family/invite` (modal overlay on `/family`)
**User Story:** 4.1 Inviting Members
**Entry:** ← W3-01 Family List ("+ Invite Member" button) | ← W1-07 Baseline Info ("Invite Family Members First")
**Exit:** ← W3-01 Family List (close/cancel) | → Success toast

**Presentation:** Modal dialog (centered overlay, backdrop blur)

**Header:**
- Close button (X)
- Title: "Invite Family Member"

**Form:**

**Email Input:**
- Label: "Email Address"
- Autofocus, inline validation

**Role Selection (radio buttons or segmented control):**
- [Admin] [Staff] [Viewer]
- Selected role shows description beneath:

| Role | Description |
|------|-------------|
| Admin | Can view, modify settings, invite others |
| Staff | Can view and acknowledge alerts |
| Viewer | Can only view health data |

**Permission Details (expandable):**
- Table showing what each role can/cannot do

**Personal Message (optional):**
- Textarea
- Placeholder: "Hi Sarah, I'm using CardiTrack to keep an eye on Dad — want to help?"

**CTA:**
- Primary button: "Send Invitation"
- Secondary: "Cancel"

---

### W3-03: Multi-Member Dashboard
**URL:** `/dashboard` (replaces W1-08 when multiple CardiMembers exist)
**User Story:** 2.2 Multi-Member View
**Entry:** Sidebar (Dashboard) — replaces W1-08 when user has multiple CardiMembers
**Exit:** → W1-08 Single Dashboard (click member) | → W1-03 Add CardiMember ("+ Add")

**Page Header:**
- Title: "My CardiMembers"
- "+ Add CardiMember" button (top right)
- Filter/Sort controls (right): [All] [Alerts Only] [Good Status] / Sort by Status

**CardiMembers Grid (3-column on desktop, 2 on tablet, 1 on mobile):**

Each card:
- CardiMember photo (circular, large) with status badge overlay
- Name (bold) + Age & relationship
- Status text + last synced
- Alert count badge (if any)
- Quick actions (hover-revealed): "Call" | "View Dashboard"

**States:**
- **W3-03a — Default:** Member cards in grid
- **W3-03b — Empty:** Illustration + "No one here yet" + "Add someone you'd like to look after" button

---

### W3-04: Shared Notes Feed
**URL:** `/family/notes`
**User Story:** 4.2 Coordination
**Entry:** Sidebar (Family) → Notes tab | ← W3-01 Family List
**Exit:** → W3-05 Add Note | ← Previous page (back)

**Layout (2-column on desktop):**

**Page Header:**
- Title: "Family Notes"
- "+ Add Note" button (top right)

**Left column — Notes Feed:**

**Add Note Input (top):**
- User avatar + text input: "Add a note for the family..."
- Click → expands or opens W3-05

**Notes Feed:**

Each note card:
- Author avatar + author name + timestamp ("2 hours ago")
- Three-dot menu (if author): Edit | Delete
- Note text (with @mentions highlighted)
- Attachments (if any) — thumbnail grid
- CardiMember tag: "About: Dad" (if associated)
- Footer: Reply count | Like count

**Threaded Replies (expandable inline):**
- Indented reply cards
- "Load more replies" if >3

**Right column (desktop) — Filters:**
- "All Notes" / "About Dad" / "About Mom" / "My Notes Only" / "Mentions Me"

---

### W3-05: Add / Edit Note
**URL:** `/family/notes/new` (or modal)
**User Story:** 4.2 Shared Notes
**Entry:** ← W3-04 Notes Feed (click input or "+ Add Note" button)
**Exit:** ← W3-04 Notes Feed (cancel or post)

**Presentation:** Full-page on mobile; modal dialog on desktop

**Header:**
- Title: "New Note"
- Cancel + Post buttons

**Note Input:**
- Rich text area (expands with content, min 4 rows)
- Placeholder: "How's Dad doing? Let the family know..."
- Character counter: "0 / 500"
- Typing "@" triggers a mention picker dropdown listing family members

**CardiMember Association:**
- Label: "About (optional)"
- Select: None (General) / Dad / Mom

**Attachments:**
- "+ Attach Photo" button or drag-and-drop zone
- Thumbnail grid when photos added (max 3)

**Visibility:**
- Label: "Who can see this" | Default: "All family members"

**CTA:**
- Primary button: "Post Note"
- Success → toast confirmation + update feed

---

### W3-06: Test Results Scanner
**URL:** `/health-records/scan`
**User Story:** 7.1 Lab Results Capture
**Entry:** ← W2-01 Settings ("Scan Test Results") | ← W1-12 CardiMember Detail ("Add Test Results") | Sidebar
**Exit:** → W3-07 Test Results Detail (analysis complete) | ← Previous page (cancel)

**Page Header:**
- Title: "Scan Test Results"
- Close / Cancel button

**CardiMember Selector:**
- Dropdown: "Scan for: [Dad]"

**Capture Options (2 large cards):**

| Option | Icon | Description |
|--------|------|-------------|
| Upload File | Document icon | "Upload a PDF or image from your computer" |
| Use Camera | Camera icon | "Take a photo with your device camera" (mobile/tablet) |

**File Upload (after selecting Upload):**
- Large drag-and-drop zone: "Drag & drop a PDF or image here, or click to browse"
- Accepted formats: PDF, JPG, PNG
- Max file size: 10 MB

**Camera View (after selecting Camera on mobile/tablet):**
- Camera viewfinder with document guide overlay
- Capture button

**Processing State (after upload/capture):**
- Document thumbnail
- Progress animation: "Analyzing results..."
- Steps: 1. Extracting text (OCR) → 2. Identifying test values → 3. Cross-referencing medical standards
- Cancel button

**Multi-Page Support:**
- "Add Another Page" button after first page
- Page indicator + thumbnail strip

**Error Handling:**
- Blurry/unreadable: retry prompt with tips
- Partial read: "We got most of it — you can fix the rest on the next screen"

**States:**
- **W3-06a — Default:** Upload and camera options
- **W3-06b — File selected / camera active:** Preview + confirm
- **W3-06c — Processing:** Analysis progress steps
- **W3-06d — Error:** Error message with retry options

---

### W3-07: Test Results Detail
**URL:** `/health-records/{id}`
**User Story:** 7.2 Results Analysis
**Entry:** ← W3-06 Test Results Scanner (analysis complete) | ← W1-12 CardiMember Detail ("View Test Results")
**Exit:** ← Previous page (back) | → W2-05 Health Data Export | → Share / Email

**Layout (2-column on desktop):**

**Page Header:**
- Title: "Test Results"
- Share button | Export button

**Left column:**

**Result Summary Card:**
- CardiMember photo + name
- Test date: "February 10, 2026"
- Source: "Uploaded PDF" | Lab name (if detected)

**Parsed Results Table:**

| Test Name | Value | Reference Range | Status |
|-----------|-------|----------------|--------|
| Hemoglobin A1c | 6.2% | 4.0–5.6% | High ↑ |
| Total Cholesterol | 185 mg/dL | <200 mg/dL | Normal ✓ |
| Vitamin D | 18 ng/mL | 20–50 ng/mL | Low ↓ |

- Each row: Edit icon (pencil) for manual OCR correction
- Row hover reveals "Edit value" button

**Corrections Section (collapsible):**
- "Review & Correct Values" — inline editable fields
- "Mark as Verified" button

**Export & Sharing:**
- "Export Results" → W2-05 | "Share with Doctor" → pre-formatted email | "Add to Health Record"

**Data Standards:**
- LOINC — standardized lab test codes *(MVP 3)*
- CCD — Continuity of Care Document *(MVP 3)*
- SNOMED CT — clinical terminology *(MVP 4)*

**Right column (desktop):**

**CardiTrack Insights Card:**
- Lightbulb icon + "CardiTrack Insights"
- Disclaimer: "These observations are here to help — but always talk to a doctor before making health decisions."
- Insights:
  - "Dad's Hemoglobin A1c is a bit high — this sometimes points to pre-diabetes"
  - "Good news — cholesterol levels look normal"
  - "His Vitamin D is low — worth mentioning to his doctor"
- "Learn More" links per insight

**Trend Comparison (if previous results exist):**
- Side-by-side table vs. last test
- Trend arrows (improving / worsening / stable)

**States:**
- **W3-07a — Default:** Parsed results with insights
- **W3-07b — Editing:** Inline editing mode for value corrections
- **W3-07c — No previous results:** Trend comparison section hidden
- **W3-07d — Loading insights:** Skeleton loading for CardiTrack insights

---

## MVP 4 — Web Native & Offline (13 screens)

Adds web-native polish: PWA installation, browser notification permission and rich notification types, offline mode with sync queue, keyboard shortcuts for power users, and a dedicated print/PDF view for health records. Also adds SNOMED CT export.

**Prerequisite:** MVP 3 must be complete.

---

### W4-01: PWA Install Prompt
**User Story:** 10.3 PWA Install
**Entry:** Automatic — browser detects PWA manifest and triggers install eligibility
**Exit:** App installed | User dismisses

**Presentation:** Sticky bottom banner (desktop) or bottom sheet (mobile)

**Banner content:**
- CardiTrack icon
- Heading: "Install CardiTrack"
- Subtext: "Add to your home screen for faster access — works offline too"
- Buttons: "Install" (primary) | "Not now" (dismiss)

**Post-install confirmation toast:**
- "CardiTrack is installed! Look for it on your home screen."

---

### W4-02: Browser Notification Permission
**User Story:** 5.1 Browser Notifications
**Entry:** ← W1-07 Baseline Info (triggered post-onboarding) | ← W2-04 Notification Settings ("Enable Browser Push")
**Exit:** → W2-04 Notification Settings | Permission granted/denied

**Presentation:** Inline card within settings or post-onboarding flow (not a bare browser dialog)

**Card Content:**
- Bell icon
- Heading: "Stay informed, instantly"
- Body: "Turn on browser notifications so you never miss a critical alert — even when CardiTrack isn't your active tab"
- Benefits: Immediate alerts | Works in background | You control the settings
- Primary button: "Enable Notifications" — triggers native browser permission dialog
- Text link: "Set up later"

**Post-permission:**
- Granted: Success toast + "Test Notification" button
- Denied: Informational card with instructions to re-enable via browser settings

---

### W4-03: Offline Mode Indicator
**User Story:** 10.1 Offline Support
**Entry:** Automatic — appears when browser loses internet connectivity
**Exit:** Automatic — disappears when connection restored

**States:**
- **W4-03a — Offline**
- **W4-03b — Connection Restored**

**W4-03a — Offline**

**Offline Banner (top of app shell, persistent):**
- Warning-level background
- Crossed-out signal icon
- Text: "Offline Mode"
- Subtext: "Last updated 2 hours ago"
- Closable (X) but reappears on navigation

**Content Modifications (offline state):**
- All data shown is cached/stale
- Refresh buttons greyed out with tooltip: "Not available offline"
- Form submissions queued: "This action will be sent when you're back online"

**Alert Queue Card (on dashboard/alerts page):**
- "2 actions pending sync"
- Shows queued acknowledgments/notes with "Not yet synced" badge

**W4-03b — Connection Restored:**
- Toast: "Back online!" with syncing animation
- Progress: "Syncing 2 pending actions..."
- Success: "All data synced"

---

### W4-04: Offline Data Cache Settings
**URL:** `/settings/offline`
**User Story:** 10.1 Cache Management
**Entry:** ← W2-01 Settings
**Exit:** ← W2-01 Settings (back)

**Cache Info Card:**
- "Cached Data Size: 45 MB"
- "Last synced: 10 minutes ago"

**Settings:**
- Slider: "Days to cache" — 1 day | 7 days | 30 days
- Toggle: "Pre-load trend charts"
- Toggle: "Cache CardiMember photos"

**Actions:**
- "Clear Cache" button — confirmation dialog: "You'll need internet to view data until the next sync"
- "Sync Now" button

---

### W4-05: Browser Notifications
**User Story:** 5.1 Notification Types
**Entry:** System — triggered by backend alert events via Service Worker

Designs for system-level browser notification UI.

**States:**
- **W4-05a — Standard alert (collapsed):** App icon + "[Name] - Alert" + summary body + timestamp
- **W4-05b — Critical alert (expanded):** Full alert text + action buttons: "Call" / "Acknowledge" / "View"
- **W4-05c — In-app banner (tab active):** Slides from top-right corner, shows summary, click to navigate, dismiss button
- **W4-05d — Notification center (browser):** Grouped by CardiMember with expandable notification stacks

---

### W4-06: Keyboard Shortcuts Reference
**URL:** `/shortcuts` (modal overlay accessible from any page)
**User Story:** 10.4 Power User Accessibility
**Entry:** Press `?` on any page | ← W2-01 Settings ("Keyboard Shortcuts")
**Exit:** Press `Escape` or click outside

**Presentation:** Modal dialog (centered, scrollable)

**Header:**
- Title: "Keyboard Shortcuts"
- Close button (X)

**Shortcut Groups:**

**Navigation**

| Shortcut | Action |
|----------|--------|
| `G D` | Go to Dashboard |
| `G A` | Go to Alerts |
| `G F` | Go to Family |
| `G S` | Go to Settings |
| `⌘K` / `Ctrl+K` | Open command palette |

**Alerts**

| Shortcut | Action |
|----------|--------|
| `J` / `K` | Next / previous alert |
| `E` | Acknowledge selected alert |
| `O` | Open alert detail |
| `C` | Call CardiMember |

**General**

| Shortcut | Action |
|----------|--------|
| `R` | Refresh data |
| `?` | Show this reference |
| `Esc` | Close modal / cancel |

---

### W4-07: Print / PDF Export View
**URL:** `/print/{memberId}` (print-optimized route)
**User Story:** 6.3 Print Export
**Entry:** ← W2-03 Trend Charts ("Print" option) | ← W2-05 Health Data Export ("Print" delivery method) | ← W3-07 Test Results Detail ("Print")
**Exit:** Browser print dialog | ← Previous page (cancel)

**Layout:** Print-optimized single-column layout (no sidebar, no top bar)

**Print Header:**
- CardiTrack logo + "Health Report" title
- CardiMember name + date range + generated date

**Report Content (configured by source screen):**
- Summary stats table
- Trend charts (static image render for print)
- Alerts table (if included)
- Test results table (if included)
- CardiTrack Insights (if included)
- Medical disclaimer footer

**Print Controls (screen-only, hidden in print CSS):**
- "Print / Save as PDF" button → triggers `window.print()`
- "Customize Report" dropdown: include/exclude sections
- "Cancel" link → back to previous page

**States:**
- **W4-07a — Preview:** Full report rendered on screen
- **W4-07b — Printing:** Browser native print dialog active
- **W4-07c — Empty selection:** "Select at least one data section to include"

---

## Design System (Designer Deliverable)

The design system is **yours to define**. The following are functional requirements and constraints — not visual prescriptions.

### What You Need to Define
- Color palette (brand colors, semantic colors, status colors)
- Typography scale (font family, sizes, weights)
- Spacing and layout grid
- Component library (buttons, cards, inputs, tables, badges, modals, etc.)
- Iconography style
- Motion and animation language
- Dark mode (if applicable)

### Functional Requirements

**4 severity levels must be visually distinct from each other:**

| Level | Meaning | Must Convey |
|-------|---------|-------------|
| Normal | Everything is fine | Calm, reassuring |
| Caution | Something to be aware of | Mild concern, not urgent |
| Urgent | Action recommended soon | Clear importance, time-sensitive |
| Critical | Immediate action needed | Emergency, cannot be missed |

**Visual hierarchy needs:**
- Primary actions must be clearly distinguishable from secondary and tertiary actions
- Destructive actions (delete, remove) must be visually distinct from standard actions
- Unread/new states must be clearly differentiated from read/acknowledged states
- Data comparison (current vs. baseline) needs clear visual treatment
- Hover states must be defined for all interactive elements
- Focus states must be visible for keyboard navigation

### Constraints

**Accessibility (non-negotiable):**
- WCAG AA minimum contrast (4.5:1 for text, 3:1 for large text)
- Status must never rely on color alone — always pair with icon, text, or pattern
- All interactive elements must be keyboard-accessible (Tab, Enter, Space, Escape)
- Focus indicators must be clearly visible
- All form inputs must have programmatically associated labels
- All images must have descriptive alt text

**Platform:**
- Desktop-first layout — sidebar + content area as primary pattern
- Responsive down to 375px mobile width
- Blazor components must support SSR + interactivity (Server render mode)
- SignalR connection state must be visually represented (connected / reconnecting / offline)

**User context:**
- Primary users are 30–65 year old adults; critical alerts may be read in high-stress moments — design for quick scanning
- The app will be used at a desk or laptop as primary context; occasional mobile browser use
- Keyboard navigation should be first-class (power caregivers check-in frequently)

---

## Asset Inventory

Icons use **Heroicons** or **Lucide** (open source, MIT license) — no custom icon design needed. Third-party logos (Google, Apple, Fitbit, Garmin, Samsung, Withings, Visa) are sourced from vendor brand kits. Items below are assets that need to be created or sourced.

### MVP 1 — Assets

#### Illustrations (Storyset / Blush or custom)

| # | Asset | Screen | Description |
|---|-------|--------|-------------|
| 1 | Hero illustration | W1-01 | Elderly person with smartwatch + family member at laptop — warm, reassuring tone |
| 2 | "How It Works" step icons | W1-01 | 3 icons: Connect device / Monitor patterns / Get alerted |
| 3 | Onboarding person silhouette | W1-03 | Placeholder for CardiMember photo — neutral, warm |
| 4 | Learning Phase | W1-07 | Brain with gears concept — "getting to know your loved one." Can be Lottie or static |
| 5 | Empty Alerts | W1-09 | Muted bell or peaceful scene — "nothing to worry about" feeling |
| 6 | No Device Connected | W1-08 | Prompt to connect a device — friendly nudge, not an error |

**Style guidance:** All 6 illustrations must share the same art style and brand color palette. Choose one Storyset or Blush collection and customize colors. Tone: warm, caring, approachable — not clinical.

#### Brand Assets (custom — must be unique)

| # | Asset | Used On | Notes |
|---|-------|---------|-------|
| 1 | CardiTrack logo | W1-01 (header + footer), W1-02, W1-05, W1-07 | SVG master. Export: full-colour, monochrome, favicon (16, 32, 180px). |
| 2 | Favicon / PWA icon | Browser tab, PWA install, W4-01 | 512×512 + all required sizes. Must look distinct at 16×16. |

#### Animations

| # | Animation | Screen | Source |
|---|-----------|--------|--------|
| 1 | Success checkmark | W1-06 | LottieFiles — "success checkmark" (free options available) |
| 2 | Skeleton / shimmer loading | W1-08, W1-09, W2-03 | CSS animation — reusable across all cards and tables |
| 3 | Critical alert pulse | W1-11 | CSS animation — opacity + border-color loop on severity banner |
| 4 | Learning phase brain/gears | W1-07 | LottieFiles — "machine learning" or "brain processing." Or use static illustration + CSS spinner |

#### Third-Party Logos (vendor-provided, no design needed)

| # | Logo | Screen | Source |
|---|------|--------|--------|
| 1 | Google | W1-02 | Google Identity branding guidelines (SVG provided) |
| 2 | Apple | W1-02 | Apple Sign In SDK (renders automatically) |
| 3 | Fitbit | W1-04, W1-05, W1-14 | Fitbit/Google developer brand assets |
| 4 | Apple Watch | W1-04, W1-05, W1-14 | Apple marketing assets (MFi partners) |
| 5 | Garmin | W1-04, W1-05, W1-14 | Garmin Connect developer program |
| 6 | Samsung | W1-04, W1-05, W1-14 | Samsung developer brand kit |
| 7 | Withings | W1-04, W1-05, W1-14 | Withings Health API partner assets |
| 8 | Visa / card brands | W2-02 | Payment SDK (Stripe, etc.) includes card icons |

---

### MVP 2 — Assets

No new custom assets required. Management & Settings screens (W2-01–W2-05) use platform components, existing brand assets, and third-party logos already sourced in MVP 1.

---

### MVP 3 — Assets

#### Illustrations

| # | Asset | Screen | Description |
|---|-------|--------|-------------|
| 7 | Empty Members | W3-03 | "No one here yet" — friendly empty state, same art style as MVP 1 illustrations |

#### Animations

| # | Animation | Screen | Source |
|---|-----------|--------|--------|
| 5 | OCR processing steps | W3-06 | Step indicator animation (3 steps). CSS or LottieFiles — "document scanning" |

#### Icons (open source — no custom design)

All icons in MVP 3 reuse Heroicons / Lucide. No new custom icons needed.

---

### MVP 4 — Assets

#### Icons (open source — no custom design)

| # | Icon | Screen | Source |
|---|------|--------|--------|
| 1 | Offline / no-signal | W4-03 | Lucide `wifi-off` |
| 2 | Keyboard | W4-06 | Lucide `keyboard` |
| 3 | Printer | W4-07 | Lucide `printer` |
| 4 | PWA install | W4-01 | Lucide `download` |

No new custom illustrations or animations needed for MVP 4.

---

### Asset Summary

| Category | MVP 1 | MVP 2 | MVP 3 | MVP 4 | Total |
|----------|-------|-------|-------|-------|-------|
| Custom illustrations | 6 | 0 | 1 | 0 | **7** |
| Brand assets (logo + icon) | 2 | 0 | 0 | 0 | **2** |
| Animations (Lottie / CSS) | 4 | 0 | 1 | 0 | **5** |
| Third-party logos | 8 | 0 | 0 | 0 | **8** |
| **Subtotal** | **20** | **0** | **2** | **0** | **22** |

**Truly custom** (must be designed): **2** — CardiTrack logo and favicon/PWA icon. Everything else can be sourced from Storyset/Blush (illustrations), LottieFiles (animations), vendor brand kits (logos), and Heroicons/Lucide (icons).

---

## Figma Delivery Requirements

This project uses **Cursor + Figma MCP** to build the UI directly from Figma designs. The following requirements ensure the designs translate accurately into Blazor components.

### File Structure

- Organise screens into **named Pages** per MVP: `MVP 1 — Core Monitoring`, `MVP 2 — Management & Settings`, `MVP 3 — Family & Multi-Member`, `MVP 4 — Web Native & Offline`
- Each screen must live in its own **named Frame**, using the Screen IDs from this document (e.g. `W1-08 Main Dashboard`)
- For each screen, provide **3 frames**: Desktop (1440px), Tablet (768px), Mobile (375px)
- Group all reusable UI into a dedicated **`Components`** page

### Layout

- Use **Auto Layout** on every frame, section, and component — this is critical for accurate Blazor code generation
- Set explicit **spacing tokens** via Auto Layout gap/padding values (do not use manual nudging)
- Define a consistent **8pt grid** for all spacing and sizing
- Mark responsive breakpoint behavior using Figma's **constraints** and **min/max width** properties

### Design Tokens (Figma Variables)

Set up the following as **Figma Variables** (not hardcoded values):

| Token Type | Examples |
|------------|---------|
| Colors | `color/status/normal`, `color/status/caution`, `color/status/urgent`, `color/status/critical`, `color/brand/primary`, `color/text/primary`, `color/background/card`, `color/background/sidebar` |
| Typography | `text/heading/large`, `text/heading/page`, `text/body/default`, `text/label/small`, `text/label/badge` |
| Spacing | `space/xs` (4), `space/sm` (8), `space/md` (16), `space/lg` (24), `space/xl` (32) |
| Radius | `radius/card`, `radius/button`, `radius/chip`, `radius/modal` |
| Shadow | `shadow/card`, `shadow/modal`, `shadow/dropdown` |

### Components

- Publish all reusable UI as **Figma Components** with variants (e.g. Button: Primary / Secondary / Destructive / Disabled)
- Name components using the pattern: `ComponentName/Variant` (e.g. `AlertRow/Critical`, `Button/Primary`)
- Use **component properties** (text, boolean, instance swap) so variants are machine-readable
- All interactive states must be defined as variants: Default, Hover, Focus, Active, Disabled, Loading, Error
- Define **table rows** as components with hover state (required for alert list and device table)

### Naming Conventions

- All layers must be **named meaningfully** — no `Frame 42`, `Rectangle 7`, or `Group 3`
- Use camelCase or kebab-case consistently (e.g. `statusHeroCard` or `status-hero-card`)
- Hidden layers that are not part of the design must be deleted, not just hidden

### Handoff Checklist (per screen)

Before marking a screen as ready:
- [ ] Screen is in a named frame with the correct Screen ID
- [ ] 3 breakpoint frames provided: Desktop (1440px), Tablet (768px), Mobile (375px)
- [ ] All layers are named
- [ ] Auto Layout is applied throughout
- [ ] All colors and text styles reference Figma Variables (no hardcoded hex values)
- [ ] All interactive states (hover, focus, loading, error) are defined as variants
- [ ] Responsive behavior is annotated (which columns collapse, what reflows to single column)
- [ ] All four severity levels (Normal / Caution / Urgent / Critical) are visually tested in context
- [ ] Keyboard focus states are defined on all interactive elements

### Figma Access

- Share the file with **Edit access** so the MCP server can read all component and variable data
- Provide a **personal access token** (Figma → Settings → Security → Personal Access Tokens) for MCP authentication
- Alternatively, use **OAuth login** if using the official Figma remote MCP server

### MCP Configuration (Cursor)

The developer will connect Cursor to Figma using the following MCP config (`~/.cursor/mcp.json`):

```json
{
  "mcpServers": {
    "figma": {
      "command": "npx",
      "args": ["-y", "figma-developer-mcp", "--figma-api-key", "YOUR_TOKEN_HERE", "--stdio"]
    }
  }
}
```

Once connected, screen designs can be referenced directly by Figma frame URL during development — no manual redlines or spec exports needed.

---

**Total Screens:** 68 (counting each state as a screen)
**MVP 1:** 33 screens — Core Monitoring (design first)
**MVP 2:** 8 screens — Management & Settings
**MVP 3:** 14 screens — Family & Multi-Member
**MVP 4:** 13 screens — Web Native & Offline

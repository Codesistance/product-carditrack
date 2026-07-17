# CardiTrack - MVP 1 Screen Specifications

> **Extract — do not edit directly.** This file is extracted from the canonical [ui_screens_maui_mobile.md](../ui_screens_maui_mobile.md) (v3.0); make changes there and re-extract. Release sequencing is governed by the [release matrix](../../../../release_matrix.md).

## Project Overview

**Product:** CardiTrack - Remote health monitoring for elderly family members
**Release:** MVP 1 — Core Monitoring (37 screens)
**Platform:** iOS 16+ (iPhone 12+) & Android 10+ (API 29)
**Minimum OS:** iOS 16.0 · Android 10 (API level 29)
**Target OS:** iOS 18 · Android 15 (API level 35)
**Orientation:** Portrait primary, landscape supported
**Target Users:** Family caregivers across the US & EU monitoring elderly relatives' wearable health data
**Document Version:** 1.0 (extracted from full spec v3.0)
**Last Updated:** February 24, 2026

---

## What MVP 1 Delivers

A single user can sign up, add one or more CardiMembers, connect devices, manage CardiMember profiles, view the health dashboard, receive and manage all alert types, and export health data in PDF, CSV, or FHIR R4 format. This is the essential monitoring loop — everything needed for the app to be useful from day one.

---

## Screen Index

| ID | Screen | Variations |
|----|--------|------------|
| M1-01 | Splash Screen | 2 (a–b) |
| M1-02 | Welcome / Landing | 1 |
| M1-03 | Sign Up | 4 (a–d) |
| M1-04 | Add First CardiMember | 3 (a–c) |
| M1-05 | Device Connection - Selection | 1 |
| M1-06 | Device Connection - OAuth | 3 (a–c) |
| M1-07 | Device Connection - Success | 3 (a–c) |
| M1-08 | Baseline Learning Info | 1 |
| M1-09 | Main Dashboard | 5 (a–e) |
| M1-10 | Alerts List | 4 (a–d) |
| M1-11 | Alert Detail - Activity | 1 |
| M1-12 | Alert Detail - Critical | 1 |
| M1-13 | CardiMember Detail | 1 |
| M1-14 | Edit CardiMember | 1 |
| M1-15 | Device Management | 1 |
| M1-16 | Alert Detail - Heart Rate | 1 |
| M1-17 | Health Data Export | 4 (a–d) |

**Total: 17 screens · 37 states**

---

## User Flows

### Flow 1: First-Time Onboarding

```
[M1-01 Splash] ──────────────────────────────────────────> [M1-09 Dashboard]
      │                                                      (returning user)
      ▼
[M1-02 Welcome]
      │              │
   "Get Started"  "Sign In"
      │              ▼
      │         [Login screen] ──────────────────────────> [M1-09 Dashboard]
      ▼
[M1-03 Sign Up]
      │
      ▼
[M1-04 Add CardiMember]
      │                  │
   "Continue"         "Skip"
      │                  └──────────────────────────────> [M1-09 Dashboard]
      ▼                                                     (empty state)
[M1-05 Device Selection]
      │
      ▼
[M1-06 OAuth Permission]
      │
   ┌──┴──────────────┐
Success             Failure
   │                   │
   ▼                   └──> Back to M1-05 | Help
[M1-07 Connection Success]
   │                   │
"Continue"    "+ Add Another Device"
   │                   └──> [M1-05 Device Selection]
   ▼
[M1-08 Baseline Learning Info]
   │
"Go to Dashboard"
   │
   ▼
[M1-09 Dashboard]
```

### Flow 2: Daily Monitoring

```
[App Launch / Tab: Home] → [M1-09 Dashboard]
                                  │
              ┌───────────────────┼─────────────────────┐
              ▼                   ▼                     ▼
         [Call/SMS]         [M1-10 Alerts]         [M1-13 CardiMember Detail]
                                  │                     │
                       ┌──────────┼──────────┐     [M1-14 Edit CardiMember]
                       ▼          ▼          ▼
                   [M1-11       [M1-12     [M1-16
                   Activity]    Critical]  Heart Rate]
                       │          │          │
                  Acknowledge  "Call Now"  "Suggest
                  / Add Note   "On My Way"  Doctor Visit"
                       │          │          │
                       └──────────┴──────────┘
                                  │
                             Back to M1-10
```

### Flow 3: Data Export

```
Entry points:
  [M1-13 CardiMember Detail] → "Export Data" ──> [M1-17 Health Data Export]

                                  [M1-17 Health Data Export]
                                            │
                           ┌────────────────┼────────────────┐
                           ▼                ▼                ▼
                      [Save to Device]   [Email to...]  [Share via...]
```

### Flow 4: Critical Alert Response

```
Push notification (any time) ────────────────────────────────> [M1-12 Alert Detail - Critical]
                                                                          │
                                                             ┌────────────┴────────────────┐
                                                             ▼                             ▼
                                                       "CALL NOW"                   "I'M ON MY WAY"
                                                       (one tap dial)               (notifies family,
                                                                                     updates status)
                                                                    │
                                                           "It's okay — explain"
                                                                    │
                                                              [Note field]
                                                                    │
                                                             Alert dismissed
                                                             (explanation logged)
```

---

## Navigation Structure

### Bottom Tab Bar (always visible)

```
┌────────────────────────────────────┐
│                                    │
│          Content Area              │
│                                    │
├──────────┬──────────┬─────┬────────┤
│ Dashboard│  Alerts  │Family│Settings│
└──────────┴──────────┴─────┴────────┘
```

- Badge count on Alerts tab for unread alerts
- Family tab shows placeholder / "Coming Soon" in MVP 1, or can be hidden

### Flyout Menu (swipe from left edge)

- User profile header (photo, name, email)
- Dashboard
- CardiMembers (badge: member count)
- Alerts (badge: unread count)
- Family & Sharing _(MVP 2)_
- Subscription
- Settings
- Help & Support
- Sign Out

### Gesture Patterns

| Gesture | Context | Action |
|---------|---------|--------|
| Pull down | Any scrollable screen | Refresh data |
| Swipe left | List items | Reveal quick actions |
| Swipe right | List items | Reveal secondary actions |
| Swipe right (edge) | Any screen | Open flyout menu |
| Pinch | Chart views | Zoom in/out |
| Long press | Chart data points | Show tooltip |
| Long press | CardiMember photo | Change photo |

---

## POC Screens

Five MVP 1 screens selected to validate the core design language — covering branding, the primary monitoring experience, the alert management system, and critical safety interactions.

| # | Screen | Why It's Here |
|---|--------|---------------|
| 1 | **M1-02 — Welcome / Landing** | Entry point; showcases brand identity, hero carousel, and marketing tone |
| 2 | **M1-04 — Add First CardiMember** | Onboarding form; demonstrates photo picker, progressive disclosure, and inline privacy messaging |
| 3 | **M1-09 — Main Dashboard** | Core monitoring screen; shows status hero card, 3-metric layout, severity color system, and sparklines |
| 4 | **M1-10 — Alerts List** | Alert management; demonstrates severity badges, grouped list design, filter chips, and swipe actions |
| 5 | **M1-12 — Alert Detail - Critical** | Highest-stakes screen; validates urgency design, pulsing severity treatment, and primary CTA hierarchy |

These five screens span onboarding → daily use → emergency response and collectively exercise the full design system: typography, color severity levels, card components, form patterns, and navigation affordances.

---

## Screens

### M1-01: Splash Screen
**User Story:** 1.1-1.3 Onboarding
**Entry:** App launch
**Exit:** → M1-02 Welcome (first launch) | → M1-09 Dashboard (returning user) | → M4-02 Biometric Login (MVP 3, if enabled)

**Duration:** 2-3 seconds while app initializes

**Layout:**
- Full-screen gradient background (CardiTrack brand colors)
- Large CardiTrack logo (centered)
- App name beneath logo
- Loading spinner (bottom third)
- Version number (bottom, small text)

**States:**
- **M1-01a — Default:** Logo + spinner animation
- **M1-01b — Error:** If initialization fails → "Hmm, something didn't work. Tap to try again." with retry button

---

### M1-02: Welcome / Landing Screen
**User Story:** 1.1 First-Time Registration
**Entry:** ← M1-01 Splash (first launch only)
**Exit:** → M1-03 Sign Up | → Login screen ("Sign In")

**Layout:**

**Header (top 20%):**
- CardiTrack logo (top-left, small)
- "Sign In" link (top-right)

**Hero Carousel (middle 50%):**
- 3 swipeable slides with pagination dots:

| Slide | Illustration | Headline | Subtext |
|-------|-------------|----------|---------|
| 1 | Happy elderly person with smartwatch | "Know They're Okay" | "Stay close to the people you love — even from far away" |
| 2 | Phone showing health dashboard | "Their Watch, Your Peace of Mind" | "Connects with Fitbit, Apple Watch, Garmin & more" |
| 3 | Family members on phones | "Care Together" | "Share the watch with your siblings — you're not in this alone" |

**CTA Section (bottom 30%):**
- Primary button (full width, bold): "Start Free 30-Day Trial"
- Pricing text (small, muted): "Then $8/month - Cancel anytime"
- Secondary button (text style, subtle): "Sign In"
- Legal link (small): "By continuing, you agree to Terms & Privacy"

**Interactions:**
- Carousel auto-advances every 4 seconds, pauses on touch
- Pagination dots indicate current slide
- Swipe left/right between slides

---

### M1-03: Sign Up Screen
**User Story:** 1.1 Account Creation
**Entry:** ← M1-02 Welcome ("Start Free Trial")
**Exit:** → M1-04 Add CardiMember (success) | ← M1-02 Welcome (back) | → Login screen ("Already have an account?")

**Header:**
- Back button
- Title: "Create Account"
- Progress indicator: "Step 1 of 4"

**Form (scrollable):**

**Email & Password Section:**
- Label: "Email Address"
  - Text input, email keyboard, autocapitalization off
- Label: "Password"
  - Password input (masked)
  - Password strength bar below (weak → medium → strong)
  - Strength label: "Weak" / "Medium" / "Strong"
- Label: "Confirm Password"
  - Password input (masked)

**Divider:** Horizontal line with "OR" centered

**Social Login:**
- Button: "Continue with Google" (white background, Google logo)
- Button: "Continue with Apple" (black background, Apple logo)

**Terms:**
- Checkbox + label: "I agree to Terms of Service and Privacy Policy"
- "Terms of Service" and "Privacy Policy" are tappable links

**CTA:**
- Primary button: "Create Account" (disabled until form is valid)
- Error message area (hidden until error occurs)

**Bottom:**
- Link: "Already have an account? Sign In"

**Validation Rules:**
- Email: valid format, real-time feedback
- Password: min 8 characters, 1 uppercase, 1 number
- Confirm password: must match
- Terms checkbox: must be checked
- Button enables only when all validations pass

**States:**
- **M1-03a — Default:** Empty form
- **M1-03b — Validating:** Inline error messages appear beneath invalid fields
- **M1-03c — Loading:** Button shows spinner, form disabled
- **M1-03d — Error:** Error banner at top (e.g., "Email already registered")

---

### M1-04: Add First CardiMember
**User Story:** 1.2 Adding First CardiMember
**Entry:** ← M1-03 Sign Up (success)
**Exit:** → M1-05 Device Selection ("Continue") | → M1-09 Dashboard ("Skip for Now")

**Header:**
- Back button
- Title: "Add CardiMember"
- Progress indicator: "Step 2 of 4"

**Introduction:**
- Icon: person silhouette
- Text: "Who would you like to look after?"
- Subtext: "Tell us about your loved one — we'll take it from there"

**Photo Section:**
- Circular photo placeholder (large)
- "Add Photo" button below
- Tap opens choice: Camera or Gallery

**Required Fields:**
- "Full Name *" — text input
- "Date of Birth *" — date picker (format: MM/DD/YYYY)
- "Relationship *" — dropdown picker:
  - Parent, Grandparent, Spouse, Sibling, Other

**Optional Fields (collapsible section):**
- Toggle: "Add More Details (Optional)"
- When expanded:
  - "Medical Notes" — multi-line text (max 500 chars, character counter shown)
    - Encrypted indicator icon visible
  - "Emergency Contact Name" — text input
  - "Emergency Contact Phone" — phone keyboard input

**Privacy Notice:**
- Info card with lock icon
- Text: "[Name] will know you're looking out for them and can give their okay"

**CTA:**
- Primary button: "Continue"
- Text link: "Skip for Now"

**States:**
- **M1-04a — Default:** Empty form with photo placeholder
- **M1-04b — Photo added:** Shows uploaded image in circle
- **M1-04c — Loading:** Button shows spinner on submit

---

### M1-05: Device Connection - Selection
**User Story:** 1.3 Device Connection Wizard
**Entry:** ← M1-04 Add CardiMember ("Continue")
**Exit:** → M1-06 OAuth Permission (device selected)

**Header:**
- Back button
- Title: "Connect Device"
- Progress indicator: "Step 3 of 4"

**Introduction:**
- Text: "What does [Name] wear?"
- Subtext: "We'll connect with their device to keep you in the loop"

**Device Grid (2 columns on phone, 3 on tablet):**

Each device card:
- Rounded frame with shadow
- Device logo (medium)
- Device name (bold)
- Badge: "Supported" (checkmark) for active devices; "Coming Soon" (clock) for future devices
- Future device cards are visible but non-tappable (greyed out, no interaction)
- Entire active card is tappable, highlights on selection

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
- Single selection — tap to select, tap again to deselect
- Only Fitbit cards are selectable in MVP 1; others are greyed out with "Coming Soon" badge
- Selected card shows highlighted border + checkmark overlay
- Selecting a device automatically proceeds to M1-06

---

### M1-06: Device Connection - OAuth Permission
**User Story:** 1.3 OAuth Flow
**Entry:** ← M1-05 Device Selection (device chosen)
**Exit:** → M1-07 Success (authorization complete) | ← M1-05 Device Selection ("Cancel")

**Header:**
- Back button
- Title: "[Device Name] Connection"

**Visual Connection (centered):**
- Large device logo
- Arrow/connection icon
- Large CardiTrack logo

**Permission List:**
- Label: "To look after [Name], CardiTrack needs:"
- Each permission in its own row:

| Icon | Permission | Info Tooltip |
|------|-----------|-------------|
| Heart | Heart Rate Data | "So we can spot if something's off" |
| Shoe | Activity & Steps | "To make sure they're staying active" |
| Moon | Sleep Data | "To know they're resting well" |

- Each row has an (i) info button that shows the tooltip on tap

**Privacy Notice:**
- Card with light background
- Lock icon + text: "Your family's health data stays private — always. We never sell or share it."

**CTA:**
- Primary button: "Authorize [Device Name]"
  - Tap opens device's OAuth login in a browser/webview
- Text link: "Cancel"

**States:**
- **M1-06a — Default:** Permission list visible
- **M1-06b — Authorizing:** Loading overlay with "Connecting to [Name]'s [Device]..." message
- **M1-06c — Error:** "We couldn't connect — let's try that again" with retry button

---

### M1-07: Device Connection - Success
**User Story:** 1.3 Connection Success
**Entry:** ← M1-06 OAuth (authorization complete)
**Exit:** → M1-08 Baseline Info ("Continue to Dashboard") | → M1-05 Device Selection ("Add Another Device")

**Animation:**
- Animated checkmark (plays once on entry)

**Success Message:**
- Heading: "You're all set!"
- Text: "[Name]'s [Device] is now connected"
- Subtext: "We're pulling in their latest data — just a moment"

**Data Preview Card:**
- Title: "Latest Data"
- Rows:
  - Steps Today: 4,250
  - Last Synced: Just now
  - Heart Rate: 72 bpm

**Options:**
- Outlined button: "+ Add Another Device"
- Helper text: "Does [Name] have another device? The more data, the better we can watch over them"

**CTA:**
- Primary button: "Continue to Dashboard"

**States:**
- **M1-07a — Syncing:** Preview card shows shimmer/skeleton loading
- **M1-07b — Synced:** Preview card shows real data
- **M1-07c — Partial sync:** Some values show, others show "Syncing..."

---

### M1-08: Baseline Learning Info
**User Story:** 1.3 Baseline Setup
**Entry:** ← M1-07 Device Success
**Exit:** → M1-09 Dashboard ("Go to Dashboard")

**Header:**
- Title: "Learning Phase"
- Progress indicator: "Step 4 of 4"

**Illustration:**
- Animated graphic: brain with gears (learning concept)

**Explanation:**
- Heading: "Getting to Know [Name]"
- Body: "Over the next 30 days, CardiTrack will learn what a normal day looks like for [Name]:"
- Bullet list:
  - "When they usually wake up and go to sleep"
  - "How active they are day to day"
  - "What their resting heart rate looks like"

**Progress:**
- Progress bar: "Day 1 of 30"
- Label: "3% Complete"

**Options Card:**
- Toggle switch: "Keep me posted while CardiTrack is learning"
- Description: "You'll get basic alerts right away (like heart rate over 100)"

**CTA:**
- Primary button: "Go to Dashboard"

**MVP 2 addition:** Text link "Invite Family Members First" → M3-02

---

### M1-09: Main Dashboard (Single CardiMember)
**User Story:** 2.1 Daily Health Overview
**Entry:** Tab bar (Home) | ← M1-08 Baseline Info (first time)
**Exit:** → M1-10 Alerts List | → M2-03 Trend Charts | → M1-13 CardiMember Detail | → Phone call / SMS

**Header (fixed):**
- Greeting: "Good Morning, [User First Name]"
- Notification bell icon (with unread badge count)
- Refresh icon (pull-to-refresh also supported)

**Status Hero Card:**
- Large card with gradient background colored by status
- CardiMember photo (circular, large)
- Name and age: "[Name], 78"
- Large status indicator:

| Status | Label | Icon |
|--------|-------|------|
| Normal | "[Name] is doing well" | Checkmark |
| Caution | "Something looks a little different" | Warning triangle |
| Urgent | "You should check in" | Lightning bolt |
| Critical | "Reach out to [Name] now" | Siren |

- Last synced: "Updated 10 minutes ago"
- Tap sync icon for manual refresh

**Quick Actions Row (3 horizontal buttons):**
- "Call [Name]" (phone icon) → initiates phone call
- "Send Message" (SMS icon) → opens SMS
- "View Details" (chart icon) → navigates to M1-13

**Key Metrics (3 cards in a row):**

**Card 1: Activity**
- Icon: shoe
- Large value: "4,250 steps"
- Visual progress bar (current vs. goal)
- Comparison text: "85% of normal" with trend arrow (up/down)
- Mini 7-day sparkline chart

**Card 2: Heart Rate**
- Icon: heart
- Large value: "72 bpm"
- Status: "Normal range"
- Range text: "68-75 bpm typical"
- Mini sparkline

**Card 3: Sleep**
- Icon: moon
- Large value: "7.2 hours"
- Quality: "Good" (4 out of 5 stars)
- Comparison: "Better than average"
- Mini sparkline

**Recent Alerts (conditional — only shown if alerts exist):**
- Section heading: "Recent Alerts"
- Horizontal scrollable alert cards
- Each card: icon, title, time, status
- Tap any card → M1-11 or M1-12 Alert Detail

**Bottom:**
- Button: "View Trends & History" → M2-03 _(MVP 2)_

**Interactions:**
- Pull-to-refresh triggers data sync
- Swipe left on metric card → see detail view
- Long-press on photo → change photo option

**States:**
- **M1-09a — Loading:** Skeleton/shimmer cards
- **M1-09b — Normal:** Full data displayed
- **M1-09c — Stale data:** Banner: "Last update was X hours ago — pull down to check in"
- **M1-09d — No device connected:** Prompt card: "Connect [Name]'s device so CardiTrack can start watching over them" → M1-05
- **M1-09e — Baseline learning:** Shows progress bar instead of "% of normal" comparisons

**MVP 2 change:** When user has multiple CardiMembers, Home tab shows M3-03 instead

---

### M1-10: Alerts List
**User Story:** 3.1 Alert Management | 3.3 Alert Acknowledgment & Notes
**Entry:** Tab bar (Alerts) | ← M1-09 Dashboard (Recent Alerts)
**Exit:** → M1-11 Alert Detail (Activity) | → M1-12 Alert Detail (Critical) | → Phone call

**Header:**
- Title: "Alerts"
- Filter icon (funnel)
- Settings icon (gear) → M2-04 Notification Settings _(MVP 2)_

**Filter Chips (horizontal scroll):**
- [All] [Unread] [Critical] [Today] [This Week]

**Alert List (grouped by date):**

Section headers: "Today" / "Yesterday" / "This Week" / "Older"

**Alert Card Layout:**
- Left border colored by severity
- Top row:
  - Severity badge: "CRITICAL" / "URGENT" / "INFO"
  - Timestamp: "2 hours ago"
  - Unread dot (if unread)
- Content:
  - CardiMember name + small photo (inline)
  - Alert title (bold): e.g., "Low Activity Detected"
  - Preview text (2 lines max): "Dad hasn't moved this morning. He usually wakes up around..."
- Bottom row:
  - Status label: "New" / "Acknowledged" / "Resolved"
  - Quick action icons: Call (phone) | Acknowledge (checkmark) | Expand (chevron)

**Swipe Actions:**
- Swipe right → "Acknowledge"
- Swipe left → "Call"

**Bottom:**
- Link: "View Archived Alerts"

**States:**
- **M1-10a — Default:** Grouped alert list
- **M1-10b — Empty:** Large bell icon (muted) + "Nothing to worry about" + "CardiTrack is keeping an eye on things — we'll let you know if anything comes up"
- **M1-10c — Filtered empty:** "No alerts match this filter"
- **M1-10d — Loading:** Skeleton cards

Heart rate alerts tap → M1-16

---

### M1-11: Alert Detail - Activity
**User Story:** 11.1 Activity Decline | 3.3 Alert Acknowledgment & Notes
**Entry:** ← M1-10 Alerts List (tap alert card)
**Exit:** ← M1-10 Alerts List (back) | → Phone call | → SMS | → M2-03 Trend Charts

**Header:**
- Back button
- Title: "Alert Details"
- Share button

**Alert Header Card:**
- Caution-level severity banner
- Warning icon
- Title: "Low Activity Alert"
- CardiMember photo + name
- Timestamp: "January 10, 2026 at 11:30 AM"

**Description:**
- Card with icon
- Large readable text: "Dad hasn't been as active as usual lately"

**Mini Trend Chart:**
- 2-week activity trend showing declining line
- Baseline range shaded, current data overlaid

**Comparison Card (2-column grid):**

| Current | Normal |
|---------|--------|
| "Recent Average" | "Normal Average" |
| 2,500 steps/day | 5,000 steps/day |

- Full-width highlighted row: "-50% below normal"

**Context Card:**
- Lightbulb icon
- "Here's what might be going on:"
  - They could be feeling under the weather
  - They might be in pain or uncomfortable
  - They may be feeling low or tired

**Recommended Actions (full-width button list):**
1. "Give Dad a Call" (primary, phone icon)
2. "Send a Quick Message" (secondary, SMS icon)
3. "Book a Doctor Visit" (secondary, calendar icon)

**More Options (collapsible):**
- "Adjust Baseline" (if this is a new normal)
- "Add Note About This Alert"
- "Share with Family"

**Acknowledgment Section:**
- If unread: Button "Mark as Acknowledged"
- If acknowledged: "Acknowledged by Sarah, 30 min ago" + any notes

**Bottom:**
- Button: "View Detailed Activity Data" → M2-03 _(MVP 2)_

---

### M1-12: Alert Detail - Critical (No Movement)
**User Story:** 11.3 No Morning Activity | 3.3 Alert Acknowledgment & Notes
**Entry:** ← M1-10 Alerts List | Push notification (direct)
**Exit:** ← M1-10 Alerts List (back) | → Phone call | → Note input

This is the most safety-critical screen in the app. Design for urgency and immediate action.

**Alert Header:**
- Full-width critical severity banner (pulsing animation)
- Large siren icon
- Title: "We haven't seen Dad move today"
- CardiMember photo + name
- Timestamp

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
1. **"CALL NOW"** — critical severity, oversized, phone icon, one-tap to dial, shows phone number
2. **"I'M ON MY WAY"** — urgent severity, large — updates status and lets your family know

**Dismissal Option:**
- Button: "It's okay — he told me he'd sleep in"
  - Opens a note field for context
  - Dismisses alert with explanation logged

**Family Notification Card:**
- "Your family has been notified too:"
  - Sarah (via SMS) — timestamp
  - John (via Push) — timestamp

**Event Timeline:**
- Vertical timeline:
  - 10:30 PM — Last movement
  - 7:00 AM — Expected wake time
  - 9:00 AM — Alert threshold reached
  - 11:30 AM — You were notified

---

### M1-13: CardiMember Detail
**User Story:** 1.4 CardiMember Profile Management
**Entry:** ← M1-09 Dashboard ("View Details") | ← M2-01 Settings ("Manage CardiMembers")
**Exit:** ← Previous screen (back) | → M1-14 Edit CardiMember | → M1-09 Dashboard | → M1-10 Alerts

**Profile Section (centered):**
- Large photo (prominent, centered)
- Name (large text)
- Age & relationship: "78 years old - Dad"

**Contact Info Card:**
- Emergency contact: name, phone (tappable to call), relationship

**Medical Info Card (encrypted):**
- Lock icon in card header
- Collapsible: "Medical Notes"
- Viewing requires biometric authentication
- "Edit" also requires biometric auth

**Monitoring Info Card:**
- Connected devices: "2 devices"
- Monitoring since: "Jan 1, 2026"
- Baseline status: "Learning (15 days)" or "Established"

**Action Buttons:**
- "View Dashboard" → M1-09
- "View Alerts" → M1-10
- "Manage Devices" → M1-15

**Danger Zone (separated):**
- "Pause Monitoring" button (warning treatment)
- "Remove CardiMember" button (destructive treatment)

---

### M1-14: Edit CardiMember
**User Story:** 1.4 CardiMember Profile Management
**Entry:** ← M1-13 CardiMember Detail (edit button)
**Exit:** ← M1-13 CardiMember Detail (cancel or save)

**Header:**
- Cancel button
- Title: "Edit [Name]"
- Save button (enabled when changes exist)

**Form (scrollable):**

**Photo:** Large circular image + "Change Photo" button

**Basic Info:**
- "Full Name" — text input
- "Date of Birth" — date picker
- "Relationship" — dropdown picker

**Optional Info:**
- "Medical Notes" — multi-line (encrypted)
- "Emergency Contact Name" — text input
- "Emergency Contact Phone" — phone input

**Monitoring Preferences:**
- Toggle: "Enable Monitoring"
- Dropdown: "Alert Sensitivity" — Low / Medium / High

**CTA:**
- Primary button: "Save Changes"

**Behavior:**
- Tracks unsaved changes
- "Unsaved changes" warning if navigating away without saving

---

### M1-15: Device Management
**User Story:** 6.2 Devices
**Entry:** ← M2-01 Settings ("Connected Devices") | ← M1-13 CardiMember Detail ("Manage Devices")
**Exit:** ← Previous screen (back) | → M1-05 Device Selection ("Add Device")

**Header:**
- Back button
- Title: "Connected Devices"
- "+ Add Device" button

**Devices List (grouped by CardiMember):**

**Group Header:** CardiMember name + photo

**Device Card:**
- Device logo (small, left)
- Device info:
  - Name: "Dad's Fitbit Charge 5"
  - Status badge:
    - Normal: "Active" (synced 10m ago)
    - Caution: "Token Expiring Soon"
    - Critical: "Disconnected"
  - Data sources: "Activity, HR, Sleep"
  - Primary device star (if designated)
- Menu icon (three dots)

**Context Menu:**
- Refresh Connection
- Set as Primary (toggle)
- View Sync History
- Remove Device (destructive text)

**Expanded Detail (tap card to expand):**
- Last sync: "10 minutes ago"
- Next sync: "In 20 minutes"
- Data synced today: "4 updates"
- Battery: "75%" (if available from device)

**Troubleshooting (bottom, collapsible):**
- "Having trouble?"
  - Make sure Bluetooth is on
  - Try reconnecting the device
  - We're here to help — contact support

---

### M1-16: Alert Detail - Heart Rate
**User Story:** 11.2 Elevated HR
**Entry:** ← M1-10 Alerts List
**Exit:** ← M1-10 Alerts List (back) | → Phone call | → M2-03 Trend Charts

**Alert Header:**
- Urgent severity banner
- Lightning bolt icon
- Title: "Elevated Heart Rate Alert"
- CardiMember photo + name + timestamp

**Description:**
- "Mom's heart rate has been running higher than usual for the past 3 days"

**Chart:**
- 7-day heart rate chart
- Shaded normal range (68-75 bpm)
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
- "Related Health Info"
- Shows medications, conditions from CardiMember profile

---

### M1-17: Health Data Export
**User Story:** 6.3 Health Data Export
**Entry:** ← M2-03 Trend Charts (Export icon) | ← M2-01 Settings ("Export Health Data") | ← M1-13 CardiMember Detail ("Export Data")
**Exit:** ← Previous screen (back) | → Share sheet / email

**Header:**
- Back button
- Title: "Export Health Data"

**CardiMember Selector:**
- Dropdown: "Export data for: [Dad]"

**Date Range:**
- "From" date picker
- "To" date picker
- Quick presets: [Last 7 Days] [Last 30 Days] [Last 90 Days] [All Data]

**Data Selection (checkboxes):**
- Activity & Steps
- Heart Rate
- Sleep Data
- Alerts & Events
- Notes (if any)

**Export Format (radio buttons):**

| Format | Description | Use Case | Available |
|--------|-------------|----------|-----------|
| PDF Report | Human-readable summary with charts | Sharing with family or personal records | **MVP 1** |
| CSV | Raw data spreadsheet | Personal analysis | **MVP 1** |
| FHIR (R4) | Fast Healthcare Interoperability Resources | Modern EHR integration, patient portals | **MVP 1** |
| HL7 v2 | Health Level Seven messaging format | Hospital system integration | MVP 2 |

**Format Info (expandable per format):**
- Tap info icon next to FHIR → explains format, typical recipients (hospitals, clinics, patient portals)

**Delivery Method:**
- "Save to Device" (default)
- "Email to..." — email input with autocomplete (self, doctor, family)
- "Share via..." — opens native share sheet

**Preview Section:**
- "Preview Export" button — shows first page / sample of export
- Estimated file size: "~2.4 MB"

**CTA:**
- Primary button: "Export Data"
- Progress: "Generating export..." with progress bar
- Success: "Export complete!" with option to share or save

**States:**
- **M1-17a — Default:** Format and date selection
- **M1-17b — Generating:** Progress bar with cancel option
- **M1-17c — Complete:** Success message with share/save actions
- **M1-17d — Error:** "That didn't work — let's try again" with retry

---

## MVP 1 Asset Inventory

### Illustrations (Storyset / Blush or custom)

| # | Asset | Screen | Description |
|---|-------|--------|-------------|
| 1 | Onboarding Slide 1 | M1-02 | Happy elderly person wearing a smartwatch — warm, reassuring tone |
| 2 | Onboarding Slide 2 | M1-02 | Phone showing a health dashboard — conveys "at-a-glance monitoring" |
| 3 | Onboarding Slide 3 | M1-02 | Family members on their phones — conveys shared caregiving |
| 4 | Learning Phase | M1-08 | Brain with gears concept. Animated (Lottie). Source from LottieFiles or use static illustration + platform progress animation |
| 5 | Empty Alerts | M1-10 | Muted bell or peaceful scene — "nothing to worry about" feeling |
| 6 | No Device Connected | M1-09 | Prompt to connect a device — friendly nudge, not an error |

**Style guidance:** All 6 illustrations must use the same art style and brand color palette. Tone: warm, caring, approachable — not clinical.

### Brand Assets (custom — must be unique)

| # | Asset | Used On | Notes |
|---|-------|---------|-------|
| 1 | CardiTrack logo | M1-01, M1-02, M1-06 | Export at multiple sizes: large (splash), small (header). SVG master. |
| 2 | App icon | Home screen, app stores | Must work at all OS-required sizes. Follow Apple and Google icon guidelines. |

### Animations

| # | Animation | Screen | Source |
|---|-----------|--------|--------|
| 1 | Success checkmark | M1-07 | LottieFiles — search "success checkmark" (free options available) |
| 2 | Shimmer / skeleton loading | M1-09, M1-10 | Open-source library (e.g., Shimmer.Maui). Reusable across all screens. |
| 3 | Critical alert pulse | M1-12 | XAML/CSS animation — opacity + scale loop on severity banner |
| 4 | Learning phase brain/gears | M1-08 | LottieFiles — search "machine learning" or "brain processing." |

### Third-Party Logos (vendor-provided, no design needed)

| # | Logo | Screen | Source |
|---|------|--------|--------|
| 1 | Google | M1-03 | Google Identity branding guidelines (SVG provided) |
| 2 | Apple | M1-03 | Apple Sign In SDK (renders automatically) |
| 3 | Fitbit | M1-05, M1-06, M1-15 | Fitbit/Google developer brand assets |
| 4 | Apple Watch | M1-05, M1-06, M1-15 | Apple marketing assets (MFi partners) |
| 5 | Garmin | M1-05, M1-06, M1-15 | Garmin Connect developer program |
| 6 | Samsung | M1-05, M1-06, M1-15 | Samsung developer brand kit |
| 7 | Withings | M1-05, M1-06, M1-15 | Withings Health API partner assets |

### Asset Summary

| Category | Count |
|----------|-------|
| Custom illustrations | 6 |
| Brand assets (logo + icon) | 2 |
| Animations (Lottie / XAML) | 4 |
| Third-party logos | 7 |
| **Total** | **19** |

**Truly custom** (must be designed): **2** — CardiTrack logo and app icon.

---

**Source:** Extracted from [ui_screens_maui_mobile.md](../ui_screens_maui_mobile.md) v3.0
**Total MVP 1 Screens:** 17 screens · 37 states

# CardiTrack - MVP 1 Screen Specifications

> **Extract — do not edit directly.** This file is extracted from the canonical [ui_screens_maui_mobile.md](../ui_screens_maui_mobile.md) (v3.1); make changes there and re-extract. Release sequencing is governed by the [release matrix](../../../../release_matrix.md).

## Project Overview

**Product:** CardiTrack - Remote health monitoring for elderly family members
**Release:** MVP 1 — Core Monitoring (R1, Q4 2026) — 17 designed screens / 37 designed states; **13 of 17 built** as of August 9, 2026
**Platform:** iOS 16+ (iPhone 12+) & Android 10+ (API 29)
**Minimum OS:** iOS 16.0 · Android 10 (API level 29)
**Target OS:** iOS 18 · Android 15 (API level 35)
**Orientation:** Portrait primary, landscape supported
**Target Users:** Family caregivers across the US & EU monitoring elderly relatives' wearable health data
**Document Version:** 1.1 (extracted from full spec v3.1)
**Last Updated:** August 7, 2026

---

## Build Status (as of August 9, 2026)

> **13 of 17 Figma M1 screens are built** in `CardiTrack.Mobile` (M1-01 through M1-10, plus M1-13 CardiMemberDetailPage, M1-14 EditCardiMemberPage, M1-15 DeviceManagementPage). **The unbuilt screens are alert detail and export** — M1-11/M1-12/M1-16 Alert Details and M1-17 Health Data Export; their entry points show "Coming soon" dialogs, except the dashboard's Add-Member action, which pushes M1-04 (AddCardiMemberPage) directly. **Four shipped screens have no Figma M1 frame — needs design sync:** SignInPage, ForgotPasswordPage, VerifyEmailPage, Onboarding/AccountSetupPage (specs in the canonical doc). Unbuilt screens below remain design intent, each marked with a status line.

---

## What MVP 1 Delivers

A single user can sign up, add a CardiMember, connect devices, manage CardiMember profiles, view the health dashboard, receive and manage all alert types, and export health data in PDF, CSV, or FHIR R4 format. This is the essential monitoring loop — everything needed for the app to be useful from day one. MVP 1 monitors **one CardiMember** — the multi-member dashboard (M3-03) is an MVP 2 feature, and the shipped dashboard displays only the first active member.

---

## Screen Index

| ID | Screen | Variations | Built |
|----|--------|------------|-------|
| M1-01 | Splash Screen | 2 (a–b) | ✅ `SplashPage` |
| M1-02 | Welcome / Landing | 1 | ✅ `WelcomePage` |
| M1-03 | Sign Up | 4 (a–d) | ✅ `CreateAccountPage` |
| M1-04 | Add First CardiMember | 3 (a–c) | ✅ `AddCardiMemberPage` |
| M1-05 | Device Connection - Selection | 1 | ✅ `DeviceSelectionPage` |
| M1-06 | Device Connection - OAuth | 3 (a–c) | ✅ `FitbitConnectionPage` |
| M1-07 | Device Connection - Success | 3 (a–c) | ✅ `ConnectionSuccessPage` |
| M1-08 | Baseline Learning Info | 1 | ✅ `BaselineLearningPage` |
| M1-09 | Main Dashboard | 5 (a–e) + 2 as-built | ✅ `DashboardPage` |
| M1-10 | Alerts List | 4 (a–d) | ✅ `AlertsPage` |
| M1-11 | Alert Detail - Activity | 1 | ❌ not built |
| M1-12 | Alert Detail - Critical | 1 | ❌ not built |
| M1-13 | CardiMember Detail | 1 + 3 as-built | ✅ `CardiMemberDetailPage` |
| M1-14 | Edit CardiMember | 1 + 2 as-built | ✅ `EditCardiMemberPage` |
| M1-15 | Device Management | 1 + 3 as-built | ✅ `DeviceManagementPage` |
| M1-16 | Alert Detail - Heart Rate | 1 | ❌ not built |
| M1-17 | Health Data Export | 4 (a–d) | ❌ not built |

**Total: 17 designed screens · 37 designed states — 13 of 17 built**

**Shipped screens without Figma M1 frames** (need design sync; no M1 IDs assigned per project convention): SignInPage, ForgotPasswordPage, VerifyEmailPage, Onboarding/AccountSetupPage — see the canonical [ui_screens_maui_mobile.md](../ui_screens_maui_mobile.md) for full specs.

---

## User Flows

### Flow 1: First-Time Onboarding

As built: Welcome's only affordances lead to Sign Up; SignInPage is reached from CreateAccount's "Already have an account? Sign In" link. Email verification (VerifyEmailPage) gates entry, then PostLoginRouter routes to AccountSetupPage (if account type not yet set) or AddCardiMemberPage. Onboarding pages hide the tab bar (`Shell.TabBarIsVisible=False`).

```
[M1-01 Splash] ──────────────────────────────────────────> [M1-09 Dashboard]
      │                                                      (returning user)
      ▼
[M1-02 Welcome]
      │              │
"Start Free Trial" "Sign up" (top-right — same destination)
      ▼
[M1-03 Sign Up (CreateAccountPage)]
      │                        │
      │              "Already have an account? Sign In"
      │                        ▼
      │                  [SignInPage] ───────────────────> [M1-09 Dashboard]
      ▼                  (no Figma M1 frame)
[VerifyEmailPage]  (email verification gate — no Figma M1 frame)
      │
      ▼
[PostLoginRouter]
      │                        │
 account type not set     account type set
      ▼                        │
[AccountSetupPage]             │
 (no Figma M1 frame)           │
      └──────────┬─────────────┘
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
   │                   │
"Go to Dashboard"   "Invite Family Member First"
   │                   └──> [M3-02 Invite Family Modal] (MVP 2 —
   ▼                        link ships in MVP 1 but is a dead end today)
[M1-09 Dashboard]
```

The dashboard's Add-Member action (empty state and add-member button) pushes M1-04 directly; when pushed from the dashboard, "Skip" pops back to the dashboard.

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
                                  │
                         "View Trends" ──> [M2-03 Trend Charts] _(MVP 2)_
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
- As built, the Shell defines a **TabBar only** (Dashboard / Alerts / Family / Settings, SVG icons). Alerts opens the real M1-10 list; the Family tab is still a stub; Settings is minimal (account card, "More settings (M2-01) coming soon", Sign out). Onboarding pages hide the tab bar via `Shell.TabBarIsVisible=False`.

### Flyout Menu

**Not built — there is no flyout menu in the shipped app.** The Shell is TabBar-only, with no edge-swipe flyout. The flyout concept below is retired unless re-introduced through Figma:

- ~~User profile header, Dashboard, CardiMembers, Alerts, Family & Sharing, Subscription, Settings, Help & Support, Sign Out~~

### Gesture Patterns

| Gesture | Context | Action | Status |
|---------|---------|--------|--------|
| Pull down | Dashboard (and future scrollable screens) | Refresh data | **Shipped** (Dashboard) |
| Swipe left | List items | Reveal quick actions | Planned (no list-item swipes shipped) |
| Swipe right | List items | Reveal secondary actions | Planned (no list-item swipes shipped) |
| Pinch | Chart views | Zoom in/out | Planned (no chart gestures shipped) |
| Long press | Chart data points | Show tooltip | Planned (no chart gestures shipped) |
| Long press | CardiMember photo | Change photo | Planned (not shipped) |

There is no edge-swipe flyout gesture (no flyout exists).

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

These five screens span onboarding → daily use → emergency response. **Build status:** M1-02, M1-04, M1-09, and M1-10 are built; M1-12 is not built — so the POC set currently validates onboarding, daily monitoring, and alert management in code, but not yet the emergency-response design language.

---

## Screens

### M1-01: Splash Screen
**Status:** Built (`SplashPage`)
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
**Status:** Built (`WelcomePage`)
**User Story:** 1.1 First-Time Registration
**Entry:** ← M1-01 Splash (first launch only)
**Exit:** → M1-03 Sign Up (primary CTA and top-right "Sign up" — same destination). There is **no Sign In affordance on Welcome**; sign-in is reached via CreateAccount's "Already have an account? Sign In" link.

**Layout:**

**Header (top 20%):**
- CardiTrack logo (top-left, small)
- "Sign up" label (top-right) → M1-03 (same destination as primary CTA)

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
- Carousel is **swipe-only** (`Loop=False`, no auto-advance)
- Pagination dots indicate current slide
- Swipe left/right between slides
- Terms & Privacy link is a **stub** — shows a placeholder alert ("Terms and privacy will open here.")

---

### M1-03: Sign Up Screen
**Status:** Built (`CreateAccountPage`)
**User Story:** 1.1 Account Creation
**Entry:** ← M1-02 Welcome ("Start Free Trial" or "Sign up")
**Exit:** → VerifyEmailPage (success; email verification gate) → PostLoginRouter → AccountSetupPage or M1-04 Add CardiMember | → SignInPage ("Already have an account? Sign In")

**Header:**
- Gradient header (no back button)
- Title: "Create Account"
- Progress indicator: "Step 1 of 4"

**Form (scrollable):**

- **First field is required "Full Name"** — text input
- Label: "Email Address"
  - Text input, email keyboard, autocapitalization off
- Label: "Password"
  - Password input (masked)
  - Password strength bar below (weak → medium → strong)
  - Strength label: "Weak" / "Medium" / "Strong"
- Label: "Confirm Password"
  - Password input (masked)

**Terms:**
- Checkbox + label: "I agree to Terms of Service and Privacy Policy"
- "Terms of Service" and "Privacy Policy" are tappable links

**CTA:**
- Primary button: "Create Account" — **never disabled**; tapping while invalid runs validation and shows inline errors; the button's gradient swaps to the active treatment when the form becomes valid
- Error banner area (reserved for weak-password, network, and generic errors)

**Divider (below CTA):** Horizontal line with "Or continue with" centered

**Social Login (2-up icon grid):**
- "Google" card (white background, Google logo)
- "Apple" card (dark background, Apple logo)
- **Unwired** — the social buttons have no tap handlers yet

**Bottom:**
- Link: "Already have an account? Sign In" → SignInPage

**Validation Rules:**
- Full Name: required
- Email: valid format, real-time feedback
- Password: min 8 characters, 1 uppercase, 1 number
- Confirm password: must match
- Terms checkbox: must be checked

**States:**
- **M1-03a — Default:** Empty form
- **M1-03b — Validating:** Inline error messages appear beneath invalid fields
- **M1-03c — Loading:** Button shows spinner, form disabled
- **M1-03d — Error:** **Duplicate email shows as an inline field error under Email**; the top error banner is reserved for weak-password, network, and generic failures

**Post-signup:** Success pushes **VerifyEmailPage** (the email-verification gate); after verification, PostLoginRouter routes to AccountSetupPage (account type not yet set) or M1-04 AddCardiMemberPage.

---

### M1-04: Add First CardiMember
**Status:** Built (`Onboarding/AddCardiMemberPage`)
**User Story:** 1.2 Adding First CardiMember
**Entry:** ← M1-03 Sign Up (success, after VerifyEmailPage/PostLoginRouter) | ← M1-09 Dashboard (Add-Member action pushes this page directly)
**Exit:** → M1-05 Device Selection ("Continue") | → M1-09 Dashboard ("Skip for Now"; when pushed from the dashboard, Skip pops back to it)

**Header:**
- Title: "Add First CardiMember"
- Progress indicator: "Step 2 of 4"

**Introduction:**
- Icon: person silhouette
- Text: "Who would you like to look after?"
- Subtext: "Tell us about your loved one — we'll take it from there"

**Photo Section:**
- Circular photo placeholder (large)
- "Add Photo" button below
- **Gallery only** (`MediaPicker.PickPhotosAsync`) — no camera option shipped

**Required Fields:**
- "Full Name *" — text input
- "Date of Birth *" — date picker (format: MM/DD/YYYY)
  - As built, **DOB silently defaults to today** if not changed — not validated (known limitation)
- "Relationship *" — dropdown picker:
  - Parent, Grandparent, Spouse, Sibling, Other

**Optional Fields (collapsible section):**
- Toggle: "Add More Details (Optional)"
- When expanded:
  - "Medical Notes" — multi-line text (max 500 chars, character counter shown)
    - Encrypted indicator icon visible
  - "Emergency Contact Name" — text input
  - "Emergency Contact Phone" — phone keyboard input
    - Placeholder is localized by device region (PR #8): US/CA "+1 555 000 0000", GB "+44 7700 900000"; **all other regions fall back to the US format** — worth noting given the product's US + EU framing

**Privacy Notice:**
- ~~Info card with lock icon: "[Name] will know you're looking out for them and can give their okay"~~
- **Not built** — the shipped screen has no privacy-notice card (product follow-up, relevant to the consent-first principle)

**CTA:**
- Primary button: "Continue" — enabled by **name ≥ 2 characters + relationship selected** only
- Text link: "Skip for Now"

**States:**
- **M1-04a — Default:** Empty form with photo placeholder
- **M1-04b — Photo added:** Shows uploaded image in circle
- **M1-04c — Loading:** Button shows spinner on submit

---

### M1-05: Device Connection - Selection
**Status:** Built (`Onboarding/DeviceSelectionPage`)
**User Story:** 1.3 Device Connection Wizard
**Entry:** ← M1-04 Add CardiMember ("Continue")
**Exit:** → M1-06 OAuth Permission ("Continue with Fitbit")

**Header:**
- Back button
- Title: "Connect Device"
- Progress indicator: "Step 3 of 4"

**Introduction:**
- Heading: "What do they wear?" (no name interpolation)
- Subtext: "We'll connect with their device to keep you in the loop"

**Device Grid (fixed 2-column grid):**

Each device card:
- Rounded frame with shadow
- Device logo (medium)
- Device name (bold)
- "Coming Soon" badge for future devices
- Coming Soon cards render at **0.55 opacity** and are non-tappable

**Supported Devices (MVP 1 — Fitbit only; remaining devices shown as Coming Soon):**

| Device | Card text (models) | MVP Availability |
|--------|--------------------|-----------------|
| Fitbit | Charge, Versa, Sense series | **MVP 1** |
| Garmin | Venu, Forerunner, etc. | MVP 2 (shown Coming Soon) |
| Apple Watch | Series 4+ | Coming Soon |
| Samsung Galaxy | All models | Coming Soon |
| Withings | ScanWatch, Move | Coming Soon |
| Other | All models | Coming Soon |

**Bottom:**
- Link: "Don't see their device? We can help"

**Interactions:**
- **Fitbit is preselected** — it cannot be deselected, and there is no auto-advance
- Explicit primary button: **"Continue with Fitbit"** proceeds to M1-06
- Coming Soon cards are greyed out (0.55 opacity) with "Coming Soon" badges

---

### M1-06: Device Connection - OAuth Permission
**Status:** Built (`Onboarding/FitbitConnectionPage`) — Fitbit-only, hardcoded copy
**User Story:** 1.3 OAuth Flow
**Entry:** ← M1-05 Device Selection ("Continue with Fitbit")
**Exit:** → M1-07 Success (authorization complete) | ← M1-05 Device Selection ("Cancel")

**Header:**
- Back button
- Title: hardcoded "Fitbit Connection"

**Visual Connection (centered):**
- Heading: "Connect Your Fitbit"
- Large device logo
- Arrow/connection icon
- Large CardiTrack logo

**Permission List:**
- Label: "To look after them, CardiTrack needs:" (no name interpolation)
- Each permission in its own row:

| Icon | Permission | Info Tooltip |
|------|-----------|-------------|
| Heart | Heart Rate Data | "So we can spot if something's off" |
| Shoe | Activity & Steps | "To make sure they're staying active" |
| Moon | Sleep Data | "To know they're resting well" |

- Each row has an (i) info button that shows the tooltip on tap

**Privacy Notice:**
- Card with light background
- Lock icon + text: "Your family's health data stays private always. We never sell or share it with third parties."

**CTA:**
- Primary button: "Authorize [Device Name]"
  - Tap opens device's OAuth login in a browser/webview
- Text link: "Cancel"

**States:**
- **M1-06a — Default:** Permission list visible
- **M1-06b — Authorizing:** Loading overlay with "Connecting to [Name]'s [Device]..." message
- **M1-06c — Error:** "We couldn't connect — let's try that again" with retry button

> **State-letter mismatch:** the shipped code maps **B = Error** and **C = Authorizing overlay** — the reverse of the lettering above. Figma is the arbiter of state letters; the code should be realigned (or the Figma frame updated) at the next design sync.

---

### M1-07: Device Connection - Success
**Status:** Built (`Onboarding/ConnectionSuccessPage`)
**User Story:** 1.3 Connection Success
**Entry:** ← M1-06 OAuth (authorization complete)
**Exit:** → M1-08 Baseline Info ("Continue to Dashboard", first device only) | → wizard exit ("Done", additional device) | → M1-05 Device Selection ("Add Another Device")

**Checkmark:**
- **Static green circle with ✓** (no entry animation shipped)

**Success Message:**
- Heading: "You're all set!"
- Text: "The Fitbit is now connected" (no name/device interpolation)
- Subtext: "We're pulling in their latest data — just a moment"

**Data Preview Card:**
- Title: "Latest Data" with **"SYNCING" badge**
- Rows:
  - Steps Today: 4,250
  - Last Synced: Just now
  - Heart Rate: 72 bpm

**Options:**
- Outlined button: "+ Add Another Device"

**CTA:**
- Primary button: "Continue to Dashboard" — connecting the member's **first** device; continues to M1-08
- Primary button: **"Done"** — connecting an **additional** device (launched from M1-15 for a member who already had one); exits the wizard straight back to where it was launched from. M1-08 is the 30-day learning story, which is news once per member, not once per device; the label changes with it because this exit does not land on the dashboard.
- Which of the two shows is fixed when the wizard opens, by whether the member had a device then — so a run that starts from none and connects two via "Add Another Device" still ends on M1-08.
- Helper text **below the button**: "You can sync multiple devices to get a more accurate picture of their health."

**States:**
- **M1-07a — Syncing:** Preview card shows shimmer/skeleton loading
- **M1-07b — Synced:** Preview card shows real data
- **M1-07c — Partial sync:** Some values show, others show "Syncing..."

---

### M1-08: Baseline Learning Info
**Status:** Built (`Onboarding/BaselineLearningPage`)
**User Story:** 1.3 Baseline Setup
**Entry:** ← M1-07 Device Success — **first connected device only**; skipped when an additional device is added (see M1-07 CTA)
**Exit:** → M1-09 Dashboard ("Go to Dashboard") | "Invite Family Member First" link (ships now; dead end until M3-02)

**Header:**
- Title: "Learning Phase"
- Progress indicator: "Step 4 of 4"

**Illustration:**
- Static emoji glyphs: 🧠⚙ (no Lottie animation shipped)

**Explanation:**
- Heading: "Getting to know them" (singular, no name interpolation)
- Body: "Over the next 30 days, CardiTrack will learn what a normal day looks like for them:"
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
- Text link: "Invite Family Member First" — **ships now in MVP 1** (unconditional), but the invite flow (M3-02) does not exist yet, so the link is currently a dead end. Not an MVP 2 addition.

---

### M1-09: Main Dashboard (Single CardiMember)
**Status:** Built (`DashboardPage`)
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
| Paused | "Monitoring is paused for [Name]" | Pause |

- Last synced: "Updated 10 minutes ago"
- Tap sync icon for manual refresh
- **Tap the card body → M1-13 CardiMember Detail**
- The Paused row is outside the green/yellow/orange/red severity scale on purpose: it says "we are not watching", not "we looked and it's fine"

**Quick Actions Row (3 horizontal buttons):**
- "Call [Name]" (phone icon) → initiates phone call
- "Send Message" (SMS icon) → opens SMS
- "View Details" (chart icon) → navigates to M1-13 (as does tapping the hero card)

**Key Metrics (3 cards in a row):**

**Star rating (1-5)** appears on every card that has something to compare against (Activity, Heart Rate, Sleep, Skin Temp): how the reading sits against this member's own normal, at finer resolution than the status colour and never contradicting it. SpO2 and Breathing Rate have no baseline yet, so their star row stays hidden rather than rating a reading against an invented normal. See `qualityScore` in [health-data.md](../../../backend/api/health-data.md).

**Card 1: Activity**
- Icon: shoe
- Large value: "4,250 steps"
- Visual progress bar (current vs. goal)
- Comparison text: "85% of normal" with trend arrow (up/down)
- Star rating (1-5) — the shortfall against normal; walking further than usual is not marked down
- Mini 7-day sparkline chart

**Card 2: Heart Rate**
- Icon: heart
- Large value: "72 bpm"
- Status: "Normal range"
- Star rating (1-5) — deviation from normal in either direction
- Range text: "68-75 bpm typical"
- Mini sparkline

**Card 3: Sleep**
- Icon: moon
- Large value: "7.2 hours"
- Star rating (1-5) — sleep efficiency, or the shortfall in duration when the device reports none
- Comparison: "Better than average"
- Mini sparkline

**Recent Alerts (conditional — only shown if alerts exist):**
- Section heading: "Recent Alerts"
- Horizontal scrollable alert cards
- Each card: icon, title, time, status
- Tap any card → M1-11 or M1-12 Alert Detail

**Verify-Email Nudge (conditional):**
- Dismissible banner prompting the user to verify their email address

**Bottom:**
- Button: "View Trends & History" → M2-03 _(MVP 2)_

**Interactions:**
- Pull-to-refresh triggers data sync; manual refresh icon sits in the page **header**
- ~~Swipe left on metric card → see detail view~~ (not shipped — no metric-card swipe gesture)
- ~~Long-press on photo → change photo option~~ (not shipped — no photo long-press gesture)

**States (8 as built):**
- **M1-09a — Loading:** Skeleton/shimmer cards
- **M1-09b — Normal:** Full data displayed
- **M1-09c — Stale data / offline:** Cached data with banner: "Last update was X hours ago — pull down to check in". **Suppressed while monitoring is paused** — the data is meant to be stale then, and "pull down to check in" is advice the app can't honour.
- **Monitoring paused:** amber banner naming the resume time; hero shows the paused status
- **M1-09d — No device connected:** Prompt card: "Connect [Name]'s device so CardiTrack can start watching over them" → M1-05
- **M1-09e — Baseline learning:** Shows progress bar instead of "% of normal" comparisons
- **Refresh / sync-error:** RefreshView in-flight state, plus sync-error surface
- **Error:** "We couldn't load the dashboard" + "Try again" button
- **No CardiMember:** "Who are you watching over?" + "Add CardiMember" button → pushes M1-04 (AddCardiMemberPage) directly; its Skip pops back here

**Single-member only:** the dashboard shows the **first `IsActive` CardiMember** (cached in Preferences); there is no member switcher. Multi-member is deferred to M3-03 (MVP 2).

---

### M1-10: Alerts List
**Status:** Built (`AlertsPage`, with `FilterChipBar` and `AlertListCard`)
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

**As built** — backed by `GET /api/v1/alerts` and `POST /api/v1/alerts/{id}/acknowledge` (see [alerts.md](../../../backend/api/alerts.md)), listing every CardiMember the caregiver may read, newest first. Differences from the frames, each deliberate:

- **Header is as drawn** — back arrow, title, filter button. Alerts is a tab root, so the arrow goes to M1-09 rather than popping a stack that isn't there. The filter button offers the same five filters as a sheet, which is why M1-10b keeps it while dropping the chip row.
- **Chips are M1-10a's set in M1-10c's styling.** The frames disagree — M1-10a has [All] [Unread] [Critical] [Today] [This Week] as plain pills, M1-10c has [Recent] [High Priority] [Heart Rate] [Oxygen] with dropdown carets. The set is M1-10a's (the one this spec documents, and the one every chip can actually filter — there is no SpO2 alert type); the pill, caret and spacing are M1-10c's.
- **Loading is built as drawn** — the "Syncing with Device… / Refresh Now" card over four structured skeleton rows (`AlertSkeletonCard`, whose shimmer blocks sit where the avatar, badge, title and status pill will land). Refresh Now supersedes the in-flight request rather than being swallowed by it, so the button works in the one state it appears in.
- **Severity badges are severity-coloured.** Wording follows this spec (CRITICAL / URGENT / INFO); the colour follows the app's own scale, so a yellow alert can't show a yellow rail beside Figma's blue "Info" chip.
- **The chevron expands the card in place.** M1-11 / M1-12 / M1-16 are not built, so a row that pushed a detail screen would open nothing; expanding reveals the full message instead. This is the "Expand (chevron)" action above.
- **"View Archived Alerts" switches this list to resolved alerts** rather than pushing an archive screen, and flips back the same way. The chip row hides while archived — it is a different list, not a narrower one.
- **Avatars are initials.** No member photo storage exists yet; `cardiMemberPhotoUrl` is on the wire and the tile keeps its designed box.
- **Swipe actions are not implemented.** The card's inline Call and Acknowledge buttons cover both gestures.

---

### M1-11: Alert Detail - Activity
**Status:** Not built — design intent below
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
**Status:** Not built — design intent below
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
**Status:** Built (`CardiMemberDetailPage`)
**User Story:** 1.4 CardiMember Profile Management
**Entry:** ← M1-09 Dashboard (hero card tap **or** "View Details") | ← M2-01 Settings ("Manage CardiMembers") _(MVP 2)_
**Exit:** ← Previous screen (back) | → M1-14 Edit CardiMember | → M1-15 Device Management | → M1-09 Dashboard | → M1-10 Alerts

Backed by a single `GET /api/v1/cardimembers/{id}` round trip — see [cardimembers.md](../../../backend/api/cardimembers.md).

**Profile Section:**
- ~~Large photo~~ — **initials avatar as built.** No photo storage exists server-side (`PhotoUrl` is always null and there is no upload path), so a photo would have nothing to render.
- Name and "78 years old • Dad" (the *requesting caregiver's* relationship, not the first link on the member)

**Monitoring Info Card:**
- Connected devices: "2 Devices" · Monitoring since: "Jan 1, 2026"
- Baseline status: "Learning" / "Established" with a `daysCaptured`/`daysRequired` progress bar

**Key Metric Trends (carousel, one swipeable card per metric):**
- Activity, Heart Rate, Sleep, Skin Temp, Blood Oxygen and Breathing Rate, over a caregiver-chosen **7 / 14 / 30-day** window. The API always sends 30 days of `series`, so switching windows is a client-side slice rather than another round trip.
- Each chart draws the daily line over **the two things the reading is compared against**: a dashed rule at this member's own learned `baseline`, and a shaded band at the published `reference` range for an adult of this member's age (60–100 bpm AHA, 7–9 h NSF — 7–8 from age 65, 94–100 % WHO, 12–20 brpm WHO — see [health-data.md](../../../backend/api/health-data.md)). A key under the chart names both and quotes their numbers; steps and skin temp have no published range, so they show the baseline alone.
- Both are drawn in neutral ink, never the status accent — they are context, not a verdict. The band is **presentational only**: the card's pill and stars still read this member against their own normal, not against the population.
- The chart's axis makes room for them, but not at any price: where admitting the band or the baseline would flatten the readings into a straight line, the readings keep the scale, the band is drawn clipped to the plot edge and the baseline rule is dropped in favour of its number in the key.
- Fewer than two readings in the window shows "Not enough readings in this window yet." instead of an empty grid.

**Contact Info Card:**
- Emergency contact name and phone, with a call button (hidden when no number is stored)
- Falls back to "No emergency contact yet" rather than rendering an empty card

**Medical Info Card (encrypted):**
- Lock icon in card header; collapsible notes, collapsed by default
- Notes are **encrypted at rest** (AES-256-GCM) as of this screen — rows written before that are still readable
- Biometric gating of medical notes remains **deferred to R4** (per the [release matrix](../../../../release_matrix.md)) — not an MVP 1 requirement

**Action Buttons:**
- "View Dashboard" → M1-09 · "View Alerts" → M1-10 · "Manage Device" → M1-15

**Management (danger zone):**
- "Pause Monitoring" — bounded 1 hour to 7 days, chosen from an action sheet then confirmed. The row becomes "Resume Monitoring" while paused. A pause **stops data collection**, not just the display: the sync worker skips paused members, and the dashboard hero shows a distinct `paused` status rather than a health colour.
- "Remove CardiMember" — confirmed, then soft-deletes the member, their caregiver links and their device connections, discarding stored OAuth tokens. Health history is retained.

**Permissions:** editing, pausing and removing require the caller to be a **primary caregiver**; the edit button is hidden and the management rows explain themselves for view-only caregivers.

**States (4 as built):**
- **Loading:** skeleton cards
- **Loaded:** as above
- **Error:** "We couldn't load these details" with retry
- **Paused:** amber banner naming the resume time and reason

---

### M1-14: Edit CardiMember
**Status:** Built (`EditCardiMemberPage`)
**User Story:** 1.4 CardiMember Profile Management
**Entry:** ← M1-13 CardiMember Detail (edit button)
**Exit:** ← M1-13 CardiMember Detail (cancel or save)

Saves via `PUT /api/v1/cardimembers/{id}` — a full replacement, so clearing a field is expressed as an empty value rather than "leave it alone".

**Header:**
- Back/cancel button, title "Edit Profile", Save button

**Form (scrollable):**

**Photo:** initials avatar, live-updating as the name is typed. ~~"Change Photo" button~~ — **not shipped**: there is no photo upload path, so the button would do nothing.

**Basic Info:** "Full Name*", "Date of Birth*" (picker), "Relationship*" (picker)

**Medical & Emergency:** "Medical Notes" (multi-line, ≤2000 chars, encrypted at rest), "Emergency Contact Name", "Emergency Contact Number"

**Monitoring Preferences:**
- ~~Toggle: "Enable Monitoring"~~ — **not shipped here.** Monitoring is paused from M1-13's Management section, where it is time-bounded; an open-ended toggle on an edit form is how someone stops being monitored without anyone deciding to.
- Dropdown: "Alert Sensitivity" — Low / Medium / High. **Stored but not yet consumed**: alert generation lives in the unbuilt GCP pipeline (`docs/llm_design.md`), and the form says so under the field.

**Behavior:**
- Client-side validation mirrors the server's rules (name 2–100, age 18–120, phone format, notes ≤2000)
- Tracks unsaved changes and warns before discarding them on cancel

**States (3 as built):** loading skeleton · form · error with retry

---

### M1-15: Device Management
**Status:** Built (`DeviceManagementPage`)
**User Story:** 6.2 Devices
**Entry:** ← M1-13 CardiMember Detail ("Manage Device") | ← M2-01 Settings ("Connected Devices") _(MVP 2)_
**Exit:** ← Previous screen (back) | → M1-05 Device Selection ("+ Add Device")

**Header:** back button, "Connected Devices", "+ Add Device" (launches the M1-05..M1-07 wizard modally; the wizard returns here on success — it continues to M1-08 only when this member had no device yet)

**Devices List:** headed by the CardiMember's name; one card per connection.

**Device Card:**
- Provider logo, device name, status chip (`ACTIVE` / `NEEDS RECONNECT` / `DISCONNECTED`), "synced 10m ago"
- **Sharing row** — a "SHARING" caption, **one pill per dataset family**, and a chevron. Datasets are mapped from the granted OAuth scopes by `DeviceDatasets` (`CardiTrack.Mobile.Core`), then collapsed by family, so a fully-granted Fitbit reads `Activity 5` · `Heart 2` · `Sleep 2` · `Body 4` rather than thirteen pills. Pills are tinted by family (activity blue, heart rose, sleep purple, body teal, unrecognised grey) in the design's `Activity, HR, Sleep` order, Body and Other after.
  - **Pill count is bounded by the five families, not by the grant.** Every card is the same height whatever each device shares, so two devices can be compared down the column, and colour means one category rather than repeating across a block.
  - A family carrying a **single** dataset names that dataset instead — `Weight`, not `Body 1` — since the name costs the same width and says more. Families carrying several show the family name and the count.
  - **Tap the SHARING header** to expand one detail line per family — `Heart  Heart Rate · Resting HR` — which is where the individual reading names now live. The header is the only tap target: it is where the disclosure state is announced, so it has to be the element an assistive-technology activation reaches, and confining the gesture there keeps a tap on a pill or on the detail text from collapsing the panel out from under the reader. The chevron is hidden when every pill already names its one dataset. The expanded state is held by the page (`_expandedSharing`), so a pull-to-refresh or a device action does not snap it shut.
  - A connection sharing nothing shows a single **"Not sharing any data"** pill in the caution tint the `NEEDS RECONNECT` chip uses — it is a problem to fix, not a neutral fact.
  - The mapping is deliberately narrower than the scopes: `health_metrics_and_measurements` also covers HRV, which `FitbitApiClient` does not fetch, so it is not named. Scopes we don't recognise are humanised (`irregular_rhythm` → "Irregular Rhythm") rather than rendered as raw URIs — the pre-pill label printed the full `googleapis.com/auth/...` scope strings on the card — and share the grey `Other` pill.
  - SpO2, VO2 max, breathing rate and body temperature are named too, in the existing **Body** family — `FitbitApiClient` ingests all four under `health_metrics_and_measurements`, which is the same test every other name passes (issue #82). Under the family row the four cost **one pill between them** (none at all where `Weight` already put `Body` on the card) instead of four — which is the point of collapsing the row: the mapping can keep growing without the card growing with it.
  - **Still open:** those four have no **M1-09 Key Metrics card**. Each card needs a hand-authored icon and a Figma slot, and the Key Metrics section is specified as steps, heart rate and sleep — so adding cards is a design decision, not a mapping fix. Detailed per-metric history remains M2-03.
- Primary device star when designated
- ~~Menu icon (three dots)~~ — **actions are inline** rather than behind a menu, matching the Figma frame

**Permissions:** the three actions below require a **primary caregiver**; a view-only caregiver can see the list but not change it.

**Actions (inline on each card):**
- **Refresh Connection** — renews the OAuth token and reports the result. Does **not** pull health data: syncing is the Worker's job per `CLAUDE.md`. A provider that can't be reached marks the connection `token_expired` so the screen agrees with what the user was told.
- **Set as Primary** — switch; disabled on the device that already is primary, since turning it off would leave the member without one
- **Remove Device** — confirmed, then soft-deletes the connection and discards its stored tokens. If it was the primary, another active device is promoted.

**Stats row:**
- Last sync · Next sync (derived from last sync + the connection's interval) · Today's data ("4 updates")
- ~~Battery: "75%"~~ — **not shipped.** No battery level is stored on `DeviceConnection` or fetched from any provider, so the tile has no source. Three tiles instead of four.
- ~~View Sync History~~ — no sync-history endpoint exists

**Troubleshooting (bottom, collapsible):** "Having trouble?" with reconnect guidance. Wording avoids Bluetooth — these are cloud OAuth connections, not paired peripherals.

**States (4 as built):** loading skeleton · device list · empty ("No devices connected yet" + connect CTA) · error with retry

---

### M1-16: Alert Detail - Heart Rate
**Status:** Not built — design intent below
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
**Status:** Not built — design intent below (P0 for MVP 1, still to be implemented)
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
| 4 | Learning Phase | M1-08 | Brain with gears concept. Animated (Lottie). Source from LottieFiles or use static illustration + platform progress animation. **As built:** static 🧠⚙ emoji glyphs. |
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
| 1 | Success checkmark | M1-07 | LottieFiles — search "success checkmark" (free options available). **As built:** static green circle + ✓ glyph, no animation. |
| 2 | Shimmer / skeleton loading | M1-09, M1-10 | **As built:** custom `SkeletonView` control (M1-09). |
| 3 | Critical alert pulse | M1-12 | XAML/CSS animation — opacity + scale loop on severity banner |
| 4 | Learning phase brain/gears | M1-08 | LottieFiles — search "machine learning" or "brain processing." **As built:** static 🧠⚙ emoji glyphs, no Lottie. |

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

**Source:** Extracted from [ui_screens_maui_mobile.md](../ui_screens_maui_mobile.md) v3.1 (manually re-synced August 9, 2026)
**Total MVP 1 Screens:** 17 designed screens · 37 designed states — **12 of 17 built**; 4 additional shipped screens have no Figma M1 frame (SignIn, ForgotPassword, VerifyEmail, AccountSetup)

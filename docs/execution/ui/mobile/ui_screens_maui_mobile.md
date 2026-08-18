# CardiTrack - Mobile App Screen Specifications

## Project Overview

**Product:** CardiTrack - Remote health monitoring for elderly family members
**Platform:** iOS 17+ (iPhone 12+) & Android 12+ (API 31)
**Minimum OS:** iOS 17.0 · Android 12 (API level 31) — the Android floor was raised for the Android 12 SplashScreen API, so one splash design matches the OS handover on every supported device
**Target OS:** iOS 18 · Android 15 (API level 35)
**Orientation:** Portrait primary, landscape supported
**Target Users:** Family caregivers across the US & EU monitoring elderly relatives' wearable health data
**Document Version:** 3.1
**Last Updated:** August 14, 2026

---

## Build Status (as of August 14, 2026)

> **16 of 17 Figma M1 screens are built** in `CardiTrack.Mobile`: M1-01 Splash, M1-02 Welcome, M1-03 Sign Up (CreateAccountPage), M1-04 Add First CardiMember, M1-05 Device Selection, M1-06 Fitbit Connection, M1-07 Connection Success, M1-08 Baseline Learning, M1-09 Dashboard, M1-10 Alerts List (AlertsPage), M1-11/M1-12/M1-16 Alert Detail (`AlertDetailPage`), M1-13 CardiMember Detail (CardiMemberDetailPage), M1-14 Edit CardiMember (EditCardiMemberPage), M1-15 Device Management (DeviceManagementPage). Unbuilt: M1-17 Health Data Export.
>
> **M1-17 is not built** — its entry points show "Coming soon" dialogs in the shipped app.
>
> **Nine shipped surfaces have no Figma M1 frame** and need design sync: SignInPage, ForgotPasswordPage, VerifyEmailPage, Onboarding/AccountSetupPage, NotificationsPage, DaybookPage, DaybookEntryPage, and — built from the existing design system by explicit decision rather than by oversight — the QuestionCard on the CardiMember detail page and the Questions & Answers page. See [Shipped Screens Without Figma M1 Frames](#shipped-screens-without-figma-m1-frames). Per project convention, only screens that exist in the Figma file get M1 IDs — no IDs have been invented for these.
>
> Unbuilt screens below remain documented as design intent, each marked with a status line.

---

## Release Strategy

68 screens across 3 releases (counting each state as a screen). Each release is a fully functional, shippable increment. Release sequencing is governed by the [release matrix](../../../release_matrix.md); waves re-baselined August 2026.

| Release | Target | Screens | Theme | User Gets |
|---------|--------|---------|-------|-----------|
| **MVP 1** (R1) | Q4 2026 | 37 | Core Monitoring | Sign up, connect and manage device (fitbit), monitor one CardiMember (multi-member arrives with M3-03 in MVP 2), CardiMember profile, device management, view dashboard, receive and manage all alert types, health data export (PDF, CSV, FHIR R4) |
| **MVP 2** (R2) | Q1 2027 | 18 | Management, Settings & Family Collaboration | Trend charts, notification preferences, personal subscription (Basic & Complete Care), health data export adds HL7/LOINC/CCD, connect and manage device (garmin), invite family, share notes, scan test results with CardiTrack medical insights |
| **MVP 3** (R3) | Q2 2027 | 13 | Native & Offline | Biometric setup and login, offline support, push notification actions, home screen widget, native sharing, export data in SNOMED CT |

---

## Screen Index

| ID | Screen | Release | Variations |
|----|--------|---------|------------|
| M1-01 | Splash Screen | MVP 1 | 2 (a–b) |
| M1-02 | Welcome / Landing | MVP 1 | 1 |
| M1-03 | Sign Up | MVP 1 | 4 (a–d) |
| M1-04 | Add First CardiMember | MVP 1 | 3 (a–c) |
| M1-05 | Device Connection - Selection | MVP 1 | 1 |
| M1-06 | Device Connection - OAuth | MVP 1 | 3 (a–c) |
| M1-07 | Device Connection - Success | MVP 1 | 3 (a–c) |
| M1-08 | Baseline Learning Info | MVP 1 | 1 |
| M1-09 | Main Dashboard | MVP 1 | 5 (a–e) |
| M1-10 | Alerts List | MVP 1 | 4 (a–d) |
| M1-11 | Alert Detail - Activity | MVP 1 | 1 |
| M1-12 | Alert Detail - Critical | MVP 1 | 1 |
| M1-13 | CardiMember Detail | MVP 1 | 1 |
| M1-14 | Edit CardiMember | MVP 1 | 1 |
| M1-15 | Device Management | MVP 1 | 1 |
| M1-16 | Alert Detail - Heart Rate | MVP 1 | 1 |
| M1-17 | Health Data Export | MVP 1 | 4 (a–d) |
| M2-01 | Settings Main | MVP 2 | 1 |
| M2-02 | Subscription Management | MVP 2 | 1 |
| M2-03 | Trend Charts | MVP 2 | 1 |
| M2-04 | Notification Settings | MVP 2 | 1 |
| M3-01 | Family Members List | MVP 2 | 1 |
| M3-02 | Invite Family Modal | MVP 2 | 1 |
| M3-03 | Multi-Member Dashboard | MVP 2 | 2 (a–b) |
| M3-04 | Shared Notes Feed | MVP 2 | 1 |
| M3-05 | Add / Edit Note | MVP 2 | 1 |
| M3-06 | Test Results Scanner | MVP 2 | 4 (a–d) |
| M3-07 | Test Results Detail | MVP 2 | 4 (a–d) |
| M4-01 | Biometric Setup | MVP 3 | 1 |
| M4-02 | Biometric Login | MVP 3 | 1 |
| M4-03 | Offline Mode Indicator | MVP 3 | 2 (a–b) |
| M4-04 | Offline Data Cache Settings | MVP 3 | 1 |
| M4-05 | Push Notifications | MVP 3 | 4 (a–d) |
| M4-06 | Home Screen Widget | MVP 3 | 3 (a–c) |
| M4-07 | Share Sheet Integration | MVP 3 | 1 |

### Shipped Screens Without Figma M1 Frames

These screens ship in the current app but have **no Figma M1 frame — needs design sync**. Per project convention, screens only get M1 IDs once they exist in the Figma file.

| Screen (code) | Purpose |
|---------------|---------|
| SignInPage | Email/password sign-in with Remember me, Forgot-password link, social buttons, inline error |
| ForgotPasswordPage | Password-reset request + confirmation states |
| VerifyEmailPage | Post-signup email verification gate (resend / open mail / checking / error) |
| Onboarding/AccountSetupPage | "My Family" / "My Organization" account-type choice with conditional Org Name |
| NotificationsPage | Data-completeness / nudge inbox — reached from the dashboard's "Complete the picture" section ("See all") |
| DaybookPage | The **Daybook** tab — daybook entries newest first, searchable and filterable; a card opens the review's page. Took the Family tab's slot |
| DaybookEntryPage | One review in full, with the fortnight's source-tagged trend charts and counted awareness lines beneath it |

Full specs in [Shipped Screens Without Figma M1 Frames](#shipped-screens-without-figma-m1-frames-1) below.

---

### QuestionCard (CardiMember detail) and QuestionnairesPage
**Status:** Built — no Figma M1 frame, needs design sync. Unlike the four above, this was a deliberate decision to build from the existing design system rather than wait on a frame; the frames are still wanted so the file matches the app.
**Entry:** ← M1-13 CardiMember Detail (the card appears inline when a question is waiting; the "Questions & Answers" row opens the page)
**Exit:** → M1-13 CardiMember Detail

- **QuestionCard** — `ElevatedCard` anatomy with no severity rail (the rail is the grammar for "something is wrong"; this is an invitation): 💬 glyph + heading, the question, a rationale sentence in `SelectedOptionBackground` (the model's everyday reason, not an "Asked because …" prefix), a scope footnote ("Just for the moment" on time-scoped questions; "We'll keep this here" on standing ones), an optional-by-design softener, and an "Answer" gradient button that expands an `AuthEntryBorder` editor in place (200 ms, the pause-drop-down animation) with Save/Cancel. A "✕" skips a pending question after a soft confirm. An answered card keeps the same chrome and puts a trash control in that header slot (the same 28px tinted action as alert-list delete) plus an "Answered …" caption inside the card; tapping trash asks for a warning confirm before the answer is removed.
- **QuestionnairesPage** — standard full-bleed scaffold (HeaderBand, RefreshView, skeleton/error/content panels, BottomNavBar): the pending question at the top, then standing answers and still-current momentary ones, newest first. Expired momentary answers are omitted. Empty state when nothing lasting is on the list. **As built:** debounced search, lazy page size 20, delete-on-card with a warning confirm.
- **Both surfaces check the question is still worth asking before drawing it** (`IQuestionValidityService`). A momentary question is about the day it was generated in, so it stops making sense once that day ends — and a card held on screen across midnight, or a page served from the seven-day offline read cache after a night with no signal, will otherwise ask "did he feel tired at all today?" on a different today. The app hides such a card and tells the server to retire it; the same check runs again on save, so an answer typed after the day ended is never filed against the wrong one (the caregiver is told the question has passed rather than seeing the save fail). The server refuses to serve a lapsed question regardless, so this is how the app is prompt, not how the rule is enforced.

## User Flows

### Flow 1: First-Time Onboarding (MVP 1)

As built: Welcome's primary CTA leads to Sign Up; the top-right **"Sign in"** opens SignInPage. CreateAccount still has "Already have an account? Sign In". Email verification (VerifyEmailPage) gates entry, then PostLoginRouter routes to AccountSetupPage (if account type not yet set) or AddCardiMemberPage. Onboarding pages hide the tab bar (`Shell.TabBarIsVisible=False`).

```
[M1-01 Splash] ──────────────────────────────────────────> [M1-09 Dashboard]
      │                                                      (returning user)
      ▼
[M1-02 Welcome]
      │              │
"Start Free Trial" "Sign in" (top-right → SignInPage)
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

### Flow 2: Daily Monitoring (MVP 1)

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
                         "View Trends" ──> [M2-03 Trend Charts]
```

### Flow 3: Settings & Management (MVP 2)

```
[Tab: Settings] → [M2-01 Settings Main]
                                    │
            ┌───────────────────────┼──────────────────────┐
            ▼                       ▼                      ▼
     [M2-02 Subscription]    [M2-04 Notification     [M1-15 Device
                                   Settings]          Management]
                                                           │
                            ┌──────┴──────┐        "+ Add Device"
                            ▼             ▼               ▼
                    [M1-13 CardiMember  [M1-17      [M1-05 Device
                       Detail]          Export]      Selection]
                            │
                       [M1-14 Edit]
```

### Flow 4: Data Export (MVP 1+)

```
Entry points:
  [M2-03 Trend Charts] → Export icon ──────────┐
  [M2-01 Settings] → "Export Health Data" ──────┤──> [M1-17 Health Data Export]
  [M1-13 CardiMember Detail] → "Export Data" ───┘
  [M3-07 Test Results Detail] → "Export Results"

                                  [M1-17 Health Data Export]
                                            │
                           ┌────────────────┼────────────────┐
                           ▼                ▼                ▼
                      [Save to Device]   [Email to...]  [Share via...]
```

### Flow 5: Family Collaboration (MVP 2)

```
[M1-08 Baseline] → "Invite Family First" ─────────────┐
[Tab: Family] → [M3-01 Family Members List]            │
                         │                             │
              ┌──────────┴──────────┐                  │
              ▼                     ▼                  │
      [Active Members]       [Pending Invites]         │
              │                     │                  │
        "Change Role"           "Resend" / "Revoke"    │
        "Remove Access"                                │
                                                       ▼
                                             [M3-02 Invite Modal]
                                                       │
                                               Success confirmation

[Tab: Family → Notes] → [M3-04 Shared Notes Feed]
                                  │
                     ┌────────────┴────────────┐
                     ▼                         ▼
              [M3-05 Add / Edit Note]    Tap note → View thread
                     │
               "Post" → back to M3-04
```

### Flow 6: Multi-Member Dashboard (MVP 2)

```
[Tab: Home — when 2+ CardiMembers] → [M3-03 Multi-Member Dashboard]
                                                │
                          ┌─────────────────────┼──────────────────┐
                          ▼                     ▼                  ▼
                   Tap CardiMember       "+ Add" button       Filter / Sort
                          │                     │
                          ▼                     ▼
                   [M1-09 Single          [M1-04 Add First
                    Dashboard]             CardiMember]
                          │
              ┌───────────┴────────────┐
              ▼                        ▼
       [M1-10 Alerts]          [M1-13 CardiMember
                                    Detail]
```

### Flow 7: Test Results Scanning (MVP 2)

```
Entry points:
  [M2-01 Settings] → "Scan Test Results" ──┐
  [M1-13 CardiMember Detail] ──────────────┤──> [M3-06 Test Results Scanner]
  [Tab: dedicated entry] ──────────────────┘
                                                          │
                                              ┌───────────┼────────────┐
                                           Camera       Upload       Error
                                              │           │             │
                                              └─────┬─────┘         Retry / Help
                                                    ▼
                                               OCR Processing
                                                    │
                                         ┌──────────┴──────────┐
                                      Success              Partial read
                                         │                  (fix on next screen)
                                         ▼
                                 [M3-07 Test Results Detail]
                                         │
                          ┌──────────────┼────────────────┐
                          ▼              ▼                ▼
                   [M1-17 Export]   "Share with      "Add to Health
                                     Doctor"           Record"

```

### Flow 8: Critical Alert Response (MVP 1)

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

### Bottom Tab Bar

Visible on tab roots (Dashboard / Alerts / Daybook / Settings). Onboarding hides it (`Shell.TabBarIsVisible=False`). Tab pages hide Shell's bar and seat `BottomNavBar` **full-bleed** (safe-area inset is padding inside the bar).

```
┌────────────────────────────────────┐
│                                    │
│          Content Area              │
│                                    │
├──────────┬──────────┬────────┬────────┤
│ Dashboard│  Alerts  │Daybook │Settings│
└──────────┴──────────┴────────┴────────┘
```

- Badge count on Alerts tab for unread alerts
- **The third tab is Daybook, not Family.** The Family tab held a stub ("Family sharing (MVP 2) is coming soon") for invitations that are R3 work, so a quarter of the bar did nothing while the daybook entries had no surface at all. `FamilyPage` is deleted. When family sharing lands it belongs under Settings or scoped to a member — a badge for pending invites goes wherever that surface ends up, not back in the bar
- As built, the Shell defines a **TabBar only** (Dashboard / Alerts / Daybook / Settings, SVG icons). Alerts opens the real M1-10 list; Daybook lists the entries; Settings is minimal (account card, a "Silenced reminders" card listing held notification mutes with a "Show me everything again" reset, "More settings (M2-01) coming soon", Sign out). Onboarding pages hide the tab bar via `Shell.TabBarIsVisible=False`.

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
| 3 | **M1-09 — Main Dashboard** | Core monitoring screen; shows status hero card, key-metric grid, severity color system, and star ratings |
| 4 | **M1-10 — Alerts List** | Alert management; demonstrates severity badges, grouped list design, filter chips, and swipe actions |
| 5 | **M1-12 — Alert Detail - Critical** | Highest-stakes screen; validates urgency design, pulsing severity treatment, and primary CTA hierarchy |

These five screens span onboarding → daily use → emergency response. **Build status:** M1-02, M1-04, M1-09, M1-10, **and M1-12** are built — `AlertDetailPage` covers the emergency-response design language for red / no-morning-activity alerts.

---

## MVP 1 — Core Monitoring (37 screens)

A single user can sign up, add a CardiMember, connect devices, manage CardiMember profiles, view the health dashboard, receive and manage all alert types, and export health data in PDF, CSV, or FHIR R4 format. This is the essential monitoring loop — everything needed for the app to be useful from day one. MVP 1 monitors **one CardiMember** — the multi-member dashboard (M3-03) is an MVP 2 feature, and the shipped dashboard displays only the first active member.

---

### M1-01: Splash Screen
**Status:** Built (`SplashPage`)
**User Story:** 1.1-1.3 Onboarding
**Entry:** App launch
**Exit:** → M1-02 Welcome (first launch) | → M1-09 Dashboard (returning user) | → M4-02 Biometric Login (MVP 3, if enabled)

**Duration:** 2-3 seconds while app initializes

**Layout:**
- Full-screen gradient background (CardiTrack brand colors)
- Large CardiTrack logo (centered)
- Loading spinner
- **As built:** default state is logo + spinner only. The wordmark appears on the **error** state, not under the logo while loading. There is **no version number**.

**States:**
- **M1-01a — Default:** Logo + spinner animation
- **M1-01b — Error:** If initialization fails → "Hmm, something didn't work. Tap to try again." with retry button

---

### M1-02: Welcome / Landing Screen
**Status:** Built (`WelcomePage`)
**User Story:** 1.1 First-Time Registration
**Entry:** ← M1-01 Splash (first launch only)
**Exit:** → M1-03 Sign Up (primary CTA "Start Free 30-Day Trial") | → SignInPage (top-right **"Sign in"** — no Figma M1 frame). There **is** a Sign In affordance on Welcome; it is the header label, not a bottom secondary button.

**Layout:**

**Header (top 20%):**
- CardiTrack logo (top-left, small)
- "Sign in" label (top-right) → SignInPage

**Hero Carousel (middle 50%):**
- 3 swipeable slides with pagination dots:

| Slide | Illustration | Headline | Subtext |
|-------|-------------|----------|---------|
| 1 | Happy elderly person with smartwatch | "Know They're Okay" | "Stay close to the people you love — even from far away" |
| 2 | Phone showing health dashboard | "Their Watch, Your Peace" | "Connects with Fitbit, Apple Watch, Garmin & more" |
| 3 | Family members on phones | "Care Together" | "Share the watch with your siblings — you're not in this alone" |

**CTA Section (bottom 30%):**
- Primary button (full width, bold): "Start Free 30-Day Trial"
- Pricing text (small, muted): "Then $8/month - Cancel anytime"
- Legal link (small): "By continuing, you agree to Terms & Privacy"
- **As built: no secondary bottom "Sign In" button** — Sign in is the header label.

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
- **Wired** — Google/Apple sign-in via Auth0's PKCE authorization-code flow in the system browser on Android/iOS; the Windows target falls back to an error message

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
- Subtext: "Tell us who you're looking after — we'll take it from there"

**Photo Section:**
- Circular photo placeholder (large)
- "Add Photo" button below
- **Gallery only** (`MediaPicker.PickPhotosAsync`) — no camera option shipped

**Required Fields:**
- "Full Name *" — text input
- "Date of Birth *" — date picker (format: MM/DD/YYYY)
  - As built, **DOB silently defaults to today** if not changed — not validated (known limitation)
- "Sex *" — picker (Male / Female), helper text: "Helps us read heart rate and sleep against the right range."
  - **Deliberate divergence from the Figma M1-04/M1-13 comps** — the field is not in the design file but ships because DOB + sex set the reference range the summaries are read against; do not drop it on a pixel-match pass

**Optional Field:**
- "Relationship" — dropdown picker (no asterisk): Parent, Grandparent, Spouse, Sibling, Other — an unpicked relationship falls back to `Other`

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
- Primary button: "Continue" — enabled by **name ≥ 2 characters + sex selected** only
- Text link: "Skip for now"

**Draft persistence:** a half-typed member (and its photo) survives app backgrounding — the form saves to `CardiMemberDraftStore` on background/stop and restores on return; the draft is cleared on successful submit.

**States:**
- **M1-04a — Default:** Empty form with photo placeholder
- **M1-04b — Photo added:** Shows uploaded image in circle
- **M1-04c — Loading:** Button shows spinner on submit

---

### M1-05: Device Connection - Selection
**Status:** Built (`Onboarding/DeviceSelectionPage`)
**User Story:** 1.3 Device Connection Wizard
**Entry:** ← M1-04 Add CardiMember ("Continue")
**Exit:** → M1-06 OAuth Permission ("Continue with [Device]")

**Header:**
- Back button
- Title: "Connect Device"
- Progress indicator: "Step 3 of 4"

**Introduction:**
- Heading: "What does [Name] wear?" (member name interpolated)
- Subtext: "We'll connect with their device to keep you in the loop"

**Device Grid (fixed 2-column grid):**

Each device card:
- Rounded frame with shadow
- Device logo (medium)
- Device name (bold)
- "Coming Soon" badge for future devices
- Coming Soon cards render at **0.55 opacity** and are non-tappable

**Supported Devices (MVP 1 — Fitbit and Google Pixel Watch, both via the Google Health API; remaining devices shown as Coming Soon):**

| Device | Card text (models) | MVP Availability |
|--------|--------------------|-----------------|
| Fitbit | Charge, Versa, Sense series | **MVP 1** |
| Google Pixel Watch | Pixel Watch 1–3 | **MVP 1** |
| Garmin | Venu, Forerunner, etc. | MVP 2 (shown Coming Soon) |
| Apple Watch | Series 4+ | Coming Soon |
| Samsung Galaxy | All models | Coming Soon |
| Withings | ScanWatch, Move | Coming Soon |
| Other | All models | Coming Soon |

**Bottom:**
- Link: "Don't see their device? We can help"

**Interactions:**
- **Fitbit and Google Pixel Watch are selectable** (single-select; Fitbit is preselected on entry) — re-tapping the selected card clears the selection, and there is no auto-advance
- Explicit primary button: **"Continue with [Device]"** proceeds to M1-06; reads "Continue" and disables while nothing is selected
- Coming Soon cards are greyed out (0.55 opacity) with "Coming Soon" badges

---

### M1-06: Device Connection - OAuth Permission
**Status:** Built (`Onboarding/DeviceConnectionPage`) — brand-agnostic; the selected device supplies the copy, logo, and wire name
**User Story:** 1.3 OAuth Flow
**Entry:** ← M1-05 Device Selection ("Continue with [Device]")
**Exit:** → M1-07 Success (authorization complete) | ← M1-05 Device Selection ("Cancel")

**Header:**
- Back button
- Title: "[Device] Connection" (device name interpolated)

**Visual Connection (centered):**
- Heading: "Connect Your [Device]"
- Large device logo (interpolated per device)
- Arrow/connection icon
- Large CardiTrack logo

**Permission List:**
- Label: "To look after [Name], CardiTrack needs:" (member name interpolated)
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
**Exit:** → M1-08 Baseline Info ("Continue", first device only) | → wizard exit ("Done", additional device) | → M1-05 Device Selection ("Add Another Device")

**Checkmark:**
- **`icon_status_check` on the status-green tile** (same glyph as the dashboard hero's "all steady"; no entry animation)

**Success Message:**
- Heading: "You're all set!"
- Text: `$"{name}'s {device.DisplayName} is now connected"` (member name + selected device)
- Subtext: "We're pulling in their latest data — just a moment"

**Data Preview Card:**
- Title: "Latest Data" with **"SYNCING" badge**
- Rows:
  - Steps Today: 4,250
  - Last Synced: Just now
  - Heart Rate: 72 bpm

**Options:**
- Outlined button: "Add Another Device" with `icon_plus` (same glyph as Device Management)

**CTA:**
- Primary button: **"Continue"** — connecting the member's **first** device; continues to M1-08. Does **not** name the dashboard: that is M1-08's job, and promising it here sent caregivers into the OAuth browser that was still sitting behind the wizard.
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
- Static `icon_monitoring` glyph in the app's tinted icon tile (not emoji).

**Explanation:**
- Heading: `$"Getting to know {_member.Name}"` (name interpolated)
- Body interpolates the member's name (not a generic "them")
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
- **As built: the switch has no handler.** Statistical rules stay silent until a **30-day** baseline exists. The toggle is dead chrome.

**CTA:**
- Primary button: "Go to Dashboard" — always `GoToAsync("//dashboard")` on a rooted `AppShell`. Must not pop back into the system-browser tab that authorized the device.
- Text link: "Invite Family Member First" — **ships now in MVP 1** (unconditional), but the invite flow (M3-02) does not exist yet, so the link is currently a dead end. Not an MVP 2 addition.

---

### M1-09: Main Dashboard (Single CardiMember)
**Status:** Built (`DashboardPage`)
**User Story:** 2.1 Daily Health Overview
**Entry:** Tab bar (Home) | ← M1-08 Baseline Info (first time)
**Exit:** → M1-10 Alerts List | → M1-13 CardiMember Detail | → Phone call / SMS / SOS | → AlertDetailPage (any open alert) | → WeatherPopupPage (hero weather chip)

**Header (fixed):**
- Greeting: "Good Morning, [User First Name]"
- Notification bell icon (with unread badge count)
- Refresh icon (pull-to-refresh also supported)

**Status Hero Card:**
- Large card with gradient background colored by status
- CardiMember photo (circular, large)
- Name and age: "[Name], 78"
- Large status indicator — the four-tier labels below are the **fallback** while `GetCurrentStatusAsync` is in flight. After load, a **single AI sentence** (under 15 words) replaces them. **"Loading" appears only after 1.5 s**, so a fast response never flashes the word.
- Weather chip (as-built) → `WeatherPopupPage` (session weather for a GPS-tagged exercise, not live)

| Status | Label | Icon |
|--------|-------|------|
| Normal | "[Name] is doing well" | Checkmark |
| Caution | "Something looks a little different" | Warning triangle |
| Urgent | "You should check in" | Lightning bolt |
| Critical | "Reach out to [Name] now" | Siren |

- Last synced: "Updated 10 minutes ago"
- Tap sync icon for manual refresh

**Quick Actions Row (4 tiles):**
- "SOS" (red treatment, leads the row) → dials the **emergency contact number, not the CardiMember**
- "Call" (phone icon) → initiates phone call
- "Message" (SMS icon) → opens SMS
- "Details" (chart icon) → navigates to M1-13

**Key Metrics (collapsible "Key Metrics" `AccordionSection` — 2-column grid of up to six `MetricCard`s):**

Heart Rate, Sleep, Skin Temp, Steps, SpO2, and Breathing Rate — the last three are **visibility-gated on the device having a reading**, and the grid re-packs at render time so no tile is left beside a gap.

**Star rating (1-5)** appears on every card that has something to compare against (Activity, Heart Rate, Sleep, Skin Temp): how the reading sits against this member's own normal — except sleep, which is also held to the published recommended band for the member's age, because a habitually short sleeper's own normal is the very reading being watched for. The row takes the status pill's colour on cards whose pill is built from `status` (Heart Rate, Skin Temp) and colours itself from the star count (3-5 green, 2 yellow, 1 orange) elsewhere — Activity, which shows no pill, and Sleep, whose GOOD/FAIR/POOR pill is itself named from those bands — never from a status the card isn't showing, which is what would paint a short sleeper's two stars green. SpO2 and Breathing Rate have no baseline yet, so their star row stays hidden rather than rating a reading against an invented normal. See `qualityScore` in [health-data.md](../../backend/api/health-data.md).

**Card 1: Activity**
- Icon: shoe
- Large value: "4,250 steps"
- Visual progress bar (today vs the previous calendar day's total; the track's max is yesterday until today exceeds it, then it is today's total). **Two stacked colours** when today is ahead: yesterday's share, then the extra. One colour while behind or level. Caption is a comparison ("vs 5,000 yesterday"), not a remainder against a goal. Hidden when day n−1 is missing.
- Comparison text: "85% of normal" with trend arrow (up/down)
- Star rating (1-5) — the shortfall against normal; walking further than usual is not marked down

**Card 2: Heart Rate**
- Icon: heart
- Large value: "72 bpm"
- Status: "Normal range"
- Star rating (1-5) — deviation from normal in either direction
- Range text: "68-75 bpm typical"

**Card 3: Sleep**
- Icon: moon
- Large value: "7.2 hours"
- Status pill: **GOOD / FAIR / POOR** — one word naming the band of the star rating (3-5 / 2 / 1), in the same pill chrome and status colours as the other cards; hidden when the night is unrated. From the rating, never from `status`: a quality vocabulary rather than NORMAL/UNUSUAL, because a 4.5-hour night is entirely usual for a member who always sleeps 4.5 hours — and still FAIR
- Star rating (1-5) — the worse of sleep efficiency and the shortfall in duration against baseline (either alone when the other is unavailable), capped on the length of the night against the published band for the member's age — both ends, so neither 4.5 nor 12 hours can rate five stars
- Comparison: "Longer than usual" / "Shorter than usual" / "In line with usual" — direction only, no verdict; the stars and pill carry the judgement

**Recent Alerts (conditional — unresolved alerts only):**
- Section heading: "Recent Alerts"
- Horizontal scrollable alert cards (resolved/settled alerts are omitted)
- Each card: icon, title, time, status
- Tap any card → `AlertDetailPage` (M1-11 / M1-12 / M1-16 depending on rule)

**Verify-Email Nudge (conditional):**
- Dismissible banner prompting the user to verify their email address

**"Complete the picture" section (conditional):**
- The top two open data-completeness nudges as compact rows, with a "See all" link → NotificationsPage (the nudge inbox)

**Poor-sleep nudge (conditional):**
- Dismissible banner pointing at the real Sleep alert the statistical worker raised; dismissing it is a local/session convenience, not an acknowledgement — the alert stays in Alerts until acted on there

**Bottom:**
- **As built: no "View Trends & History" button.** Trends live on M1-13's Key Metric Trends carousel.

**Interactions:**
- Pull-to-refresh triggers data sync; manual refresh icon sits in the page **header**
- Android back / gesture-nav at this tab root: first swipe shows "Go back again to leave CardiTrack"; a second swipe within two seconds leaves the app. One swipe is not enough — this is home, and a single back used to finish the activity as if the app had crashed.
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
- Settings icon (gear) → M2-04 Notification Settings

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

**As built** — backed by `GET /api/v1/alerts` and `POST /api/v1/alerts/{id}/acknowledge` (see [alerts.md](../../backend/api/alerts.md)), listing every CardiMember the caregiver may read, newest first. Differences from the frames, each deliberate:

- **Header is as drawn** — back arrow, title, filter button. Alerts is a tab root, so the arrow goes to M1-09 rather than popping a stack that isn't there. The filter button offers the same five filters as a sheet, which is why M1-10b keeps it while dropping the chip row.
- **Chips are M1-10a's set in M1-10c's styling.** The frames disagree — M1-10a has [All] [Unread] [Critical] [Today] [This Week] as plain pills, M1-10c has [Recent] [High Priority] [Heart Rate] [Oxygen] with dropdown carets. The set is M1-10a's (the one this spec documents, and the one every chip can actually filter — there is no SpO2 alert type); the pill, caret and spacing are M1-10c's.
- **Loading is built as drawn** — the "Syncing with Device… / Refresh Now" card over four structured skeleton rows (`AlertSkeletonCard`, whose shimmer blocks sit where the avatar, badge, title and status pill will land). Refresh Now supersedes the in-flight request rather than being swallowed by it, so the button works in the one state it appears in.
- **Severity badges are severity-coloured.** Wording follows this spec (CRITICAL / URGENT / INFO); the colour follows the app's own scale, so a yellow alert can't show a yellow rail beside Figma's blue "Info" chip.
- **The title opens M1-11 / M1-12 / M1-16** (`AlertDetailPage`). The chevron still expands the card in place so the full message can be read without leaving the list.
- **"View Archived Alerts" switches this list to resolved alerts** rather than pushing an archive screen, and flips back the same way. The chip row hides while archived — it is a different list, not a narrower one.
- **Avatars are initials.** No member photo storage exists yet; `cardiMemberPhotoUrl` is on the wire and the tile keeps its designed box.
- **Swipe actions are not implemented.** The card's inline Call and Acknowledge buttons cover both gestures.


---

### M1-11: Alert Detail - Activity
**Status:** Built (`AlertDetailPage`, Shell route `alertdetail`) — one page shared with M1-12/M1-16; activity-decline and long-term-trend rules show the steps chart only.

**As built (applies to M1-11/12/16):** reason icon, **one rule-specific `TrendChart`**, comparison card, context, shared `QuickActionRow` (SOS / Call / Message / Details). More Options = View Detailed (→ M1-13) + OS share. Acknowledge, and **"I'm on my way"** on red alerts, plus undo. **Not built:** header Share as a distinct control, notes, family-notify card, timeline, medical history, Book a Doctor / CALL NOW as distinct screens.

The layout below is the Figma design intent; where it disagrees with the as-built paragraph, the as-built paragraph wins.
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
  _As built:_ daily-grain rules that judge yesterday (`activity_decline`, `elevated_heart_rate`, `long_term_trend`) print that civil day with no clock ("13 August 2026") — the quieter day has no time-of-day, and dating it as the afternoon the worker noticed it filed the alert under Today. Intra-day rules keep the raise datetime. The list groups by `aboutDate`, not `triggeredAt`.

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
- Button: "View Detailed Activity Data" → M2-03

---

### M1-12: Alert Detail - Critical (No Movement)
**Status:** Built (`AlertDetailPage`) — `no_morning_activity` and red alerts; Call now + I'm on my way. Family-notify and notes are still not built.
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
**Status:** Built (`CardiMemberDetailPage`, Shell route `memberdetail`)
**User Story:** 1.4 CardiMember Profile Management
**Entry:** ← M1-09 Dashboard (hero card / "Details" quick action) | ← M2-01 Settings ("Manage CardiMembers", MVP 2)
**Exit:** ← Previous screen (back) | → M1-14 Edit CardiMember (edit button) | → M1-15 Device Management ("Manage Device") | → M1-10 Alerts ("View Alerts")

**Profile Section (as built):**
- Display picture (`MemberAvatar`, 80dp) — initials as built (`PhotoUrl` is always null; there is no photo storage or upload path). The control already renders a photo when one exists.
- Name
- Age & relationship: "78 years old • Dad"
- Connection status on the third row: a red / amber / blue / green dot (the same data-pipeline freshness as the dashboard — no sync in 12 h / 4 h / synced / processed) in front of the last-contact age ("Updated 4 minutes ago"). The whole row is hidden while monitoring is paused — a freshness colour would misread a deliberate pause as a connection gap, and the paused banner is the status then. Device count lives on M1-15, not here.

**Contact Info (as built):**
- Emergency contact and the member's own phone are **two looping carousel slides** (same wrap-around as Key Metric Trends): swipe past Phone and Emergency Contact is next. Indicators under the row.
- Emergency slide: name + number, call when a number exists
- Phone slide: number, call + message when a number exists; primary caregivers can tap the number column (or the edit affordance when empty) to open M1-14 scrolled to the Phone Number field (`focus=phone`)

**Medical Info Card (encrypted):**
- Lock icon in card header
- Collapsible: "Medical Notes"
- Biometric gating of medical notes is **deferred to R4** (per the [release matrix](../../../release_matrix.md)) — not an MVP 1 requirement

**Action Buttons (as built):**
- "View Alerts" → M1-10
- "Manage Device" → M1-15
- "Questions & Answers" row → QuestionnairesPage

**As-built additions beyond the comp:** an AI summary card with suggestions, an inline `QuestionCard` when a question is waiting, a paused-monitoring banner, a looping **Key Metric Trends** carousel (`MetricTrendCard` + 7/14/30-day `TrendWindowSelector`), and a looping Emergency Contact / Phone carousel. The profile's connection-status dot is as-built (the Figma frame has no freshness indicator).

**Danger Zone (separated — "Management" / "Critical settings" as built):**
- "Pause Monitoring" (warning treatment; expands an inline duration picker: 24h / 48h / 3 days / 1 week)
- "Remove CardiMember" button (destructive treatment)

---

### M1-14: Edit CardiMember
**Status:** Built (`EditCardiMemberPage`, Shell route `editcardimember`)
**User Story:** 1.4 CardiMember Profile Management
**Entry:** ← M1-13 CardiMember Detail (edit button)
**Exit:** ← M1-13 CardiMember Detail (back or save)

**Header:**
- Back button
- Title: "Edit Profile"
- Save button (enabled when changes exist)

**Form (scrollable):**

**Photo:** Large circular avatar (photo or initials)

**Basic Info:**
- "Full Name*" — text input
- "Date of Birth*" — date picker
- "Sex" — picker (Male / Female — same two options as the M1-04 add form; PreferNotToSay is deliberately not re-offered)
- "Relationship" — dropdown picker (optional; same order and labels as M1-04)

**Medical & Emergency:**
- "Medical Notes" — multi-line (encrypted)
- "Emergency Contact Name" — text input
- "Emergency Contact Number" — phone input
- "Phone Number" — the member's own phone

**Monitoring Preferences (as built):**
- Dropdown: "Alert Sensitivity" — Low / Medium / High, with the caption that alerting isn't live yet (there is no "Enable Monitoring" toggle here — pausing lives on M1-13)

**CTA:**
- "Save" button (header)

**Behavior:**
- Tracks unsaved changes
- "Unsaved changes" warning if navigating away without saving

---

### M1-15: Device Management
**Status:** Built (`DeviceManagementPage`, Shell route `devicemanagement`)
**User Story:** 6.2 Devices
**Entry:** ← M1-13 CardiMember Detail ("Manage Device") | ← M2-01 Settings ("Connected Devices", MVP 2)
**Exit:** ← Previous screen (back) | → M1-05 Device Selection ("Add Device")

**Header:**
- Back button
- Title: "Connected Devices"
- "+ Add Device" button

**Devices List (single member as built):**

**Group Header:** "Devices List" + member subtitle (multi-member grouping arrives with M3-03)

**Device Card (`DeviceCard`):**
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
- Battery tile (shipped): shown **only when the server sent a reading** — the tile's grid column collapses when absent, so no gap is left; the value turns **red at `DeviceBattery.IsLow`**, which is **any of the three tiers** (Warning ≤30% / Urgent ≤20% / Critical ≤10% or Low/Empty band), matching `DEVICE_BATTERY_LOW`. Freshness is **12 hours**.

**Troubleshooting (bottom, collapsible — as built):**
- "Having trouble?"
  - Make sure the device has synced with its own app recently
  - Try "Refresh Connection" — it renews the link without reconnecting
  - Still stuck? Remove the device and connect it again

---

### M1-16: Alert Detail - Heart Rate
**Status:** Built (`AlertDetailPage`) — `elevated_heart_rate` uses the 7-day resting-HR series; `realtime_hr` uses that hour's granular HR, not the dashboard metrics.
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

**MVP 2 addition:** HL7 v2 format added to this screen — no new screen created

**MVP 2 addition:** LOINC and CCD formats added (see M3-07)

**MVP 3 addition:** SNOMED CT format added (see M3-07)

**Format Info (expandable per format):**
- Tap info icon next to FHIR/HL7 → explains format, typical recipients (hospitals, clinics, patient portals)

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

## Shipped Screens Without Figma M1 Frames

The following screens exist in the shipped app but have **no Figma M1 frame — needs design sync**. Per project convention, only screens present in the Figma file receive M1 IDs, so no IDs are assigned here.

### DaybookPage
**Status:** Built — no Figma M1 frame, needs design sync. Replaced the Family tab stub.
**Entry:** Bottom nav, third tab
**Exit:** → M1-09 Dashboard (back arrow, or the Dashboard tab)

- Gradient header band with back arrow and "Summaries", same treatment as Alerts and Settings
- Pull-to-refresh; **no periodic poll** — a daybook entry is written once, at 02:00 in the member's own local time, and cannot change afterwards. Refreshes on app resume only
- **Search and two chooser chips** above the list, outside the scroller so narrowing stays reachable while scrolled (the alert chips' reasoning). The search is debounced 350ms and server-side over the whole history; the chips open the app's option popup — urgency (Any / Watch / Check in / Concerning / Act now) and window (All time / 7 / 30 / 90 days). The filter row appears once the member has ever had a review, then stays: hiding it on an empty *filtered* result would take away the one control that undoes the emptiness
- One card per finished day, newest first, up to a month. Each card carries:
  - the day, said the way someone says it — "Yesterday", then the weekday within the week, then the date
  - the review's own generated headline, falling back to "A day in review" for entries written before headlines existed
  - an urgency pill — WATCH / CHECK IN / CONCERNING / ACT NOW on the green/yellow/orange/red status colours. **No pill at all** when the model returned no urgency or one this app does not know: a pill is a claim about a member's health, and a grey one would imply the service judged the day and found it unremarkable
  - the review clipped to three lines and "Read the full day", which **opens the review's own page** (below) — a navigation, not an expansion: the full account carries the trend charts, which is more than a list row can hold and stay a list
- **States:** loading (three placeholder cards, so the list does not jump when the real ones arrive) · empty · error with retry · loaded
- **Two empty states, said apart:** an unfiltered "No daybook entries yet" over "The first review is written after {Name}'s first full day of readings", and a filtered "No reviews match" over "clear one and look again" — a bare "nothing here" reads as a fault either way. A member-less account gets "Add the person you care about, and their days will be summarised here"
- **Error while a list is already on screen** shows a popup over it rather than replacing it with an error panel: the reviews describe finished days and do not go stale, so taking them away costs the caregiver something still worth reading
- Backed by `GET /api/v1/insights/members/{id}/digests?audience=daybook` with `limit`, `search`, `from` and `urgency`

### DaybookEntryPage
**Status:** Built — no Figma M1 frame, needs design sync
**Entry:** ← DaybookPage ("Read the full day")
**Exit:** ← DaybookPage (back arrow, or the Daybook tab)

- The review in full: day, headline, urgency pill, the whole account, "One thing you could do" (the suggestion, hidden when the generation produced none), and when it was written
- **"The last 14 days"** — trend charts for Sleep, Resting heart rate and Steps, drawn with the same `TrendChart` the alert detail uses: the member's own usual dashed, the published band shaded, per-day markers
- **Every chart's key names its sources**: "Dashed: their usual 4.1 · Shaded: recommended 7–8 (NSF)". The key names only marks the chart actually drew — Steps gets no band because no standards body publishes one
- **An awareness line under each chart** — "Under their usual on 10 of the last 14 nights" — a deterministic count over exactly the days the chart draws (`TrendAwareness`). Counts, never scores, per the release matrix's standing no-risk-scores decision; partial and unmeasured days are on neither side of the sentence; nothing is said without a baseline or below 7 measured days
- The charts are deliberately the **current** fortnight whatever day the review describes, and the section title says so — the dashboard series always runs to today
- Footer, always visible with the charts: "For awareness, not medical advice — CardiTrack never diagnoses. Talk to a clinician about anything that worries you."
- **Edge — charts fetch fails:** the review stands and the trends section hides; a loaded review must not be replaced by an error panel over its garnish. **Edge — no review for the date:** "No review was written for this day"
- Backed by `GET .../digest?date=YYYY-MM-DD&audience=daybook` + `GET /api/v1/cardimembers/{id}` for the chart series

### SignInPage
**Status:** Built — no Figma M1 frame, needs design sync
**Entry:** ← M1-03 Sign Up ("Already have an account? Sign In")
**Exit:** → M1-09 Dashboard (success, via PostLoginRouter) | → ForgotPasswordPage | → M1-03 Sign Up ("Don't have an account — Sign Up")

- Gradient header: "Welcome Back" / "Sign in to keep watching over your family"
- Email Address + Password fields (password show/hide eye toggle)
- **"Remember me" checkbox** + "Forgot password" link on one row
- Inline sign-in error label (no banner)
- "Sign in" gradient button
- "Or continue with" divider + 2-up social grid (Google / Apple), same treatment as M1-03 — wired to the same Auth0 PKCE system-browser flow (Android/iOS)
- Bottom link: "Don't have an account — Sign Up"

### ForgotPasswordPage
**Status:** Built — no Figma M1 frame, needs design sync
**Entry:** ← SignInPage ("Forgot password")
**Exit:** ← SignInPage ("Back to sign in")

- **State A — Request:** email input + send-reset-link CTA + "Back to sign in" link
- **State B — Confirmation:** "Check your email" + "We sent a password reset link to your email" + "Resend link" (30-second cooldown) + "Back to sign in"

### VerifyEmailPage
**Status:** Built — no Figma M1 frame, needs design sync
**Entry:** ← M1-03 Sign Up (after account creation — the hard verification gate)
**Exit:** → PostLoginRouter → AccountSetupPage or M1-04 (once verified)

- Gradient header: "Verify your email"
- "I've verified — continue" button — re-attempts sign-in to check verification status (checking state with activity indicator)
- "Open mail app" button
- "Resend" via the API's anonymous endpoint (45-second cooldown; "Sent — check your inbox" status)
- Inline error label for unverified/failed checks

### AccountSetupPage (Onboarding)
**Status:** Built — no Figma M1 frame, needs design sync
**Entry:** ← PostLoginRouter (first login with no account type set)
**Exit:** → M1-04 Add CardiMember ("Continue")

- Gradient header: "Almost There" / "Tell us how you'll use CardiTrack"
- Radio-cards:
  - **"My Family"** — "I'm watching over my parents or loved ones"
  - **"My Organization"** — "I provide care professionally (care home, agency)" — selecting reveals a conditional **Organization Name** field
- Inline error label; "Continue" disabled until a type is selected

> **Flagged scope question (not a resolution):** the Organization option surfaces business/organization onboarding in MVP 1, while the Guardian Plus business tier is scoped **post-R4** in the release matrix. Whether organization sign-up should ship this early needs a product decision at the next design sync.

### NotificationsPage
**Status:** Built — no Figma M1 frame, needs design sync
**Entry:** ← M1-09 Dashboard ("Complete the picture" section → "See all")
**Exit:** ← M1-09 Dashboard (back) | deep links into the screen each nudge asks the caregiver to fix

- The data-completeness / nudge inbox: what CardiTrack is missing, what supplying it unlocks, and the three things a caregiver can do about each — fix it now (deep link), put it off, or turn it off
- Standard full-bleed scaffold (HeaderBand, loading/loaded/empty/error states); each notification renders as a `NudgeCard`
- Mutes created here are listed and reversible on SettingsPage ("Silenced reminders")

---

## Telemetry & Consent (MVP 1 note)

Datadog telemetry ships **logs + traces only** — RUM was removed in PR #185, and with it Datadog crash reporting (`NativeCrashReportEnabled=false`); crashes and ANRs come from **Play Console vitals** instead. `TrackingConsent.Granted` is still hardcoded — there is **no in-app telemetry control in MVP 1**: no opt-out toggle and no diagnostics screen exists. This is flagged as a product follow-up; it is in tension with the "consent-first" design principle and the Story 7.1 framing in the user stories.

---

## MVP 2 — Management, Settings & Family Collaboration (18 screens)

Extends MVP 1 with account management, trend history, notification preferences, subscription billing, Garmin device support, and HL7 v2 export; plus family collaboration — invite siblings, shared notes, multi-member dashboard, test results scanning with CardiTrack medical insights, and LOINC/CCD export formats.

**Prerequisite:** MVP 1 must be complete.

> **Export note:** M1-17 (Health Data Export) ships in MVP 1 with PDF, CSV, and FHIR R4. This release updates M1-17 to add HL7 v2, LOINC, and CCD to the format picker — no new screen is created for these additions.

---

### M2-01: Settings Main
**User Story:** 6.1, 6.2 Settings
**Entry:** Tab bar (Settings)
**Exit:** → M2-02 Subscription | → M2-04 Notification Settings | → M1-13 CardiMember Detail | → M1-15 Device Management | → M1-17 Health Data Export

> **As built today (MVP 1 `SettingsPage`):** account card, a **"Silenced reminders"** card listing every held notification mute with a "Show me everything again" reset, an "M2-01 coming soon" placeholder card, and Sign out. The full grouped list below is the MVP 2 design intent.

**User Profile Section (top card):**
- Profile photo (large, tappable to edit)
- Name: "[User Name]"
- Email: "[user@email.com]"
- Edit button (pencil icon)

**Settings Groups (grouped list):**

**Account**
- My Profile →
- Subscription & Billing → (badge: current plan name)
- Family & Sharing → (MVP 2)

**CardiMembers**
- Manage CardiMembers →
- Connected Devices → M1-15
- Export Health Data → M1-17

**Health Records (MVP 2)**
- Scan Test Results → M3-06

**Notifications**
- Alert Settings →
- Notification Preferences →
- Quiet Hours →

**Security**
- Change Password →
- Biometric Login (inline toggle switch)
- Privacy Settings →

**Support**
- Help Center →
- Contact Support →
- Terms & Privacy →

**About**
- App Version (value: "1.0.0")
- Check for Updates

**Danger Zone (separated visually):**
- "Sign Out" (destructive text)
- "Delete Account" (destructive text)

**MVP 2 addition:** Family & Sharing → M3-01

---

### M2-02: Subscription Management
**User Story:** 6.1 Subscription
**Entry:** ← M2-01 Settings ("Subscription & Billing")
**Exit:** ← M2-01 Settings (back) | → Payment method change | → Plan change

> **Scope note:** MVP 2 covers personal tiers only (Basic and Complete Care). The Guardian Plus business tier is excluded from MVP and will be addressed in a dedicated business account flow post-MVP.

**Current Plan Card (gradient background):**
- Badge: "COMPLETE CARE"
- Price: "$15/month"
- Renewal date: "Renews Feb 10, 2026"
- Button: "Manage Subscription"

**Included Features (checklist):**
- Up to 5 CardiMembers
- Advanced ML Alerts
- Family Sharing
- 90-day data retention
- Priority support

**Usage Section:**
- Progress bars with labels:
  - CardiMembers: 2 of 5
  - Data retention: 45 days of 90

**Plan Comparison (horizontal swipeable cards):**
- 2 plan cards (Basic and Complete Care), swipe to compare
- Each card:
  - Plan name + price/month
  - "Current Plan" badge (if active)
  - Condensed feature list
  - Button: "Current Plan" (disabled) / "Upgrade" / "Downgrade"

**Annual Discount Banner:**
- "Save 15% with Annual Billing"
- "Switch to Annual" button

**Billing Section:**
- Payment method: "Visa ---- 1234" (with card icon)
- "Change" button
- "Billing History" button

---

### M2-03: Trend Charts
**User Story:** 2.3 Historical Data
**Entry:** ← M1-09 Dashboard ("View Trends") | ← M1-11 Alert Detail ("View Detailed Data")
**Exit:** ← Previous screen (back) | → M1-17 Health Data Export

**Header:**
- Back button
- Title: "[Name]'s Trends"
- Export/share icon

**Time Range Selector (segmented control):**
- [7D] [30D] [90D] [Custom]
- Custom opens a date range picker modal

**Metric Tabs (horizontal scroll):**
- [Activity] [Heart Rate] [Sleep] [All]

**Chart Area:**
- Line chart:
  - X-axis: dates
  - Y-axis: metric values
  - Shaded area: baseline/normal range
  - Line: actual data
  - Markers: alert events on timeline
- Pinch to zoom
- Double-tap to reset zoom

**Interactive Tooltip (long-press on data point):**
- Popup showing:
  - Date/time
  - Exact value
  - "120% above baseline"
  - Note icon (if notes exist for that date)

**Timeline Annotations (below chart, horizontal scroll):**
- Alert markers with icons
- Note markers with text preview
- Tap to expand details

**Summary Stats Card (bottom):**
- Average: "4,500 steps"
- High: "8,200 (Jan 5)"
- Low: "1,200 (Jan 8)"
- Trend: "Declining 15%" with down arrow

**Export Options (via share icon):**
- Export to PDF
- Export to CSV
- Share screenshot
- Send to email

---

### M2-04: Notification Settings
**User Story:** 3.2 Alert Preferences
**Entry:** ← M2-01 Settings | ← M1-10 Alerts List (gear icon)
**Exit:** ← Previous screen (back)

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
- Description: "Let me know if their heart rate seems higher than usual"

**Sleep Alerts**
- Toggle: enabled/disabled
- Checkboxes:
  - Poor sleep quality
  - Unusual sleep patterns

**Pattern Break Alerts**
- Toggle: always on (cannot disable)
- Label: "Always on — this is how CardiTrack catches emergencies"

**Notification Channels (per alert type):**
- Multi-select chips: [Email] [SMS] [Push] [All]

**Quiet Hours (collapsible):**
- Toggle: "Enable Quiet Hours"
- Time pickers: From 10:00 PM → To 7:00 AM
- Exception toggle: "Still wake me for emergencies"

**Family Routing (MVP 2):**
- "Also let these family members know:"
- Checkboxes with severity chips:
  - Sarah Johnson — [High Severity] [Critical]
  - John Doe — [Critical Only]

**Test Section:**
- "Send Test Push Notification" button
- "Send Test Email" button
- "Send Test SMS" button

---

### M3-01: Family Members List
**User Story:** 4.1 Family Management
**Entry:** Tab bar (Family) | ← M2-01 Settings ("Family & Sharing")
**Exit:** → M3-02 Invite Modal | → Role management | → M3-04 Shared Notes

**Header:**
- Title: "Family & Sharing"
- "+ Invite" button

**Tabs:**
- [Active Members] [Pending Invites]

**Active Members List:**

Each member card:
- Profile photo (circular, small)
- Name: "Sarah Johnson"
- Email: "sarah@email.com"
- Role badge: "ADMIN" / "STAFF" / "VIEWER"
- Last active: "Active 2 hours ago"
- Menu icon (three dots)

**Context Menu (on three-dot tap):**
- Change Role → role picker
- View Activity Log
- Remove Access (destructive text)

**Pending Invites Tab:**
- Email shown
- Role assigned
- Sent date: "2 days ago"
- "Resend" button
- "Revoke" button (destructive text)

**Empty State (pending tab):** "No pending invitations"

**Floating Action Button (bottom-right):**
- "+" icon → opens M3-02 Invite Modal

---

### M3-02: Invite Family Modal
**User Story:** 4.1 Inviting Members
**Entry:** ← M3-01 Family List ("+" or "Invite") | ← M1-08 Baseline Info ("Invite Family Members First")
**Exit:** ← M3-01 Family List (close/cancel) | → Success confirmation

**Presentation:** Bottom sheet or full-screen modal

**Header:**
- Close button (X)
- Title: "Invite Family Member"

**Form:**

**Email Input:**
- Label: "Email Address"
- Email keyboard
- Inline validation indicator

**Role Selection (segmented control):**
- [Admin] [Staff] [Viewer]
- Selected role shows description:

| Role | Description |
|------|-------------|
| Admin | Can view, modify settings, invite others |
| Staff | Can view and acknowledge alerts |
| Viewer | Can only view health data |

**Permission Details (collapsible):**
- Table showing what each role can/cannot do

**Personal Message (optional):**
- Label: "Add a message (optional)"
- Multi-line input
- Placeholder: "Hi Sarah, I'm using CardiTrack to keep an eye on Dad — want to help?"

**CTA:**
- Primary button: "Send Invitation"
- Text link: "Cancel"

---

### M3-03: Multi-Member Dashboard
**User Story:** 2.2 Multi-Member View
**Entry:** Tab bar (Home) — replaces M1-09 when user has multiple CardiMembers
**Exit:** → M1-09 Single Dashboard (tap member) | → M1-04 Add CardiMember ("+ Add")

**Header:**
- Title: "My CardiMembers"
- Filter icon → opens filter sheet
- "+ Add" button

**Filter Bar (collapsible, horizontal scroll):**
- Chips: [All] [Alerts Only] [Good Status]
- Sort button: "Sort by Status"

**CardiMember Cards (vertical scroll):**

Each card:
- Left: Circular photo (medium) with status badge overlay
- Middle: Name (bold), Age & relationship, Status text, Last synced
- Right: Chevron + alert count badge (if any)

**Swipe Actions:**
- Swipe left → "Call" button
- Swipe right → "Details" button

**Floating Action Button (bottom-right):**
- "+" icon → Add CardiMember flow

**States:**
- **M3-03a — Default:** Member cards listed
- **M3-03b — Empty:** Illustration + "No one here yet" + "Add someone you'd like to look after" button

---

### M3-04: Shared Notes Feed
**User Story:** 4.2 Coordination
**Entry:** ← M3-01 Family List | Tab bar (Family) → Notes sub-tab
**Exit:** → M3-05 Add Note | ← Previous screen (back)

**Header:**
- Back button
- Title: "Family Notes"
- Filter dropdown: "All Notes"

**Add Note Input (top):**
- User photo + text input: "Add a note for the family..."
- Tap → opens M3-05 full composer

**Notes Feed:**

Each note card:
- Author photo (small) + author name + timestamp ("2 hours ago")
- Menu (three dots) — only shown if you're the author
- Note text content (with @mentions highlighted)
- Attachments (if any)
- CardiMember tag: "About: Dad" (if associated)
- Footer: Reply button + count | Like button + count

**Threaded Replies (expandable):**
- Tap reply count → expands inline
- Indented reply cards
- "Load more replies" if more than 3

**Filter Options (dropdown):**
- All Notes / About Dad / About Mom / My Notes Only / Mentions Me

---

### M3-05: Add / Edit Note
**User Story:** 4.2 Shared Notes
**Entry:** ← M3-04 Notes Feed (tap input or "+" button)
**Exit:** ← M3-04 Notes Feed (cancel or post)

**Presentation:** Full-screen modal

**Header:**
- Cancel button
- Title: "New Note"
- Post button (enabled when content exists)

**Note Input:**
- Multi-line text editor (expands with content)
- Placeholder: "How's Dad doing? Let the family know..."
- Character counter: "0 / 500"
- Typing "@" triggers a mention picker overlay listing family members

**CardiMember Association:**
- Label: "About (optional)"
- Dropdown: None (General) / Dad / Mom

**Attachments:**
- Button: "+ Attach Photo"
- Shows thumbnail grid when photos added
- Maximum 3 attachments

**Visibility:**
- Label: "Who can see this"
- Default: "All family members"

**CTA:**
- Primary button: "Post Note"
- Success → toast confirmation + return to feed

---

### M3-06: Test Results Scanner
**User Story:** 12.1 Lab Results Capture
**Entry:** ← M2-01 Settings ("Scan Test Results") | ← M1-13 CardiMember Detail ("Add Test Results") | Tab bar (dedicated entry point)
**Exit:** → M3-07 Test Results Detail (scan complete) | ← Previous screen (cancel)

**Header:**
- Close button (X)
- Title: "Scan Test Results"

**CardiMember Selector:**
- Dropdown: "Scan for: [Dad]"

**Capture Options (2 large cards):**

| Option | Icon | Description |
|--------|------|-------------|
| Camera Scan | Camera icon | "Take a photo of a lab report or test result" |
| Upload File | Document icon | "Upload a PDF or image from your files" |

**Camera View (after selecting Camera Scan):**
- Full-screen camera viewfinder
- Guide overlay: document frame outline with corner markers
- Instructions: "Align the test results within the frame"
- Capture button (large, centered bottom)
- Flash toggle (top-right)
- Gallery shortcut (bottom-left)

**Processing State (after capture/upload):**
- Document thumbnail (showing captured image)
- Progress animation: "Analyzing results..."
- Steps indicator:
  1. "Extracting text..." (OCR)
  2. "Identifying test values..."
  3. "Cross-referencing medical standards..."
- Cancel button: "Cancel Analysis"

**Multi-Page Support:**
- After first page captured: "Add Another Page" button
- Page indicator: "Page 1 of 1"
- Swipe between captured pages

**Error Handling:**
- Blurry image: "That came out a bit blurry — try holding steady and retake"
- Unreadable: "We're having trouble reading this — try better lighting or upload a PDF instead"
- Partial read: "We got most of it, but a few values need your help — you can fix them on the next screen"

**States:**
- **M3-06a — Default:** Capture options
- **M3-06b — Camera active:** Viewfinder with guide overlay
- **M3-06c — Processing:** Analysis progress animation
- **M3-06d — Error:** Error message with retry/retake options

---

### M3-07: Test Results Detail
**User Story:** 12.2 Medical Insights from Lab Results
**Entry:** ← M3-06 Test Results Scanner (analysis complete) | ← M1-13 CardiMember Detail ("View Test Results")
**Exit:** ← Previous screen (back) | → M1-17 Health Data Export | → Share

**Header:**
- Back button
- Title: "Test Results"
- Share icon | Export icon

**Result Summary Card:**
- CardiMember photo + name
- Test date: "February 10, 2026"
- Source: "Scanned lab report" | "Uploaded PDF"
- Lab name (if detected): "City Medical Lab"

**Parsed Results Table:**

Each result row:
- Test name (bold): e.g., "Hemoglobin A1c"
- Value: "6.2%"
- Reference range: "4.0 - 5.6%"
- Status indicator:
  - Normal: within range
  - High: above range (with severity indication)
  - Low: below range (with severity indication)
- Edit icon (pencil) — allows manual correction of OCR errors

**CardiTrack Insights Card:**
- Lightbulb icon + "CardiTrack Insights"
- Disclaimer banner: "These observations are here to help — but always talk to a doctor before making health decisions."
- CardiTrack observations:
  - "Dad's Hemoglobin A1c is a bit high — this sometimes points to pre-diabetes"
  - "Good news — cholesterol levels look normal"
  - "His Vitamin D is low — worth mentioning to his doctor"
- Each insight can be expanded for more detail
- "Learn More" links to relevant health information

**Trend Comparison (if previous results exist):**
- Side-by-side comparison with last test
- Trend arrows (improving / worsening / stable)
- Mini chart showing value over time

**Corrections Section (collapsible):**
- "Review & Correct Values"
- Editable fields for each parsed value
- "Mark as Verified" button

**Export & Sharing:**
- "Export Results" → M1-17 Health Data Export
- "Share with Doctor" → pre-formatted email/share
- "Add to Health Record" → saves to CardiMember profile

**Data Standards:**
- Results are encoded using:
  - **LOINC** — standardized lab test codes (e.g., Hemoglobin A1c = LOINC 4548-4) *(MVP 2)*
  - **CCD** — Continuity of Care Document for structured clinical summaries *(MVP 2)*
  - **SNOMED CT** — clinical terminology for conditions and findings *(MVP 3 addition)*
- These formats are available in M1-17 Health Data Export as additional export options

**States:**
- **M3-07a — Default:** Parsed results with insights
- **M3-07b — Editing:** Inline editing mode for value corrections
- **M3-07c — No previous results:** Trend section hidden
- **M3-07d — Loading insights:** Skeleton loading for CardiTrack insights section

---

## MVP 3 — Native & Offline (13 screens)

Adds platform-native polish: biometric security (setup and login), offline data access with sync queue, rich push notifications with inline actions, home screen widgets for at-a-glance monitoring, native share sheet for exporting data to doctors or family, and SNOMED CT health data export.

**Prerequisite:** MVP 2 must be complete.

---

### M4-01: Biometric Setup
**User Story:** 10.2 Biometric Login
**Entry:** ← M2-01 Settings (Security section)
**Exit:** ← M2-01 Settings | → Skip ("Set Up Later")

**Header:**
- "Skip" link (top-right)
- Title: "Secure Your Account"

**Biometric Icon (centered, large):**
- iOS: Face ID icon
- Android: Fingerprint icon

**Explanation:**
- Heading: "Enable [Face ID / Fingerprint]"
- Body: "Quickly and securely access health data"
- Benefits:
  - Login in seconds
  - Extra security layer
  - Required for sensitive actions

**CTA:**
- Primary button: "Enable [Biometric]" — triggers device biometric enrollment
- Text link: "Set Up Later"

---

### M4-02: Biometric Login
**User Story:** 10.2 Biometric Auth
**Entry:** ← M1-01 Splash (when biometric enabled via M4-01)
**Exit:** → M1-09 Dashboard (success) | → Password fallback

Replaces password entry on app launch when biometric is enabled.

- CardiTrack logo + user name/photo
- Platform biometric prompt (Face ID on iOS, fingerprint on Android)
- "Scan to unlock" label
- Fallback: "Use Password" link → password entry field
- Configurable biometric requirements: app launch, viewing alerts, acknowledging alerts, changing settings

---

### M4-03: Offline Mode Indicator
**User Story:** 10.1 Offline Support
**Entry:** Automatic — appears when device loses connectivity
**Exit:** Automatic — disappears when connection restored

**States:**
- **M4-03a — Offline:** Persistent banner + read-only mode + alert queue
- **M4-03b — Connection Restored:** Toast + sync animation + success confirmation

**M4-03a — Offline**

**Offline Banner (top of screen, persistent):**
- Warning-level background
- Crossed-out signal icon
- Text: "Offline Mode"
- Subtext: "Last updated 2 hours ago"
- Closable (X) but reappears on navigation

**Dashboard Modifications (offline state):**
- All data shown is cached/stale
- Sync icons greyed out
- "Syncing disabled" tooltip on refresh attempt

**Alert Queue Card:**
- "2 alerts pending sync"
- Shows queued alert cards with "Not yet synced" badge
- "Will sync when connected"

**Behavior:**
- Read-only mode: no POST operations
- Actions queued for sync when reconnected

**M4-03b — Connection Restored:**
- Toast: "Back online!"
- Syncing animation with progress
- Success message: "All data synced"

---

### M4-04: Offline Data Cache Settings
**User Story:** 10.1 Cache Management
**Entry:** ← M2-01 Settings
**Exit:** ← M2-01 Settings (back)

**Cache Info Card:**
- "Cached Data Size: 45 MB"
- "Last synced: 10 minutes ago"

**Settings:**
- Slider: "Days to cache" — 1 day | 7 days | 30 days
- Toggle: "Auto-download charts"
- Toggle: "Cache photos"

**Actions:**
- "Clear Cache" button — confirmation dialog: "You'll need internet to view data"
- "Sync Now" button

---

### M4-05: Push Notifications
**User Story:** 5.1

Designs for system-level notification UI.

**States:**
- **M4-05a — Lock Screen (compact):** App icon + "[Name] - Critical Alert" + body preview + timestamp
- **M4-05b — Lock Screen (expanded on long press):** Full alert text + action buttons: "Call" / "View" / "Acknowledge"
- **M4-05c — In-App Banner:** Slides from top, shows summary, tap to navigate, swipe up to dismiss
- **M4-05d — Notification Center:** Grouped by CardiMember with expandable lists + app badge count

---

### M4-06: Home Screen Widget
**User Story:** 5.2

**States:**
- **M4-06a — Small Widget (2x2):** Logo + CardiMember photo + status indicator + name + last synced
- **M4-06b — Medium Widget (4x2):** 2 CardiMembers side-by-side with photo, name, status, key metric
- **M4-06c — Large Widget (4x4, iOS):** Up to 4 CardiMembers with mini dashboards (photo, name, status, 3 metrics, alert badge)

**Configuration:** Long-press → select CardiMembers, choose metrics, set update frequency

---

### M4-07: Share Sheet Integration
**User Story:** 5.3 Native Sharing

Native share sheet triggered from charts and alerts.

**Options:** Export PDF / Export CSV / Share screenshot / Email to self / Share to family member / Save to Files
**Custom Actions:** "Send to Doctor" (pre-configured email) / "Add to Health App" (iOS HealthKit)

---

## Design System (Designer Deliverable)

The design system is **yours to define**. The following are functional requirements and constraints — not visual prescriptions.

### As-Built Component Library (`CardiTrack.Mobile/Controls`)

The shipped app already includes these reusable controls — designs should map onto (or consciously replace) them:

| Control | Type | Purpose |
|---------|------|---------|
| `AccordionSection` | XAML component | Generic collapsible section (header + expand hint + chevron); tucks the dashboard's Key Metrics behind a tap |
| `AlertListCard` | XAML component | One alert row on M1-10 (severity treatment, expand, acknowledge, call) |
| `ContactCardItem` | C# model | One slide of the Member Detail Emergency Contact / Phone carousel |
| `AlertMiniCard` | XAML component | M1-09 recent-alerts strip card |
| `AlertSkeletonCard` | XAML component | Loading placeholder for M1-10 alert rows |
| `AppChooserPage` | XAML component | App-styled list-of-choices sheet replacing `DisplayActionSheet` |
| `AppPopupPage` | XAML component | Transparent modal shell behind `PopupService`, above whatever root is active |
| `BottomNavBar` | XAML component | The app's bottom navigation, drawn in XAML rather than by Shell |
| `DashboardHeader` | XAML component | M1-09 gradient header: greeting, presence line, refresh, unread-alert bell |
| `DeviceCard` | XAML component | One connected wearable on M1-15, with its refresh / primary / remove actions and battery tile |
| `FilterChipBar` | C# control | M1-10 filter chips: All · Unread · Critical · Today · This Week |
| `HeaderBand` | XAML component | Gradient header band shared by every screen that has a header |
| `MemberAvatar` | XAML component | Member photo, or initials when there isn't one — shared by hero card and Member Detail |
| `MetricCard` | XAML component | One dashboard key-metric tile (value, status pill, star rating) |
| `MetricStatus` | C# helper | The one reading of a metric's status string → accent colour + pill wording |
| `MetricTrend` | C# model | One slide of the Member Detail Key Metric Trends carousel |
| `MetricTrendCard` | C# control | One trends-carousel card: metric icon, name, latest reading, status, chart |
| `NudgeCard` | XAML component | One notification in the inbox, with its comply / not-now / mute affordances |
| `NudgeMiniRow` | C# control | Compact nudge for the dashboard's "Complete the picture" slots and safety banners |
| `PopupCard` | C# control | Sizes the card `AppPopupPage` and `AppChooserPage` both draw themselves in |
| `QuestionCard` | XAML component | One question the service is asking a family, with its inline answer editor |
| `SkeletonView` | C# drawn control | Loading placeholder block with a gentle opacity pulse |
| `StarRatingView` | C# drawn control | A metric's rating against the member's own normal, drawn as five stars |
| `StatusHeroCard` | XAML component | M1-09 dashboard status hero |
| `TrendChart` | C# drawn control | Line chart inside a `MetricTrendCard` — daily series with baseline and typical-range marks |
| `TrendLegendSwatch` | C# control | Legend swatch naming a `TrendChart`'s comparison marks |
| `TrendWindowSelector` | C# control | 7 / 14 / 30-day segmented window picker above the trends carousel |
| `WeatherPopupPage` | XAML component | Session-weather sheet from the hero/detail weather chip |
| `QuickActionRow` | XAML component | SOS / Call / Message / Details row on alert detail and member detail |
| `AnsweredQuestionRow` | XAML component | One answered Q&A card with in-card delete |
| `WizardHeader` | XAML component | Onboarding wizard pages (title + "Step N of 4" progress) |

### What You Need to Define
- Color palette (brand colors, semantic colors, status colors)
- Typography scale (font family, sizes, weights)
- Spacing and layout grid
- Component library (buttons, cards, inputs, badges, etc.)
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

### Constraints

**Layout (binding project rule):**
- All pages/screens are **full-bleed** — edge-to-edge backgrounds and content, with safe-area insets for system UI only. Do not wrap a page in a rounded card/sheet or other page-level clipped chrome.
- Corner radius belongs on **components** (buttons, inputs, chips, logos, in-layout cards), never on the page shell.

**Accessibility (non-negotiable):**
- WCAG AA minimum contrast (4.5:1 for text, 3:1 for large text)
- Status must never rely on color alone — always pair with icon, text, or pattern
- Minimum 48x48dp touch targets on all tappable elements
- Dynamic font sizing support (user system preferences)
- All interactive elements must have screen reader labels
- Form labels must be programmatically associated with inputs

**Platform:**
- iOS: Follow Human Interface Guidelines (SF Symbols for icons, safe area insets, native modal drag handles)
- Android: Follow Material Design 3 conventions (FABs, bottom sheets, system back button)

**User context:**
- Primary users are 30-55 year old adults, but they may hand the phone to elderly parents (70+) — consider readability
- Critical alerts may be viewed in high-stress moments — design for quick scanning and large tap targets
- The app will be used in varied lighting conditions (bedside at night, outdoors)

---

## Asset Inventory

Icons use **SF Symbols** (iOS) and **Material Symbols** (Android) — no custom icon design needed. Third-party logos (Google, Apple, Fitbit, Garmin, Samsung, Withings, Visa) are sourced from vendor brand kits. Items below are the assets that need to be created or sourced.

### MVP 1 — Assets

#### Illustrations (Storyset / Blush or custom)

| # | Asset | Screen | Description |
|---|-------|--------|-------------|
| 1 | Onboarding Slide 1 | M1-02 | Happy elderly person wearing a smartwatch — warm, reassuring tone |
| 2 | Onboarding Slide 2 | M1-02 | Phone showing a health dashboard — conveys "at-a-glance monitoring" |
| 3 | Onboarding Slide 3 | M1-02 | Family members on their phones — conveys shared caregiving |
| 4 | Learning Phase | M1-08 | Brain with gears concept — "getting to know your loved one." Animated (Lottie). Source from LottieFiles marketplace or simplify to static illustration + platform progress animation |
| 5 | Empty Alerts | M1-10 | Muted bell or peaceful scene — "nothing to worry about" feeling |
| 6 | No Device Connected | M1-09 | Prompt to connect a device — friendly nudge, not an error |

**Style guidance:** All 6 illustrations must use the same art style and brand color palette. Choose one Storyset or Blush collection and customize colors for consistency. Tone: warm, caring, approachable — not clinical.

#### Brand Assets (custom — must be unique)

| # | Asset | Used On | Notes |
|---|-------|---------|-------|
| 1 | CardiTrack logo | M1-01 Splash, M1-02 Welcome, M1-06 OAuth, M4-02 Login | Export at multiple sizes: large (splash), small (header), favicon. SVG master. |
| 2 | App icon | Home screen, app stores | Must work at all OS-required sizes. Follow Apple and Google icon guidelines. |

#### Animations

| # | Animation | Screen | Source |
|---|-----------|--------|--------|
| 1 | Success checkmark | M1-07 | LottieFiles — search "success checkmark" (free options available). **As built:** `icon_status_check` on the status-green tile the dashboard uses, no animation. |
| 2 | Shimmer / skeleton loading | M1-09, M1-10, M2-03 | **As built:** custom `SkeletonView` control (M1-09). |
| 3 | Critical alert pulse | M1-12 | XAML/CSS animation — opacity + scale loop on severity banner |
| 4 | Learning phase brain/gears | M1-08 | LottieFiles — search "machine learning" or "brain processing." **As built:** `icon_monitoring` in the app's tinted icon tile, no Lottie. |

#### Third-Party Logos (vendor-provided, no design needed)

| # | Logo | Screen | Source |
|---|------|--------|--------|
| 1 | Google | M1-03 | Google Identity branding guidelines (SVG provided) |
| 2 | Apple | M1-03 | Apple Sign In SDK (renders automatically) |
| 3 | Fitbit | M1-05, M1-06, M1-15 | Fitbit/Google developer brand assets |
| 4 | Apple Watch | M1-05, M1-06, M1-15 | Apple marketing assets (MFi partners) |
| 5 | Garmin | M1-05, M1-06, M1-15 | Garmin Connect developer program |
| 6 | Samsung | M1-05, M1-06, M1-15 | Samsung developer brand kit |
| 7 | Withings | M1-05, M1-06, M1-15 | Withings Health API partner assets |
| 8 | Visa / card brands | M2-02 | Payment SDK (Stripe, etc.) includes card icons |

---

### MVP 2 — Assets

No new custom assets required. Management & Settings screens (M2-01–M2-04) use platform components, existing brand assets, and third-party logos already sourced in MVP 1.

---

### MVP 2 — Assets

#### Illustrations

| # | Asset | Screen | Description |
|---|-------|--------|-------------|
| 7 | Empty Members | M3-03 | "No one here yet" — friendly empty state, same art style as MVP 1 illustrations |

#### Animations

| # | Animation | Screen | Source |
|---|-----------|--------|--------|
| 5 | OCR processing steps | M3-06 | Custom step indicator animation (3 steps with progress). Can be built with XAML or sourced from LottieFiles — search "document scanning" |

#### Icons (platform — no custom design)

All icons in MVP 2 reuse platform icon sets. No new custom icons needed.

---

### MVP 3 — Assets

#### Icons (platform-provided)

| # | Icon | Screen | Source |
|---|------|--------|--------|
| 1 | Face ID | M4-01, M4-02 | iOS: SF Symbol `faceid` (system-provided) |
| 2 | Fingerprint | M4-01, M4-02 | Android: Material Symbol `fingerprint` (system-provided) |
| 3 | Crossed-out signal | M4-03 | SF Symbol `wifi.slash` / Material `signal_wifi_off` |

#### Widget Assets

| # | Asset | Screen | Notes |
|---|-------|--------|-------|
| 1 | Widget backgrounds | M4-06 | Defined by design system. Small (2x2), medium (4x2), large (4x4, iOS only). Must adapt to system light/dark mode. |

No new custom illustrations or animations needed for MVP 3.

---

### Asset Summary

| Category | MVP 1 | MVP 2 | MVP 3 | Total |
|----------|-------|-------|-------|-------|
| Custom illustrations | 6 | 1 | 0 | **7** |
| Brand assets (logo + icon) | 2 | 0 | 0 | **2** |
| Animations (Lottie / XAML) | 4 | 1 | 0 | **5** |
| Third-party logos | 8 | 0 | 0 | **8** |
| Widget assets | 0 | 0 | 1 | **1** |
| **Subtotal** | **20** | **2** | **1** | **23** |

**Truly custom** (must be designed): **2** — CardiTrack logo and app icon. Everything else can be sourced from Storyset/Blush (illustrations), LottieFiles (animations), vendor brand kits (logos), and platform icon sets (SF Symbols / Material Symbols).

---

## Figma Delivery Requirements

This project uses **Cursor + Figma MCP** to build the UI directly from Figma designs. The following requirements ensure the designs translate accurately into code.

### File Structure

- Organise screens into **named Pages** per MVP: `MVP 1 — Core Monitoring`, `MVP 2 — Management, Settings & Family Collaboration`, `MVP 3 — Native & Offline`
- Each screen must live in its own **named Frame**, using the Screen IDs from this document (e.g. `M1-09 Main Dashboard`)
- Group all reusable UI into a dedicated **`Components`** page

### Layout

- Use **Auto Layout** on every frame, section, and component — this is critical for accurate code generation
- Set explicit **spacing tokens** via Auto Layout gap/padding values (do not use manual nudging)
- Define a consistent **8pt grid** for all spacing and sizing

### Design Tokens (Figma Variables)

Set up the following as **Figma Variables** (not hardcoded values):

| Token Type | Examples |
|------------|---------|
| Colors | `color/status/normal`, `color/status/caution`, `color/status/urgent`, `color/status/critical`, `color/brand/primary`, `color/text/primary`, `color/background/card` |
| Typography | `text/heading/large`, `text/body/default`, `text/label/small` |
| Spacing | `space/xs` (4), `space/sm` (8), `space/md` (16), `space/lg` (24), `space/xl` (32) |
| Radius | `radius/card`, `radius/button`, `radius/chip` |

### Components

- Publish all reusable UI as **Figma Components** with variants (e.g. Button: Primary / Secondary / Destructive / Disabled)
- Name components using the pattern: `ComponentName/Variant` (e.g. `AlertCard/Critical`, `Button/Primary`)
- Use **component properties** (text, boolean, instance swap) so variants are machine-readable
- All interactive states must be defined as variants: Default, Hover, Pressed, Disabled, Loading, Error

### Naming Conventions

- All layers must be **named meaningfully** — no `Frame 42`, `Rectangle 7`, or `Group 3`
- Use camelCase or kebab-case consistently (e.g. `statusHeroCard` or `status-hero-card`)
- Hidden layers that are not part of the design must be deleted, not just hidden

### Handoff Checklist (per screen)

Before marking a screen as ready:
- [ ] Screen is in a named frame with the correct Screen ID
- [ ] All layers are named
- [ ] Auto Layout is applied throughout
- [ ] All colors and text styles reference Figma Variables (no hardcoded hex values)
- [ ] Interactive states are defined as component variants
- [ ] Responsive behaviour is defined (iPhone 14 base, with notes for larger screens)
- [ ] All four severity levels (Normal / Caution / Urgent / Critical) are visually tested in context

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

**Total Screens:** 68 designed (counting each state as a screen), plus 6 shipped screens without Figma M1 frames (SignIn, ForgotPassword, VerifyEmail, AccountSetup, Notifications, Questionnaires)
**MVP 1:** 37 screens — Core Monitoring (design first) — **16 of 17 Figma M1 screens built** as of August 14, 2026
**MVP 2:** 18 screens — Management, Settings & Family Collaboration (Q1 2027)
**MVP 3:** 13 screens — Native & Offline (Q2 2027)

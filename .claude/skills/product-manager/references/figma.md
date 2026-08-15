# Figma — the arbiter of UI scope

## The file

**Mobile M1 (MVP 1): https://www.figma.com/design/ux4slk0SA3BsAxFpGzv4NB**

Contains frames **M1-01 … M1-17** — 17 designed screens, 37 designed states. There is no separate web design file; web UI is specified in [ui_screens_blazor_web.md](../../../../docs/execution/ui/web/ui_screens_blazor_web.md) only.

## Project conventions — enforce these

1. **Only screens that exist in Figma get built.** If a PRD proposes a screen with no frame, the deliverable includes a design request, not a build ticket. ([apps/mobile/readme.md](../../../../docs/apps/mobile/readme.md) states this explicitly.)
2. **Only screens that exist in Figma get M1 IDs.** Never invent an ID for a shipped screen that lacks a frame — the four in the design-sync backlog below deliberately have none.
3. **Figma is the arbiter of state letters** (the `a`/`b`/`c` variations). Where code and Figma disagree, Figma wins or the frame gets updated at the next design sync — the code doesn't silently redefine the lettering.
4. Screen specs in `docs/execution/ui/` are the written companion to the frames. When they drift, treat it as a design-sync item and say so; don't quietly pick one.

## Frame index

| ID | Screen | States | Build status |
|---|---|---|---|
| M1-01 | Splash | 2 (a–b) | ✅ `SplashPage` |
| M1-02 | Welcome / Landing | 1 | ✅ `WelcomePage` |
| M1-03 | Sign Up | 4 (a–d) | ✅ `CreateAccountPage` |
| M1-04 | Add First CardiMember | 3 (a–c) | ✅ `AddCardiMemberPage` |
| M1-05 | Device Connection — Selection | 1 | ✅ `DeviceSelectionPage` |
| M1-06 | Device Connection — OAuth | 3 (a–c) | ✅ `DeviceConnectionPage` |
| M1-07 | Device Connection — Success | 3 (a–c) | ✅ `ConnectionSuccessPage` |
| M1-08 | Baseline Learning Info | 1 | ✅ `BaselineLearningPage` |
| M1-09 | Main Dashboard | 5 (a–e) + 2 as-built | ✅ `DashboardPage` |
| M1-10 | Alerts List | 4 (a–d) | ✅ `AlertsPage` |
| M1-11 | Alert Detail — Activity | 1 | ✅ `AlertDetailPage` (rule branch) |
| M1-12 | Alert Detail — Critical | 1 | ✅ `AlertDetailPage` (severity branch) |
| M1-13 | CardiMember Detail | 1 + 3 as-built | ✅ `CardiMemberDetailPage` |
| M1-14 | Edit CardiMember | 1 + 2 as-built | ✅ `EditCardiMemberPage` |
| M1-15 | Device Management | 1 + 3 as-built | ✅ `DeviceManagementPage` |
| M1-16 | Alert Detail — Heart Rate | 1 | ✅ `AlertDetailPage` (rule branch) |
| M1-17 | Health Data Export | 4 (a–d) | ❌ not built |

Source: [mvp1/screens.md](../../../../docs/execution/ui/mobile/mvp1/screens.md) (extract of the canonical [ui_screens_maui_mobile.md](../../../../docs/execution/ui/mobile/ui_screens_maui_mobile.md)).

> **The correct count is 16 of 17**, verified against `src/Presentation/CardiTrack.Mobile` (2026-08-14). Intermediate counts: 9 (onboarding + dashboard) → 12 (M1-13/14/15) → 13 (`AlertsPage` became the real M1-10 list on 2026-08-09) → 16 (`AlertDetailPage` covering M1-11/12/16). The only unbuilt Figma frame is **M1-17 Health Data Export**.

Unbuilt Figma work is now **export only**. The alerting loop (list + detail + ack/undo + push) is shipped; notes/photos and M1-17 remain open.

Frame → page mapping verified in code: M1-01 `SplashPage` · M1-02 `WelcomePage` · M1-03 `CreateAccountPage` · M1-04 `Onboarding/AddCardiMemberPage` · M1-05 `Onboarding/DeviceSelectionPage` · M1-06 `Onboarding/DeviceConnectionPage` · M1-07 `Onboarding/ConnectionSuccessPage` · M1-08 `Onboarding/BaselineLearningPage` · M1-09 `DashboardPage` · M1-10 `AlertsPage` · M1-11/12/16 `AlertDetailPage` · M1-13 `CardiMemberDetailPage` · M1-14 `EditCardiMemberPage` · M1-15 `DeviceManagementPage`. `FamilyPage` is a tab stub ("Family sharing (MVP 2) is coming soon"). `SettingsPage` is minimal (account card, Silenced reminders, M2-01 coming soon, Sign out). Verify-email dismiss lives on the **dashboard**, not Settings.

## Design-sync backlog

Carry these into any PRD that touches the affected surface:

| Item | Detail |
|---|---|
| **Ten shipped screens have no Figma frame** | `SignInPage`, `ForgotPasswordPage`, `VerifyEmailPage`, `Onboarding/AccountSetupPage`, plus `NotificationsPage`, `QuestionnairesPage`, `QuestionCard`, **`DaybookPage`**, **`DaybookEntryPage`** and `AskCard` on M1-13 — built and shipped, specced only in [ui_screens_maui_mobile.md § Shipped Screens Without Figma M1 Frames](../../../../docs/execution/ui/mobile/ui_screens_maui_mobile.md), no M1 IDs assigned |
| **The bottom bar's third tab changed** | Family → **Summaries**. `FamilyPage` was a stub for R3 family invitations and is deleted; `DaybookPage` lists the daybook entries. The M1 file's bar reads Home/Health/Alerts/Profile and matches neither, so the tab set is now two steps from the design — worth resolving at the next sync rather than accumulating a third. The `icon_tab_daybook` pair is hand-authored in the style of the other unselected glyphs |
| **M1-06 state-letter mismatch** | Shipped code maps **B = Error, C = Authorizing overlay** — the reverse of the frame's lettering. Figma is the arbiter; realign the code or update the frame at the next sync |
| **Flyout menu retired** | The Shell is TabBar-only with no edge-swipe flyout. The flyout concept in older spec text is retired unless re-introduced through Figma — don't spec navigation that assumes it |
| **Dashboard "as-built" states** | M1-09, M1-13, M1-14, M1-15 each have as-built states beyond the designed ones. Reconcile before adding new states |
| **No web design file** | Web has no Figma coverage at all, and the web app is still template-stage. Web PRDs must say whether they're requesting design or writing to the written spec |

## Reading the file with the Figma MCP tools

The Figma tools are deferred — load schemas first with `ToolSearch` (e.g. `select:mcp__figma__get_metadata,mcp__figma__get_screenshot,mcp__figma__get_design_context`). Requires an authenticated Figma MCP connection; if it isn't connected, say so rather than guessing at frame contents.

Typical PM flow:

1. `mcp__figma__get_metadata` on the file URL — enumerate frames and node IDs. **Use this to get real node IDs; never invent them.**
2. `mcp__figma__get_screenshot` on a frame — see the actual design before writing acceptance criteria about it.
3. `mcp__figma__get_design_context` — layout, components, and tokens for a frame, when criteria need to reference specific elements or states.
4. `mcp__figma__get_variable_defs` — design tokens (colour, spacing, type), e.g. checking the severity palette maps to red/orange/yellow/green as the matrix requires.
5. `mcp__figma__get_code_connect_map` — where a frame already maps to a `CardiTrack.Mobile` component, so a spec reuses rather than re-invents.

Before any **write** to Figma (`use_figma`, `generate_figma_design`, `create_new_file`), load the matching Figma skill first (`/figma-use`, `/figma-generate-design`) — the server requires it. As a PM, prefer reading; creating frames is a design decision, so propose it and get agreement rather than pushing speculative UI into the shared file.

## Severity colour contract

Internal `Critical / High / Medium / Low` maps to user-facing **red / orange / yellow / green** everywhere — matrix decision #5, defined in [llm_design.md](../../../../docs/llm_design.md). Any alert frame or spec using a different mapping is wrong.

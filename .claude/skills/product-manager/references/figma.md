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
| M1-06 | Device Connection — OAuth | 3 (a–c) | ✅ `FitbitConnectionPage` |
| M1-07 | Device Connection — Success | 3 (a–c) | ✅ `ConnectionSuccessPage` |
| M1-08 | Baseline Learning Info | 1 | ✅ `BaselineLearningPage` |
| M1-09 | Main Dashboard | 5 (a–e) + 2 as-built | ✅ `DashboardPage` |
| M1-10 | Alerts List | 4 (a–d) | ✅ `AlertsPage` |
| M1-11 | Alert Detail — Activity | 1 | ❌ not built |
| M1-12 | Alert Detail — Critical | 1 | ❌ not built |
| M1-13 | CardiMember Detail | 1 + 3 as-built | ✅ `CardiMemberDetailPage` |
| M1-14 | Edit CardiMember | 1 + 2 as-built | ✅ `EditCardiMemberPage` |
| M1-15 | Device Management | 1 + 3 as-built | ✅ `DeviceManagementPage` |
| M1-16 | Alert Detail — Heart Rate | 1 | ❌ not built |
| M1-17 | Health Data Export | 4 (a–d) | ❌ not built |

Source: [mvp1/screens.md](../../../../docs/execution/ui/mobile/mvp1/screens.md) (extract of the canonical [ui_screens_maui_mobile.md](../../../../docs/execution/ui/mobile/ui_screens_maui_mobile.md)).

> **The correct count is 13**, verified against `src/Presentation/CardiTrack.Mobile`: `CardiMemberDetailPage.xaml` (307 lines), `EditCardiMemberPage.xaml` (210) and `DeviceManagementPage.xaml` (142) are all real, substantial pages (checked 2026-08-08), and `AlertsPage.xaml` became the real M1-10 list on 2026-08-09 — it was a 13-line placeholder until then, which is where the earlier count of 12 came from.
>
> The build-status prose in `ui_screens_maui_mobile.md` and the `mvp1/` extracts previously said 9, contradicting their own tables; corrected at source (canonical doc + re-extract + PDF regen via `convert_to_pdf.py`) on 2026-08-09.

Unbuilt frames cluster hard around **alert detail and export** (M1-11, M1-12, M1-16, M1-17; M1-10 itself is now built) — which matches the release matrix showing the entire alerting loop as ⬜ Not started. That's the R1 critical path.

Frame → page mapping verified in code: M1-01 `SplashPage` · M1-02 `WelcomePage` · M1-03 `CreateAccountPage` · M1-04 `Onboarding/AddCardiMemberPage` · M1-05 `Onboarding/DeviceSelectionPage` · M1-06 `Onboarding/FitbitConnectionPage` · M1-07 `Onboarding/ConnectionSuccessPage` · M1-08 `Onboarding/BaselineLearningPage` · M1-09 `DashboardPage` · M1-13 `CardiMemberDetailPage` · M1-14 `EditCardiMemberPage` · M1-15 `DeviceManagementPage`. `FamilyPage` and `SettingsPage` also ship with no M1 frame, but they are documented tab stubs rather than design gaps — `FamilyPage` is a tab stub and `SettingsPage` is minimal (sign-out, verify-email nudge reset), per [apps/mobile/readme.md](../../../../docs/apps/mobile/readme.md).

## Design-sync backlog

Carry these into any PRD that touches the affected surface:

| Item | Detail |
|---|---|
| **Four shipped screens have no Figma frame** | `SignInPage`, `ForgotPasswordPage`, `VerifyEmailPage`, `Onboarding/AccountSetupPage` — built and shipped, specced only in [ui_screens_maui_mobile.md § Shipped Screens Without Figma M1 Frames](../../../../docs/execution/ui/mobile/ui_screens_maui_mobile.md), no M1 IDs assigned |
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

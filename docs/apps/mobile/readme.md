# CardiTrack Mobile App Documentation

## Overview

The CardiTrack Mobile App is a cross-platform **.NET 10 MAUI** application for family members and caregivers. What exists today is **all 17 Figma M1 screens** plus ten shipped surfaces that have no M1 frame: native Auth0 email/password and social login with a hard email-verification gate, the M1 onboarding wizard (account setup → add CardiMember → device selection → device connection (brand-agnostic) → baseline learning), Dashboard, Alerts list, **Alert detail** (`AlertDetailPage` covering M1-11/12/16), CardiMember detail/edit, device management, Questionnaires, Notifications, a weather popup, and a minimal Settings page. **CardiJournal** (Daybook, Weekbook and Monthbook entries) replaced the Family stub; no tab is a stub any more. **Push notifications are wired up** — see [Push notifications](#push-notifications). HealthKit/Health Connect is **retired**, not planned — see [Planned](#planned) for what replaced it. Reads are **cache-first**: every screen renders the device's last saved answer at once and refreshes behind it — see [Navigation & App Flow](#navigation--app-flow).

The app is built **code-behind first (XAML + `.xaml.cs`) — there is no MVVM layer**, no ViewModels folder, and no data-binding framework. Platform-independent logic (API client, Auth0 client, token handling, localization) lives in the separate plain-`net10.0` library **`CardiTrack.Mobile.Core`**, which is what the unit tests target.

> Figma governs M1 IDs — only screens that exist in Figma get an M1 ID. Several shipped screens (SignIn, ForgotPassword, VerifyEmail, AccountSetup, Notifications, Questionnaires) await design sync. Screen specs: [mobile screen specifications](../../execution/ui/mobile/ui_screens_maui_mobile.md), [MVP 1 user stories](../../execution/ui/mobile/mvp1/user_stories.md).

## Technology Stack

- **.NET 10 / .NET MAUI** (`Microsoft.Maui.Controls` 10.0.x): cross-platform UI
- **XAML + code-behind**: no MVVM — event handlers in `.xaml.cs`, DI via constructor injection for Shell tab pages and `ServiceHelper` for pages constructed with `new`
- **CardiTrack.Mobile.Core** (`net10.0` class library): `CardiTrackApiClient`, Auth0 auth stack, options, localization — unit-testable without MAUI
- **Auth0**: native password-realm login (no browser redirect)
- **SecureStorage** (`SecureTokenStore`): token persistence — there is **no SQLite** in the app
- **`EncryptedFileOfflineReadCache`** (`Mobile.Core/Offline`): the last successful body of every `GET`, AES-256-GCM per file under `FileSystem.AppDataDirectory/offline-cache`, keyed by the request path, with the key in the platform keystore. Fail-closed — a device that cannot encrypt does not cache. Wiped and re-keyed on sign-out
- **Datadog.Maui** (Android/iOS only): **logs, traces and RUM with Datadog crash reporting**, on by default with a Settings opt-out (crashes/ANRs also in Play Console vitals), wired through the `MobileApm` registry
- **Serilog** (`AppLogging`): debug + local-file sinks; logs stay on device (no remote log shipping outside the APM engine). File lines are prefixed with the app version (`v<ApplicationDisplayVersion>`, set from the release tag by the signed CI builds) so a support bundle names the build that wrote it

## Platform Support

`TargetFrameworks` is **OS-conditional** in the csproj — you can only build the targets your host OS supports:

| Host OS | Targets |
|---|---|
| Windows | `net10.0-android;net10.0-windows10.0.19041.0` |
| macOS | `net10.0-ios` |
| Linux | `net10.0-android` |

**iOS cannot be built on Windows** — CI's macOS runner (or a Mac) produces iOS builds.

Platform minimums:

- **iOS**: 17.0
- **Android**: API 31 (Android 12) — raised for the **Android 12 SplashScreen API**, so one splash design matches the OS handover on every supported device (the csproj comment explains; 23 was the old Datadog/Firebase floor)
- **Windows**: 10.0.17763.0, optional dev convenience target (`WindowsPackageType=None` — unpackaged, no MSIX identity; Datadog does not support Windows)
- **MacCatalyst**: **not targeted** (the `Platforms/MacCatalyst` folder is template residue)

App identity: `com.codesistance.carditrack.mobile`, display name **CardiTrack**. Release Android builds run R8 (`AndroidLinkTool=r8`).

## Project Structure (actual)

```
src/Presentation/CardiTrack.Mobile/
├── SplashPage / WelcomePage                  # Entry + carousel (WelcomeSlide model)
├── SignInPage / CreateAccountPage            # Auth0 credential forms + wired Google/Apple social buttons
├── ForgotPasswordPage / VerifyEmailPage      # Reset flow; hard email-verification gate
├── DashboardPage                             # Main tab — hero, quick actions, key metrics, nudges
├── AlertsPage                                # Real M1-10 alerts list (API-backed)
├── JournalPage                             # Tab — the CardiJournal; Days / Weeks / Months control
├── SettingsPage                              # Account card, mutes, four grouped sections, sign-out, delete request
├── CardiMemberDetailPage                     # M1-13 (routed page)
├── EditCardiMemberPage                       # M1-14 (routed page)
├── DeviceManagementPage                      # M1-15 (routed page)
├── QuestionnairesPage                        # Questions & Answers archive (routed page)
├── NotificationsPage                         # Data-completeness / nudge inbox (routed page)
├── Onboarding/
│   ├── AccountSetupPage                      # Account-type choice (no Figma frame; atomic setup call)
│   ├── AddCardiMemberPage                    # M1-04: first CardiMember (skippable)
│   ├── DeviceSelectionPage                   # M1-05: Fitbit / Pixel Watch selection
│   ├── DeviceConnectionPage                  # M1-06: brand-agnostic OAuth explainer + round-trip
│   ├── ConnectionSuccessPage                 # M1-07
│   └── BaselineLearningPage                  # M1-08
├── Controls/                                 # 30 shared controls: AccordionSection, AlertListCard,
│                                             # AlertMiniCard, AlertSkeletonCard, AnsweredQuestionRow,
│                                             # AppChooserPage, AppPopupPage, BottomNavBar,
│                                             # DashboardHeader, DeviceCard, FilterChipBar, HeaderBand,
│                                             # MemberAvatar, MetricCard, MetricStatus, MetricTrend,
│                                             # MetricTrendCard, NudgeCard, NudgeMiniRow, PopupCard,
│                                             # QuestionCard, QuickActionRow, SkeletonView,
│                                             # StarRatingView, StatusHeroCard, TrendChart,
│                                             # TrendLegendSwatch, TrendWindowSelector, WeatherPopupPage,
│                                             # WizardHeader
├── Notifications/
│   └── PushRegistrationCoordinator.cs        # FCM token registration + tapped-push routing (Android/iOS)
├── Services/
│   ├── AppLogging.cs                         # Serilog config + unhandled-exception hooks
│   ├── AppResumeNotifier.cs                  # Fans out Window.Resumed to the refresh plumbing
│   ├── AppStartup.cs                         # Startup sequencing
│   ├── BackNavigation.cs                     # Shared back-navigation helper
│   ├── IPopupService.cs / PopupService.cs    # App-styled popups (AppPopupPage / AppChooserPage)
│   ├── MobileApm.cs                          # APM engine registry (Datadog)
│   ├── NameFormatting.cs                     # Display-name helpers
│   ├── PeriodicRefresh.cs                    # 30 s in-app polling for live screens
│   ├── PostLoginRouter.cs                    # Root-page routing after login
│   ├── RelativeTime.cs
│   ├── ResumeRefresh.cs / ScreenRefresh.cs   # Unattended-refresh plumbing (foreground / visible page)
│   ├── SecureStorageKeyValueStore.cs         # ISecureKeyValueStore over SecureStorage
│   ├── SecureTokenStore.cs                   # ITokenStore over SecureStorage
│   ├── ServiceHelper.cs                      # Service locator for non-DI pages
│   └── WebBrowserAuthenticator.cs            # System-browser hand-off for social PKCE sign-in
├── AppConfig.cs                              # Build-time config from assembly metadata
├── WindowNavigation.cs                       # Root-page swaps
├── AppShell.xaml                             # TabBar-only shell + routed-page registration
├── Local.props.sample                        # Dev-local config overrides (git-ignored copy)
├── MauiProgram.cs
└── Platforms/ (Android, iOS, Windows, MacCatalyst)

src/Presentation/CardiTrack.Mobile.Core/
├── Api/           # ICardiTrackApiClient, CardiTrackApiClient, ApiException
├── Auth/          # Auth0AuthClient, AuthService, TokenRefresher, JwtPayloadReader, Pkce,
│                  # IBrowserAuthenticator, AccessTokenAudience, AuthTokens, ITokenStore,
│                  # AuthErrorCode/AuthException
├── Charts/        # TrendScale, MetricExplanations (trend-chart maths + copy)
├── Configuration/ # ApiOptions, Auth0Options
├── Devices/       # ConnectableDevice, DeviceDataset(s) — the brand/data-source catalogue
├── Http/          # AuthHttpMessageHandler (bearer attach + refresh), ClientHeaders
├── Localization/  # PhonePlaceholder
├── Notifications/ # NudgeCopy, NudgeDestination, PushDeviceRegistrationService
├── Onboarding/    # CardiMemberDraft(Store), FileDraftPhotoStore, PostLoginRouteResolver,
│                  # PrimaryCardiMember, ISecureKeyValueStore
└── Questionnaires/# MemberQuestionnaires
```

## Navigation & App Flow

### Shell

`AppShell.xaml` is a **TabBar-only Shell** — four tabs (Dashboard, Alerts, Summaries, Settings); there is no flyout. Tab pages resolve through DI (`AddTransient` in `MauiProgram`).

The **platform tab bar is hidden** (`Shell.TabBarIsVisible="False"` on each tab page) and `Controls/BottomNavBar` draws the Figma bar (node `101:2949`) instead — the native bar cannot swap an icon on selection or carry the design's upward shadow. Shell still owns routing: each item navigates `GoToAsync("//route")`, and a tap on the tab you are already on is swallowed so it can't pop a page pushed above it. Each host page sets `Tab="…"`, which picks the gradient `_active` glyph and the `PrimaryDark` label.

> Figma's bar reads Home / Health / Alerts / Profile, but the M1 file has no Health or Profile screen, so the tab set does not follow it. Dashboard and Alerts use the exported Figma glyphs; Summaries and Settings keep hand-authored ones, with `_active` variants that apply the tab-bar gradient to the existing stroke rather than inventing a filled glyph.
>
> **The Journal tab replaced Family.** The Family tab was a stub for family invitations, which are R3 work — a permanent quarter of the bar spent on a "coming soon" card, while the Daybook entries had no surface. The tab is labelled **Journal** and titled **CardiJournal** in its header: the umbrella that holds the Daybook today, and the Weekbook and Monthbook when they land (R2). `FamilyPage` is deleted; when family sharing lands it belongs under Settings or scoped to a member, not back in the bar. The tab reads the Daybook and Weekbook series through one page, switched by a Days / Weeks / Months control; `JournalPage` has **no Figma frame** and therefore no M1 ID — a design-sync gap, not a built frame.

Android back at a **tab root** finishes the activity, except on the dashboard: the first swipe shows "Go back again to leave CardiTrack" and a second swipe within two seconds is what actually leaves (`TabNavigation.TryHoldDashboardExit`, armed in `MainActivity`'s back callback alongside the origin return). Other tabs keep the one-press exit. iOS has no system-back-to-exit, so this is Android-only.

### Auth & onboarding flow

```
Splash → Welcome → SignIn / CreateAccount
                     └→ VerifyEmail (hard gate: unverified accounts go here, not into the app)
                          └→ PostLoginRouter:
                               no server user record   → AccountSetupPage (wizard)
                               user but no CardiMember → AddCardiMemberPage (wizard, skippable)
                               otherwise               → AppShell (Dashboard)
```

- `PostLoginRouter` asks the API for onboarding status and swaps the window's **root page** (`WindowNavigation.SetRootPage`) — the wizard runs in a `NavigationPage`, outside the Shell.
- **Onboarding pages hide the tab bar** (`Shell.TabBarIsVisible="False"` on the wizard pages) so the wizard also renders chrome-free when pushed over the Shell.
- **Dashboard empty state** (recent change): "Add your first CardiMember" now pushes `AddCardiMemberPage` — the real M1-04 wizard page — directly onto the navigation stack, instead of the previous "Coming soon" alert.
- `AddCardiMemberPage` **Skip** is context-aware: pushed from the dashboard it pops back; as the onboarding root it hands over to a fresh `AppShell`.
- **Member details** are reachable two ways from the dashboard: tapping the status hero card, or the "View Details" quick action. Both route to `CardiMemberDetailPage` (M1-13), which in turn reaches `EditCardiMemberPage` (M1-14) and `DeviceManagementPage` (M1-15).
- The app has **six routed (non-tab) pages** — `CardiMemberDetailPage`, `EditCardiMemberPage`, `DeviceManagementPage`, `QuestionnairesPage` (Questions & Answers, from M1-13), `NotificationsPage` (the nudge inbox, from the dashboard's "Complete the picture" section), and `AlertDetailPage` (M1-11/12/16, from the alerts list, dashboard tiles, and `carditrack://alerts/{id}` pushes): registered with `Routing.RegisterRoute` in `AppShell` and navigated to as `GoToAsync("<route>?…")`, resolved through DI like the tab pages.
- **Refresh** (header button, hero-card sync button, and pull-to-refresh) calls `POST .../devices/sync` and *then* reloads, so it pulls from the wearable rather than re-reading what the Worker last stored. The button disables and the `RefreshView` spinner runs for the duration; a refused sync (paused, no device, too soon) is reported afterwards rather than swallowed. The reload happens either way, so a merely stale screen still catches up.
- **Auto-refresh on foreground**: the app's `Window.Resumed` event is fanned out by `AppResumeNotifier`, and `ResumeRefresh.RefreshWhenAppResumes` (wired in the constructor of Dashboard, Alerts, Notifications, CardiMember Detail and Device Management) reloads whichever of those is the screen on display. So returning to the app already shows what the Workers processed while it was away — pull-to-refresh is no longer the only way to see new data. `OnAppearing` alone would not do: it is a navigation event, raised again on resume by Android but not by iOS. Details:
  - **Read, not sync** — the server collects from the wearable on its own (webhook-triggered within seconds, with `WearableSyncWorker`'s 10-minute poll as the loss-proof fallback), so the gap on screen is the fetch, not the collection; a `devices/sync` on every foreground would also hit the "too soon" refusal and pop a dialog nobody asked for. The explicit Refresh actions still sync.
  - **Only the visible page** reloads (Shell keeps visited tabs alive), and not while a modal — the connect-device wizard — is over it.
  - **Silent**: a resume refresh that fails leaves what is on screen alone instead of raising "Couldn't refresh".
- **Three unattended triggers, one path.** Every live screen now routes arriving on the page (`OnAppearing`), the app resuming, and the periodic tick through a single `RefreshUnattendedAsync`, gated only by `ResumeRefresh.MinimumGap` (5 s — it exists so a load that has just run is not repeated, since Android raises `OnAppearing` again on resume and iOS does not). The per-page "skip the load if the last one was under 2–5 minutes old" windows are **gone**: a caregiver who navigates to a screen is asking for its current state, and could previously be handed one minutes stale with no request in flight.
- **In-app polling** (`PeriodicRefresh.RefreshEvery`, wired in the constructor of Dashboard, CardiMember Detail and Alerts): a screen left open also re-reads itself every **`PeriodicRefresh.LiveDataInterval` = 30 s**, under the same visible-page and silent-failure rules. All of this is a pull of **already-computed values** — `GET` against the dashboard / member-detail / alerts / notifications endpoints — never a device sync. 30 s rather than the 2–5 min these screens used to sit at: those windows were set when the Worker's 10-minute poll was the only way data arrived, and webhook-triggered syncs plus the 5-minute GCP aggregator/assessor jobs mean a reading, an assessment or an alert can now land at any moment (see [data_sync_architecture.md](../../technical/data_sync_architecture.md)).
- **Cache-first reads (stale-while-revalidate).** Landing on a screen with nothing on it puts up the device's saved answer to *exactly* that request — same path, same query — and fetches the live one behind it. The loading skeleton is now only for a question the device has never answered. While saved data is up the screen says so (`SavedDataBanner`: "Showing data saved 10 minutes ago — checking for updates…", or "You're offline…" / "Couldn't refresh…"), and when the live answer replaces it a one-second `UpdatingOverlay` marks the swap. Rules worth knowing: the overlay marks **only** the saved→fresh transition, never a cold load and never the 30 s tick or a resume, which replace in place and silently; a live answer equal to the saved one is not redrawn at all (a finished day's journal entry, unchanged settings); and a 404 over a saved answer drops it for the error panel rather than letting it stand in for something that is gone. The sequence is `Offline/SnapshotRefresh.RunAsync`, guarded by `LoadGate`; **await it from the UI thread — nothing in `Mobile.Core` may use `ConfigureAwait(false)`**, since the render and feedback callbacks run on the caller's context. A successful mutation evicts the keys it makes stale, and a 404 evicts its own.
- **Call / Send Message dial `CardiMember.Phone`**; **SOS dials the emergency contact**. M1-14 captures Phone Number; when it is empty the Call/Message tiles offer to add one. The SOS tile dims when `EmergencyContactPhone` is unset and stays tappable.
- **Alerts tab**: `AlertsPage` is the real M1-10 list, backed by `GET /api/v1/alerts`, with filter chips, date-grouped `AlertListCard` rows, inline expand, call and acknowledge actions, and the empty / filtered-empty / loading / error states. Tap opens `AlertDetailPage` (M1-11/12/16).
- **Dashboard recent-alerts strip** shows **unresolved alerts only** (acknowledged-but-open still appear; resolved/settled do not). There is no "View Trends & History" control on the dashboard — trends live on M1-13's carousel. The hero status is a **single AI sentence** (under 15 words); **"Loading" appears only after 1.5 s**. A weather chip on the hero opens `WeatherPopupPage` (session weather, not live). Manual sync is refused with "too soon" after a recent pull.

## Configuration

Build-time configuration is stamped into the assembly as **MSBuild properties → `AssemblyMetadata`**, read at runtime by `AppConfig` (no appsettings.json in the app):

- Keys: `ApiBaseUrl`, `Auth0Domain`, `Auth0ClientId`, `Auth0Audience`, `ApmEngine`, `ApmData`.
- Defaults in the csproj: Debug → `https://api.dev.carditrack.com`, Release → `https://api.carditrack.com`; Auth0/APM values default empty.
- **Local development**: copy `Local.props.sample` to `Local.props` (git-ignored) — e.g. `ApiBaseUrl` `http://10.0.2.2:5230` for the Android emulator against a local API (cleartext allowed via `network_security_config.xml`), plus the Auth0 dev-tenant identifiers.
- **CI** stamps values with `-p:ApiBaseUrl=... -p:Auth0Domain=...` etc.
- `AppConfig.Validate()` runs at startup: a missing/invalid `ApiBaseUrl` throws; empty Auth0 values are tolerated (auth then fails with `AuthErrorCode.NotConfigured`).

## Authentication

All auth logic lives in `CardiTrack.Mobile.Core/Auth`:

- **`Auth0AuthClient`** — email/password uses native, embedded login via Auth0's **password-realm grant** (`http://auth0.com/oauth/grant-type/password-realm`) against the tenant's DB connection (no browser); signup and password reset go through `/dbconnections`. **Social sign-in (Google/Apple)** uses the **system browser with the PKCE authorization-code flow** (`WebBrowserAuthenticator` + `Pkce`) — Android/iOS only; the Windows target falls back to an error message.
- **`TokenRefresher`** — `refresh_token` grant; **`JwtPayloadReader`** extracts claims (email, verification state) from access tokens without validation (validation is the API's job).
- **`AuthHttpMessageHandler`** — DelegatingHandler on the API client: attaches the bearer token and coordinates refresh. The Auth0 client deliberately has **no** auth handler, so login/refresh calls can't recurse through the bearer pipeline.
- **`SecureTokenStore`** — tokens persist in platform `SecureStorage` (Keychain / Keystore). No database.
- **Email verification is a hard gate**: sign-in with an unverified account lands on `VerifyEmailPage` (which can resend, rate-limited server-side at 5/hour/IP); the dashboard additionally shows a dismissible verify-email nudge while `IsEmailVerified == false`.

## Device OAuth Deep Link

Wearable (Fitbit / Google Health API) OAuth returns to the app via the **`carditrack://` custom scheme**:

- **iOS**: `CFBundleURLTypes` in `Platforms/iOS/Info.plist` registers the `carditrack` scheme.
- **Android**: `WebAuthenticationCallbackActivity` (intent filter with `DataScheme = "carditrack"`).
- Google's web OAuth clients cannot redirect to a custom scheme, so the provider first redirects to the API's **https bounce endpoint** (`GET /api/v1/oauth/redirect/fitbit`), which hands off into the deep link.
- **The deep link is the only thing that dismisses the in-app browser.** Whatever the provider returns — grant, denial or malformed callback — the bounce endpoint hands off into `carditrack://oauth/callback`, carrying `error`/`error_description` when there is no code. An endpoint that ends the response in the browser instead strands the user on the consent page with the app still waiting behind it.
- The hand-off is an **HTML page that calls `location.replace()`**, not a bare 302: a `Location` header naming a custom scheme is honoured by Chrome Custom Tabs and `ASWebAuthenticationSession` but dropped by browsers and proxies that only forward http(s). The page then **closes the tab** (`window.close()`, and on Android an `intent://` URL naming `com.codesistance.carditrack.mobile`) so it cannot sit in the task for "Go to Dashboard" to walk back into. A tappable fallback remains for browsers that block scripted close.

### Running against a locally-hosted API

`appsettings.json` ships the bounce as `https://localhost:7001/...`, which works for Swagger on the dev box but **not from a phone or emulator** — there `localhost` is the device itself, so the provider's redirect dies on a connection error and no deep link is ever fired. Either:

- point the app at the deployed dev API (the default for Debug builds), or
- run the API over the LAN/`10.0.2.2` (Android emulator's alias for the host) and set `DeviceProviders__0__RedirectUri` to that address — it must also be registered verbatim as an authorized redirect URI on the Google client.

## Monitoring (Mobile APM)

`Services/MobileApm.cs` is the mobile twin of the server's `ApmProviderRegistry` (shipped in PR #4):

- **Engine selection**: `AppConfig.ApmEngine` names an entry in the registry — currently **Datadog only**. `ApmData` carries that engine's client-side connection JSON (`{"ClientToken":"pub...","ApplicationId":"...","Site":"Uk1","IntakeHost":"browser-intake-uk1-datadoghq.com"}`), stamped by CI as base64 (raw JSON accepted from `Local.props`). The client token and application id are write-only identifiers, safe to embed.
- **Fail-soft**: unlike the server, an unknown engine or malformed data **logs and disables monitoring** instead of failing — a monitoring misconfiguration must never brick the app. Unstamped builds ship nothing.
- **Datadog config**: `Datadog.Maui` package on **Android/iOS only** (no Windows support); logs and traces always, **RUM when `ApplicationId` is set** (sessions, views, errors, and Datadog crash reporting — `NativeCrashReportEnabled` follows it); site defaults to **Eu1** when unset; service name `carditrack-mobile`; environment tag derived from the API base URL (dev/prod).
- **UK1 is reached through `IntakeHost`.** Our org is on **UK1**, which neither `Datadog.Maui`'s `DatadogSite` enum nor the native SDKs it bundles can name, so `IntakeHost` points every feature at that host's intake through the SDK's per-feature custom endpoint — the same mechanism on Android and iOS. The app composes the full per-feature URLs (`Mobile.Core/Diagnostics/DatadogIntake.cs`) because the native SDKs use a custom endpoint verbatim: a bare host posts to the site root and returns `404`. A `Site` the enum cannot name with no `IntakeHost` **disables monitoring outright** rather than falling back. Details in the [APM setup runbook](../../technical/apm_setup_runbook.md) §5.
- **RUM stays clear of names**: views are named from the Shell route or the page class, never `Page.Title`; automatic action tracking is off (an action is named after the control it hit, and a member card's text is that member's name); automatic resource tracking is off (request URLs ship verbatim, and journal search text and caregiver invite tokens travel in them). **Crashes and ANRs also remain in Play Console** (Quality → Android vitals).
- **Session Replay is deliberately NOT enabled** — health data must not be recorded.
- **`FirstPartyHosts`**: the API host is marked first-party with W3C `traceparent` (+ Datadog) headers, so mobile spans join the API's OTel traces.
- **On by default, with an opt-out** (since 2026-09-24; opt-in before that): `TrackingConsent` starts at `Granted` and drops to `NotGranted` when the caregiver turns off Settings → Privacy → **Send session telemetry**; it is disclosed in the Terms of Service and Privacy Policy. `DiagnosticsConsent` (`Services/DiagnosticsConsent.cs`) owns the stored choice (only ever the caregiver's "off") and hands each change straight to `DdSdk.SetTrackingConsent`, so a toggle takes effect without a restart; sign-out removes it and returns to the default, so one caregiver's "off" is not inherited by the next caregiver on the same phone; an unreadable preference store falls to off. `NotGranted` rather than `Pending` is deliberate — Pending would still collect and hold events on the device, so "off" would not mean off. A one-time dashboard modal (`TelemetryNotice`, `DashboardPage.ShowTelemetryNoticeIfDueAsync`) tells each caregiver what is sent and where to turn it off; "Got it" or "Open Settings" records it as seen under `TelemetryNoticeSeenFor` (a per-caregiver hash, cleared on sign-out), and Back does not.
- **Provisioning**: the `apm_mobile_engine` tfvar plus the per-environment secrets `carditrack-<env>-apm-mobile-engine` / `carditrack-<env>-apm-mobile-data` (env stacks, not `common/`) feed CI's `-p:ApmEngine`/`-p:ApmData` stamping — see the [APM setup runbook](../../technical/apm_setup_runbook.md).

## Push notifications

The device half of the push spine (`docs/technical/notification_engine.md` Phase 3). **`Plugin.Firebase.CloudMessaging`**, referenced on **Android/iOS only** — the type does not exist on the Windows target, so `Notifications/PushRegistrationCoordinator.cs` is wrapped in `#if ANDROID || IOS` in its entirety.

- **Registration**: the coordinator retrieves the FCM token and registers it with `POST api/v1/notifications/devices`, keyed by a self-minted device GUID held in `ISecureKeyValueStore` (never a hardware id). It runs after the first device connection succeeds (§4's "moment of value") and again on **every foreground** (`AppShell.WirePush`), which doubles as the reachability heartbeat the server ages tokens against.
- **Permission**: Android's `POST_NOTIFICATIONS` is requested explicitly and the **real** grant status is reported, along with whether the Safety channel is actually enabled. Both were hardcoded to "granted" until PR #246, which meant `PUSH_UNREACHABLE` — the signal for *nobody is listening* — could never arm on Android. iOS keeps the plugin's own `UNUserNotificationCenter` prompt.
- **Channels** (Android): `carditrack.safety.v2` at IMPORTANCE_HIGH (wakes from Doze, bundled `carditrack_alert` sound + vibration), `carditrack.health.v4` at HIGH (same sound, no vibration; v3 was Default and often silent), `carditrack.nudges.v2` at Default (`carditrack_nudge` ding, no vibration). Channel ids are versioned because Android freezes a channel's sound/vibration on first create. iOS plays `carditrack_alert.wav` for Safety/Health and `carditrack_nudge.wav` for Nudges.
- **Receipt**: the background handler acks with `POST api/v1/notifications/{deliveryId}/delivered` **before any user interaction** — a missed ack is what the escalation ladder keys off, so it must not wait for a tap. Tapping parses the payload's deep link through `NudgeLinkParser` and navigates.
- **Payloads are PHI-free teasers** via `PushTeaser` (e.g. "Heart rate alert" / "Open CardiTrack to check on this.") — no names or metrics cross APNs or FCM. The iOS Notification Service Extension that would rewrite them into richer copy on-device is **deferred** (§17), so iOS shows the teaser as sent.
- **Firebase config**: `Platforms/Android/google-services.json` and `Platforms/iOS/GoogleService-Info.plist`, wired as `GoogleServicesJson` / `BundleResource` items in the csproj. They were missing from the build until 2026-08-13 — the frameworks shipped without them, `GetTokenAsync()` threw *"Default FirebaseApp is not initialized"* on every launch, and no device ever registered. If push silently stops working, check those items first.

## Localization

PR #8 added region-localized **emergency-phone placeholders** (`CardiTrack.Mobile.Core/Localization/PhonePlaceholder.cs`): US/CA `+1 555 000 0000`, GB `+44 7700 900000` (Ofcom drama range), any other region falls back to the US format. The placeholder is resolved once at page construction from `RegionInfo.CurrentRegion` (e.g. `AddCardiMemberPage`'s emergency-contact field).

## Privacy & Platform Manifests

- **iOS**: `Platforms/iOS/Resources/PrivacyInfo.xcprivacy` — Apple privacy manifest (required-reason API declarations).
- **Android**: `Platforms/Android/Resources/xml/network_security_config.xml` — permits cleartext to the emulator loopback (`10.0.2.2`) for local development only.

## Building and Deploying

### Prerequisites

- .NET 10 SDK with the MAUI workloads (`dotnet workload install maui` — pulls the matching Android SDK/JDK toolchain)
- Visual Studio 2025 or VS Code with the .NET MAUI extension
- **Xcode 16+** on macOS for iOS builds (iOS cannot be built on Windows)

### Build & run

```bash
cd src/Presentation/CardiTrack.Mobile

# Android (Windows/macOS/Linux)
dotnet build -f net10.0-android                                          # compile only (optional — e.g. to warm the build while the emulator boots)
dotnet build -f net10.0-android -t:Run "-p:AdbTarget=-s emulator-5554"   # compile + deploy to + launch on the running emulator/device

# iOS (macOS only)
dotnet build -f net10.0-ios
dotnet build -f net10.0-ios -c Release -p:RuntimeIdentifier=ios-arm64 -t:Run

# Windows (Windows only, unpackaged)
dotnet build -f net10.0-windows10.0.19041.0
```

Starting the emulator, unlocking it, deploying, driving it from `adb`, screenshots, sign-in, and every known pitfall (Fast Deployment, keyguard, bad snapshots, path length) are in the **[Android emulator runbook](../../technical/android_emulator_runbook.md)** — read it before doing UI work against a device.

### Store builds

Signed store builds are normally produced by CI (below). For a local signed Android AAB, use the same properties CI uses (note the plural `-p:AndroidPackageFormats=aab`):

```bash
dotnet publish src/Presentation/CardiTrack.Mobile/CardiTrack.Mobile.csproj \
  -f net10.0-android -c Release \
  -p:AndroidPackageFormats=aab -p:AndroidKeyStore=true \
  -p:AndroidSigningKeyStore=<path>.jks -p:AndroidSigningKeyAlias=carditrack \
  -p:AndroidSigningStorePass=<password> -p:AndroidSigningKeyPass=<password>
```

iOS release signing uses `CodesignKey=Apple Distribution` and `CodesignProvision=CardiTrack Distribution`. Certificates, profiles, keystores, and store accounts are covered step-by-step in **[store_provisioning.md](./store_provisioning.md)**.

### CI/CD Pipeline

Mobile is **built** by `.github/workflows/deploy-apps-dev.yml`, one tick per platform (`mobile_android`, `mobile_ios`, and `mobile_windows` as a compile check that archives nothing), and **pushed** to the stores by `.github/workflows/deploy-mobile-dev.yml`, by release tag. The split matters because the iOS device build is ~13 macOS minutes at ten times the Linux price: tick iOS when you want a TestFlight candidate, not by default. `deploy-apps-prod.yml` carries the production track.

```bash
# Build (main: signed + archived + tagged; any other ref: unsigned compile check)
# The service lanes default to on; switch them off for a mobile-only run.
gh workflow run deploy-apps-dev.yml --ref main -f api=false -f web=false -f worker=false -f pipeline=false -f webhook=false -f mobile_android=true                      # Android only, no Mac
gh workflow run deploy-apps-dev.yml --ref main -f api=false -f web=false -f worker=false -f pipeline=false -f webhook=false -f mobile_android=true -f mobile_ios=true   # TestFlight candidate too
gh workflow run deploy-apps-dev.yml --ref <branch> -f api=false -f web=false -f worker=false -f pipeline=false -f webhook=false -f mobile_android=true -f mobile_ios=true   # compile check; ships nothing

# Push (main only; builds nothing, takes the tag's binaries from the builds bucket)
gh workflow run deploy-mobile-dev.yml --ref main -f platform=android                 # latest v* tag
gh workflow run deploy-mobile-dev.yml --ref main -f platform=ios -f tag=v0.2.308
```

- **Any other dispatched ref** — the ticked platforms run the unsigned compile gates (Release Android, Debug iOS simulator; no secrets, a few minutes). This is the only compile coverage a mobile change on a branch gets: there is no push or PR lane at all, and the gated-off one it replaced never built mobile even when it was on (#1111).
- **`main`** — the ticked platforms are built signed, archived to the GCS builds bucket under the release tag, and the tag is created only once that archive has landed. **Deploy Mobile → Dev** then pushes them:
  - **Android**: a signed AAB + APK is produced (`build-mobile-android-signed`) and the AAB is uploaded to the **Play Console internal testing track** (`deploy-play-internal` in `deploy-mobile-dev.yml`). Release builds run R8 (`AndroidLinkTool=r8` in the csproj), and the upload includes the R8 deobfuscation map (`mapping.txt`) plus a `native-debug-symbols.zip` built from the pre-strip native libraries (`obj/**/app_shared_libraries`), so Play crash reports show readable stack traces. Note: symbol coverage extends to the app's own native libs; Microsoft does not ship unstripped Mono runtime libraries, so frames inside e.g. `libmonosgen-2.0.so` remain unsymbolicated.
  - **iOS**: a signed device IPA is produced (`build-mobile-ios-device`) and uploaded to **TestFlight** (`deploy-testflight` in `deploy-mobile-dev.yml`) via the App Store Connect API. The build job zips the build's `.dSYM` and ships it as the `mobile-ios-symbols` artifact (90-day retention) and to `…/ios/symbols/` in the builds bucket, because the store binary is stripped and that bundle is the only thing that can name the frames in a crash report — AOT'd managed methods included. The step fails the build if no `.dSYM` is found: a build nobody can symbolicate is indistinguishable from a working one until the first crash arrives.
  - Signed artifacts are archived to GCS under the release tag (`upload-mobile-artifacts`, in the pipeline); the push jobs download them from there.

Store versioning is stamped by CI at build time: `ApplicationDisplayVersion` comes from the computed semver tag, and `ApplicationVersion` (iOS build number / Android versionCode) is derived from that same tag by `.github/scripts/mobile-build-number.py` as `MAJOR × 10,000,000 + MINOR × 10,000 + PATCH` — so `v0.2.318` builds as `20318`. The values in the csproj are placeholders.

It was the commit count until 2026-09. That is not a function of the tag, which is what broke it: two tags minted on one commit got the same number, and because neither store ever forgets a build number, the second was unpushable for the life of the app. It had happened seven times, most recently `v0.2.312` and `v0.2.313`, both on `63e3de08` and both claiming 1538.

The number a build was stamped with is archived to `<tag>/build-number.txt` alongside the binaries, and the push workflow reads it back from there rather than working it out again — two sides deriving it independently is how they came to disagree. Tags archived before that file existed fall back to the commit count, so they still push exactly as they did.

A build number a store has already seen is spent for the life of the app, so the push workflow asks each store what it holds before it uploads anything. A store that already holds the tag's build is **skipped, not failed**: the platform counts as delivered and the run stays green, so a run that lost one platform can be dispatched again for `both` and only the missing store is uploaded — on iOS the re-run still attaches the release notes the first run never reached. The skip is only taken when the tag's build number was recorded at build time (`build-number.txt`, a function of the tag alone); a legacy tag on the commit-count fallback still fails on a held number, since another tag minted on the same commit shares that count and the store cannot say whose build it holds. (That is the shape of the 2026-09-21 failure: Play took the build, then the TestFlight step timed out on the upload action's ten-minute token after its binary had shipped; the action no longer waits for processing; `testflight-release-notes.py` does, on tokens it refreshes, as a step that fails the job when the build ends `FAILED`/`INVALID` or is not `VALID` within 30 minutes — so "delivered" on iOS means processed, not merely uploaded.) On App Store Connect only a `VALID` or `PROCESSING` build counts as held: one that failed processing has spent its number and fails the run like a higher number would. A store holding a *higher* number than the tag's fails with the tag named and the remedy spelled out, since nothing lower can ever land. A store that cannot be reached is not a veto — the upload step remains the backstop. The bucket keeps a tag's builds for 10 days; an older tag has to be rebuilt before it can be pushed.

Each store upload includes **What's new** text built by `.github/scripts/mobile-build-changelog.py` from the commits since the previous `v*` tag. It is written for the person reading the store listing, not for engineers: no commit hashes, PR numbers or `feat:`-style prefixes, and only commits that touch the app or its domain (`CardiTrack.Mobile`, `CardiTrack.Mobile.Core`, `CardiTrack.Domain`, `CardiTrack.Application`) become bullets. Everything else — API, workers, infrastructure, docs, tests, CI, review-round commits — folds into one closing line, "Plus stability and performance improvements behind the scenes."

To control what customers read, add a `Release-note:` trailer to the commit body:

```
Release-note: Chat replies now arrive faster and remember where you left off.
```

The trailer text becomes the bullet verbatim, whatever paths the commit touched. `Release-note: none` hides a commit that would otherwise be listed. Testers see the result as TestFlight **What to Test** and Play internal **What's new** (Play is capped at 500 characters, TestFlight at 4000; bullets are dropped from the oldest end to fit). The same text is written to the Actions job summary.

### Symbolicating an iOS crash

A TestFlight crash report names no frames of ours — the shipped binary is stripped, so everything in the app appears as `CardiTrack.Mobile 0x… 0x100704000 + 53662144`. Turning those back into method names needs the `.dSYM` for that exact build, matched on the UUID in the report's **Binary Images** section.

1. Read the build off the report header (`Version: 0.2.303 (1510)`) and the UUID off the `CardiTrack.Mobile` line under Binary Images.
2. Fetch the symbols for that release tag: `gcloud storage cp -r gs://carditrack-common-builds/v0.2.303/ios/symbols ./`. Production builds are under `…/ios/prod-symbols` — `deploy-apps-prod.yml` rebuilds from the tag rather than promoting the dev IPA, so its binary has its own UUID and the dev symbols will not match it. The bucket deletes everything after **10 days**; past that, take the `mobile-ios-symbols` artifact off the build's Actions run, which is kept for 90.
3. Confirm they match, or the output will be silently wrong: `dwarfdump --uuid CardiTrack.Mobile.app.dSYM` against the UUID from step 1.
4. Resolve a frame: `atos -o CardiTrack.Mobile.app.dSYM/Contents/Resources/DWARF/CardiTrack.Mobile -arch arm64 -l 0x100704000 0x103a311c0`, where `-l` is the app's load address (the first number on the Binary Images line) and the last argument is the frame address.

Reading the result: offsets below roughly 50 MB are AOT-compiled managed code and come back as method names; the band above it is the Mono runtime linked into the same binary, and frames there stay anonymous. A stack that is *entirely* runtime frames, ending at `abort`, is the signature of an unhandled managed exception — the managed frames were unwound before the abort, so the on-device log (below) and the error-log relay are the only places the exception itself survives.

Before symbols existed in CI, a build's `.dSYM` died with the runner: builds up to **1510** cannot be symbolicated at all unless the tag is rebuilt on a Mac.

### Getting the log off a device

The app writes Warning-and-above to a rolling Serilog file under `FileSystem.AppDataDirectory/logs`, and `AppLogging.HookUnhandledExceptions` writes the full exception there before the process dies. **Settings → Privacy → Share app logs** zips those files with a short `about.txt` (version, build, model, OS) and hands them to the share sheet, which is the only route off an iPhone that does not need Xcode's Download Container and a developer machine. The row works whether or not **Send diagnostics** is on: that toggle governs what the app sends by itself, this is the caregiver sending a file deliberately.

### Getting the exception without the device

Stamped builds also **relay** every Error-and-above line — the unhandled exception included — to `POST /api/v1/mobile/diagnostics/logs`, which the API re-emits to Datadog as `service:carditrack-mobile` (`MobileDiagnosticsRelay.cs`, `MobileDiagnosticsSink.cs`; the full account is in [apm_setup_runbook.md §5](../../technical/apm_setup_runbook.md#5-mobile-app-monitoring)). The unhandled handler blocks for up to three seconds to send the crash before the runtime aborts; anything that does not make it is queued on disk and sent at the next launch or resume. So for a crash on a stamped build, look in Datadog Logs under `service:carditrack-mobile` first — `error.stack` is the managed exception chain the `.ips` file cannot give you, `MobileFrames` the same stack as structured frames (with native addresses for the dSYM where the runtime exposes them), and `MobileRecentLog` the tail of the device log — and fall back to Share app logs only when the relay had no network.

Known gap: an exception thrown inside `MauiProgram.CreateMauiApp` before `HookUnhandledExceptions` runs is still lost — Serilog is configured by then, but nothing is catching, and the relay only sends what Serilog saw.

Signing material and store credentials live in GCP Secret Manager (`carditrack-common-*` secrets, defined in `infrastructure/common/secret_manager.tf`):

| Secret | Content |
|---|---|
| `carditrack-common-apple-distribution-cert-p12` | Apple distribution certificate (.p12, base64) |
| `carditrack-common-apple-cert-password` | Certificate password |
| `carditrack-common-appstore-provisioning-profile` | App Store provisioning profile named "CardiTrack Distribution" (base64) |
| `carditrack-common-appstore-connect-issuer-id` | App Store Connect API issuer ID |
| `carditrack-common-appstore-connect-api-key-id` | App Store Connect API key ID |
| `carditrack-common-appstore-connect-api-private-key` | App Store Connect API private key (.p8 contents) |
| `carditrack-common-android-keystore` | Upload keystore (.jks, base64, key alias `carditrack`) |
| `carditrack-common-android-keystore-password` | Keystore and key password |
| `carditrack-common-play-service-account-key` | Google Play service account key (JSON) |

The common secrets file also defines three **operator-only** secrets (no deploy-workflow accessor grant; loaded and read manually by an operator): `carditrack-common-apns-auth-key-p8` (APNs auth key, .p8 PEM contents), `carditrack-common-apns-key-id`, and `carditrack-common-apple-team-id`.

Until a secret is populated (i.e. still holds the `REPLACE_ME` placeholder), the corresponding signed-build/upload jobs skip with a warning instead of failing, so the pipeline stays green during initial setup.

One-time setup before the first store upload — full step-by-step commands in
**[store_provisioning.md](./store_provisioning.md)**. In summary:

1. **Apple**: distribution certificate (.p12), App Store provisioning profile named **CardiTrack Distribution**, app record for `com.codesistance.carditrack.mobile` in App Store Connect, App Store Connect API key (App Manager role), internal-tester group in TestFlight.
2. **Google**: upload keystore (alias `carditrack`), app in Play Console with Play App Signing, **first AAB uploaded manually** (required before the Play API accepts uploads), publisher service account with *Release to testing tracks*, internal testers.
3. Run *Deploy Infrastructure → Common* to create the secrets, then populate each (base64-encode binary payloads).

## Testing

Unit tests live in `tests/CardiTrack.UnitTests/Mobile/` — **xunit + NSubstitute**, exercising the platform-independent `CardiTrack.Mobile.Core` code (19 test classes plus the shared `FakeHttpMessageHandler`):

- `Auth0AuthClientTests` — login/signup/reset request shapes and error mapping
- `AuthServiceTests` / `PkceTests` / `AccessTokenAudienceTests` — the auth service, the social PKCE flow, and token-audience checks
- `AuthHttpMessageHandlerTests` — bearer attach + refresh behavior (via `FakeHttpMessageHandler`)
- `CardiTrackApiClientTests` / `ClientHeadersTests` / `QuestionnaireApiClientTests` — API client contract
- `JwtPayloadReaderTests` / `TokenRefresherTests`
- `CardiMemberDraftStoreTests` / `FileDraftPhotoStoreTests` — add-member draft persistence
- `DeviceDatasetsTests` — device/data-source catalogue
- `MemberQuestionnairesTests` / `MetricExplanationsTests` / `TrendScaleTests` — questionnaires + trend-chart maths
- `PostLoginRouteResolverTests` / `PrimaryCardiMemberTests` — post-login routing and primary-member selection
- `PhonePlaceholderTests` — region placeholder mapping

```bash
dotnet test tests/CardiTrack.UnitTests
```

There are no UI/device automation tests yet.

## Planned

None of the following exists in the app today:

- ~~**HealthKit (iOS) / Health Connect (Android)** integration for on-device health data~~ — **retired 2026-09-05.** Apple Watch and Galaxy Watch data reaches CardiTrack through the wearer's own Google Health app and the server-side Google Health API engine, never through this app; the wearer is not an app user. The only mobile follow-up is routing those two brands in the device picker to the Google Health connect flow once they are mapped server-side ([devices.md](../../execution/backend/api/devices.md))
- **The offline sync queue** — writes made offline, held and replayed. The *read* half shipped (the encrypted last-known-good cache above, and the cache-first reads below); queuing writes did not, so an action taken with no connection still fails at the time it is taken. There is still no SQLite: the read cache is one encrypted file per request path
- **The iOS Notification Service Extension** — the Xcode App Extension target that would replace a content-free push with richer copy on-device. Its server side (the content-fetch endpoint) shipped; the target itself needs Mac-based verification. Push itself is not planned, it is built — see [Push notifications](#push-notifications).
- **Widgets, Siri shortcuts, app shortcuts**
- **MVVM refactor** — only if/when page complexity warrants it; the current code-behind approach is deliberate

## Related Documentation

- [Store provisioning (signing, TestFlight, Play Console)](./store_provisioning.md)
- [Notification engine](../../technical/notification_engine.md) — the push spine this app is the device half of
- [Web Dashboard Documentation](../web/readme.md)
- [API Documentation](../api/readme.md)
- [Infrastructure Guide](../../infrastructure.md)
- [.NET MAUI Official Docs](https://learn.microsoft.com/dotnet/maui/)

## Support

For mobile app issues, contact: mobile-support@carditrack.com

---

**Last Updated:** August 14, 2026

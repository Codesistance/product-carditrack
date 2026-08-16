# CardiTrack Mobile App Documentation

## Overview

The CardiTrack Mobile App is a cross-platform **.NET 10 MAUI** application for family members and caregivers. What exists today is **16 of 17 Figma M1 screens** plus several shipped surfaces that have no M1 frame: native Auth0 email/password and social login with a hard email-verification gate, the M1 onboarding wizard (account setup → add CardiMember → device selection → device connection (brand-agnostic) → baseline learning), Dashboard, Alerts list, **Alert detail** (`AlertDetailPage` covering M1-11/12/16), CardiMember detail/edit, device management, Questionnaires, Notifications, a weather popup, and a minimal Settings page. Only Family remains a stub. **Push notifications are wired up** — see [Push notifications](#push-notifications). HealthKit/Health Connect and offline storage are **planned** — see [Planned](#planned).

The app is built **code-behind first (XAML + `.xaml.cs`) — there is no MVVM layer**, no ViewModels folder, and no data-binding framework. Platform-independent logic (API client, Auth0 client, token handling, localization) lives in the separate plain-`net10.0` library **`CardiTrack.Mobile.Core`**, which is what the unit tests target.

> Figma governs M1 IDs — only screens that exist in Figma get an M1 ID. Several shipped screens (SignIn, ForgotPassword, VerifyEmail, AccountSetup, Notifications, Questionnaires) await design sync. Screen specs: [mobile screen specifications](../../execution/ui/mobile/ui_screens_maui_mobile.md), [MVP 1 user stories](../../execution/ui/mobile/mvp1/user_stories.md).

## Technology Stack

- **.NET 10 / .NET MAUI** (`Microsoft.Maui.Controls` 10.0.x): cross-platform UI
- **XAML + code-behind**: no MVVM — event handlers in `.xaml.cs`, DI via constructor injection for Shell tab pages and `ServiceHelper` for pages constructed with `new`
- **CardiTrack.Mobile.Core** (`net10.0` class library): `CardiTrackApiClient`, Auth0 auth stack, options, localization — unit-testable without MAUI
- **Auth0**: native password-realm login (no browser redirect)
- **SecureStorage** (`SecureTokenStore`): token persistence — there is **no SQLite** in the app
- **Datadog.Maui** (Android/iOS only): **logs + traces only — no RUM, no Datadog crash reporting** (removed in PR #185; crashes/ANRs come from Play Console vitals), wired through the `MobileApm` registry
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
├── FamilyPage                                # Tab stub (family sharing is MVP 2)
├── SettingsPage                              # Minimal (account card, silenced reminders, sign-out)
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

`AppShell.xaml` is a **TabBar-only Shell** — four tabs (Dashboard, Alerts, Family, Settings); there is no flyout. Tab pages resolve through DI (`AddTransient` in `MauiProgram`).

The **platform tab bar is hidden** (`Shell.TabBarIsVisible="False"` on each tab page) and `Controls/BottomNavBar` draws the Figma bar (node `101:2949`) instead — the native bar cannot swap an icon on selection or carry the design's upward shadow. Shell still owns routing: each item navigates `GoToAsync("//route")`, and a tap on the tab you are already on is swallowed so it can't pop a page pushed above it. Each host page sets `Tab="…"`, which picks the gradient `_active` glyph and the `PrimaryDark` label.

> Figma's bar reads Home / Health / Alerts / Profile, but the M1 file has no Health or Profile screen, so the tab set is unchanged. Dashboard and Alerts use the exported Figma glyphs; Family and Settings keep their hand-authored ones, with `_active` variants that apply the tab-bar gradient to the existing stroke rather than inventing a filled glyph.

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

- **Engine selection**: `AppConfig.ApmEngine` names an entry in the registry — currently **Datadog only**. `ApmData` carries that engine's client-side connection JSON (`{"ClientToken":"pub...","Site":"Eu1"}`), stamped by CI as base64 (raw JSON accepted from `Local.props`). The client token is a write-only identifier, safe to embed.
- **Fail-soft**: unlike the server, an unknown engine or malformed data **logs and disables monitoring** instead of failing — a monitoring misconfiguration must never brick the app. Unstamped builds ship nothing.
- **Datadog config**: `Datadog.Maui` package on **Android/iOS only** (no Windows support); **logs and traces only**; site defaults to **Eu1** when unset; `NativeCrashReportEnabled = false`; service name `carditrack-mobile`; environment tag derived from the API base URL (dev/prod).
- **RUM is not enabled, and mobile telemetry currently cannot be delivered.** Our org is on **UK1**, which has no member in `Datadog.Maui`'s `DatadogSite` enum *or* in the native `dd-sdk-android-core` enum beneath it (`us1/us3/us5/eu1/ap1/ap2` + gov). `CustomEndpoint` is **not** a workaround — 0.2.0 never calls the native `useCustomEndpoint` for any feature, so everything targets the site-derived intake regardless. RUM was removed in PR #185 after returning `404` for exactly this reason. A `Site` the enum cannot name now **disables monitoring outright** rather than falling back to `Eu1`. Only 0.2.0 has ever been published, so no package bump fixes it. Fixing it natively is Android-only, which the Android/iOS parity rule rules out.
- **Crashes and ANRs come from Play Console** (Quality → Android vitals), not Datadog — Datadog crash reporting is a RUM feature and went with it.
- **Session Replay is deliberately NOT enabled** — health data must not be recorded.
- **`FirstPartyHosts`**: the API host is marked first-party with W3C `traceparent` (+ Datadog) headers, so mobile spans join the API's OTel traces.
- **Consent (follow-up)**: `TrackingConsent` is currently set to `Granted` at first launch, and there is no in-app opt-out. This is the current state of the code, flagged for follow-up, not a settled privacy posture.
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
dotnet build -f net10.0-android
dotnet run -f net10.0-android          # deploys to the running emulator/device

# iOS (macOS only)
dotnet build -f net10.0-ios
dotnet build -f net10.0-ios -c Release -p:RuntimeIdentifier=ios-arm64 -t:Run

# Windows (Windows only, unpackaged)
dotnet build -f net10.0-windows10.0.19041.0
```

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

Dev / internal-track mobile CI lives in `.github/workflows/deploy-apps-dev.yml` (jobs gated by the `mobile` path filter); `deploy-apps-prod.yml` also carries mobile references for the production track:

- **Pull requests** — validation builds only: Android (unsigned APK), iOS (simulator), Windows (MSIX). No signing secrets are exposed to PR runs.
- **Push to `main`** — in addition to the validation builds:
  - **Android**: a signed AAB + APK is produced (`build-mobile-android-signed`) and the AAB is uploaded to the **Play Console internal testing track** (`deploy-play-internal`). Release builds run R8 (`AndroidLinkTool=r8` in the csproj), and the upload includes the R8 deobfuscation map (`mapping.txt`) plus a `native-debug-symbols.zip` built from the pre-strip native libraries (`obj/**/app_shared_libraries`), so Play crash reports show readable stack traces. Note: symbol coverage extends to the app's own native libs; Microsoft does not ship unstripped Mono runtime libraries, so frames inside e.g. `libmonosgen-2.0.so` remain unsymbolicated.
  - **iOS**: a signed device IPA is produced (`build-mobile-ios-device`) and uploaded to **TestFlight** (`deploy-testflight`) via the App Store Connect API.
  - Signed artifacts are archived to GCS under the release tag (`upload-mobile-artifacts`).

Store versioning is stamped by CI: `ApplicationDisplayVersion` comes from the computed semver tag and `ApplicationVersion` (iOS build number / Android versionCode) from the monotonic commit count — the values in the csproj are placeholders.

Each store upload includes a **changelog** generated from `git log` since the previous `v*` tag (commit subjects, no merges). Testers see it as TestFlight **What to Test** and Play internal **What's new** (Play is truncated to 500 characters). The same text is written to the Actions job summary.

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

- **HealthKit (iOS) / Health Connect (Android)** integration for on-device health data
- **SQLite offline cache** and sync queue (today the app is online-only; tokens persist in SecureStorage, and the add-member draft — including its photo — persists locally via `CardiMemberDraftStore`/`FileDraftPhotoStore`; there is still no SQLite)
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

# Android emulator runbook — running CardiTrack.Mobile locally

How to get the MAUI app (`src/Presentation/CardiTrack.Mobile`, package `com.codesistance.carditrack.mobile`) onto an Android emulator, drive it, screenshot it, and redeploy it after a change. Written for developers **and** for coding agents (Claude Code and similar) doing UI work against a live device. Everything here was verified on the Windows dev box with the `Pixel_9_Pro` AVD; commands are Git Bash unless marked PowerShell.

Related: [mobile app readme](../apps/mobile/readme.md) (project structure, config, CI), [store provisioning](../apps/mobile/store_provisioning.md).

---

## 0. TL;DR

```bash
# 1. Emulator (SDK lives at %LOCALAPPDATA%\Android\Sdk)
export PATH="$PATH:$LOCALAPPDATA/Android/Sdk/emulator:$LOCALAPPDATA/Android/Sdk/platform-tools"
rm -rf ~/.android/avd/Pixel_9_Pro.avd/*.lock
nohup emulator -avd Pixel_9_Pro -no-snapshot-load > "$TEMP/emu.log" 2>&1 &
until [ "$(adb shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" = "1" ]; do sleep 5; done
adb shell wm dismiss-keyguard

# 2. Deploy + launch (from the mobile project directory; Local.props must exist there)
cd src/Presentation/CardiTrack.Mobile
dotnet build -f net10.0-android -t:Run "-p:AdbTarget=-s emulator-5554"

# 3. Look
adb exec-out screencap -p > shot.png
adb shell dumpsys window | grep mCurrentFocus
```

Repeat step 2 after every code change. Step 1 only when the emulator is not already running (`adb devices` shows `emulator-5554  device`).

---

## 1. Prerequisites

| Need | Check |
|---|---|
| .NET 10 SDK + MAUI workload | `dotnet workload list` shows `maui` (install: `dotnet workload install maui`) |
| Android SDK with emulator + platform-tools | `%LOCALAPPDATA%\Android\Sdk\emulator\emulator.exe` and `...\platform-tools\adb.exe` exist (Android Studio's SDK Manager installs both) |
| An AVD | `emulator -list-avds` prints `Pixel_9_Pro` (preferred; 1280×2856, 480 dpi = 3 px/dp) or `Pixel_9` |
| `Local.props` | git-ignored file next to `CardiTrack.Mobile.csproj`; copy `Local.props.sample` and fill in the dev Auth0 tenant identifiers (public values, see the [readme's Configuration section](../apps/mobile/readme.md#configuration)). Without it the app builds against whatever the csproj defaults are and sign-in will not work |

**Worktree placement (Windows).** Build the Android target from a **short path that is not under `.claude/worktrees`**: the aapt2 step overruns `MAX_PATH` from deep paths (spurious `APT2098`/`APT2261` errors on Google sign-in drawables), and Application Control on the dev box refuses to load freshly built DLLs from `.claude\worktrees\*`. Create task worktrees as siblings of the repo instead:

```bash
git worktree add -b <branch> /c/Code/GitHub/product-carditrack-<task> origin/main
cp src/Presentation/CardiTrack.Mobile/Local.props /c/Code/GitHub/product-carditrack-<task>/src/Presentation/CardiTrack.Mobile/
```

`Local.props` is git-ignored, so every fresh worktree needs its own copy.

---

## 2. Starting the emulator

```bash
export PATH="$PATH:$LOCALAPPDATA/Android/Sdk/emulator:$LOCALAPPDATA/Android/Sdk/platform-tools"
adb devices                         # already running? then skip to §3
rm -rf ~/.android/avd/Pixel_9_Pro.avd/*.lock   # stale locks from a crashed instance (hardware-qemu.ini.lock is a directory)
nohup emulator -avd Pixel_9_Pro -no-snapshot-load > "$TEMP/emu.log" 2>&1 &
```

Wait for boot, then unlock:

```bash
until [ "$(adb shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" = "1" ]; do sleep 5; done
adb shell dumpsys user | grep State:     # want RUNNING_UNLOCKED
adb shell wm dismiss-keyguard
```

A cold boot takes about 30 s. The keyguard dismiss matters: until the first unlock, credential-encrypted storage is locked and the deploy fails with `run-as: couldn't stat /data/user/0/...` (XA0137).

### When it does not come up

| Symptom | Fix |
|---|---|
| `adb devices` shows `emulator-5554  offline` for more than a minute, `adb kill-server && adb start-server` does not help | The saved snapshot is bad. `taskkill //F //IM qemu-system-x86_64.exe //IM emulator.exe`, delete the `*.lock` files, relaunch with `-no-snapshot-load` (as above). Seen 2026-09-08. |
| Screen is 100 % black, focus stuck on `NotificationShade`, logcat says "user not unlocked", `dumpsys user` shows `RUNNING_LOCKED`, and `wm dismiss-keyguard` / `am unlock-user 0` / reboots do nothing | Only known fix: kill the emulator and relaunch with `-wipe-data -no-snapshot` (fresh user data), then reinstall the app. |
| Nothing renders, or rendering is suspect | Add `-gpu swiftshader_indirect` to the launch line (software rendering, reliable). |
| Emulator crashed earlier and refuses to start | Delete `~/.android/avd/Pixel_9_Pro.avd/*.lock` — remember `hardware-qemu.ini.lock` is a **directory**, so use `rm -rf`. |

Sanity-check that the screen actually renders before spending time on navigation: take a screencap and check its mean brightness (a black screen is ~0).

```bash
adb exec-out screencap -p > shot.png
python -c "from PIL import Image; im=Image.open('shot.png').convert('L'); print(sum(im.getdata())/(im.width*im.height))"
```

---

## 3. Build, deploy, launch

From the mobile project directory in your worktree:

```bash
cd src/Presentation/CardiTrack.Mobile
dotnet build -f net10.0-android -t:Run "-p:AdbTarget=-s emulator-5554"
```

- First build after a clean checkout: 3–5 min. Incremental rebuild + redeploy: about 1–2 min.
- `-t:Run` installs (or updates) the app **and** launches it. Plain `dotnet build -f net10.0-android` only compiles — use it to warm the build while the emulator boots.
- Confirm the app is in the foreground:

  ```bash
  adb shell dumpsys window | grep mCurrentFocus
  # mCurrentFocus=Window{... com.codesistance.carditrack.mobile/crc....MainActivity}
  ```

### Fast Deployment (Debug builds) — read this before you `pm clear`

Debug builds use Xamarin **Fast Deployment**: the managed assemblies live in the app's data directory, not in the APK.

- `adb shell pm clear com.codesistance.carditrack.mobile` deletes those assemblies and the app then aborts on launch with *"No assemblies found … Fast Deployment"*. Recover by redeploying with `-t:Run`. To "log out", prefer signing out in the app, or uninstall + redeploy:

  ```bash
  adb uninstall com.codesistance.carditrack.mobile
  dotnet build -f net10.0-android -t:Run "-p:AdbTarget=-s emulator-5554"
  ```

- After the first install, later `-t:Run` builds push assemblies via `_Upload` **without** changing `dumpsys package ... lastUpdateTime`. So do not use the install time to prove a redeploy landed — check for something only the new code produces (a new label in a `uiautomator dump`, a screenshot diff).

---

## 4. Driving the app from the shell

All coordinates below are **pixels on `Pixel_9_Pro`** (1280×2856). Other AVDs differ; re-measure from a screenshot.

```bash
adb shell input tap X Y
adb shell input swipe X1 Y1 X2 Y2 300         # drag; the last number is milliseconds
adb shell input text 'hello'
adb shell "input text 'p@ss(word'"           # quote the whole thing for the device shell when the text has ( ) or spaces
adb exec-out screencap -p > shot.png
```

**Git Bash path mangling.** MSYS rewrites device paths like `/sdcard/ui.xml` into `C:/Program Files/Git/...`. Prefix such commands with `MSYS_NO_PATHCONV=1`, or run them from PowerShell.

**Do not press the hardware back key on a tab root** (`input keyevent KEYCODE_BACK`) — it exits the app to the launcher. Use the page header's back circle instead (at about (123, 262) px on Pixel_9_Pro; it is an `ImageView` at x < 70 dp, y 55–115 dp).

### Known tap targets (Pixel_9_Pro, verified 2026-09-07)

| Screen / control | px |
|---|---|
| Header back circle (any page with a header) | 123, 262 |
| Sign-in page: email / password / Sign in button | 640, 732 / 598, 1014 / 640, 1422 |
| Tab bar (y) and tab centres (x) | y 2758; x 160 / 480 / 800 / 1120 |
| Dashboard → Details tile | 1023, 1110 |
| Member Details, after 4 swipes `640 2300 → 640 700`: Alert Settings / Your Alarms / Journal Settings / Manage Device / Export Data | y 1088 / 1310 / 1532 / 1754 / 1976 (x 643) |
| Member Details, Questions & Answers (one swipe back up) | 643, 2111 |
| Chat FAB / chat entry / send / collapse chevron | 1136, 2462 / 555, 2373 / 1083, 2370 / 165, 444 |
| Settings → Sign out (two taps within 2 s) | 640, 2471 |

### Reading the UI tree (`uiautomator dump`)

Useful for finding what the layout actually rendered (which node consumed safe-area insets, whether a label changed), with two traps:

1. **The Dashboard never goes accessibility-idle** (something animates continuously), so `uiautomator dump` fails there with *"could not get idle state"*. Drive the Dashboard by raw coordinates and only dump the other pages.
2. When a dump fails, a **stale `/sdcard/ui.xml` from the previous screen** is still there and reads as if the screen never changed. Always delete first and treat a missing file as "no dump":

```bash
MSYS_NO_PATHCONV=1 adb shell rm -f /sdcard/ui.xml
MSYS_NO_PATHCONV=1 adb shell uiautomator dump /sdcard/ui.xml && MSYS_NO_PATHCONV=1 adb pull /sdcard/ui.xml ui.xml
```

### Measuring layout from screenshots

480 dpi → 3 px/dp. The blue header band starts at y = 52 dp on every page; scan the centre column of a screenshot for the blue pixels to find its bottom edge. For "how tall is the bottom bar" checks, scan x = 320 upward from the bottom for the first non-white pixel (2594 px = 864.7 dp is the reference on Pixel_9_Pro).

---

## 5. Signing in

The app talks to the **dev** environment by default (`https://api.dev.carditrack.com`, Auth0 tenant `carditrack-dev.uk.auth0.com`). Sign-in is the app's native email/password form: tap **Sign in** on the welcome carousel, then use the field coordinates in §4. Warm sign-in reaches Dashboard content in about 5 s.

- **Credentials are not in this repo and must never be committed or pasted into a PR/issue/chat log.** Ask the product owner for a dev account. Self-signup on dev is blocked by the email-verification gate, so you cannot mint your own.
- Sign out is a two-tap gate on Settings (see §4). If you need a clean slate, uninstall + redeploy (§3) rather than `pm clear`.
- Data you see on dev is shared with every other client signed into that account (the member chat thread is server-side, for example). If chat history contains messages you did not send, another session or the owner's own device did.

### Screens you cannot reach with a dev account

Some states do not exist on dev (specific alert severities, devices that are not connected, onboarding after verification). To visually verify such a page, build a **temporary preview harness**: a stub `ICardiTrackApiClient` implementing only the methods the page calls, and swap `App.CreateWindow`'s root to construct the page directly with canned data (the page's public `[QueryProperty]` string setters can be set directly instead of Shell navigation). Build, screenshot, then delete the harness and `git checkout` `App.xaml.cs` before committing. This worked for `DaybookEntryPage` (PR #374).

---

## 6. Working against a local API instead of dev

Set `<ApiBaseUrl>http://10.0.2.2:5230</ApiBaseUrl>` in `Local.props` (`10.0.2.2` is the emulator's alias for the host; cleartext to it is allowed by `network_security_config.xml`) and run `CardiTrack.API` locally. Device-OAuth deep links need extra care — see the readme's [Device OAuth Deep Link](../apps/mobile/readme.md#device-oauth-deep-link).

---

## 7. Rules for agents doing UI work on the emulator

1. **One emulator, one build.** Every `-t:Run` replaces the whole app. If several mobile branches are in flight at once, the emulator only ever shows the last one deployed, and the reviewer watching it sees changes vanish. When more than one mobile branch exists: create a throwaway sibling worktree from `origin/main`, merge every open mobile branch into it (never push it), copy `Local.props`, and deploy **only** that preview. Other agents build and run tests only — no deploy, no screenshots — and the preview is re-merged and redeployed whenever a branch moves.
2. **Verify a redeploy functionally**, not by install time (see §3).
3. **Screenshot after every change** and check the affected screen before reporting it done. The review loop the owner expects is: make the change → rebuild + redeploy → screenshot → one-line confirmation. Commit per concern to the one open PR.
4. **Say what you could not verify.** OAuth round-trips, push notifications, and data states that do not exist on dev cannot be exercised on the emulator; state that plainly in the PR body and ask for confirmation on a real device.
5. **Never commit `Local.props`, credentials, or screenshots containing account emails.**
6. **Clean up.** When the task's PR is merged, remove the sibling worktree (`git worktree remove`), delete the local branch, and delete the remote branch. If `Remove-Item` fails part-way with `DirectoryNotFoundException` under `obj/Debug/net10.0-android`, that is `MAX_PATH` on delete: mirror an empty directory over it with `robocopy "$env:TEMP\empty" "<worktree>" /MIR` (PowerShell), then delete the husk and `git worktree prune`.

---

## 8. Quick reference — pitfalls

| You see | It means | Do |
|---|---|---|
| `run-as: couldn't stat /data/user/0/...` (XA0137) on deploy | Device booted but never unlocked | `adb shell wm dismiss-keyguard`, retry |
| App aborts on launch: "No assemblies found … Fast Deployment" | Someone ran `pm clear` | Redeploy with `-t:Run` |
| `emulator-5554  offline` that never turns into `device` | Bad snapshot | Kill emulator, delete locks, relaunch `-no-snapshot-load` |
| Black screencap, `RUNNING_LOCKED` user, nothing unlocks it | Corrupt user data | Relaunch `-wipe-data -no-snapshot`, reinstall app |
| `uiautomator dump`: "could not get idle state" | Dashboard is never idle | Drive by coordinates; delete stale `/sdcard/ui.xml` |
| `adb pull /sdcard/...` complains about `C:/Program Files/Git/...` | MSYS path conversion | `MSYS_NO_PATHCONV=1` |
| `APT2098` / `APT2261` aapt2 errors on `.9.png` files | Path too long | Build from a short sibling worktree path; delete `obj/Debug/net10.0-android` first |
| `dotnet test` says "No test is available", `0x800711C7` in the output | Application Control blocks DLLs under `.claude\worktrees` | Use a sibling worktree path |
| Hardware back key sent the app to the launcher | Back on a tab root exits | Use the header back circle |

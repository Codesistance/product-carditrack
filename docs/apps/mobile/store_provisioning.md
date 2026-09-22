# Store Provisioning — Keys, Certificates & Secrets

One-time setup that enables CI (`deploy-apps-dev.yml` to build, `deploy-mobile-dev.yml` to push) to deliver signed mobile
builds to **TestFlight** (iOS) and the **Google Play internal testing track** (Android). Everything
lands in GCP Secret Manager as the `carditrack-common-*` secrets defined in
`infrastructure/common/secret_manager.tf`: eleven read by the mobile workflows (the nine signing
and store credentials plus the two Firebase client configs, section G), two Slack secrets read
only by `post-digest.yml` (`slack-bot-token`, `slack-channel-id`, not covered here), and three
**operator-only** APNs secrets (`apns-auth-key-p8`, `apns-key-id`, `apple-team-id` — see
section F) that no deploy workflow reads. Run *Deploy Infrastructure → Common* first so the
secrets exist (seeded `REPLACE_ME`). Until a secret holds a real value, the corresponding
*build* job in `deploy-apps-dev.yml` skips with a warning; a *push* requested from
`deploy-mobile-dev.yml` for that platform fails, on purpose, because uploading is the only
thing that workflow is for.

Commands below are Windows-oriented (PowerShell + the JDK/openssl paths used on the dev machine);
any environment with `keytool`, `openssl`, and `gcloud` works the same way.

> **Related but separate:** signed builds also need the **per-environment mobile APM secrets**
> (`carditrack-<env>-apm-mobile-engine` / `carditrack-<env>-apm-mobile-data` — defined in the
> env stacks, *not* `common/`) populated before mobile **log and trace** shipping works in the
> shipped app (there is no crash/session monitoring in Datadog — crashes/ANRs come from Play
> Console vitals); unstamped builds run fine but ship no telemetry. See the
> [APM setup runbook §5 — Mobile app monitoring](../../technical/apm_setup_runbook.md#5-mobile-app-monitoring).

## Secret reference

| Secret | Content | Encoding |
|---|---|---|
| `carditrack-common-android-keystore` | Upload keystore (.jks, key alias `carditrack`) | base64 |
| `carditrack-common-android-keystore-password` | Keystore **and** key password (same value) | plain text |
| `carditrack-common-play-service-account-key` | Play publisher service-account key | JSON text |
| `carditrack-common-firebase-android-config` | Firebase `google-services.json` for the app (section G) | JSON text |
| `carditrack-common-firebase-ios-config` | Firebase `GoogleService-Info.plist` for the app (section G) | plist XML text |
| `carditrack-common-apple-distribution-cert-p12` | Apple Distribution certificate + private key | base64 |
| `carditrack-common-apple-cert-password` | .p12 export password | plain text |
| `carditrack-common-appstore-provisioning-profile` | App Store profile named `CardiTrack Distribution` | base64 |
| `carditrack-common-appstore-connect-issuer-id` | App Store Connect API issuer ID | plain text |
| `carditrack-common-appstore-connect-api-key-id` | App Store Connect API key ID | plain text |
| `carditrack-common-appstore-connect-api-private-key` | App Store Connect API `.p8` contents | PEM text (not base64) |
| `carditrack-common-apns-auth-key-p8` *(operator-only)* | APNs auth key `.p8` contents (section F) | PEM text (not base64) |
| `carditrack-common-apns-key-id` *(operator-only)* | APNs auth key ID | plain text |
| `carditrack-common-apple-team-id` *(operator-only)* | Apple Developer Team ID | plain text |

The three *operator-only* secrets are not read by any deploy workflow (no CI accessor grant) —
they are loaded and read manually by an operator.

Loading pattern (binary payloads are stored as base64 *text* because CI decodes with `base64 --decode`):

```bash
# text values
echo -n "VALUE" | gcloud secrets versions add <secret-id> --data-file=- --project=carditrack-490120
# files (pre-encoded .b64, .json, .p8)
gcloud secrets versions add <secret-id> --data-file=<file> --project=carditrack-490120
```

PowerShell base64 helper:

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("<file>")) |
  Set-Content "<file>.b64" -NoNewline -Encoding ascii
```

## A. Android upload keystore

```powershell
& "$env:USERPROFILE\.jdk\bin\keytool.exe" -genkeypair -v `
  -keystore "$env:USERPROFILE\carditrack-upload.jks" `
  -alias carditrack -keyalg RSA -keysize 2048 -validity 10000
```

- Alias **must** be `carditrack` (hard-coded in the workflow).
- Use the **same password** for keystore and key — CI passes one value to both.
- Base64-encode and load into `carditrack-common-android-keystore`; password into
  `carditrack-common-android-keystore-password`.
- **Back up the .jks** (password manager / offline). With Play App Signing it is the *upload* key —
  recoverable through Google support if lost, but painful.

## B. Apple Distribution certificate

Needs an Apple Developer Program membership. No Mac required.

```powershell
# 1. Private key + CSR (key is intentionally unencrypted and never leaves the machine)
& "C:\Program Files\Git\usr\bin\openssl.exe" req -new -newkey rsa:2048 -nodes `
  -keyout "$env:USERPROFILE\carditrack-apple.key" `
  -out "$env:USERPROFILE\carditrack.csr" `
  -subj "/emailAddress=cloudoperations@codesistance.com/CN=Codesistance Ltd/C=GB"
```

2. [developer.apple.com/account](https://developer.apple.com/account) → Certificates → **+** →
   **Apple Distribution** → upload the CSR → download `distribution.cer`.
   (CSR subject fields are cosmetic — Apple stamps the issued cert with the team identity.)

```powershell
# 3. Convert to .p12 — the export password you choose here becomes the cert-password secret
& "C:\Program Files\Git\usr\bin\openssl.exe" x509 -inform DER `
  -in "$env:USERPROFILE\Downloads\distribution.cer" -out "$env:USERPROFILE\distribution.pem"
& "C:\Program Files\Git\usr\bin\openssl.exe" pkcs12 -export `
  -certpbe PBE-SHA1-3DES -keypbe PBE-SHA1-3DES -macalg sha1 `
  -inkey "$env:USERPROFILE\carditrack-apple.key" -in "$env:USERPROFILE\distribution.pem" `
  -out "$env:USERPROFILE\carditrack-dist.p12"
```

The SHA1/3DES flags are required: OpenSSL 3 otherwise emits modern PKCS#12 (PBES2/AES-256,
SHA-256 MAC), which macOS `security import` on the CI runner rejects with
`MAC verification failed during PKCS12 import (wrong password?)`. (`-legacy` achieves the same
but Git for Windows' openssl.exe fails to locate its legacy provider DLL outside an MSYS shell.)

Base64 the `.p12` → `carditrack-common-apple-distribution-cert-p12`; export password →
`carditrack-common-apple-cert-password`. The key/CSR/cert are generated as a set — if the CSR is
ever regenerated, redo the whole chain.

## C. App ID + provisioning profile

In the Apple developer portal:

1. **Identifiers** → **+** → App ID, explicit bundle ID `com.codesistance.carditrack.mobile`.
   Enable capabilities (e.g. Push Notifications) *now* — adding one later invalidates profiles.
2. **Profiles** → **+** → Distribution → **App Store Connect** → select the App ID and the
   certificate from B → name it exactly **`CardiTrack Distribution`** — CI selects the profile by
   this name (`-p:CodesignProvision`).
3. Download the `.mobileprovision`, base64 → `carditrack-common-appstore-provisioning-profile`.

> **APNs environment must match the signing identity.** `Platforms/iOS/Entitlements.plist`
> carries `aps-environment = development` (local and simulator builds); the signed-IPA CI step
> passes `-p:CodesignEntitlements=Platforms/iOS/Entitlements.Release.plist`, which carries
> `production`. Uploading a distribution build with `development` fails App Store Connect
> validation outright — `ERROR ITMS-90046: Invalid Code Signing Entitlements`. Add any new
> capability to **both** plists.

Sanity-check a downloaded profile (name / app ID / type) before loading:

```powershell
$raw = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes("<profile>.mobileprovision"))
# Expect: Name = CardiTrack Distribution, application-identifier = <TEAMID>.com.codesistance.carditrack.mobile,
# and NO ProvisionedDevices key (its presence means Ad Hoc/Development — wrong type)
```

## D. App Store Connect — app record, API key, testers

1. [appstoreconnect.apple.com](https://appstoreconnect.apple.com) → My Apps → **+** → New App:
   iOS, name **CardiTrack**, the bundle ID from C, any SKU. Price: **Free** (monetization is
   in-app subscription; price is irrelevant to TestFlight).
2. **Users and Access → Integrations → App Store Connect API → Team Keys** → **+**, role
   **App Manager**, name e.g. `CardiTrack CI` (name is a label only). Capture:
   - **Issuer ID** → `carditrack-common-appstore-connect-issuer-id`
   - **Key ID** → `carditrack-common-appstore-connect-api-key-id`
   - the `.p8` file — **downloadable exactly once** — its text content →
     `carditrack-common-appstore-connect-api-private-key`
3. App → **TestFlight → Internal Testing** → create a group with automatic distribution and add
   testers. Testers must be App Store Connect team users; to test on a phone signed into a
   personal Apple ID, invite that address in Users and Access with the minimal **Customer Support**
   role and add it to the group.

## E. Google Play — app, first upload, service account

1. [Play Console](https://play.google.com/console) → Create app: name **CardiTrack**, package
   `com.codesistance.carditrack.mobile`, free.
2. Build a signed AAB locally (the signature of this first upload **registers the upload key** —
   it must be the keystore from step A). A signed publish refuses to run without the Firebase
   config (`_CardiTrackRequireFirebaseConfig`), and that file is not in git, so load it into
   Secret Manager first (section G, steps 1–3) and fetch it into the worktree:

   ```powershell
   # --out-file, not a > redirect: Windows PowerShell re-encodes redirected output (UTF-16, BOM)
   gcloud secrets versions access latest --secret=carditrack-common-firebase-android-config `
     --project=carditrack-490120 --out-file=src/Presentation/CardiTrack.Mobile/Platforms/Android/google-services.json
   dotnet publish src/Presentation/CardiTrack.Mobile/CardiTrack.Mobile.csproj `
     -f net10.0-android -c Release `
     -p:AndroidPackageFormats=aab -p:AndroidKeyStore=true `
     -p:AndroidSigningKeyStore="$env:USERPROFILE\carditrack-upload.jks" `
     -p:AndroidSigningKeyAlias=carditrack `
     -p:AndroidSigningStorePass="<password>" -p:AndroidSigningKeyPass="<password>" `
     -p:ApplicationDisplayVersion=1.0 -p:ApplicationVersion=1
   ```

   `versionCode 1` is safe — CI stamps builds with a number derived from the release tag
   (`v0.2.318` → `20318`), which is always higher.
3. **Test and release → Testing → Internal testing** → Create release → accept the Play App
   Signing default → upload `...\publish\com.codesistance.carditrack.mobile-Signed.aab` →
   **Save and publish**. This manual first upload is mandatory: the Play API refuses uploads for a
   package it has never seen. (Warnings about missing deobfuscation/symbol files are non-blocking.)
4. **Testers tab** → create an email list with the Google accounts of the test phones → save →
   share the opt-in link from "How testers join".
5. Service account:

   ```bash
   # the Play Developer API must be enabled on the project that owns the key
   gcloud services enable androidpublisher.googleapis.com --project=carditrack-490120
   gcloud iam service-accounts create carditrack-play-publisher \
     --display-name="CardiTrack Play Publisher" --project=carditrack-490120
   gcloud iam service-accounts keys create carditrack-play-publisher.json \
     --iam-account=carditrack-play-publisher@carditrack-490120.iam.gserviceaccount.com \
     --project=carditrack-490120
   gcloud secrets versions add carditrack-common-play-service-account-key \
     --data-file=carditrack-play-publisher.json --project=carditrack-490120
   rm carditrack-play-publisher.json
   ```

   Enable the Play API in the project (one-time — CI uploads fail with "androidpublisher ... has
   not been used" otherwise):

   ```bash
   gcloud services enable androidpublisher.googleapis.com --project=carditrack-490120
   ```

   No GCP IAM roles are needed — authority comes from the Play Console invite:
   **Users and permissions → Invite new user** → the service-account email → app access
   **CardiTrack** → grant **Release to testing tracks**. Permissions can take a few minutes to
   propagate; a first 403 from CI shortly after setup usually self-resolves.

## F. APNs auth key (operator-only secrets)

These three secrets are **not read by any deploy workflow** — they carry the APNs credentials an
operator uses for push-notification delivery.

1. [developer.apple.com/account](https://developer.apple.com/account) → **Certificates,
   Identifiers & Profiles → Keys** → **+** → name it (label only), enable
   **Apple Push Notifications service (APNs)** → Continue → Register.
2. Download the `.p8` — like the App Store Connect key, it is **downloadable exactly once** —
   and note the **Key ID** shown on the key's page. The **Team ID** is in the portal's
   Membership details.
3. Load the three values:
   - the `.p8` text content → `carditrack-common-apns-auth-key-p8`
   - the Key ID → `carditrack-common-apns-key-id`
   - the Team ID → `carditrack-common-apple-team-id`

## G. Firebase client config

`google-services.json` and `GoogleService-Info.plist` are the Firebase SDK's per-app config
files. Each carries a Google API key, which GitHub secret scanning flags, so **they are not in
git**: both paths are git-ignored, the signed Android and iOS device jobs in `deploy-apps-dev.yml`
(and the iOS prod job) write them into `src/Presentation/CardiTrack.Mobile/Platforms/…` from
these two secrets before publishing, and the csproj fails a store build that has no config
(`_CardiTrackRequireFirebaseConfig`). While a secret is still `REPLACE_ME`, the **dev** jobs
skip the signed build with a warning, like the signing secrets above; the **prod** iOS job has
no such gate and fails at "Write Firebase config", because a production build that cannot
register for push is not one to ship.

1. [Firebase console](https://console.firebase.google.com/project/carditrack-490120/settings/general)
   → **Your apps** → the Android app (`com.codesistance.carditrack.mobile`) → download
   `google-services.json`; the Apple app → download `GoogleService-Info.plist`. Both apps are
   Terraform-managed (`infrastructure/deployments/firebase.tf`).
2. Restrict the key each file carries (GCP console → **APIs & Services → Credentials**):
   API restrictions to the Firebase services the app uses (Firebase Installations, FCM
   Registration, Firebase Remote Config if enabled), application restrictions to the iOS bundle
   ID, and for Android the package name with **every** SHA-1 the app is signed with — the upload
   key, the Play app-signing key, **and each developer's Debug keystore**, because the emulator
   loop deploys a Debug build (`dotnet build -t:Run`, signed with `~/.android/debug.keystore`,
   alias `androiddebugkey`) and Firebase Installations rejects a build whose certificate is not
   on the key. Read a Debug SHA-1 with
   `keytool -list -v -keystore ~/.android/debug.keystore -alias androiddebugkey -storepass android`.
   Without restrictions the key can be used against any API enabled on the project by anyone
   holding a copy of the app.
3. Load them as text (no base64):

   ```bash
   gcloud secrets versions add carditrack-common-firebase-android-config \
     --data-file=google-services.json --project=carditrack-490120
   gcloud secrets versions add carditrack-common-firebase-ios-config \
     --data-file=GoogleService-Info.plist --project=carditrack-490120
   ```

4. Rotating, one platform at a time, without an outage. Each file carries its own key, and
   every installed copy of the app keeps using the key baked into it, so deleting a key before
   the replacement build has reached those installs breaks Firebase initialisation and push
   registration on that platform.
   1. **Credentials → Create credentials → API key**; restrict it as in step 2.
   2. Put the new key's value into the downloaded config file yourself (`api_key[].current_key`
      in `google-services.json`, `API_KEY` in `GoogleService-Info.plist`) — the console download
      always names the Firebase auto-created key, and the SDK only reads the value in the file.
   3. Add the edited file as a new secret version (step 3). Builds pick up `latest` on their next
      run; ship a build and let it reach installed devices (TestFlight / Play internal is a day,
      a production release is however long the rollout takes).
   4. Only then delete the old key in **Credentials**, and resolve the secret-scanning alert for
      it as *revoked*.
   5. Repeat for the other platform.

## Verification

1. Confirm no placeholders remain:

   ```bash
   for s in android-keystore android-keystore-password play-service-account-key \
            firebase-android-config firebase-ios-config \
            apple-distribution-cert-p12 apple-cert-password appstore-provisioning-profile \
            appstore-connect-issuer-id appstore-connect-api-key-id appstore-connect-api-private-key \
            apns-auth-key-p8 apns-key-id apple-team-id; do
     # An unreadable secret (not created yet, no access) yields an empty value, which is
     # as unusable as the placeholder — report it, do not let it pass as "ok".
     v=$(gcloud secrets versions access latest --secret="carditrack-common-$s" --project=carditrack-490120 2>/dev/null || true)
     if [ -z "$v" ]; then echo "MISSING: $s"; elif [ "$v" = "REPLACE_ME" ]; then echo "NOT SET: $s"; else echo "ok: $s"; fi
   done
   ```

2. Run *CI / Deploy Apps → Dev* (workflow_dispatch) with only **Mobile** checked. The
   `Build Mobile (Android, signed)` / `Build Mobile (iOS, device)` jobs should run instead of
   warn-skipping, and `Deploy → TestFlight` / `Deploy → Play Console (internal)` should succeed.
3. iPhone: build appears in the TestFlight app after ~5–15 min of processing, with **What to Test** filled with customer-facing notes for the commits since the last `v*` tag (see [`readme.md → CI/CD Pipeline`](./readme.md#cicd-pipeline) for how a commit gets a bullet). Android: build appears under Internal testing with **What's new** from the same notes (500-character cap); install via the opt-in link.

## Related

- CI flow and secret table: [`readme.md → CI/CD Pipeline`](./readme.md#cicd-pipeline)
- Secret definitions: `infrastructure/common/secret_manager.tf`
- GitHub↔GCP auth (WIF): `scripts/setup-gcp-auth.sh` — the OIDC attribute condition must match the
  current repo slug (`Codesistance/product-carditrack`); a repo rename/transfer breaks all CI auth
  until the provider condition and service-account binding are updated.

---

**Last Updated:** August 7, 2026

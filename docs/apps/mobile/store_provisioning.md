# Store Provisioning — Keys, Certificates & Secrets

One-time setup that enables CI (`.github/workflows/deploy-apps-dev.yml`) to deliver signed mobile
builds to **TestFlight** (iOS) and the **Google Play internal testing track** (Android). Everything
lands in GCP Secret Manager as the nine `carditrack-common-*` secrets defined in
`infrastructure/common/secret_manager.tf` — run *Deploy Infrastructure → Common* first so the
secrets exist (seeded `REPLACE_ME`). Until a secret holds a real value, the corresponding CI jobs
skip with a warning; nothing fails.

Commands below are Windows-oriented (PowerShell + the JDK/openssl paths used on the dev machine);
any environment with `keytool`, `openssl`, and `gcloud` works the same way.

## Secret reference

| Secret | Content | Encoding |
|---|---|---|
| `carditrack-common-android-keystore` | Upload keystore (.jks, key alias `carditrack`) | base64 |
| `carditrack-common-android-keystore-password` | Keystore **and** key password (same value) | plain text |
| `carditrack-common-play-service-account-key` | Play publisher service-account key | JSON text |
| `carditrack-common-apple-distribution-cert-p12` | Apple Distribution certificate + private key | base64 |
| `carditrack-common-apple-cert-password` | .p12 export password | plain text |
| `carditrack-common-appstore-provisioning-profile` | App Store profile named `CardiTrack Distribution` | base64 |
| `carditrack-common-appstore-connect-issuer-id` | App Store Connect API issuer ID | plain text |
| `carditrack-common-appstore-connect-api-key-id` | App Store Connect API key ID | plain text |
| `carditrack-common-appstore-connect-api-private-key` | App Store Connect API `.p8` contents | PEM text (not base64) |

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
   it must be the keystore from step A):

   ```powershell
   dotnet publish src/Presentation/CardiTrack.Mobile/CardiTrack.Mobile.csproj `
     -f net10.0-android -c Release `
     -p:AndroidPackageFormats=aab -p:AndroidKeyStore=true `
     -p:AndroidSigningKeyStore="$env:USERPROFILE\carditrack-upload.jks" `
     -p:AndroidSigningKeyAlias=carditrack `
     -p:AndroidSigningStorePass="<password>" -p:AndroidSigningKeyPass="<password>" `
     -p:ApplicationDisplayVersion=1.0 -p:ApplicationVersion=1
   ```

   `versionCode 1` is safe — CI stamps builds with the repo commit count, which is always higher.
3. **Test and release → Testing → Internal testing** → Create release → accept the Play App
   Signing default → upload `...\publish\com.codesistance.carditrack.mobile-Signed.aab` →
   **Save and publish**. This manual first upload is mandatory: the Play API refuses uploads for a
   package it has never seen. (Warnings about missing deobfuscation/symbol files are non-blocking.)
4. **Testers tab** → create an email list with the Google accounts of the test phones → save →
   share the opt-in link from "How testers join".
5. Service account:

   ```bash
   gcloud iam service-accounts create carditrack-play-publisher \
     --display-name="CardiTrack Play Publisher" --project=carditrack-490120
   gcloud iam service-accounts keys create carditrack-play-publisher.json \
     --iam-account=carditrack-play-publisher@carditrack-490120.iam.gserviceaccount.com \
     --project=carditrack-490120
   gcloud secrets versions add carditrack-common-play-service-account-key \
     --data-file=carditrack-play-publisher.json --project=carditrack-490120
   rm carditrack-play-publisher.json
   ```

   No GCP IAM roles are needed — authority comes from the Play Console invite:
   **Users and permissions → Invite new user** → the service-account email → app access
   **CardiTrack** → grant **Release to testing tracks**. Permissions can take a few minutes to
   propagate; a first 403 from CI shortly after setup usually self-resolves.

## Verification

1. Confirm no placeholders remain:

   ```bash
   for s in android-keystore android-keystore-password play-service-account-key \
            apple-distribution-cert-p12 apple-cert-password appstore-provisioning-profile \
            appstore-connect-issuer-id appstore-connect-api-key-id appstore-connect-api-private-key; do
     v=$(gcloud secrets versions access latest --secret="carditrack-common-$s" --project=carditrack-490120)
     [ "$v" = "REPLACE_ME" ] && echo "NOT SET: $s" || echo "ok: $s"
   done
   ```

2. Run *CI / Deploy Apps → Dev* (workflow_dispatch) with only **Mobile** checked. The
   `Build Mobile (Android, signed)` / `Build Mobile (iOS, device)` jobs should run instead of
   warn-skipping, and `Deploy → TestFlight` / `Deploy → Play Console (internal)` should succeed.
3. iPhone: build appears in the TestFlight app after ~5–15 min of processing. Android: build
   appears under Internal testing; install via the opt-in link.

## Related

- CI flow and secret table: [`readme.md → CI/CD Pipeline`](./readme.md#cicd-pipeline)
- Secret definitions: `infrastructure/common/secret_manager.tf`
- GitHub↔GCP auth (WIF): `scripts/setup-gcp-auth.sh` — the OIDC attribute condition must match the
  current repo slug (`Codesistance/product-carditrack`); a repo rename/transfer breaks all CI auth
  until the provider condition and service-account binding are updated.

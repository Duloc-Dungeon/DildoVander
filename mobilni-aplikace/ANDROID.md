# Wand Night Stand Android

Android packages the existing Vue/Vite application with Capacitor 8.5.3. It uses
the existing backend and server launch controls. Firebase is distribution only;
there is no Firebase runtime SDK, Google Play dependency or second frontend.
The Android application ID is permanently `de.dildovander.companion`. The iOS
bundle ID is unchanged.

## Windows development

Android 9 (API28) or newer with a working passkey provider is required for
accounts. Install Node from `.nvmrc`, Python 3, JDK21 and the Android SDK (Android Studio
is optional for command-line builds). Set `JAVA_HOME` to JDK 21 and `ANDROID_HOME`
to the SDK directory. Gradle 8.14.3 is supplied by the checksum-pinned wrapper.
Install `platforms;android-36`, `build-tools;35.0.0`, `build-tools;36.0.0` and
`platform-tools` with SDK Manager. Accept the Android SDK licenses interactively.
The Windows wrapper uses a dedicated JDK at
`%USERPROFILE%\.wand-night-stand\tools\jdk-21` when installed, otherwise
`JAVA_HOME`. It restores the calling process environment after the build.

From the repository root:

```powershell
npm ci --prefix contracts
npm ci --prefix mobilni-aplikace
Set-Location mobilni-aplikace
./scripts/android.ps1 debug
./scripts/android.ps1 run
```

`run` builds, installs and launches on an already running emulator or USB device
visible to `adb devices`. Enable USB debugging and approve the computer on the
device. The scripts generate assets from the checksum-verified existing emblem,
build Vite, sync Capacitor, compile and run Android lint. Generated assets and
SDK paths are ignored. No release key is required for debug builds.

## Persistent signing identity

Create the release key once, before the first friend installation:

```powershell
./scripts/init-android-signing.ps1
./scripts/android.ps1 release -VersionCode 1
```

The default private location is `%USERPROFILE%\.wand-night-stand\android-signing`.
`release.p12` contains the RSA signing key; `password.clixml` holds a Windows
DPAPI-encrypted password readable only by the current Windows identity. Neither
belongs in Git. The directory ACL excludes inherited permissions.

**Back up the keystore and its password in a password manager/offline encrypted
backup before distributing.** A copied `password.clixml` alone is not portable
to a new Windows profile. Recover the password locally through
`[Net.NetworkCredential]::new('', (Import-Clixml <private-password-file>)).Password`
directly into a secure destination; never paste it into chat, commands, logs or
source. Keep the original key for every update. Losing it prevents installing
updates over the existing application.

For an existing signing key set `ANDROID_KEYSTORE_PATH`,
`ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS` and, if different,
`ANDROID_KEY_PASSWORD` in the private process environment. The PowerShell
wrapper preserves/restores previous signing environment values.

## Release APK

```powershell
./scripts/android.ps1 release -VersionCode 2
./scripts/android.ps1 verify -Apk ./dist/android/app-release.apk -VersionCode 2
```

Every release needs an explicit increasing `versionCode`; version name defaults
to `package.json`. Local releases use codes below 1000000. CI reserves codes
`1000000 + GITHUB_RUN_NUMBER`, from the single Android signing workflow. Once CI
has distributed a release, use CI for subsequent releases or explicitly choose a
higher unused local code. Reruns reuse that code and are retries of one release.

Output: `dist/android/app-release.apk` and `dist/android/release-metadata.json`.
Verification runs `apksigner verify --verbose --print-certs` and `aapt dump
badging`, rejects debug certificates/debuggable packages, checks package/version,
and records certificate/APK SHA-256. Set `ANDROID_SIGNING_CERTIFICATE_SHA256` to
require a specific release certificate. Builds fail clearly for missing keys,
tools, invalid version codes, or an insecure/private release backend origin.
`KUFR_ACCOUNT_ORIGIN` defaults to the canonical HTTPS service; staging must also
be a publicly reachable HTTPS origin. Release cleartext traffic is disabled and
only system CAs are trusted.

## CI and delivery ownership

PR CI selects Android for its platform and shared inputs, compiles the native
app and runs lint without release credentials. `kufr-android-release.yml` is a
manual current-main signing workflow. It reuses the checksummed `web-dist`
artifact from a successful direct-main `ci.yml` run and rechecks current main.
It produces `android-release-apk`, containing the existing `package.py` payload
and provenance manifest plus package/version/certificate/APK digest metadata.

Configure the app repository's `android-signing` environment for main only:

- Secrets: `ANDROID_RELEASE_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD`, and
  `ANDROID_KEY_PASSWORD` if different from the store password.
- Variables: `ANDROID_KEY_ALIAS`, `ANDROID_SIGNING_CERTIFICATE_SHA256`, optional
  `KUFR_ACCOUNT_ORIGIN`.

Dispatch direct-main CI first, then dispatch `kufr-android-release.yml` with
that `build_run_id`. Signing does not publish. Firebase upload, credential
configuration, tester management and release readback are owned by the separate
private deployment repository, following the same verified-artifact architecture
as the other mobile releases. Distribution is manual; PRs and ordinary
development builds never invite testers or upload releases automatically.

Friends receive Firebase email invitations and install through its tester page,
allowing installation from that source when Android asks. Future releases appear
in the same tester app/group. An update must have the same package/signing key
and a greater version code. Do not uninstall to fix an update: that can delete
local state. Check signature/version mismatch first; use a higher code for a
rollback release built from the reviewed earlier source.

Firebase membership permits downloading builds. Existing backend invitations,
roles and launch restrictions still decide application access. Removing Firebase
membership does not revoke an already installed application's account session.

## Platform audit and acceptance

| Surface | Android implementation / acceptance |
| --- | --- |
| Launch restrictions, registration, login, characters, enrichment, art, keepsakes | Shared screens and existing authorized account/guest HTTPS APIs. No backend bypass. |
| Account persistence and recovery | App-private native session cookies; passkey invitation/recovery stays on the existing server. Backup is disabled. |
| Passkeys | Android Credential Manager; the relying-party domain must publish `/.well-known/assetlinks.json` for this package and release certificate, and the backend must accept its Android origin. Verify on a real device. |
| GPT Live | Native microphone/PCM capture, authenticated stream and playback; runtime microphone denial and background cancellation. Requires device/network/audio acceptance; no paid test runs occur during builds. |
| Customer BLE | Native scan/connect/GATT adapter, runtime Bluetooth/location permissions by OS version. Physical ESP32 interoperability remains a hardware check. |
| LAN device control | Native certificate-pinned HTTPS/WSS transport and Android Keystore-encrypted pairing data. Never replace pinning with trust-all TLS. |
| Diagnostics/downloads | Native bounded files shared via a narrowly scoped FileProvider; no broad storage permission. |
| Navigation/layout | Hardware Back follows Vue history and minimizes at root; keyboard dismissal, Capacitor SystemBars safe-area CSS, rotation and DayNight theme. |

Native acceptance requires a signed APK, not only browser or debug success.
Record fresh install/first launch, invitation/login, character create/retrieve,
artwork, launch restrictions, GPT Live without hardware, microphone denial and
recovery, BLE denial/recovery, network reconnect, background/resume, process
restart, update preserving state, logout/login, and export/downloads. Use a real
Android device outside the developer network. Browser fixtures do not establish
native microphone, routing, Bluetooth, passkey or physical-device acceptance.

ESP Security2 provisioning, Android camera capture and any unsupported staff
native tools must remain unavailable until their native adapters and hardware
acceptance exist. Legacy Realtime remains disabled under the current product
contract. An Android beta must not advertise these as verified capabilities.

The existing backend provides the association when
`ApplicationIdentity__AndroidPackageName` and
`ApplicationIdentity__AndroidCertificateSha256__0` are configured. Only these
explicit certificates authorize Android passkey origins; the existing HTTPS
Origin and CSRF rules remain authoritative for HTTP requests. Deploy the exact
GET/HEAD `/.well-known/assetlinks.json` ingress route and verify its public JSON
before native enrollment. A missing/malformed identity returns404. Adding a
certificate requires backend restart because passkey origins are captured at
startup; this is an operator configuration action, not a frontend build step.

Google Play migration can reuse this application ID/key; preserve the key for
Play App Signing enrollment and plan any key upgrade with Android's signing
lineage rules. Firebase APK distribution itself does not require Play Console
verification.

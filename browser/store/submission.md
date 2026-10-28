# Chrome private tester-group submission guide

This prepares a **draft upload**, not a production release or a review
submission. The publisher is Meld Research; the approved public contact is
markusmobius@gmail.com. No store account, deployment, or listing has been created
by the build scripts. The publisher has created a dashboard draft and supplied its
public key. The source, native host, and local UI now use the assigned identity
`pejcljoimoeklelcfbfddnkimlkjgfhe`.

## Build the upload

From the repository root in PowerShell 7:

```powershell
.\browser\prepare-chrome.ps1
```

The ignored `artifacts\chrome-web-store` directory contains the extension ZIP,
its SHA-256 checksum, store artwork, listing copy, this guide, and a
`privacy-site` directory ready for static hosting. Only the explicitly
allowlisted extension files and third-party notice enter the ZIP. The manifest
is at the archive root. No backend binaries, browser profiles, downloads,
credentials, test fixtures, source maps, or development identity key are bundled.

The extension code is not minified or obfuscated. Each ZIP entry has a fixed
timestamp, making the ZIP repeatable for the same inputs and compression runtime.
The SVG files next to this guide are the original artwork sources, not
third-party brand assets. The PNG exports are the browser/store assets.

Run the focused checks with:

```powershell
node --test tests\extension.test.mjs tests\chrome-package.test.mjs
```

The package tests inspect and extract the exact ZIP, then rerun the connector's
behavior suite from those extracted files with a synthetic browser-assigned ID.
They also check required image dimensions, privacy links, the absence of the
development key, repeatable hashes, permission restrictions, and metadata limits.
The normal `.\run-dev.ps1 -Check` includes these checks too.

## Create a Chrome dashboard draft

1. Sign in personally to the Chrome Web Store Developer Dashboard. Complete its
   developer registration, account protection, and publisher/contact requirements.
   Never put dashboard passwords, recovery codes, or API credentials in this repo.
2. Choose **Add new item** and upload the generated ZIP. Uploading a draft is
   distinct from clicking **Submit for review**.
3. Record the assigned item/extension ID. Under **Package > View public key**,
   copy the public key. Do not create a second dashboard item to update the first.
4. Set **Private** distribution, add the publisher-managed tester Google Group,
   use `listing.md`, and leave the draft unsubmitted until every gate below is
   resolved. Private items still require store review.

## Align the store identity with the desktop companion

The supplied dashboard public key has been applied to the source manifest.
The following is the procedure for any future identity change; do not create a
second dashboard item or replace the key with the former development key.

1. In the source `browser\overleaf\manifest.json`, replace the `key` value with
   the dashboard's public key as one base64 line, excluding PEM markers.
   This is a **public** key, not a credential or a private signing key.
2. Rebuild the desktop companion and native host together. The shared C#
   `ExtensionIdentity` reads that source manifest; `/api/extension` therefore
   supplies the same new ID to Angular, and native-host `--register` authorizes it.
   Updating only `--register <id>` is insufficient: the frontend would still try
   to contact the old bundled identity.
3. Update the identity regression expectation in
   `tests\MeldResearch.Tests\NativeHandshakeTests.cs` to the new dashboard ID.
   The browser harness derives its development ID from the manifest.
4. Stop the old local launcher before rebuilding its in-use binaries. Then run
   the launcher to rebuild and register the new identity. Remove/disable the
   old development-ID extension to avoid competing native connections.
5. Verify that the installed/test-loaded extension ID, native host
   `allowed_origins`, and `/api/extension` all match. Verify project listing and
   one selected ZIP through the intended Chrome profile.
6. Rebuild the package and compare its checksum. Changing only the source `key`
   does not change the upload ZIP, because the packager removes it. If packaged
   content changes, increase the manifest version before uploading an update.

Keep the wire protocol and desktop compatibility checks coordinated. The current
desktop UI accepts different extension release versions when their wire protocol
matches. Incompatible protocol versions fail explicitly. This is covered by a
regression test; protocol-breaking releases still require coordinated updates.

## Gates before submitting for review

### Public privacy policy and support

Review the bundled privacy notice for accuracy, including the contact address,
data handling, deletion steps, and current protection limitations. This is
technical disclosure material, not legal advice. Host the contents of
`privacy-site` at a stable public HTTPS location with no sign-in requirement.
The page uses only its adjacent stylesheet and icon. Enter the actual HTTPS page
URL in the store dashboard. The chosen URL is
`https://www.meldresearch.org/privacy.html`. The new remote backend serves that
path without login, but public DNS/HTTPS deployment has not been performed.

### Desktop companion and reviewer access

The extension is not useful without a working native desktop companion.
Provide reviewers and invited testers a real, accessible download and exact
Windows install/start instructions. The repository's source launcher works for
development. `tools\package-local.ps1` builds a self-contained test distribution
with per-user registration/install scripts; it is not a signed consumer installer.
The remote download route requires an existing Meld account and serves only
packages actually placed in its configured download directory.

Do not list macOS as verified until the native registration, data paths,
browser interaction, and distribution package have been tested on a Mac.
Do not fabricate an installer URL or mark companion installation complete
merely because the extension ZIP is valid.

### Data-protection policy review

Chrome's User Data FAQ requires disclosure for local-only data handling and
describes secure transmission and encryption at rest. The current companion
encrypts indexed research and credentials with AES-GCM/OS-protected keys. Manual
ZIP/JSON snapshots and temporary incoming files are still ordinary files, and
loopback HTTP/WebSocket traffic is not TLS-encrypted. Account filesystem
permissions and HMAC authentication are **not** encryption.

Before certifying policy compliance or submitting, resolve these requirements
for the complete extension/native-companion data path: implement the needed
protection, or obtain authoritative clarification on how the store requirements
apply to this local-native design. Do not claim all storage or loopback traffic
is encrypted. The privacy notice describes the remaining limitations explicitly.

### Genuine functional verification and screenshots

Automated connector/native/ZIP tests use synthetic data. The research end-to-end
check also uses synthetic evidence but real Foundry inference: initial import,
delta, and zero model calls for unchanged content. These checks do not establish
a real Google login, a real post-identity-change Overleaf read, or macOS support.
Those first-install/reconnect workflows still need live verification.

Capture at least one genuine working-flow screenshot with only synthetic
research data, at 1280x800 or 640x400 with no added padding. A privacy-page
screenshot or a promotional illustration does not prove the download flow works.
Never upload private project screenshots or a fake connected state.

## Reviewer instructions to complete

Provide these through the dashboard's private test-instructions field after
the companion download is available:

1. Install the compatible Meld Research desktop companion from the actual
   reviewer download URL and start it. It registers a per-user native host and
   opens the local application on port 6001 (6010 for the development launcher).
2. Install this extension in a normal Chrome profile. Confirm the requested
   Overleaf permission. Incognito is intentionally unsupported.
3. Sign in to a test Overleaf account with a harmless project such as
   "Meld connector review" containing a short `main.tex`. Complete any normal
   Overleaf/Google verification in the browser, never in Meld or chat.
4. Reload the local Meld page after initial installation. The bridge connects,
   and project listing is requested automatically. If necessary, use
   **Sign in to Overleaf** and finish loading the project page.
5. Select the synthetic project and choose **Download ZIP**. Verify a saved
   snapshot and **Save ZIP copy**. The original Overleaf project is unchanged.
6. Pause auto-connect, restart Chrome, and confirm it stays paused. Resume it;
   then restart only Meld and verify automatic reconnection without reopening
   the extension popup.
7. With Overleaf signed out, verify that a read reports sign-in required,
   not an empty successful list. With Meld stopped, verify the connector reports
   an unavailable companion and does not export project contents.

For research monitoring, additionally create/link a real test Meld account using
Google, consent to processing the harmless project, enable monitoring, and check
that a meaningful edit produces an update while unchanged content does not.
Google OAuth configuration and real authentication must be ready for this flow.
No personal research is required. If a reviewer cannot create a test account,
arrange dedicated reviewer access privately; never publish credentials or enable
the loopback-only Dummy identity on the public service.

## Submit only after the gates pass

Use the same dashboard item, choose private tester-group visibility, review all data-use
declarations personally, and choose deferred/manual publication if you want a
final approval step after review. Store approval is not guaranteed by local
validation. Native installers and public hosting are separate deliverables.

## Official references

- [Prepare a Chrome extension](https://developer.chrome.com/docs/webstore/prepare)
- [Publish and defer publication](https://developer.chrome.com/docs/webstore/publish)
- [Use the dashboard public key to maintain identity](https://developer.chrome.com/docs/extensions/reference/manifest/key)
- [Privacy declarations](https://developer.chrome.com/docs/webstore/cws-dashboard-privacy)
- [User Data FAQ, including local handling and protection](https://developer.chrome.com/docs/webstore/program-policies/user-data-faq)
- [Image dimensions and screenshot requirements](https://developer.chrome.com/docs/webstore/images)
- [Private test instructions](https://developer.chrome.com/docs/webstore/cws-dashboard-test-instructions)

# Meld Research

An Angular/C# research application rebuilt separately from Project Meld.
**Overleaf is the only private-data source, Updates are the only feed content,
and chat/embedding inference uses Foundry only.** There is no WorkIQ runtime,
other news ingestion, or alternative-provider fallback.

This is a test release, not a deployed service or signed consumer distribution.

## Product identity and navigation

Meld Research is an independent project maintained by **Markus Mobius**, not an
internal Microsoft service. Keep the familiar Meld color distinction:
**green Local Assistant** (`#084d42` to `#0a6655`) and **blue Remote Backend**
(`#1e3c72` to `#2a5298`). The colors identify service roles, not deployment location.
Both services run on the same computer in routine development.

The interface uses **Angular Material and the actual Project Meld UI patterns**:
the 64px toolbar, share icon, Outfit typography, pill navigation, full-height
workspace, and compact source-grounded Update cards. It is not a separate
landing-page/dashboard design. The local working tabs are **Feed**, **About me**
(projects, monitoring and downloads), and **Account** (account/connector setup).
**More** contains Docs, Privacy and Project Team. Only the Updates feed is
implemented; other Meld feed types are not shown as nonfunctional placeholders.
The team currently contains only Markus Mobius. Do not restore Microsoft corporate
footers, internal research-study language, WorkIQ workflows, or unrelated feed types.
Keep accurate Azure/Microsoft provider disclosures and required third-party notices.

Shared Docs/Team markup and site layout live in
[frontend/public](apps/frontend/public); the local and remote `theme.css` files
provide only role-specific palettes and labels. The canonical privacy notice is
[browser/overleaf/privacy.html](browser/overleaf/privacy.html), shared by the connector
and both websites. Do not maintain separate, drifting copies of privacy text.
The npm build/test hooks copy its generated assets into the Angular workspace;
edit the canonical connector notice, not the ignored generated copies. The
website wrapper uses its own styles rather than importing extension-popup
control styles.

[MeldToolbar](apps/frontend/src/app/meld-toolbar.ts) is shared by the local app,
the remote pages, and public Docs/Privacy/Team pages. The remote forms remain
server-rendered with their existing antiforgery protection; Angular supplies the
same Material navigation around them. The npm `postbuild` hook
[finalizes public assets](apps/frontend/finalize-site-assets.mjs) with the hashed
Angular bundles. **Build the frontend before the C# solution** so the remote
backend copies the current bundles. Outfit and Material Icons fonts are
self-hosted; keep the generated font licenses in distribution packages.
See [AGENTS.md](AGENTS.md) for the persistent development instructions.

## Architecture

- [Remote backend](apps/RemoteBackend): Google signup, existing-account device
  approval, Foundry chat/embeddings, protected account/device records, gated
  desktop downloads, and a public `/privacy.html`.
- [Local backend](apps/LocalBackend): browser bridge, explicit project selection,
  background synchronization, encrypted research state, and an Updates feed.
- [Angular frontend](apps/frontend): account/connector onboarding, processing
  consent, monitoring controls, project status, citations, and manual ZIP copies.
- [Research contracts](libraries/Research): source evidence, deterministic
  revisions, grounded changes, semantic-match candidates, and state reduction.
- [Browser connector](browser/overleaf) and [native host](apps/BrowserHost):
  read-only requests through the user's normal Overleaf browser session.

```text
Selected Overleaf project
  -> normal browser + connector + native host
  -> local snapshot/content delta
  -> authenticated Meld remote API
  -> configured Foundry chat and embeddings
  -> local research knowledge and Updates
```

Foundry credentials never go to the Angular app or browser extension. The remote
service does not persist project archives or log research payloads.

## Run the full development stack

Requires .NET 10, Node 24/npm, PowerShell 7, and Windows or macOS.

```powershell
# Explicitly reuse the existing Project Meld Foundry credential service for development.
# Saves route/deployment settings, NOT the returned token.
.\tools\configure-foundry.ps1

.\run-dev.ps1 -Mode Dummy
```

- Local Angular application/API: <http://127.0.0.1:6010>
- Dummy remote account service: <http://127.0.0.1:6050>
- Public policy preview: <http://127.0.0.1:6050/privacy.html>
- Docs and Project Team: `/docs.html` and `/team.html` on either app address.

Dummy uses synthetic Overleaf evidence and an explicitly labelled synthetic
account, but **real configured Foundry inference**, which consumes quota.
It does not access a real Overleaf profile or register the browser connector.

1. Open **Account** in Meld and choose **Link the test account**.
2. Open the approval link, use the synthetic account, and approve the device.
3. Open **About me**, expand **Select Overleaf projects**, read the model-processing
   notice, and check consent.
4. Choose **Monitor project**. Initial ingestion produces baseline research updates
   in **Feed**; **Review** opens the complete source evidence.
5. In **About me**, choose **Apply synthetic project change** to exercise a meaningful delta.
6. The **Check for changes** refresh control in **Feed** makes zero model calls
   when supported content has not changed.

Keep the launcher terminal open. Ctrl+C stops only its owned backend processes.
Occupied ports are reported; unrelated or installed applications are not stopped.
Logs and Dummy data persist in ignored `.local-test\Dummy` directories.

```powershell
.\run-dev.ps1 -Mode Dummy -SkipBuild
.\run-dev.ps1 -Check
```

`-Check` runs C#, Angular, connector/package, native-transport/ZIP, and research
end-to-end checks. The research check uses fresh isolated data and real Foundry.
It verifies account and consent gates, initial import, deltas, exactly zero model
calls for unchanged content, encrypted persistence, and revocation. All owned
test processes and temporary directories are cleaned up. No browser-injected
fixtures or separate frontend-only server are used.

The development credential-source option is rejected in Production. For
independent development, configure your own Foundry settings instead of importing
Project Meld's routes.

## Real Overleaf and the Chrome identity

```powershell
.\run-dev.ps1 -Mode Live
# Once the real remote service is deployed:
.\run-dev.ps1 -Mode Live -RemoteUrl https://www.meldresearch.org
```

Live uses the normal Chrome/Edge Overleaf session and registers this application's
per-user native host. With no `-RemoteUrl`, it starts a local remote backend on 6050;
that backend requires real Google OAuth configuration. No synthetic identity is
available in Live mode. Listing/manual ZIP downloads remain available separately
from account-gated model processing.

The source manifest now uses the supplied Chrome draft public key:
**`pejcljoimoeklelcfbfddnkimlkjgfhe`**, version **0.3.0**, wire protocol **2**.
It is a public identity key, not a signing secret.

Until the private listing is approved, remove the old development-ID unpacked
extension and load [browser/overleaf](browser/overleaf) again through
`chrome://extensions` or `edge://extensions`. Verify the ID above, then reload
Meld. A published store installation still requires one browser approval.
Native registration is automatic; silent consumer extension installation is not.

Normal use does not require the extension popup. The connector reconnects on
startup, uses a one-minute fallback alarm, respects a persistent pause preference,
and creates background Overleaf tabs without stealing focus. Explicit sign-in
opens a normal tab. One browser/profile can connect at a time.

## Updates-only ingestion

- Monitoring is off until an existing Meld account is linked and the current
  processing-consent version is explicitly accepted for each selected project.
- The default check interval is five minutes. Metadata suppresses unnecessary
  downloads; content is also rechecked hourly and on explicit request.
- File hashes, not ZIP timestamps or ZIP byte order, identify content revisions.
- Initial imports establish existing knowledge; observation time is not presented
  as the date the underlying research happened.
- Delta analysis receives current/before text and relevant existing facts.
  Foundry embeddings rank semantic candidates; they do not decide a novelty
  threshold. Foundry decides additions, revisions, removals, or no meaningful change.
- The model selects source line ranges; the server copies the exact source text
  rather than trusting the model to reproduce quotations. Every accepted change
  must refer to changed evidence. Invalid citations, unknown fact IDs, malformed results, and
  failed embeddings are errors, not empty successful updates.
- Knowledge, feed entries, and the processed revision commit together. Failure,
  cancellation, account switching, pausing, and deletion cannot commit stale
  in-flight results. Source-access failures do not erase saved knowledge.
- Unchanged supported text makes no inference calls. Format-only changes may
  require model analysis but need not produce an update.
- PDFs/images are tracked by hash but not analyzed. This release supports UTF-8
  `.tex`, `.bib`, `.md`, and `.txt`: 128 KiB per text file, 256 KiB total text,
  2,000 file entries, 200 active facts, and at most 12 changes per analysis.
- The UI shows the latest 100 updates; earlier entries remain stored. The
  encrypted test-release store is bounded at 64 MiB and fails explicitly at capacity.

The browser must be running for fresh reads. Saved research remains local; model
processing also requires remote connectivity. Revocation retries are explicit if
the remote server is unavailable. **Forget local research** deletes the selected
account's index/history, not Overleaf originals or manually downloaded ZIPs.

## Google signup and production configuration

Google credentials have **not** been provisioned or live-tested. The implementation
uses ASP.NET Core Google authentication with PKCE and verified email claims.
Signing into Google during device linking does not silently register an account:
signup takes place explicitly on the remote website. Installation credentials
last 30 days and can be revoked.

1. Create a Google OAuth **Web application** client.
2. Register `https://www.meldresearch.org/auth/google/callback` as its redirect URI.
3. Configure the Google consent screen and test users. Chrome Web Store tester
   groups and Google OAuth test-user access are separate settings.
4. Supply `Google:ClientId` and `Google:ClientSecret` on the remote server through
   a secret store or `MELDRESEARCH_Google__ClientId` /
   `MELDRESEARCH_Google__ClientSecret`. Never put secrets in this repository.
   Development can use `dotnet user-secrets --project apps\RemoteBackend`.
5. Configure `Foundry:Endpoint` as an HTTPS `/openai/v1/` base URL,
   `Foundry:ChatDeployment`, and `Foundry:EmbeddingDeployment`. Production uses
   managed identity (optional `Foundry:ManagedIdentityClientId`) or a secret-stored
   `Foundry:ApiKey`. Grant the identity inference access to the Foundry resource.
6. Set `PublicOrigin=https://www.meldresearch.org`, a durable private
   `DataDirectory`, and optionally `DownloadDirectory` for available packages.

The remote backend defaults to loopback port 8080 in Production, for a same-host
HTTPS reverse proxy. Configure `ListenAddress` and explicit `TrustedProxies`
when deploying elsewhere; never trust arbitrary forwarded headers. `/healthz`
is available for liveness checks. Non-development application routes require
HTTPS. Production on non-Windows platforms also requires
`DataProtection:CertificatePath` and its secret-stored password to protect the
persisted ASP.NET key ring. Back up that key ring with the protected account store.
This version supports one remote process per data directory, not multi-instance
database deployment. Do not log authorization headers, OAuth codes, or bodies
at the reverse proxy.

No DNS, certificate, cloud resource, production deployment, or store submission
is performed by the source launcher. The chosen public policy URL is
<https://www.meldresearch.org/privacy.html>; it must actually be hosted publicly
before store review.

## Desktop and extension packaging

```powershell
.\tools\package-local.ps1 -Runtime win-x64
.\tools\package-local.ps1 -Runtime osx-arm64
.\tools\package-local.ps1 -Runtime osx-x64
.\browser\prepare-chrome.ps1
```

Desktop ZIPs in `artifacts\desktop` are self-contained **test distributions**.
They include per-user installation/start scripts, native-host registration, the
Angular UI, and a development connector folder. They do not require a separate
.NET installation. Windows uses `Install-Meld.ps1` under the machine's normal
PowerShell policy. macOS uses `Install-Meld.command`. The terminal remains the
owned launcher; autostart/update distribution is not implemented.

After the private store listing is approved, build with `-StorePublished` so
onboarding opens the store installation flow. The browser still asks the user
to approve installation. Signing/notarization and live macOS verification remain
release gates; these scripts are not a signed MSI/PKG distribution.

Use the existing Chrome draft, **Private** visibility and the tester Google
Group under Distribution. See [submission instructions](browser/store/submission.md)
and [listing/disclosures](browser/store/listing.md). Version 0.3.0 changes data
processing disclosures: do not submit the old "no AI processing" text.

## Storage, boundaries, and remaining release gates

Live/installed data remains in `%LOCALAPPDATA%\MeldResearch\OverleafPrototype`
on Windows or `~/Library/Application Support/MeldResearch/OverleafPrototype`
on macOS, preserving existing downloads. `MELDRESEARCH_DATA_DIR` is an absolute
test override; a real browser-launched native host must see the same override.
Research text, credentials, vectors, and updates are AES-GCM encrypted with a
Windows DPAPI- or macOS Keychain-protected key. Separate linked accounts do not
see each other's indexed projects. Manual ZIP/JSON downloads and temporary
incoming archives are not encrypted by Meld; exported copies/backups are the
user's responsibility.

The local API checks loopback Host/Origin and protects mutations with antiforgery.
Native peers use nonce-bound mutual HMAC authentication. Loopback UI/native
traffic is HTTP/WS, not TLS. These remaining plaintext paths and store-policy
requirements still need resolution/authoritative clarification before review.
Do not claim end-to-end encrypted local transport or complete store compliance.

Only metadata fields and selected ZIP bytes cross the browser boundary.
Cookies, passwords, browser databases, Overleaf page tokens, and unused account
metadata are not exported. ZIP paths, symlinks, duplicates, and expansion are
validated; TeX is never executed. Limits: 8 MiB metadata, 100 MiB ZIP, 256 MiB
expanded, 10,000 archive entries, one active transfer, five-minute transfer limit.

Hosted `www.overleaf.com` only. Firefox, Safari, self-hosted Overleaf, editing,
and live collaboration are unsupported. A real post-identity-change Overleaf
read and production Google login still require user verification. macOS
registration/Keychain/package paths need a Mac test before being claimed verified.

HTTP examples: [Overleaf](apps/LocalBackend/Overleaf.http),
[research](apps/LocalBackend/Research.http), and [remote](apps/RemoteBackend/Remote.http).
Both development APIs expose `/openapi/v1.json`.

For public-page/branding changes, the focused integration check does not need
Foundry credentials and makes no model calls:

```powershell
node tests\research.e2e.mjs --public-pages-only
```

Build the Angular app and both backends before running it. The full `-Check`
continues to include real Foundry processing.

The Angular CLI's development-only MCP dependency is pinned via an override;
there is no application MCP integration. The Overleaf technique is informed by
[pyoverleaf](https://github.com/jkulhanek/pyoverleaf), with attribution in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). No browser-cookie database is read.

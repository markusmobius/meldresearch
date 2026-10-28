# Meld Research

Meld Research turns changes in your Overleaf projects into a private feed of
grounded research **Updates**. A desktop app (Angular UI on a local C# backend)
reads the projects you select through your normal browser session, keeps the
research index on your computer, and sends only the changed text you consented
to process to Azure AI Foundry through a small remote service. Every Update cites
the exact source lines it came from.

**Overleaf is the only private-data source, Updates are the only feed content,
and chat/embedding inference uses Azure AI Foundry only**, with no fallback to
another provider.

This is a test release, not a signed consumer distribution.

## Product identity and navigation

Meld Research is an independent project maintained by **Markus Mobius**, not an
internal Microsoft service. Two colors identify the two service roles:
**green Local Assistant** (`#084d42` to `#0a6655`) and **blue Remote Backend**
(`#1e3c72` to `#2a5298`). The colors identify service roles, not deployment location.
Both services run on the same computer in routine development.

The interface uses **Angular Material**: a 64px toolbar with a share icon, Outfit
typography, pill navigation, a full-height workspace, and compact source-grounded
Update cards. The local working tabs are **Feed**, **About me**
(projects, monitoring and downloads), and **Account** (account/connector setup).
**More** contains Docs, Privacy and Project Team. Only the Updates feed exists;
there are no placeholder feed types. The team currently contains only Markus Mobius.
The app keeps accurate Azure/Microsoft provider disclosures and the required
third-party notices.

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
  approval, Foundry chat/embeddings, a SQLite account database, gated
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

Development needs a Foundry configuration (see
[Foundry setup](#foundry-setup-and-llm-smoke-test)) in the ignored
`.local-test\Foundry.Local.json`:

```json
{ "Foundry": {
    "Endpoint": "https://meldllm.openai.azure.com/openai/v1/",
    "ChatDeployment": "gpt-6-luna",
    "EmbeddingDeployment": "text-embedding-3-large" } }
```

Your workstation has no managed identity, so authenticate in one of two
development-only ways (both are rejected in Production):

- **API key** (if the resource allows key authentication), kept in .NET user
  secrets rather than in the file:
  `dotnet user-secrets set Foundry:ApiKey <key> --project apps\RemoteBackend`.
- **Development token service**: an HTTPS endpoint you operate that returns
  short-lived Foundry tokens for the same endpoint and deployments.
  `.\tools\configure-foundry.ps1 -TokenSource <url>` checks it and writes the
  settings file with `Foundry:DevelopmentTokenSource`; no token is saved.

```powershell
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

Google sign-in has **not** been live-tested with the real client yet. It uses
**Sign in with Google** (Google Identity Services): the browser receives a
Google-signed ID token, and the remote backend verifies its signature against
Google's published keys, the issuer, this site's client ID as audience, its
expiry, a verified email address, and a one-time nonce bound to the browser
session. **No client secret is used.** Google's script loads only on the sign-in
pages (`/signup`, `/login`, and `/connect` before sign-in), whose content-security
policy allows `accounts.google.com/gsi`; the button's callback submits the token
through the site's own antiforgery-protected form. Signing in does not silently
register an account: signup takes place explicitly on the remote website.
Installation credentials last 30 days and can be revoked.

1. Create a Google OAuth **Web application** client and add
   `https://www.meldresearch.org` under **Authorized JavaScript origins**. No
   redirect URI or client secret is needed. For local Live testing, also add
   `http://127.0.0.1:6050`, `http://localhost:6050` and `http://localhost`.
2. Configure the Google consent screen and test users. Chrome Web Store tester
   groups and Google OAuth test-user access are separate settings.
3. Supply the public client ID as `Google:ClientId`
   (`MELDRESEARCH_Google__ClientId`; already in [remote.env.example](deploy/remote.env.example)).
4. Configure `Foundry:Endpoint` as an HTTPS `/openai/v1/` base URL,
   `Foundry:ChatDeployment`, and `Foundry:EmbeddingDeployment`. Production uses
   managed identity (optional `Foundry:ManagedIdentityClientId`) or a secret-stored
   `Foundry:ApiKey`. Grant the identity inference access to the Foundry resource.
   See [Foundry setup](#foundry-setup-and-llm-smoke-test).
5. Set `PublicOrigin=https://www.meldresearch.org`, a durable private
   `DataDirectory`, and optionally `DownloadDirectory` for available packages.

### Foundry setup and LLM smoke test

Production uses the Azure AI Foundry resource `meldllm` (Central US) with a
**`gpt-6-luna`** (model version `2026-09-22`) chat deployment named `gpt-6-luna`
and a **`text-embedding-3-large`** deployment of the same name (3,072 dimensions).
Do not change the embedding model later: users' locally stored vectors depend on it.
Settings are deployment names; the endpoint is the resource's OpenAI endpoint,
`https://<resource>.openai.azure.com/openai/v1/`, not the Foundry project endpoint.
Authenticate with a dedicated managed identity holding only **Cognitive Services
OpenAI User** on that resource (token scope `https://ai.azure.com/.default`).
Chat calls are stateless, strict-schema Chat Completions with `store: false`.
That does not disable Azure abuse monitoring; choose a regional or Data Zone
deployment when processing location matters.

The smoke test exercises the production path. It sends synthetic Overleaf evidence
through the real prompt, strict schema and grounded-citation validation, then
embeds two texts. It **consumes model quota**, prints no model content or
credentials (unless `--show-response`), never falls back to another provider, and
does not open the data directory, so it can run beside the live service.
`--expect-endpoint` refuses to send anything if the configuration targets a
different resource.

```powershell
# Development, with the configured development Foundry settings:
dotnet build .\apps\RemoteBackend --nologo
.\apps\RemoteBackend\bin\Debug\net10.0\MeldResearch.RemoteBackend.exe --foundry-smoke-test `
  --environment Development --SettingsFile .\.local-test\Foundry.Local.json
```

```bash
# On the server, as the service user with the service's configuration and managed identity:
sudo systemd-run --wait --pipe --quiet --collect -p User=meldresearch \
  -p EnvironmentFile=/etc/meldresearch/remote.env -p Environment=ASPNETCORE_ENVIRONMENT=Production \
  /opt/meldresearch/current/MeldResearch.RemoteBackend --foundry-smoke-test \
  --expect-endpoint https://meldllm.openai.azure.com/openai/v1/
```

Or deploy and run that server check in one step: `./deploy/deploy.sh -FoundrySmokeTest`.
The production template authenticates to `meldllm` with the VM's user-assigned
managed identity; that identity needs the role above on `meldllm` and network
access to it. `meldllm` is shared with another application, so both draw on the
same deployment capacity; give Meld Research its own deployments (same model
versions, its own tokens-per-minute limit) if rate limits ever collide.

Offline Foundry checks (pinned deployments, `store: false`, managed-identity bearer
scope, no fallback, content-free smoke-test output) run with the normal C# tests.

The remote backend defaults to loopback port 8080 in Production, for a same-host
HTTPS reverse proxy. Configure `ListenAddress` and explicit `TrustedProxies`
when deploying elsewhere; never trust arbitrary forwarded headers. `/healthz`
is available for liveness checks. Non-development application routes require
HTTPS. Production on non-Windows platforms also requires
`DataProtection:CertificatePath` and its secret-stored password to protect the
persisted ASP.NET key ring. Do not log authorization headers, OAuth codes, or
bodies at the reverse proxy.

### Remote database

The remote backend stores accounts, linked-device credentials, pending device-link
approvals, and the ASP.NET Data Protection key ring in a SQLite database,
`<DataDirectory>/remote.db` (WAL mode, owner-only file permissions), through
EF Core ([RemoteDbContext](apps/RemoteBackend/Data/RemoteDbContext.cs)). Pending
links and sign-in sessions therefore survive restarts. Migrations in
[Data/Migrations](apps/RemoteBackend/Data/Migrations) are applied automatically
at startup. Names, email addresses, device names, and not-yet-collected access
tokens are stored as Data Protection ciphertext; installation and device codes
are stored only as SHA-256 hashes. The Google subject is the plain lookup key.

SQLite supports one remote process per data directory (`server.lock` enforces this),
not multiple load-balanced instances. Research text is never stored remotely.
After changing the model, add a migration with the repository's pinned tool:

```powershell
dotnet tool restore
dotnet ef migrations add <Name> --project apps\RemoteBackend --output-dir Data\Migrations --namespace MeldResearch.RemoteBackend.Data.Migrations
```

Check that `RemoteDbContextModelSnapshot.cs` was written to `Data\Migrations`.

No DNS, certificate, cloud resource, production deployment, or store submission
is performed by the source launcher. The chosen public policy URL is
<https://www.meldresearch.org/privacy.html>; it must actually be hosted publicly
before store review.

## Deploy the remote backend to Ubuntu

[deploy/](deploy) hosts the blue remote service on an Ubuntu server as
`https://www.meldresearch.org`: nginx terminates TLS and proxies to Kestrel on
`127.0.0.1:8080`, run by systemd as the unprivileged `meldresearch` user. **You
set the server up once by hand; releases are built and deployed on the server
from a clone of this repository.**

| File | Purpose |
| --- | --- |
| [deploy.sh](deploy/deploy.sh) | Bash entry point: installs missing prerequisites (curl, sqlite3, ICU, PowerShell 7; asks first, `--yes` skips the prompt, `--prerequisites` stops after this), checks the build tools, then runs `deploy.ps1` with the same arguments. |
| [deploy.ps1](deploy/deploy.ps1) | Run on the server as your deploy user: builds the remote backend (always) and the desktop packages (only when the client changed; cross-built for Windows/macOS), signs desktop releases, then runs `apply-release.sh` with sudo. |
| [apply-release.sh](deploy/apply-release.sh) | Verifies your setup, installs the release under `/opt/meldresearch/releases`, restarts, health-checks (rollback on failure), then publishes desktop updates. Never installs packages, nginx, certificates, units, users or secrets. |
| [meldresearch-remote.service](deploy/meldresearch-remote.service) | Reference systemd unit (hardened, non-secret production settings). |
| [nginx/meldresearch.conf](deploy/nginx/meldresearch.conf) | Reference nginx site: TLS, `meldresearch.org` → `www`, 3 MiB body limit, 5-minute analysis timeout, path-only access logs. |
| [remote.env.example](deploy/remote.env.example) | Template for `/etc/meldresearch/remote.env`, with the (non-secret) Google client ID and Foundry values filled in. |

### Server setup (once, by hand)

Prerequisites: DNS A/AAAA records for `www.meldresearch.org` and `meldresearch.org`
point to the server; inbound TCP 80 and 443 are open (on Azure, in the VM's network
security group); you have a normal **deploy user** with `sudo` (not root, not
`meldresearch`); the VM's user-assigned managed identity holds **Cognitive Services
OpenAI User** on the Foundry resource and can reach it over the network.

1. **Build tools, repository and prerequisites** (as the deploy user). Install
   git and Node 24 with npm (e.g. NodeSource), and the .NET 10 SDK from one apt
   source only (`apt-cache policy dotnet-sdk-10.0`). Then clone the repository and
   let `deploy.sh` install the remaining prerequisites: curl, sqlite3, the ICU
   library .NET needs, and PowerShell 7 (from Microsoft's apt repository, or the
   snap when that repository does not cover your Ubuntu release yet):

   ```bash
   git clone <repository URL> ~/meldresearch && cd ~/meldresearch
   bash deploy/deploy.sh --prerequisites      # asks before installing anything with sudo
   cd deploy
   ```

2. **System configuration** (from `~/meldresearch/deploy`):

   ```bash
   sudo apt-get install -y nginx certbot
   sudo useradd --system --user-group --home-dir /var/lib/meldresearch --no-create-home --shell /usr/sbin/nologin meldresearch
   sudo install -d -m 0750 -o root -g meldresearch /etc/meldresearch
   sudo install -m 0640 -o root -g meldresearch remote.env.example /etc/meldresearch/remote.env
   sudoedit /etc/meldresearch/remote.env      # review; it needs no secrets with a managed identity
   # Data-protection certificate (protects the key ring in remote.db); back it up with the database.
   export pw=$(openssl rand -hex 32); d=$(mktemp -d)
   openssl req -x509 -newkey rsa:3072 -sha256 -nodes -days 36500 -subj "/CN=Meld Research Remote Data Protection" -keyout $d/k.pem -out $d/c.pem
   openssl pkcs12 -export -inkey $d/k.pem -in $d/c.pem -keypbe AES-256-CBC -certpbe AES-256-CBC -macalg sha256 -passout env:pw -out $d/dp.pfx
   sudo install -m 0640 -o root -g meldresearch $d/dp.pfx /etc/meldresearch/dataprotection.pfx
   printf 'MELDRESEARCH_DataProtection__CertificatePassword=%s\n' "$pw" | sudo sh -c 'umask 027; cat > /etc/meldresearch/dataprotection.env'
   sudo chown root:meldresearch /etc/meldresearch/dataprotection.env
   rm -rf $d; unset pw
   sudo install -m 0644 meldresearch-remote.service /etc/systemd/system/ && sudo systemctl daemon-reload && sudo systemctl enable meldresearch-remote
   # TLS certificate (any method; the site expects /etc/letsencrypt/live/www.meldresearch.org/), e.g.:
   sudo install -d /var/www/letsencrypt
   sudo certbot certonly --standalone -d www.meldresearch.org -d meldresearch.org \
     --pre-hook 'systemctl stop nginx' --post-hook 'systemctl start nginx'
   sudo install -m 0644 nginx/meldresearch.conf /etc/nginx/sites-available/
   sudo ln -s ../sites-available/meldresearch.conf /etc/nginx/sites-enabled/
   sudo nginx -t && sudo systemctl reload nginx
   ```

3. **Update signing key**: install it as described in the next section.
4. **First release**: `cd ~/meldresearch && bash deploy/deploy.sh -FoundrySmokeTest`
   (it builds everything, asks for the signing passphrase, starts the service and
   checks Foundry with the managed identity).
5. **Verify**: `curl https://www.meldresearch.org/healthz`, then open the site and
   sign up with Google. If the Google button reports that the origin is not
   allowed, add `https://www.meldresearch.org` to the OAuth client's Authorized
   JavaScript origins; in OAuth "Testing" mode, add your account as a test user.

### Update signing key

Desktop update manifests are signed with an ECDSA P-256 key whose public half is
[committed](apps/LocalBackend/update-signing-public-key.txt) and stamped into every
package. The private key is a **passphrase-encrypted PEM** in the deploy user's
`~/.config/meldresearch/update-signing-key.pem` (mode 0600). The `meldresearch`
service account cannot read it, so a compromise of the web service cannot sign
updates; the passphrase is typed only when a desktop release is signed, so the
file alone is useless. Someone with root who captures the passphrase during a
deploy could still sign releases.

```bash
# One-time move of the existing key (created on Windows) to the server:
#   Windows:  .\tools\update-signing-key.ps1 -ConvertWindowsKey .\update-signing-key.pem
#             scp .\update-signing-key.pem you@www.meldresearch.org:
#   then delete %APPDATA%\MeldResearch\update-signing-key.dpapi.
cd ~/meldresearch
pwsh tools/update-signing-key.ps1 -Install ~/update-signing-key.pem && rm ~/update-signing-key.pem
pwsh tools/update-signing-key.ps1          # verify key + passphrase
```

Keep one offline copy of the encrypted PEM and its passphrase. Without the key,
installed clients can no longer be updated automatically; `-Create` makes a new
key, but only fresh installs trust it. Never commit the private key.

### Releases

```bash
cd ~/meldresearch && git pull --ff-only
./deploy/deploy.sh                         # add -FoundrySmokeTest to verify Foundry afterwards
./deploy/deploy.sh -BuildOnly              # build and stage in artifacts/remote without applying
```

`deploy.sh` and `pwsh deploy/deploy.ps1` are equivalent; the PowerShell script is
the single implementation (on Windows it can build with `-BuildOnly`; applying a
release needs the Ubuntu server). If `./deploy/deploy.sh` reports "Permission
denied", run `bash deploy/deploy.sh`, or record the executable bit once with
`git update-index --chmod=+x deploy/deploy.sh` and commit.

- The **remote backend** is rebuilt and deployed every time. Releases live in
  `/opt/meldresearch/releases` (latest five kept); a release failing
  `http://127.0.0.1:8080/healthz` is rolled back and publishes no desktop update.
- The **desktop client** has a content-based release identity: the SHA-256 of
  the committed client inputs' git tree (`apps/LocalBackend`, `apps/BrowserHost`,
  `apps/frontend`, `libraries`, `browser/overleaf`, the packaging/install/launch
  scripts and build settings; see [release-common.ps1](tools/release-common.ps1)).
  Packages for `win-x64`, `osx-arm64` and `osx-x64` are rebuilt (and the
  passphrase requested) only when that identity changes, and republished only
  when they differ from the live release. Uncommitted client changes are refused,
  because a changed build with an old version would be skipped by every updater.
  `-ForceDesktopBuild` rebuilds anyway; `-DesktopRuntimes` limits platforms.
- Packages are served publicly at `/api/updates/{runtime}` and
  `/api/updates/{runtime}/package/{version}` from `/var/lib/meldresearch/downloads`.
  The same ZIPs remain account-gated website downloads at `/downloads/{runtime}`.

**Back up together:** the database and `/etc/meldresearch/dataprotection.pfx` +
`dataprotection.env`, and separately the encrypted update signing key with its passphrase. Take a consistent online copy with
`sudo -u meldresearch sqlite3 /var/lib/meldresearch/remote/remote.db ".backup '/var/lib/meldresearch/remote-backup.db'"`
(copying `remote.db` alone while the service runs can miss WAL data). Never
regenerate the certificate while `remote.db` exists: the key ring, sessions and
encrypted account columns would become unreadable. Logs: `journalctl -u meldresearch-remote`.

## Desktop and extension packaging

```powershell
.\tools\package-local.ps1 -Runtime win-x64 -AllowUncommitted   # local test build; never self-updates
.\browser\prepare-chrome.ps1
```

Desktop ZIPs in `artifacts\desktop` are self-contained **test distributions**.
They include per-user installation/start scripts, native-host registration, the
Angular UI, and a development connector folder. They do not require a separate
.NET installation. Windows uses `Install-Meld.ps1` under the machine's normal
PowerShell policy; macOS uses `Install-Meld.command`. Each release installs side
by side in `MeldResearch\App\<first 12 characters of the release>`, and a stable
`App\Start-Meld.ps1` / `Start-Meld.command` launcher starts the version named in
`App\current.json`. The terminal remains the owned launcher; autostart is not implemented.

**Automatic updates.** An installed client checks `/api/updates/{runtime}` after
45 seconds and then every two minutes. It installs a release only if the manifest
signature verifies with the stamped public key, the runtime matches, the release
is **newer** (by commit date; replays and downgrades are refused) and the package
length and SHA-256 match. Extraction rejects unsafe ZIP entries. The new version is
installed into its own folder (the running files are never modified), the native
host is re-registered, `current.json` and the stable launcher are repointed, and
once no research cycle or browser transfer is running the application exits with
code 75. The launcher then starts the new version; if it does not become ready,
the launcher returns to the previous version and never retries that release. The
current and previous versions are kept; older ones are pruned. `/api/version` reports
the installed release and update state. Development, Dummy and uncommitted builds
never update themselves. Releases installed before this mechanism need one manual
install. The browser extension still updates through the Chrome Web Store.

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

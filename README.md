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
Update cards. The local working tabs are **Feed**, **About me** (OpenAlex
publications), and **Account**: vertical setup steps for the **Meld account**
(sign in, disconnect, leave), **Remote Sync** (required when the server offers
it: choose or enter the passphrase; afterwards only *Show recovery key*),
**Backup** (during setup an optional restore that can be skipped; afterwards
download and restore), **Overleaf** (**Connection** and **Projects**, where
projects are included or excluded) and **OpenAlex**. During setup a step opens
only once the previous one is finished (later steps are greyed out with a lock);
after setup all stay open. Remote Sync blocks setup
(also while its state is still being checked); the Overleaf project nudge waits
for the restore step.
**More** contains Docs, Privacy and Project Team. Only the Updates feed exists;
there are no placeholder feed types. The Project Team page lists Markus Mobius
(developer), Tanya Rosenblat (University of Michigan), Lukas Bolte (Carnegie
Mellon University) and Sid Satya (Brown University).
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

- [Remote backend](apps/RemoteBackend): Google signup, local-app sign-in
  (loopback redirect + PKCE), Foundry chat/embeddings, a SQLite account database, one-line
  desktop installers on the account page, and a public `/privacy.html`.
- [Local backend](apps/LocalBackend): browser bridge, explicit project selection,
  background synchronization, encrypted research state, and an Updates feed.
- [Angular frontend](apps/frontend): account/connector onboarding, processing
  consent, monitoring controls, project status and summaries, and citations.
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

1. Open **Account** in Meld and choose **Sign in with the test account**.
2. On the blue website, choose **Use the synthetic test account**; it returns to Meld signed in.
3. Meld opens **Account → Overleaf → Projects**. Read the model-processing notice
   above the list; choosing **Include** is the consent for that project and starts
   processing it at once, ahead of queued work. **Summarize** on a recent project
   sends only its main `.tex` file (`\documentclass` + `\begin{document}`, top folder
   and `main.tex` preferred) for a one-paragraph description to help you decide; it
   is stored locally and the model is not called again while that file is unchanged.
4. Choose **Include** for the synthetic project. Initial ingestion produces baseline
   research updates in **Feed**; **Review** opens the complete source evidence.
5. In **Projects**, choose **Apply synthetic project change** to exercise a meaningful delta.
6. **OpenAlex** offers a search by your name (Foundry plus OpenAlex; consumes quota)
   that answers while you wait; confirm an author or choose **I'm not on OpenAlex**.
   The confirmed author's publications are imported by a persisted background job
   (resumes after restart, backs off on failure, refreshes weekly) into **About me**.
7. The **Check for changes** refresh control in **Feed** makes zero model calls
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
available in Live mode.

The source manifest now uses the supplied Chrome draft public key:
**`pejcljoimoeklelcfbfddnkimlkjgfhe`**, version **0.6.12**, wire protocol **2**.
It is a public identity key, not a signing secret.

Until the private listing is approved, remove the old development-ID unpacked
extension and load [browser/overleaf](browser/overleaf) again through
`chrome://extensions` or `edge://extensions`. Verify the ID above, then reload
Meld. A published store installation still requires one browser approval.
Native registration is automatic; silent consumer extension installation is not.

Normal use does not require the extension popup. The connector reconnects on
startup, uses a one-minute fallback alarm, respects a persistent Disconnect
preference, and reads Overleaf in its **own background tab** without stealing focus:
never a tab you work in, so switching projects or reloading Overleaf cannot break a
read. That tab is kept awake during reads and closed after two idle minutes (unless
you switched to it). A read that breaks before any data reached Meld is retried once
in a fresh tab; otherwise the error says whether the tab was closed, moved, put to
sleep or needs sign-in, and Meld retries automatically. Explicit sign-in uses your
normal Overleaf tab. One browser/profile can connect at a time. The popup
offers only **Sign in to Overleaf** until Overleaf is signed in, then **Connect** or
**Disconnect**.

### Connector updates

Chrome keeps loading an unpacked extension from the folder it was given, while each
desktop release lives in its own folder that later updates delete. An installed Meld
therefore mirrors its connector to the stable folder `<install root>\connector`
(Windows: `%LOCALAPPDATA%\MeldResearch\App\connector`) at startup, and setup points
**Load unpacked** there. When the running connector reports an older version than
the release ships, the Meld page asks it once per tab to reload itself from that
folder. If it is still older afterwards (loaded from an old release folder, or 0.3.0,
which cannot reload on request), Meld opens Account → Overleaf with update steps
instead of the feed. **Bump the manifest version whenever connector files change**;
the version is what Meld compares. A store-published connector updates through the
Chrome Web Store instead.

## Research projects: details, summary and changelog

About me shows one card per **research project**, built as specified in
[OVERLEAF.md](OVERLEAF.md):

- **a) Summary**: two sentences, rewritten from b) once a cycle that changed b) ends.
- **b) Details**: the current state of the project, at most 30 one-sentence details,
  each with its **sources**: an Overleaf document at a content hash, or a Gmail
  thread up to a message. Select a detail to see its sources and the cited lines;
  **Remove** takes a source off every detail of the card (a detail left without
  sources is deleted) and puts it in **Housekeeping**, where the user adds it to
  another project or skips it.
- **c) Changelog**: every source that updated the project, with what it added,
  revised or removed, newest first and loaded while scrolling. The feed's
  **Updates** are the changelog entries that changed details.

How Overleaf is read:

- Monitoring is off until an existing Meld account is linked and the current
  processing-consent version is accepted by choosing **Include** for a project.
- **Document trees.** Meld builds the `\input`/`\include`/`\subfile`/`\import`
  graph of the project's `.tex`, `.md` and `.txt` documents
  ([LatexTree](libraries/Research/LatexTree.cs)). Each **root** (a document nothing
  includes: the paper, a memo, a slide deck, or an orphan fragment of at least
  1,000 characters) is read with everything it includes, in reading order. A
  tree's date is the latest Overleaf edit of any of its documents.
- **Window.** Roots whose tree changed in the last 183 days are read, oldest first,
  so the newest writing wins; if none did, the newest root is read. The same
  window decides which projects need an include/exclude choice
  (`ResearchCoordinator.RecentDays`, reported as `recentDays`).
- **Steps.** A tree's documents are sent in parts of at most 60,000 characters
  (split at sections, then paragraphs); each part is one Foundry step that edits the
  details with add/revise/remove operations. Each detail's sources are sent with
  their dates (and today's), so plans and statuses the new evidence overtook are
  revised or removed; removals apply first, so a full list always makes room for
  new results. The model names line ranges; the
  server copies the cited lines verbatim and drops citations, ids or additions it
  cannot ground. Every step commits atomically (details, folded hashes, changelog),
  so a restart continues with the next part.
- **Daily checks.** Each included project is read again once a day
  (`ResearchSweepHours`, default 24) or at once with **Check now**. Only roots whose
  tree changed are processed, and only their changed documents are sent; unchanged
  content makes **zero** model calls, also after a restart. Deleted documents lose
  their sources without a model call.
- **Review.** At most every 30 days, a card whose details changed is reviewed
  once (`/api/research/detail-review`, details and dates only, before the
  summary): overlapping details are merged and plans or statuses that newer
  details overtook are retired, keeping at least half. Reviews appear in the
  card's changelog, not in the feed (OVERLEAF.md §8a).
- **Project chooser.** Adding something to a project (Housekeeping groups, threads
  and removed files) and About me's **Move** and **Merge into** open one wide chooser:
  every research project with its title, summary, Overleaf projects and latest
  change, a search field, and the closest projects first (a group's suggestion leads,
  the next three are marked **Close match**). Choosing takes two steps: select a
  project, then confirm. Closeness comes from Foundry embeddings
  ([Ranking](apps/LocalBackend/Services/Research/Ranking.cs), `/api/research/embeddings`)
  of each card (title, summary, first details), Housekeeping group (thread titles and
  preview points), included Overleaf project (paper title, abstract, the details it
  supports) and removed file (path and text). Each is embedded once, and again only
  when that text changes, up to 20 per call; opening the chooser makes no call.
  Vectors are stored compactly (8-bit) on this computer only, so another computer
  embeds its own once; until they exist the chooser lists projects by latest change.
- **Cards.** A newly read project gets a card of its own; Foundry then decides with
  strict matching (the same paper or core question; a shared topic, data set or
  method is not enough) whether it belongs on an existing card, and combining two
  cards merges their details in one call. A card is shown by a **short name** (2–5
  words, e.g. "Publisher Content Marketplace (PCM)") that the summary call writes along
  with the summary, with the main paper's full `\title`
  ([Papers](libraries/Research/PaperContracts.cs)) as a smaller line beneath it;
  **Rename** (the pencil next to the name) sets the user's own name, which is never
  replaced. The full title follows the paper whenever its title changes; when the way
  papers are read improves (`Papers.Version`), stored papers are read again from their
  stored text, without a model call.
  **Make main paper** chooses another member. A card's **Projects** tab moves a project to another card
  (its sources leave the old card and its trees are read into the new one), merges
  the whole card into another, or skips it. Projects placed by hand are pinned.
- **Failures.** A failed step commits nothing and backs off (1 minute doubling to a
  day) without blocking other projects; **Check now** retries at once. Including (or
  resuming) a project reads it before older queued work. Pausing, excluding,
  forgetting or switching accounts discards an in-flight step.
- **Parallel processing** (as Project Meld's inference queue). Up to
  `ResearchParallelProjects` (default 3) projects are read at once; each
  project's parts stay strictly in order because every step builds on the details
  the previous one left. The remote service admits at most `Foundry:MaxConcurrency` (default 4)
  Foundry requests at a time and `Foundry:MaxConcurrencyPerAccount` (default 3) per
  account; further requests wait up to `Foundry:QueueSeconds` (default 30, at most
  60) instead of being refused. A Foundry 429 or 503 is retried up to three times
  after its `Retry-After` (or 2, 4 s with jitter), and a 429 pauses all requests for
  that time. All waiting inside the service stops after 45 seconds so a request stays
  within Cloudflare's 100-second origin limit; the service then answers "busy" and the
  local app tries that project again in 30 seconds without reporting an error.
- **Server problems.** The local app drops idle connections after 50 s and retries a
  dropped connection once. Connection, timeout, proxy and busy failures are retried
  with the usual backoff but shown as *Waiting to retry* (not an error) until a
  project has failed four times in a row, and the account banner appears only after
  five minutes without the server.
- **Skipped work.** Work that is not the user's own research (for example helping a
  family member) can be skipped: a card (**Skip — not my research** in its Projects
  tab) or an email thread (Skip in Housekeeping). Skipped cards move to the About me →
  Skipped tab (a small grey last tab, shown only while something is skipped; cards are
  collapsed, the most recent email first, and threads load 20 at a time), leave the
  feed, and their Overleaf projects pause. They are grouped
  generously and without confirmation; new threads that belong to a skipped project
  are skipped too. **Bring back** returns a card; **Unskip** returns a thread to
  Housekeeping.
- **Collaborators.** Each research project's **Collaborators** tab lists the people
  it is done with: who edits or owns its Overleaf projects, the authors named in its
  paper, and who is on its email threads (From, To, Cc), each once with their name
  and email addresses (either may be missing). The same person under different names
  or addresses is merged: by address and exact name locally, and by one Foundry call
  per project only when someone new appears (OVERLEAF.md §12a). Only authors, people
  who edited the project, and people whose email shows substantive work (judged by the
  model from what they wrote that Meld used) count; recipients, assistants receiving
  slides, owners who never edited, the user, mailing lists and role addresses do not.
  Each changelog change shows who it rests on (you, collaborators, others): the senders
  of the cited email, or who edited the cited files since Meld last read them.
- **Log.** More → Log shows the local app's own activity (checks, progress per file,
  grouping, updates, warnings and errors), newest first, with level and source
  filters, as in Project Meld. Entries name projects by Overleaf id (the page shows
  the name) and contain no research text or credentials; the newest 2,000 are kept in
  `logs/app-log.jsonl` under the runtime folder so they survive restarts. A badge
  counts warnings and errors of the last ten minutes.
- **One-time resets.** Raising `ResearchCoordinator.ResetEpoch` makes every
  installation start its research over once on the first start of that release.
  Epoch 2 introduced this format and epoch 3 repeats it once (with dated citations
  and reviews): projects' processing state, cards, details, changelog, recorded
  threads and stored texts are erased; the sign-in, the sync key, Gmail
  connections, include/exclude choices, skipped work and the OpenAlex match are
  kept, and included projects and mail are read again. Synced research uses format
  3 blob names (`profile3.bin`, `p3/`); older blobs are ignored and deleted, so
  deploy the server first (OVERLEAF.md §13).
- Source text is kept in an encrypted, content-addressed store next to the state
  (`research/texts`), so the state file stays small; unreferenced texts are pruned.
- UTF-8 `.tex`, `.md` and `.txt` files of up to 128 KiB are documents. Bibliographies,
  PDFs, images, larger or non-UTF-8 files are tracked by hash only. A project may
  hold up to 16 MiB of analyzed text and 2,000 files.
- The feed shows the latest 100 Updates; each card's full changelog is in About me.
- Not yet built (phase 2): the public profile and News suggestions from Updates.

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
pages (the signed-out home page, `/signup`, `/login`, and `/connect` before sign-in), whose content-security
policy allows `accounts.google.com/gsi`; the button's callback submits the token
through the site's own antiforgery-protected form. Signing in does not silently
register an account: signup takes place explicitly on the remote website.
App credentials last 30 days and can be revoked.

The home page is a single centered action. Signed out, it shows Google's button:
**Sign up with Google**, or **Sign in with Google** once this browser has shown the
install page to an account holder (`localStorage` key `meldresearch.account`, set by
the install card and read by [google-sign-in.js](apps/RemoteBackend/wwwroot/google-sign-in.js)).
Signed in, it shows **Install Meld Research**, which opens the install card at
`/account`. Account management (name, email, Install, Sign out) is the avatar menu
at the top right of the toolbar; static pages read it from `/api/session`.

**Signing in to the local app** uses the website, like signing in to a desktop app
with a browser (OAuth loopback redirect with PKCE). **Sign in with Google** in the
local app's Account page opens `/connect` with a random state, the SHA-256 of a
random verifier, and its own callback, `http://127.0.0.1:<port>/account/callback`.
If the browser already has a website session with a Meld account, `/connect`
immediately redirects back with a one-time code (valid five minutes). Otherwise it
shows Google's button, or one-click account creation for a new Google account.
The app checks the state and redeems the code with the verifier at
`/api/sign-in/redeem`, receiving a 30-day app credential. Redirects go only to
loopback callbacks, so a forwarded link cannot sign another computer in, and there
is no code to compare or approval step. No Google console changes are needed.

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
   Optional admission limits: `Foundry:MaxConcurrency` (default 4),
   `Foundry:MaxConcurrencyPerAccount` (3) and `Foundry:QueueSeconds` (120); keep them
   within the deployment's tokens-per-minute quota.
   See [Foundry setup](#foundry-setup-and-llm-smoke-test).
5. Set `OpenAlex:ApiKey` (`MELDRESEARCH_OpenAlex__ApiKey`, secret; server env file only)
   for researcher matching. Without it OpenAlex is called anonymously.
6. Set `PublicOrigin=https://www.meldresearch.org`, a durable private
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
# On the server, with the service's configuration and the VM's managed identity:
(set -a; . ~/meldresearch-data/remote.env; set +a; ASPNETCORE_ENVIRONMENT=Production \
  ~/meldresearch/deployed/current/MeldResearch.RemoteBackend --foundry-smoke-test \
  --expect-endpoint https://meldllm.openai.azure.com/openai/v1/)
```

Or deploy and run that server check in one step: `./deploy/deploy.sh -FoundrySmokeTest`.
The production template authenticates to `meldllm` with the VM's user-assigned
managed identity; that identity needs the role above on `meldllm` and network
access to it. `meldllm` is shared with another application, so both draw on the
same deployment capacity; give Meld Research its own deployments (same model
versions, its own tokens-per-minute limit) if rate limits ever collide.

### Network access to `meldllm` (private endpoint)

`meldllm` has public network access disabled; Project Meld reaches it only through
a private endpoint. A token is not network access: from a VM without a private path
every model call is rejected with HTTP 403 (the smoke test prints the status and
Foundry's error code). Keep public access disabled and give this VM the same kind of
path. Do not route production inference through Project Meld's gateways or model
relay; they authenticate Project Meld installations and would couple the two apps.

1. Create a private endpoint for `meldllm` in the VM's virtual network, in a small
   dedicated subnet (not the VM's own, not an existing endpoint subnet). The group
   ID is `account`. Approve the connection under `meldllm` → Networking if needed.
   ```bash
   az network vnet subnet create -g <vnet-rg> --vnet-name <vm-vnet> -n snet-meldllm-pe \
     --address-prefixes <free /28>
   az network private-endpoint create -g <vnet-rg> -n meldresearch-meldllm-pe \
     --vnet-name <vm-vnet> --subnet snet-meldllm-pe --group-id account \
     --connection-name meldresearch-meldllm \
     --private-connection-resource-id $(az cognitiveservices account show -g <ai-rg> -n meldllm --query id -o tsv)
   az network private-endpoint show -g <vnet-rg> -n meldresearch-meldllm-pe --query customDnsConfigs
   ```
2. Make `meldllm.openai.azure.com` resolve to that endpoint's IP on this VM only, keeping
   the hostname so TLS validation is unchanged: add one `/etc/hosts` line
   (`<endpoint IP> meldllm.openai.azure.com`). Do not link the shared
   `privatelink.openai.azure.com` zone to the VNet or let the endpoint create DNS records:
   that changes Azure OpenAI name resolution for every machine in the VNet and is shared
   with Project Meld's configuration.
3. Grant the VM's user-assigned identity **Cognitive Services OpenAI User** on
   `meldllm` (not on the resource group).
4. Verify on the VM: `getent hosts meldllm.openai.azure.com` shows the private IP;
   `curl -s -o /dev/null -w '%{http_code}\n' https://meldllm.openai.azure.com/openai/v1/models`
   returns 401 (reachable, no token) rather than 403; then run the smoke test above.

Offline Foundry checks (pinned deployments, `store: false`, managed-identity bearer
scope, no fallback, content-free smoke-test output) run with the normal C# tests.

The remote backend defaults to loopback port 8096 in Production, for a same-host
HTTPS reverse proxy. Configure `ListenAddress` and explicit `TrustedProxies`
when deploying elsewhere; never trust arbitrary forwarded headers. `/healthz`
is available for liveness checks. Non-development application routes require
HTTPS. Production on non-Windows platforms also requires
`DataProtection:CertificatePath` and its secret-stored password to protect the
persisted ASP.NET key ring. Do not log authorization headers, OAuth codes, or
bodies at the reverse proxy.

### Remote database

The remote backend stores accounts, app credentials, one-time app sign-in codes,
and the ASP.NET Data Protection key ring in a SQLite database,
`<DataDirectory>/remote.db` (WAL mode, owner-only file permissions), through
EF Core ([RemoteDbContext](apps/RemoteBackend/Data/RemoteDbContext.cs)). Pending
sign-ins and website sessions therefore survive restarts. Migrations in
[Data/Migrations](apps/RemoteBackend/Data/Migrations) are applied automatically
at startup. Names, email addresses, and device names are stored as Data
Protection ciphertext; app credentials and sign-in codes are stored only as
SHA-256 hashes. The Google subject is the plain lookup key. The Google profile picture
named in the ID token (https `*.googleusercontent.com` only, no redirects, at most
512 KB, JPEG/PNG/GIF/WebP checked by content) is copied at sign-in into
`account_pictures` as Data Protection ciphertext, fetched again only when its URL
changes and removed when Google sends none. The website and the local app (through
the installation token, cached in memory for an hour) show it from
`/api/account/picture`, so no page loads an image from Google.

The same database keeps one `inference_usage` row per Foundry request (time,
deployment, chat/embedding, purpose, HTTP status, token counts, duration; no
content and no account), pruned after 400 days. Superusers (config `Superusers`,
comma-separated verified Google emails, default `markusmobius@gmail.com`; the
synthetic account in Dummy mode) see it at `/usage` (LLM usage dashboard, ported
from Project Meld; also in the account menu) and `/api/usage`. Everyone else
gets 404. The Foundry smoke test does not record usage.

SQLite supports one remote process per data directory (`server.lock` enforces this),
not multiple load-balanced instances. Research text is never stored remotely in
readable form (see [Encrypted sync](#encrypted-sync)).
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

### Encrypted sync

Encrypted sync lets a user's computers share one profile: a second computer pulls
the first one's work and processes only what changed since. Everything is
encrypted on the client ([SyncCrypto](libraries/Research/SyncCrypto.cs)):

- A random 256-bit data key per account encrypts each blob with AES-256-GCM
  (gzip JSON, bound to the account and blob name). The account's plaintext
  `key.json` holds the data key wrapped by a PBKDF2-SHA256 (600,000 iterations)
  passphrase key and by a 20-byte recovery key
  (`XXXX-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX`), plus the recovery key sealed under
  the data key so any unlocked computer can show it again
  (`POST /api/research/encrypted-sync/recovery-key`; a key file without that copy
  gets a new recovery key, which replaces the old one). On computers, the data
  key lives only inside the DPAPI/Keychain-protected research state.
- Blobs: `{account:N}/key.json`, `profile.bin` (decisions, research cards,
  summaries, OpenAlex match) and `p/<hmac>.bin` per included project (summary,
  processing state, Updates and the texts they need). Project names are a keyed
  HMAC, so storage does not reveal project ids.
- [ResearchSync](apps/LocalBackend/Services/Research/ResearchSync.cs) pulls
  before work (every 5 minutes or on a forced check), pushes changed projects at
  the end of a cycle (long imports every 10 minutes as checkpoints) and deletes
  blobs of excluded projects. Uploads are conditional on the ETag; a computer
  that lost a race pulls and reconciles (more folded-in files wins). Check-time
  bookkeeping does not cause uploads. A changed `key.json` creation time (start
  over elsewhere) locks the computer until the new passphrase is entered.
- Forgotten passphrase and recovery key: **Start over** deletes the account's
  blobs, creates a new key and uploads this computer's research again.
- A new computer (or a wiped one) finds the synced research by signing in with
  the same Google account: the account id names the folder. New accounts get an
  id derived from the Google subject (`AccountService.AccountId`, a version-8
  UUID), so even a server rebuilt with an empty `remote.db` recreates the same
  id and folder. Accounts created before this keep their random id; for them,
  back up `~/meldresearch-data/remote.db` (good practice for all accounts and
  devices anyway), otherwise a lost database orphans their synced copy.

The remote backend ([SyncStorage](apps/RemoteBackend/Services/SyncStorage.cs))
never sees plaintext. With a bearer installation token it lists the account's
blobs (`GET /api/sync/blobs`), issues 10-minute per-blob SAS links
(`POST /api/sync/links`, user delegation key, names validated, quota
`Storage:QuotaMegabytes`, default 500) and deletes everything for start over
(`DELETE /api/sync/blobs`). Clients talk to Blob Storage directly with the links.
Dummy mode uses an on-disk stand-in with signed links under `/dev-sync`. Without
`Storage:AccountUrl` sync is off and the app behaves as before.

Production uses the VM's managed identity on the `meldresearch` container of
storage account `ratiodatanew` (settings in
[remote.env.example](deploy/remote.env.example)). That identity needs
**Storage Blob Data Contributor** on the container (or account) and
**Storage Blob Delegator** on the storage account (to create user delegation
keys); the account must accept public network access (the companion connects
from users' computers). No CORS is needed: the local backend, not the browser,
calls Blob Storage.

### Gmail (optional, read-only)

Account → Gmail (shown when Meld's server offers it) connects one or more Gmail
accounts with the read-only `gmail.readonly` permission
([GmailConnector](apps/LocalBackend/Services/Research/GmailConnector.cs),
[GmailGoogle](apps/LocalBackend/Services/Research/GmailGoogle.cs)). The local app
opens Google's consent page, which returns to `http://127.0.0.1:<port>/gmail/callback`;
the app checks its one-time state and redeems the code with its PKCE verifier.
Meld's server only supplies the Desktop OAuth client to signed-in installations
(`GET /api/gmail/client`, settings `Gmail:ClientId` and `Gmail:ClientSecret` from a
separate Google Cloud project with the Gmail API enabled, in Testing with the team
as test users). Google's refresh token is kept in the encrypted research state and
the end-to-end encrypted sync, never in backups, logs or on the server; removing an
account revokes it at Google.

[GmailImporter](apps/LocalBackend/Services/Research/GmailImporter.cs) runs inside the
research worker, a few threads per cycle. About hourly (an hour after the last
import, offset by up to ±10 minutes per account) it lists the threads the user
wrote in during the last 30 days (`in:sent newer_than:30d -in:chats`) and reads
only threads whose Gmail `historyId` changed, oldest first. Messages are cleaned
locally ([EmailText](apps/LocalBackend/Services/Research/EmailText.cs): plain text
preferred, quoted replies, forwards and signatures cut, 8,000 characters); bulk
mail counts only if the user wrote it. Each thread is one `GmailThreadRecord`.
A thread seen for the first time goes to `POST /api/research/email-step` with the
user's research cards as compact candidates (title, summary, first details), and
Foundry answers **skip** (not work), **project** (a card id) or **unassigned**
(with a suggested name and short cited preview bullets,
[EmailSteps](libraries/Research/EmailContracts.cs)).

An assigned thread is a **source** of its card: its new messages (with up to four
earlier ones as context) are one detail step (`POST /api/research/detail-step`,
kind `gmail`) that adds, revises or removes the card's details, cites the
messages' lines verbatim and adds one changelog entry; only messages after the
last one folded are sent ([OVERLEAF.md](OVERLEAF.md) §12). Unassigned threads wait
in the feed's **Housekeeping** column, where Meld never starts a project itself;
removing a thread as a source of a card sends it back there.

**Housekeeping** ([Housekeeping](apps/LocalBackend/Services/Research/Housekeeping.cs))
groups the waiting threads loosely (the same topic, people, event or kind of work),
so the user decides on a whole group at once: **Create project** (one new project
from all its threads, named after the group), **Add to project**, or **Skip all**
(one skipped project). Each newly waiting thread is matched once against the groups
(only the threads' titles and preview points are sent, not the emails); after
groups gain threads, one regrouping pass combines related groups. Waiting threads
get a punchy 3–7 word headline (`/api/research/thread-titles`, up to 12 threads per
call, again only when new mail arrives) instead of a vague subject. A group shows its
threads' preview points and has no details or summary until the user keeps one.
Whenever a group's threads change, its name falls back at once (no call) to the name
the first check suggested for its oldest thread; then one call per changed group
(`/api/research/housekeeping-group`) writes a 2–5 word name from all its threads and
compares the group strictly with the research projects, offering a match as **Looks
like …, Add to it**. A name the user gave a group is never replaced. Per thread, **Move to group**
or **New group** splits a group (no model call); moved threads stay pinned, and
groups the user made, renamed or arranged are never combined automatically. Removed
Overleaf files are listed by the project they came from. Each thread commits
atomically, so an unchanged mailbox makes no model call, also after a restart. With encrypted sync the
threads and `LastImportAt` merge across computers, and a lease
(`LeaseBy`/`LeaseUntil`, 15 minutes, renewed per thread) is pushed before any
mail is read, so other computers wait instead of analyzing mail twice. Account →
Gmail shows progress and offers Import now. State from earlier analyses
(workstreams, email streams) is replaced once and the mail imported again. Dummy mode uses a synthetic mailbox (a thread about the
synthetic Overleaf project, a paper and a grant that fit no project, a lunch) and
needs no Google setup, but analysis uses real Foundry.

### Backup, restore and leaving

Account → Backup and Account → Meld account → Leave Meld Research
([ResearchArchive](apps/LocalBackend/Services/Research/ResearchArchive.cs),
[LeaveService](apps/LocalBackend/Services/Research/LeaveService.cs)):

- **Download my data** queues a download (`POST /api/research/export`; one at a
  time, asking again while one is prepared returns it). The page polls
  `GET /api/research/export`, keeps the button disabled while it is prepared,
  starts the download itself when ready and shows a link
  (`GET /api/research/export/{id}`) that works for five minutes, also after a
  reload. The ZIP is held only in the app's memory, never written to disk by
  Meld. It is a ZIP of indented, unencrypted JSON: `meld-research-export.json` (format, time, account),
  `profile.json` (choices, research cards, summaries, OpenAlex author and works),
  `projects/<id>.json` (summary, Updates, processing state and the source texts
  by SHA-256) and a README. These are the same records encrypted sync uses.
- **Upload data** (`POST /api/research/import`, at most 256 MB) locks the whole
  interface (a modal overlay; the page behind it is inert and the browser asks
  before leaving) until it finished or failed, then shows the outcome. It checks the format
  and every text hash, rebinds the records to the signed-in account (so an export
  from a deleted account restores into a new one), merges choices by newest
  decision, keeps local work that folded in more files, and marks the rest
  unsynced so encrypted sync uploads it. Imported work is never processed again.
- **Leave Meld Research** (`POST /api/research/leave`, body `{"confirmation":
  "DELETE"}`) calls the remote `DELETE /api/account` (synced blobs, then the
  account with its devices, codes and picture), erases the local research store,
  texts and downloads, and for an installed copy unregisters the browser bridge
  and starts a removal script outside its process tree (WMI on Windows, `nohup`
  on macOS) before exiting. The script waits for the app to stop, removes the
  `MeldResearch` scheduled task / `org.meldresearch.desktop` LaunchAgent, the Start
  menu entry, this installation's processes, the data and app folders (and the
  macOS Keychain entry), and logs to `%TEMP%\meldresearch-uninstall.log`
  (`$TMPDIR` on macOS). It refuses drive roots, home and other broad folders.
  Development and Dummy copies only erase their data. The macOS path is not yet
  execution-tested; the browser connector is removed by the user.

## Deploy the remote backend to Ubuntu

[deploy/](deploy) runs the blue remote service on an Ubuntu server as
`https://www.meldresearch.org`. systemd runs it as **`markusmobius`** on
`127.0.0.1:8096`, behind your own reverse proxy and TLS certificate (not set up
here). Everything lives in that user's home folder, and one command builds and
deploys from the repository clone.

| File | Purpose |
| --- | --- |
| [deploy.sh](deploy/deploy.sh) | The command you run. Installs missing prerequisites (curl, sqlite3, ICU, PowerShell 7; asks first, `--yes` skips the prompt, `--prerequisites` stops after this), then runs `deploy.ps1` with the same arguments. |
| [deploy.ps1](deploy/deploy.ps1) | The deploy itself: builds and signs the desktop packages (only when the client changed; cross-built for Windows/macOS), builds the remote backend, creates `~/meldresearch-data` on the first run, copies the build to `deployed/releases/…`, points `deployed/current` at it, restarts the service (`sudo systemctl`), health-checks it (rollback on failure), then publishes desktop updates. `-SkipBuild` redeploys the last build; `-BuildOnly` only builds. |
| [meldresearch-remote.service](deploy/meldresearch-remote.service) | systemd unit: runs `~/meldresearch/deployed/current` as `markusmobius`; writable only `~/meldresearch-data`. |
| [remote.env.example](deploy/remote.env.example) | Becomes `~/meldresearch-data/remote.env` on the first deploy: the (non-secret) Google client ID and Foundry values. |

### Where things live on the server

```text
~/meldresearch/                       the git clone: source code
├── artifacts/                        build output (git-ignored)
│   ├── remote/publish-linux-x64/     the compiled remote backend
│   └── desktop/                      desktop ZIPs + signed update manifests
└── deployed/                         the running code (git-ignored)
    ├── releases/<timestamp>.<id>/    one copy per deploy (latest five kept)
    └── current -> releases/...       systemd runs current/MeldResearch.RemoteBackend
~/meldresearch-data/                  remote.env, dataprotection.pfx/.env,
                                      remote/remote.db, downloads/
~/.config/meldresearch/               the encrypted update signing key
```

Each deploy copies the build into a new `deployed/releases` folder and switches
`current` in one step, so a build in progress never runs and rollback is just
repointing `current` (automatic if the health check fails). `deployed/` and
`artifacts/` are in `.gitignore`; avoid `git clean -x`, which would delete them.

### Server setup (once)

You need: `markusmobius` with `sudo`; the repository cloned at `~/meldresearch`
(the unit assumes both; edit it otherwise); git, Node 24 with npm, and the .NET 10
SDK (from one apt source only); and the VM's managed identity holding **Cognitive
Services OpenAI User** on the Foundry resource.

```bash
cd ~/meldresearch
bash deploy/deploy.sh --prerequisites        # curl, sqlite3, ICU, PowerShell 7 (asks first)
sudo install -m 0644 deploy/meldresearch-remote.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable meldresearch-remote
pwsh tools/update-signing-key.ps1 -Create    # update signing key; choose a passphrase (see below)
git add apps/LocalBackend/update-signing-public-key.txt && git commit -m "Update signing key" && git push
bash deploy/deploy.sh -FoundrySmokeTest      # first release; also creates ~/meldresearch-data
```

The first deploy creates `~/meldresearch-data/remote.env` from
[remote.env.example](deploy/remote.env.example) and a data-protection certificate
(`dataprotection.pfx` + `.env`, which protect the key ring in the database). It
never replaces them later.

**Your reverse proxy** serves `https://www.meldresearch.org` and forwards to
`http://127.0.0.1:8096`. It must pass the original `Host` header, set
`X-Forwarded-Proto: https` and `X-Forwarded-For` to the client address (overwrite,
don't append), allow request bodies up to 3 MiB and responses up to 5 minutes
(research analysis), and should not log query strings (sign-in and device codes).
[deploy/nginx/meldresearch.conf](deploy/nginx/meldresearch.conf) is a working
nginx example. Then check `curl https://www.meldresearch.org/healthz` and sign up
with Google. If the Google button reports that the origin is not allowed, add
`https://www.meldresearch.org` to the OAuth client's Authorized JavaScript origins.

### Update signing key

Desktop update manifests are signed with an ECDSA P-256 key whose public half is
[committed](apps/LocalBackend/update-signing-public-key.txt) and stamped into every
package. The private key is a **passphrase-encrypted PEM** at
`~/.config/meldresearch/update-signing-key.pem` (mode 0600), outside the clone.
The passphrase is typed only when a desktop release is signed and is never stored,
so the key file alone cannot sign anything. Because the service runs as the same
user, a compromise of the web service could read the encrypted file, but not use
it without the passphrase.

```bash
pwsh tools/update-signing-key.ps1 -Create                 # new key; then commit the public key file
pwsh tools/update-signing-key.ps1                         # verify key + passphrase
pwsh tools/update-signing-key.ps1 -Install <file.pem>     # move an existing encrypted key to this server
```

`-Create` writes the public key to `apps/LocalBackend/update-signing-public-key.txt`.
Commit it before deploying: it is part of the desktop client, so the deploy refuses
to build while it is uncommitted. Keep one offline copy of the encrypted PEM and its
passphrase. Without the key, installed clients can no longer be updated
automatically; a new key is trusted only by installs made after it. Never commit
the private key.

### Releases

```bash
cd ~/meldresearch && git pull --ff-only
./deploy/deploy.sh                         # add -FoundrySmokeTest to verify Foundry afterwards
./deploy/deploy.sh -SkipBuild              # redeploy the last build without rebuilding
./deploy/deploy.sh -BuildOnly              # build into artifacts/ without deploying
```

`deploy.sh` only checks prerequisites and then runs `deploy.ps1`, which does the
whole build and deploy (on Windows it can build with `-BuildOnly`; deploying needs
the Ubuntu server). If `./deploy/deploy.sh` reports "Permission denied", run
`bash deploy/deploy.sh`, or record the executable bit once with
`git update-index --chmod=+x deploy/deploy.sh` and commit.

- The **remote backend** is rebuilt and deployed every time. Releases live in
  `~/meldresearch/deployed/releases` (latest five kept); a release failing
  `http://127.0.0.1:8096/healthz` is rolled back and publishes no desktop update.
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
  `/api/updates/{runtime}/package/{version}` from `~/meldresearch-data/downloads`.
  The one-line installers at `/api/install/windows` and `/api/install/macos`
  (shown on the account page) install that same signed release. The ZIPs also
  remain account-gated downloads at `/downloads/{runtime}`, no longer linked.

**Back up:** `~/meldresearch-data` (database, `dataprotection.pfx` and
`dataprotection.env` together), and separately the encrypted update signing key
with its passphrase. Take a consistent online copy of the database with
`sqlite3 ~/meldresearch-data/remote/remote.db ".backup '$HOME/remote-backup.db'"`
(copying `remote.db` alone while the service runs can miss WAL data). Never
regenerate the certificate while `remote.db` exists: the key ring, sessions and
encrypted account columns would become unreadable. Logs: `journalctl -u meldresearch-remote`.

## Desktop and extension packaging

```powershell
.\tools\package-local.ps1 -Runtime win-x64 -AllowUncommitted   # local test build; never self-updates
.\browser\prepare-chrome.ps1
```

Desktop ZIPs in `artifacts\desktop` are self-contained **test distributions**
(not signed MSI/PKG installers). They include per-user installation/start scripts,
native-host registration, the Angular UI, and a development connector folder, and
need no separate .NET installation.

**Installing.** After signing up, the website's account page shows an install
card: Windows/macOS, *Automatic* (a one-liner) or *Manual* (download the script):

```text
Windows (PowerShell):  irm -useb https://www.meldresearch.org/api/install/windows | iex
macOS (Terminal):      curl -fsSL https://www.meldresearch.org/api/install/macos | bash
```

The script is served by [RemoteEndpoints](apps/RemoteBackend/Endpoints/RemoteEndpoints.cs)
from [apps/RemoteBackend/Install](apps/RemoteBackend/Install) with the server's
address filled in. It downloads the published release from the update endpoints,
checks its length and SHA-256 against the manifest, and runs the package's
`Install-Meld.ps1` / `Install-Meld.command`. No administrator rights are needed.
That installer:

1. stops this installation's running launcher and app (never other programs; an
   occupied port 6001 is reported),
2. installs the release side by side in `MeldResearch\App\<first 12 characters of
   the release>` (Windows `%LOCALAPPDATA%`, macOS `~/Library/Application Support`),
   or reuses it if already installed, and registers the native browser host,
3. points `App\current.json` and the stable `Start-Meld.ps1` / `Start-Meld.command`
   launcher at it (re-running keeps the previous version as the rollback target),
4. registers a start-at-sign-in entry: the per-user scheduled task `MeldResearch`
   (hidden through `conhost --headless`; log `App\launcher.log`) or the LaunchAgent
   `org.meldresearch.desktop` (log `~/Library/Logs/MeldResearch/launcher.log`),
5. starts it in the background, waits for `/api/version`, and opens the local app.

Re-running the command repairs or updates an installation and keeps research data.
On Windows the Start menu entry **Meld Research** opens the app, starting it first
if needed. To stop autostart: `Unregister-ScheduledTask MeldResearch` or
`launchctl bootout gui/$(id -u)/org.meldresearch.desktop` and delete
`~/Library/LaunchAgents/org.meldresearch.desktop.plist`. Isolated tests set
`MELDRESEARCH_Install__Autostart=false`, which runs the launcher in the terminal instead.

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
Windows DPAPI- or macOS Keychain-protected key. The optional synced copy is
end-to-end encrypted under the user's passphrase (see [Encrypted sync](#encrypted-sync)). Separate linked accounts do not
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

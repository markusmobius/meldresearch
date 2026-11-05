# Meld Research: agent guide

This is the single repository-wide agent instruction file. Keep it current as
the application changes; do not add a singular `AGENT.md` or a duplicate global
instruction file. Start with the [README](README.md) for setup and release details.

## Product and scope

- Meld Research is an independent project. The Project Team page lists Markus
  Mobius (developer), Tanya Rosenblat (Professor of Information and Economics,
  University of Michigan), Lukas Bolte (Assistant Professor, Carnegie Mellon
  University) and Sid Satya (PhD Candidate, Brown University); Markus Mobius is
  the developer and public contact (`markusmobius@gmail.com`).
- Keep Angular and C# with distinct local and remote backends. The UI must use
  **Angular Material and the actual Project Meld interface**, not just its colors.
  Preserve the ported 64px toolbar, share icon, Outfit typography, pill navigation,
  full-height scrolling workspace and compact Update cards. Do not replace them
  with a new hero/marketing dashboard or import unrelated Meld functionality.
- Private-data sources: Overleaf (required) and, optionally, the user's Gmail
  accounts (read-only `gmail.readonly`, connected by the user under Account →
  Gmail; [GmailConnector](apps/LocalBackend/Services/Research/GmailConnector.cs)).
  The only other source is the user's public OpenAlex record (ported from
  Project Meld): searched only on request, candidates confirmed by the user, works
  stored in the local encrypted state. The feed contains research **Updates
  only** (News from email is a later phase). Do not add WorkIQ, other news
  sources, TRAPI, or alternate inference fallbacks.
- Gmail access: Google's consent page returns to the local app's loopback
  `/gmail/callback` (state + PKCE S256, single use, 10 minutes); the app redeems
  the code itself. Meld's server only hands out the Desktop OAuth client
  (`GET /api/gmail/client`, signed-in installations; `Gmail:ClientId/ClientSecret`).
  Refresh tokens live only in the encrypted local state and the E2E-encrypted
  sync (`GmailAccountState`: connection fields from the newest `ChangedAt`,
  analyzed threads unioned, latest `LastImportAt`, synced lease wins; removals
  are tombstones) — never in backups, logs or on the server. Removing an account
  revokes it at Google; leaving revokes all.
- Gmail import ([GmailImporter](apps/LocalBackend/Services/Research/GmailImporter.cs),
  called from the coordinator's cycle): only threads whose `historyId` changed
  are read, and a new thread is placed once (`/api/research/email-step`): on a
  research card, skipped, or left for the user in Housekeeping. An assigned
  thread is a source of its card: only its messages after `FoldedMessageId` go to
  a `gmail` detail step (verbatim line citations). Never create a project without
  the user's choice. Unchanged mail must make zero model calls. Commit each thread
  atomically; with sync, push the lease before reading mail and stop if another
  computer holds it.
- All model decisions and embeddings use configured Azure AI Foundry.
  Deterministic source hashing is appropriate; canned model responses or local
  novelty heuristics are not substitutes for real inference.
- The original Project Meld repository is an optional read-only reference, not
  a source or production runtime dependency. Work in this repository.

## Architecture and invariants

| Area | Responsibility |
| --- | --- |
| [Angular frontend](apps/frontend) | Local UI, account/connector setup, consent, monitoring and cited Updates. |
| [Local backend](apps/LocalBackend) | Serves the built UI; owns the browser bridge, snapshots and encrypted research state. |
| [Remote backend](apps/RemoteBackend) | Google accounts, app sign-in (loopback + PKCE), one-line desktop installers and authenticated Foundry requests. |
| [Research library](libraries/Research) | Shared contracts, evidence validation, content revisions and state reduction. |
| [Overleaf extension](browser/overleaf) / [native host](apps/BrowserHost) | Read-only access through the user's normal signed-in Chrome/Edge session. |

- Research card members are Overleaf projects; email threads attach to cards
  directly (`GmailThreadRecord.CardId`). The older `gmail-` workstream and
  email-stream members are legacy: only read so old state loads, never created.
  New optional fields are omitted from JSON when empty so sync fingerprints stay
  unchanged. Gmail plan and decisions: card-first placement, Housekeeping for
  the rest (loosely grouped, [Housekeeping](apps/LocalBackend/Services/Research/Housekeeping.cs):
  one match per newly waiting thread, a regroup pass only when groups gained threads,
  one headline + project-suggestion call per changed group (suggestions only, never
  automatic; a provisional name from the oldest thread meanwhile; user names kept),
  batched 3–7 word thread titles; cards show a 2–5 word short name written by the
  summary call (`ChosenName` from the user wins, the full paper title stays beneath); user-made, renamed or arranged groups
  are never regrouped; the project chooser is ordered by local, unsynced Foundry
  embeddings made once per changed card/group/project/file, never at chooser open),
  threads the user wrote in, 30-day first import, Overleaf stays
  required, hourly jittered imports coordinated by a synced lease, Google
  testing mode first. The feed is Project Meld's column layout with Updates and
  Housekeeping only (no News/Share/Help/Ask).
- Start processing work in [OVERLEAF.md](OVERLEAF.md) (the specification),
  [ResearchCoordinator](apps/LocalBackend/Services/Research/ResearchCoordinator.cs),
  [DetailPlan](apps/LocalBackend/Services/Research/DetailPlan.cs),
  [LatexTree](libraries/Research/LatexTree.cs) and
  [FoundryService](apps/RemoteBackend/Services/FoundryService.cs).
- Account linking, explicit processing consent and project selection are required
  before monitoring. Preserve account isolation and server-side authorization.
- Compare supported source content, not ZIP order/timestamps. Unchanged supported
  content must make **zero inference calls**, including after a restart.
- A research card (About me) is a) a two-sentence summary, b) at most 30
  one-sentence details, each with sources (an Overleaf document at a hash, a Gmail
  thread up to a message), and c) a changelog of the sources that updated it
  (OVERLEAF.md §1). Overleaf is read as include trees: roots whose tree changed
  within the one 183-day window (`ResearchCoordinator.RecentDays`, also the
  include/exclude window) are folded oldest first, in parts of at most 60,000
  characters, one detail step per part; a) is rewritten only at the end of a cycle
  that changed b). Later cycles send only changed documents; deleted documents lose
  their sources without a model call. `PendingTrees`, `Partial` and `Processed` are
  the persistent queue: commit per part, resume after restart, never resend an
  unchanged hash. Steps see each source's date; edits apply removals first so a full
  list takes new details; a review (OVERLEAF.md §8a, at most monthly per changed card, before
  the summary) merges overlaps and retires overtaken plans. The feed's Updates are
  changelog entries that changed details, except reviews. Raising `ResetEpoch`
  that must also discard synced research needs a new sync blob format in
  [SyncCrypto](libraries/Research/SyncCrypto.cs) (old names become legacy).
  A source the user removes leaves every detail of the card and waits in Private
  updates (`PendingFile` or a pending thread). Include/resume is processed first.
  Project summaries (one paragraph from the main
  `.tex`) are sent only on the user's per-project Summarize click, recent projects
  only, and never resent while the main file's hash is unchanged. Research cards
  ([ThemePlan](apps/LocalBackend/Services/Research/ThemePlan.cs)) group settled
  projects by Foundry decision (strictly "same paper or same core question"),
  regroup all cards once per card-set change when imports are idle, and never move a
  pinned (hand-placed) project. A card's title is its anchor's (main paper:
  `\title` read locally by [Papers](libraries/Research/PaperContracts.cs), or the
  user's choice) and the abstract is context for every step. Combining cards merges
  their details in one call (`/detail-merge`); moving a project strips its sources
  from the old card and reads its trees into the new one.
  Skipped cards (not the user's own research) live in About me → Skipped: out of the
  feed, Overleaf members paused, grouped loosely without confirmation, new email of
  them skipped too; Bring back / Unskip undo it. Keep them out of normal matching
  and regrouping (`ThemePlan.Active`).
  Collaborators (OVERLEAF.md §12a, [CollaboratorPlan](apps/LocalBackend/Services/Research/CollaboratorPlan.cs)):
  people from Overleaf editors/owner (connector history and project list), paper
  `\author` blocks and the From/To/Cc of a card's threads, the user left out; known
  people merge locally by address or exact name, anything new costs one
  `/api/research/collaborators` call per card; unchanged people make zero calls.
  The model only groups mention ids, picks names as written and judges email-only
  people's involvement from quotes Meld already used; never let it add addresses or
  invented names. Collaborators are authors, editors and substantively involved email
  people only. Changelog changes record who they rest on (`DetailChange.By`) locally.
  Keep every remote request under Cloudflare's 100 s origin limit (45 s wait budget).
- More → Log ([AppLog](apps/LocalBackend/Services/AppLog.cs)) records only Meld's own
  log categories. Keep log messages free of research text, project names and
  credentials; tag project work with a ProjectId scope instead.
- Models select source line ranges; the server copies and validates actual
  citation text. Preserve grounding, evidence limits and visible validation errors.
- The remote backend persists accounts, devices, one-time app sign-in codes, the
  Data Protection key ring, the encrypted Google profile picture and content-free,
  account-free LLM usage rows (shown
  only to superusers at `/usage`) in SQLite via EF Core
  ([RemoteDbContext](apps/RemoteBackend/Data/RemoteDbContext.cs)). Change the
  schema only through checked-in migrations (`dotnet tool restore`, then
  `dotnet ef migrations add`). Keep names, emails and device names as Data
  Protection ciphertext and credentials/codes as hashes. Make
  state transitions conditional updates or transactions, not read-then-write.
  Research text is never stored remotely in readable form: encrypted sync
  ([ResearchSync](apps/LocalBackend/Services/Research/ResearchSync.cs),
  [SyncCrypto](libraries/Research/SyncCrypto.cs)) encrypts on the client with a
  passphrase-wrapped data key and uploads through short-lived per-blob SAS links
  ([SyncStorage](apps/RemoteBackend/Services/SyncStorage.cs)); the server only
  lists, links and deletes ciphertext under the account GUID. Never send the
  passphrase, recovery key or data key to the server, never upload under a key the
  account's `key.json` no longer matches, and keep uploads ETag-conditional.
  With sync offered, nothing is processed until this computer has the key; pulled
  work must not be processed again. New account ids are derived from the sign-in
  subject (`AccountService.AccountId`) so a rebuilt server finds the same sync
  folder; never change that derivation or existing accounts' ids (the id is bound
  into the encryption).
- The local app signs in through the website with a loopback redirect + PKCE
  ([LocalSignIn](apps/RemoteBackend/Services/LocalSignIn.cs)): `/connect` returns
  a one-time code only to `http://127.0.0.1|localhost:<port>/account/callback`,
  the app checks its in-memory state, and redeems the code with its verifier at
  `/api/sign-in/redeem`. Keep the redirect loopback-only, codes single-use and
  short-lived, and the verifier on the local machine; do not reintroduce an
  approval code step or accept other redirect targets.
- Commit each detail step (details, folded hashes, changelog) atomically. Failed
  processing must not advance the stored revision or folded hashes. Pause, deletion, cancellation and account changes
  must not allow an in-flight result to restore removed data.
- Keep the existing minimal API/service patterns, antiforgery and strict
  Host/Origin checks. The remote service deliberately uses
  `Referrer-Policy: same-origin` so normal browser form submissions retain a
  usable Origin; do not casually replace it with `no-referrer`.
- Remote sign-in is **Sign in with Google** with server-verified ID tokens
  ([GoogleIdTokenVerifier](apps/RemoteBackend/Services/GoogleIdTokenVerifier.cs)):
  Google signature, issuer, client-ID audience, expiry, verified email and a
  per-page nonce cookie. No client secret. Google's script and the relaxed CSP /
  `strict-origin-when-cross-origin` referrer apply only to the sign-in pages; the
  token is posted through the site's own antiforgery-protected form. Keep it that way.

## Running locally

Use .NET 10, Node 24/npm and PowerShell 7. Run from this repository's root, e.g.
`C:\github\meldresearch` on the current Windows development machine.

```powershell
.\run-dev.ps1 -Mode Dummy
.\run-dev.ps1 -Mode Dummy -SkipBuild
```

| Service | Development | Installed / intended production |
| --- | --- | --- |
| Green local UI and API | `http://127.0.0.1:6010` | `http://127.0.0.1:6001` |
| Blue remote website/API | `http://127.0.0.1:6050` | `https://www.meldresearch.org` |

- Both development backends run on the same computer. "Remote" is the service's
  role, not evidence that anything is publicly hosted. No VPN is required.
- Port 6000 is browser-restricted: use **6001 installed**, **6010 development**.
- Dummy uses a synthetic account and Overleaf source, but real configured Foundry
  inference that consumes quota. It does not access a real browser profile or
  register the extension. Preserve synthetic source revisions across restarts.
- Live uses real Overleaf and account linking; see
  [Live setup](README.md#real-overleaf-and-the-chrome-identity) before running it.
- Use the full launcher, not a separate frontend preview or browser-injected
  fixtures. Angular is served from build output, **not hot-reloaded**.
  `-SkipBuild` skips frontend and backend builds; omit it after source changes.
- Run one mode at a time. Keep the launcher alive; Ctrl+C stops its owned
  processes. Report occupied ports instead of stopping unrelated/installed apps.
- Preserve existing data and downloads. Dummy data/logs live under ignored
  `.local-test\Dummy`; installed/Live storage retains the `OverleafPrototype`
  directory name for compatibility. See [storage details](README.md#storage-boundaries-and-remaining-release-gates).

## Branding, Docs and Privacy

- Local is green (`#084d42` to `#0a6655`); remote is blue (`#1e3c72` to `#2a5298`).
  [MeldToolbar](apps/frontend/src/app/meld-toolbar.ts) is the shared Angular
  Material toolbar, including visible service roles. The local tabs are Feed,
  About me and Account; Docs / Privacy / Project Team are under More.
  Account has vertical steps Meld account / Remote Sync (only when offered;
  required; once set up its only action is Show recovery key) / Backup (optional
  restore during setup, then download and restore) / Overleaf (horizontal
  Connection | Projects) / OpenAlex / Gmail (optional, last; only when offered).
  During setup later steps stay greyed out
  until the previous step is finished. Projects owns include/exclude choices, consent,
  monitoring and project summaries (no manual ZIP download UI); About me has Project Meld's tabs: Private profile
  (research cards), Public profile (only once one exists) and Publications, then a quieter
  Skipped tab only while something is skipped. Cards (and a skipped card's threads) show
  20 at a time and load more while scrolling.
  Backup: Download my data (a ZIP of readable, unencrypted JSON built from the
  sync bundles) and Upload data (merge, then sync uploads). Meld account holds
  Leave Meld Research (typed DELETE; deletes the remote
  account and synced blobs first, then erases local research, then an installed
  copy uninstalls itself through a script started outside its process tree;
  [LeaveService](apps/LocalBackend/Services/Research/LeaveService.cs)). Keep the
  uninstall in step with the installers and never let it remove a broad folder.
- Projects updated within six months need an active include/exclude choice;
  older ones are optional. The feed unlocks once one project is included. Each
  page load, and each newly seen undecided project, opens Overleaf → Projects;
  then OpenAlex is offered once per load until confirmed or declined. Neither
  nudge blocks the feed.
- Do not restore Microsoft corporate footers, internal-service or research-study
  claims. Retain accurate Azure/Microsoft provider disclosures and required
  [third-party notices](THIRD-PARTY-NOTICES.md).
- Shared website content/layout lives in [frontend public assets](apps/frontend/public):
  [Docs](apps/frontend/public/docs.html), [Team](apps/frontend/public/team.html),
  and [layout CSS](apps/frontend/public/info.css).
- Keep the [local green theme](apps/frontend/public/theme.css) separate from the
  [remote blue theme](apps/RemoteBackend/wwwroot/theme.css).
  The [remote project](apps/RemoteBackend/MeldResearch.RemoteBackend.csproj)
  copies the built Angular/public assets into build/publish output; do not
  overwrite its blue theme. Build Angular before the C# solution.
- Canonical privacy content is [extension privacy](browser/overleaf/privacy.html),
  with [connector CSS](browser/overleaf/popup.css). The
  [website wrapper](apps/frontend/public/popup.css) uses website-only styling;
  do not import popup-wide button styles into Material navigation.
  `.site-only` elements remain hidden inside the extension.
- [Asset synchronization](apps/frontend/sync-site-assets.mjs) runs through npm
  `prebuild`/`pretest`. Do not edit generated privacy/connector/icon copies under
  frontend public assets or configure Angular to copy outside its workspace.
  Use npm scripts rather than bypassing these hooks with direct `ng` commands.
- [Public-page finalization](apps/frontend/finalize-site-assets.mjs) runs in
  `postbuild`, attaching the same hashed Material toolbar bundles to Docs,
  Privacy and Team. The remote backend uses that asset manifest around its
  server-rendered forms. Preserve antiforgery inputs and no-JavaScript form
  behavior. No local research component is instantiated on remote/static pages.
- Outfit and Material Icons are self-hosted npm fonts; retain generated
  `font-licenses` and Angular third-party notices. Do not add external Google
  Fonts requests or substitute text/emoji for the original Material icons.

## Credentials, privacy and the connector

- Keep secrets in user-secrets, environment settings or a secret store, never in
  source, screenshots or logs. Foundry credentials must not reach the frontend
  or extension; do not log research payloads or authentication material.
- See [configuration](README.md#google-signup-and-production-configuration).
  The optional Project Meld credential-source helper is development-only,
  explicitly selected and rejected in Production; independent deployments need
  their own Foundry configuration. Never silently fall back to a different provider.
- Do not extract browser cookies/passwords/databases, automate Google login, or
  restore the removed application Playwright approach. Use the normal browser
  and connector; never execute downloaded TeX.
- Current Chrome identity: `pejcljoimoeklelcfbfddnkimlkjgfhe`; extension version
  `0.6.14`, native protocol `2`. Identity derives from the public manifest key,
  not a secret signing key. Update related identity/handshake checks together;
  compatible extension releases need not have identical version numbers.
- Bump the connector manifest version whenever extension files change: installed
  Meld mirrors the connector to `<install root>\connector`, asks older connectors
  to reload, and otherwise holds the app on Account → Overleaf with update steps.
- Browser installation needs user approval. Installer-guided store installation
  is supported; silent consumer installation/policy bypass is not.
- Research state uses AES-GCM with Windows DPAPI/macOS Keychain protection.
  Manual ZIP/JSON exports and temporary archives are not all encrypted; local
  loopback HTTP/WS is not TLS. Do not claim end-to-end transport encryption,
  provider zero retention, or complete store-policy compliance without evidence.

## Validation

Run the smallest relevant checks, rebuilding changed outputs first. These checks
do not use live Overleaf or model inference:

```powershell
npm --prefix .\apps\frontend run build
npm --prefix .\apps\frontend test
dotnet build .\MeldResearch.slnx --nologo
dotnet test .\tests\MeldResearch.Tests\MeldResearch.Tests.csproj --no-build --no-restore --nologo
node --test .\tests\extension.test.mjs .\tests\chrome-package.test.mjs
node .\tests\overleaf.e2e.mjs
node .\tests\research.e2e.mjs --public-pages-only
```

- Public-page checks require built frontend/backends, start isolated services,
  verify both themes and anonymous Docs/Privacy/Team routes, and assert zero model
  calls. For UI work, also inspect the real full-stack pages at desktop/mobile
  widths; do not treat a CSS text assertion as visual verification.
- `.\run-dev.ps1 -Mode Dummy -Check` runs the full validation workflow, including
  **real Foundry** import/delta/no-change/restart/revocation tests. It requires
  configured inference and consumes quota; use it for processing changes, not
  merely public CSS/documentation changes.
- `MeldResearch.RemoteBackend --foundry-smoke-test` is the cheaper live LLM check
  (two chat steps, one embedding call; see [README](README.md#foundry-setup-and-llm-smoke-test)).
  Use it after Foundry client, prompt/schema or deployment changes, and on the
  server to verify its managed identity. It consumes quota and prints no content.
  Keep Foundry chat calls stateless with `store: false`, matching Project Meld.
  Inference admission also follows Project Meld: the remote service queues requests
  (global and per-account limits, Retry-After retries, 429 cooldown) instead of
  refusing them, and the local worker runs several projects in parallel while each
  project's files stay in order.
- Keep regression tests aligned with consent, account isolation, exact unchanged
  inference counts, grounded citations and persistence. Report what actually ran.

## Packaging and release status

- Use [desktop packaging](tools/package-local.ps1) for `win-x64`, `osx-arm64` or
  `osx-x64`, and [Windows package smoke tests](tests/desktop-package.e2e.ps1).
  Rebuild packages after source changes; old ZIPs are not current builds.
- [Remote deployment](deploy): `deploy.sh`/`deploy.ps1` run on the Ubuntu server as
  the owner (`markusmobius`), keep code in `~/meldresearch/deployed` and data in
  `~/meldresearch-data`. The owner manages the reverse proxy, TLS certificates
  and the systemd unit; the deploy must never configure them. Keep the documented
  proxy requirements (`Host`, overwritten `X-Forwarded-Proto/For`, path-only
  access logs, body/timeout limits) aligned with the backend. Run it only with
  explicit approval.
- Desktop release identity is the SHA-256 of the committed client inputs listed
  in [release-common.ps1](tools/release-common.ps1); keep that list complete when
  adding anything the package contains. Packages rebuild only when it changes.
  Update manifests are signed with the owner's ECDSA key, a passphrase-encrypted
  PEM in `~/.config/meldresearch` on the server (never in the repo; the passphrase
  is typed at signing time and never stored); clients verify signature, runtime, newer date, length and SHA-256,
  install side by side, and restart through the stable launcher (exit code 75).
  Never weaken those checks or make clients accept unsigned or older releases.
  Validate with `tests\desktop-update.e2e.ps1` (isolated; no registry changes).
- [Chrome preparation](browser/prepare-chrome.ps1) creates local draft materials
  only. Follow [store submission guidance](browser/store/submission.md).
  Do not upload, publish, deploy or change native registrations without approval.
- Desktop packages are self-contained test distributions, not signed MSI/PKG
  installers. The account page installs them with one-line scripts
  ([apps/RemoteBackend/Install](apps/RemoteBackend/Install)) that verify the signed
  release, install per user without administrator rights, and start Meld at sign-in
  (scheduled task `MeldResearch` / LaunchAgent `org.meldresearch.desktop`). Installers
  stop only their own installation's processes. Validate the Windows flow with
  `tests\desktop-update.e2e.ps1`; the macOS flow is not yet execution-tested.
- At this handoff, no public deployment or store submission has been performed
  from this work. Real Google login, a post-store-identity live Overleaf read,
  macOS execution/Keychain/registration, signing/notarization, and remaining
  privacy/store-policy gates still need verification. Do not describe Dummy
  checks, a macOS cross-build, or draft assets as those completed release gates.

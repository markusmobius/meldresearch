# Meld Research: agent guide

This is the single repository-wide agent instruction file. Keep it current as
the application changes; do not add a singular `AGENT.md` or a duplicate global
instruction file. Start with the [README](README.md) for setup and release details.

## Product and scope

- Meld Research is an independent project. The Project Team currently consists
  only of Markus Mobius; the public contact is `markusmobius@gmail.com`.
- Keep Angular and C# with distinct local and remote backends. The UI must use
  **Angular Material and the actual Project Meld interface**, not just its colors.
  Preserve the ported 64px toolbar, share icon, Outfit typography, pill navigation,
  full-height scrolling workspace and compact Update cards. Do not replace them
  with a new hero/marketing dashboard or import unrelated Meld functionality.
- Overleaf is the only private-data source. The only other source is the user's
  public OpenAlex record (ported from Project Meld): searched only on request,
  candidates confirmed by the user, works stored in the local encrypted state.
  The feed contains research **Updates only**, including initial imports and
  meaningful project changes. Do not add WorkIQ, other news sources, TRAPI, or
  alternate inference fallbacks.
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

- Start processing work in [ResearchCoordinator](apps/LocalBackend/Services/Research/ResearchCoordinator.cs),
  [ResearchEvidence](libraries/Research/ResearchEvidence.cs), and
  [FoundryService](apps/RemoteBackend/Services/FoundryService.cs).
- Account linking, explicit processing consent and project selection are required
  before monitoring. Preserve account isolation and server-side authorization.
- Compare supported source content, not ZIP order/timestamps. Unchanged supported
  content must make **zero inference calls**, including after a restart.
- Projects are summarized file by file into a <= 5,000-character JSON profile,
  earliest edited first (Overleaf history via the connector). The initial import
  builds the baseline and produces no Updates; daily checks (and Check now) fold in
  changed files, and meaningful summary changes become cited Updates. The pending
  manifest, order and per-file processed hashes are the persistent queue: commit per
  file, resume after restart, never reprocess an unchanged hash or identical copy.
  Include/resume is processed first. Project summaries (one paragraph from the main
  `.tex`) are sent only on the user's per-project Summarize click, recent projects
  only, and never resent while the main file's hash is unchanged.
- Models select source line ranges; the server copies and validates actual
  citation text. Preserve grounding, evidence limits and visible validation errors.
- The remote backend persists accounts, devices, one-time app sign-in codes, the
  Data Protection key ring and content-free, account-free LLM usage rows (shown
  only to superusers at `/usage`) in SQLite via EF Core
  ([RemoteDbContext](apps/RemoteBackend/Data/RemoteDbContext.cs)). Change the
  schema only through checked-in migrations (`dotnet tool restore`, then
  `dotnet ef migrations add`). Keep names, emails and device names as Data
  Protection ciphertext and credentials/codes as hashes. Make
  state transitions conditional updates or transactions, not read-then-write.
  Research text is never stored remotely.
- The local app signs in through the website with a loopback redirect + PKCE
  ([LocalSignIn](apps/RemoteBackend/Services/LocalSignIn.cs)): `/connect` returns
  a one-time code only to `http://127.0.0.1|localhost:<port>/account/callback`,
  the app checks its in-memory state, and redeems the code with its verifier at
  `/api/sign-in/redeem`. Keep the redirect loopback-only, codes single-use and
  short-lived, and the verifier on the local machine; do not reintroduce an
  approval code step or accept other redirect targets.
- Commit each file step (summary, processed hash, Updates) atomically. Failed processing must not
  advance the stored revision. Pause, deletion, cancellation and account changes
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
  Account has vertical tabs Meld account / Overleaf (horizontal Connection |
  Projects) / OpenAlex. Projects owns include/exclude choices, consent,
  monitoring and ZIP downloads; About me has Project Meld's tabs: Private profile,
  Public profile (only once one exists) and Publications.
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
  `0.5.2`, native protocol `2`. Identity derives from the public manifest key,
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

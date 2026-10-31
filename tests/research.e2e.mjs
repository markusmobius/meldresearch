import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash, generateKeyPairSync, sign } from 'node:crypto';
import { mkdtemp, mkdir, rm, readFile, readdir, writeFile } from 'node:fs/promises';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const publicPagesOnly = process.argv.includes('--public-pages-only');
const settingIndex = process.argv.indexOf('--foundry-settings');
assert.ok(publicPagesOnly || settingIndex >= 0 && process.argv[settingIndex + 1], 'Supply an explicit development Foundry settings file.');
const settings = settingIndex >= 0 ? path.resolve(process.argv[settingIndex + 1]) : null;
const temporary = await mkdtemp(path.join(tmpdir(), 'meldresearch-research-e2e-'));
const children = [];
const logs = [];
const suffix = process.platform === 'win32' ? '.exe' : '';
const projectId = '0123456789abcdef01234567';
const wait = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));

async function freePort() {
  const server = createServer();
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const port = server.address().port;
  await new Promise(resolve => server.close(resolve));
  return port;
}
function start(name, args) {
  const child = spawn(path.join(repo, 'apps', name, 'bin', 'Debug', 'net10.0', `MeldResearch.${name}${suffix}`), args, {
    cwd: repo, env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', MELDRESEARCH_DATA_DIR: path.join(temporary, 'local') },
    windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
  });
  child.stdout.on('data', data => logs.push(data.toString()));
  child.stderr.on('data', data => logs.push(data.toString()));
  children.push(child);
  return child;
}
async function ready(child, base) {
  for (let attempt = 0; attempt < 150; attempt++) {
    assert.equal(child.exitCode, null, `An isolated backend exited during startup: ${logs.join('').slice(-2000)}`);
    try { if ((await fetch(base + '/api/status')).ok) return; }
    catch (error) { if (!(error instanceof TypeError)) throw error; }
    await wait(100);
  }
  throw new Error('An isolated backend did not become ready.');
}
function client(base) {
  const cookies = new Map();
  return async (route, { method = 'GET', body, form, csrf = false } = {}) => {
    const headers = {};
    if (cookies.size) headers.Cookie = [...cookies].map(([name, value]) => `${name}=${value}`).join('; ');
    if (csrf) headers['X-XSRF-TOKEN'] = decodeURIComponent(cookies.get('XSRF-TOKEN') ?? '');
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    if (form) { headers['Content-Type'] = 'application/x-www-form-urlencoded'; headers.Origin = base; }
    const response = await fetch(base + route, { method, headers, redirect: 'manual',
      body: form ? new URLSearchParams(form).toString() : body === undefined ? undefined : JSON.stringify(body) });
    for (const cookie of response.headers.getSetCookie()) {
      const pair = cookie.split(';', 1)[0], equals = pair.indexOf('=');
      cookies.set(pair.slice(0, equals), pair.slice(equals + 1));
    }
    return response;
  };
}
function formToken(html) {
  const token = html.match(/name="__RequestVerificationToken" value="([^"]+)"/)?.[1];
  assert.ok(token, 'The remote form must carry antiforgery protection.');
  return token;
}
async function statusUntil(local, predicate, description, timeout = 240000) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    const response = await local('/api/research/status');
    assert.equal(response.status, 200, 'Research status must remain available.');
    const state = await response.json();
    const error = state.projects.find(project => project.error)?.error ?? state.accountError;
    if (error) throw new Error(`${description}: ${error}`);
    if (predicate(state)) return state;
    await wait(1000);
  }
  throw new Error(`Timed out: ${description}`);
}

async function verifyPublicPages(local, remote) {
  for (const [site, request, color] of [['local', local, '#084d42'], ['remote', remote, '#1e3c72']]) {
    const theme = await request('/theme.css');
    assert.equal(theme.status, 200, `${site} theme must be served.`);
    assert.ok((await theme.text()).includes(`--brand-start: ${color}`));
    for (const resource of ['/info.css', '/popup.css', '/connector/popup.css']) {
      const response = await request(resource);
      assert.equal(response.status, 200, `${site} ${resource} must be served.`);
      assert.match(response.headers.get('content-type'), /text\/css/);
    }
    for (const page of ['docs', 'team', 'privacy']) {
      const response = await request(`/${page}.html`);
      assert.equal(response.status, 200, `${site} ${page} must not require sign-in.`);
      const html = await response.text();
      assert.match(html, /Meld Research/);
      assert.match(html, /aria-label="Main navigation"/);
      assert.match(html, /<app-meld-toolbar>/, `${site} ${page} must use the shared Angular Material toolbar.`);
      assert.match(html, /<script src="\/main-[\w-]+\.js" type="module">/);
      assert.doesNotMatch(html, /(?:&copy;|\u00a9)\s*\d{4}\s+Microsoft|go\.microsoft\.com\/fwlink|Research Ethics Review/);
      for (const destination of ['docs', 'team', 'privacy']) assert.ok(html.includes(`href="${destination}.html"`));
      if (page === 'team') {
        assert.equal((html.match(/data-team-member/g) ?? []).length, 1);
        assert.match(html, /<h2 class="team-name">Markus Mobius<\/h2>/);
        assert.match(html, /src="assets\/markusmobius\.png"/);
        assert.equal((await request('/assets/markusmobius.png')).status, 200, `${site} team photo must be served.`);
      } else if (page === 'docs') {
        assert.match(html, /both backends run on your computer/i);
        assert.match(html, /AGENTS\.md/);
      } else {
        assert.match(html, /responsible contact: Markus Mobius/);
        assert.match(html, /Azure AI Foundry/);
        assert.match(html, /applicable Microsoft terms/);
        assert.match(html, /not encrypted/);
        assert.match(html, /HTTP\/WS, not TLS/);
      }
    }
  }
  const stylesheet = await remote('/site.css');
  assert.equal(stylesheet.status, 200);
  assert.match(stylesheet.headers.get('content-type'), /text\/css/);
  const home = await remote('/');
  assert.equal(home.headers.get('referrer-policy'), 'same-origin');
  const html = await home.text();
  assert.match(html, /<app-meld-toolbar>/);
  assert.match(html, /<span class="brand-role">Remote Backend<\/span>/);
  assert.match(html, /href="\/docs.html"/);
  assert.match(html, /href="\/team.html"/);
  assert.doesNotMatch(html, /(?:&copy;|\u00a9)\s*\d{4}\s+Microsoft/);
  for (const request of [local, remote]) {
    const assets = await request('/angular-assets.html');
    assert.equal(assets.status, 200);
    const markup = await assets.text();
    for (const [, resource] of markup.matchAll(/(?:src|href)="([^"]+)"/g)) {
      const response = await request(resource);
      assert.equal(response.status, 200, `Shared Material bundle ${resource} must be served.`);
      assert.match(response.headers.get('content-type'),
        resource.endsWith('.js') ? /javascript/ : resource.endsWith('.png') ? /image\/png/ : /text\/css/);
    }
    for (const font of ['outfit', 'material-icons'])
      assert.equal((await request(`/font-licenses/${font}.txt`)).status, 200);
  }
}

async function signInLocal(local, remote, remoteBase, localBase) {
  const response = await local('/api/research/account/link', { method: 'POST', body: {}, csrf: true });
  assert.equal(response.status, 200);
  const url = new URL((await response.json()).signInUrl);
  assert.equal(url.origin + url.pathname, `${remoteBase}/connect`);
  assert.equal(url.searchParams.get('redirect_uri'), `${localBase}/account/callback`);
  assert.deepEqual([...url.searchParams.keys()].sort(), ['challenge', 'device', 'redirect_uri', 'state'],
    'Only the PKCE challenge leaves the app; the verifier stays local.');
  const page = await remote(url.pathname + url.search);
  assert.equal(page.status, 200, 'Without a website session the sign-in page is shown.');
  assert.match(page.headers.get('content-security-policy'), /form-action 'self' http:\/\/127\.0\.0\.1:\* http:\/\/localhost:\*/);
  const login = await remote('/auth/dummy', { method: 'POST', form: {
    ...Object.fromEntries(url.searchParams), __RequestVerificationToken: formToken(await page.text()) } });
  assert.equal(login.status, 302);
  assert.match(login.headers.get('location'), /^\/connect\?state=/);
  const back = await remote(login.headers.get('location'));
  assert.equal(back.status, 302, 'A signed-in account holder returns to the app without an approval step.');
  const callback = new URL(back.headers.get('location'));
  assert.equal(callback.origin + callback.pathname, `${localBase}/account/callback`);
  assert.equal(callback.searchParams.get('state'), url.searchParams.get('state'));
  const forged = await local(`/account/callback?code=${callback.searchParams.get('code')}&state=${'0'.repeat(64)}`);
  assert.equal(forged.status, 302);
  assert.equal((await (await local('/api/research/status')).json()).account, null, 'A callback with another state cannot sign the app in.');
  const done = await local(callback.pathname + callback.search);
  assert.equal(done.status, 302);
  assert.equal(done.headers.get('location'), '/');
  const state = await (await local('/api/research/status')).json();
  assert.equal(state.account?.name, 'Demo Researcher');
  assert.equal(state.pendingLink, null);
  const replay = await client(remoteBase)('/api/sign-in/redeem', { method: 'POST',
    body: { code: callback.searchParams.get('code'), verifier: 'a'.repeat(64) } });
  assert.equal(replay.status, 410, 'Each sign-in code is single-use.');
  return state;
}

async function verifyModelFlow(local, remote, localProcess, localArgs, localBase, remoteBase) {
  let state = await signInLocal(local, remote, remoteBase, localBase);
  assert.equal((await local(`/api/research/projects/${projectId}`, {
    method: 'PUT', body: { enabled: true, consentVersion: null }, csrf: true,
  })).status, 400, 'Monitoring requires explicit consent.');
  assert.equal((await local(`/api/research/projects/${projectId}`, {
    method: 'PUT', body: { enabled: true, consentVersion: state.consentVersion }, csrf: true,
  })).status, 204);
  state = await statusUntil(local, state => !state.busy && state.projects[0]?.phase === 'ready' && state.projects[0]?.profile,
    'real Foundry file-by-file initial import');
  assert.equal(state.totalUpdates, 0, 'The initial import builds the summary; it is not news.');
  assert.ok(state.projects[0].profile.title && state.projects[0].profile.description);
  const firstRevision = state.projects[0].revision, firstCount = state.totalUpdates;
  const counts = await (await remote('/api/development/inference-counts')).json();
  assert.ok(counts.chatRequests >= 1, 'The check must exercise real configured inference.');
  const lastCheck = state.projects[0].lastSweepAt;
  assert.equal((await local('/api/research/sync', { method: 'POST', body: {}, csrf: true })).status, 202);
  state = await statusUntil(local, state => !state.busy && state.projects[0]?.lastSweepAt !== lastCheck, 'unchanged content check');
  assert.equal(state.projects[0].revision, firstRevision);
  assert.equal(state.totalUpdates, firstCount);
  assert.deepEqual(await (await remote('/api/development/inference-counts')).json(), counts,
    'Unchanged file content must cause exactly zero model calls.');
  assert.equal((await local('/api/development/overleaf/change', { method: 'POST', body: {}, csrf: true })).status, 200);
  state = await statusUntil(local, state => !state.busy && state.projects[0]?.phase === 'ready' &&
    state.projects[0]?.revision !== firstRevision && state.totalUpdates > firstCount, 'real Foundry delta processing');
  assert.ok(state.updates.every(update => update.kind === 'change' && update.evidence.length > 0 &&
    update.evidence.every(citation => citation.path === 'main.tex')), 'Updates cite the changed file verbatim.');
  assert.ok(state.projects[0].profile.contributions.length > 0);
  const processedRevision = state.projects[0].revision, processedCount = state.totalUpdates;
  const countsBeforeRestart = await (await remote('/api/development/inference-counts')).json();
  localProcess.kill();
  await new Promise(resolve => localProcess.once('exit', resolve));
  localProcess = start('LocalBackend', localArgs);
  await ready(localProcess, localBase);
  await local('/api/status');
  state = await statusUntil(local, state => !state.busy && state.projects[0]?.revision === processedRevision,
    'encrypted state and synthetic source restart persistence');
  assert.equal(state.totalUpdates, processedCount);
  assert.deepEqual(await (await remote('/api/development/inference-counts')).json(), countsBeforeRestart,
    'A restart must not duplicate model work or re-import an already processed revision.');
  const encrypted = await readFile(path.join(temporary, 'local', 'research', 'research.protected'));
  assert.equal(encrypted.includes(Buffer.from('synthetic-researcher')), false);
  assert.equal(encrypted.includes(Buffer.from('structured weekly feedback')), false);
  const textsDirectory = path.join(temporary, 'local', 'research', 'texts');
  for (const name of await readdir(textsDirectory))
    assert.equal((await readFile(path.join(textsDirectory, name))).includes(Buffer.from('structured weekly feedback')), false,
      'Stored source texts are encrypted.');
  assert.equal((await local('/api/research/account', { method: 'DELETE', csrf: true })).status, 204);
  const disconnected = await (await local('/api/research/status')).json();
  assert.equal(disconnected.account, null);
  assert.equal(disconnected.projects.length, 0, 'Disconnected accounts must not expose indexed project data.');
  assert.equal(disconnected.updates.length, 0);
  assert.equal(disconnected.pendingRevocations, 0);
  const output = logs.join('');
  assert.equal(output.includes('structured weekly feedback'), false, 'Logs must omit source text.');
  assert.equal(output.includes('Bearer '), false, 'Logs must omit bearer credentials.');
  console.log('Research E2E passed: public policy; account/proof/consent gates; real Foundry file-by-file initial import (no Updates) and a cited delta Update; exactly zero inference for unchanged content or restart; encrypted state and texts; revocation and account isolation.');
}

async function verifyGoogleSignIn() {
  const port = await freePort(), base = `http://127.0.0.1:${port}`;
  const clientId = 'synthetic-client.apps.googleusercontent.com';
  const server = start('RemoteBackend', ['--Port', String(port), '--PublicOrigin', base, '--Dummy', 'false',
    '--DataDirectory', path.join(temporary, 'remote-google'), '--Google:ClientId', clientId]);
  await ready(server, base);
  const browser = client(base);
  assert.doesNotMatch((await browser('/docs.html')).headers.get('content-security-policy'), /accounts\.google\.com/,
    'Only sign-in pages allow Google scripts.');
  const home = await browser('/');
  assert.match(home.headers.get('content-security-policy'), /accounts\.google\.com\/gsi\/client/, 'Home is the sign-up page.');
  const homeHtml = await home.text();
  assert.match(homeHtml, /class="g_id_signin"[^>]*data-text="signup_with"[^>]*data-meld-auto-mode="true"/,
    'Home says Sign up unless this browser has seen a Meld account.');
  assert.ok(homeHtml.indexOf('src="/google-sign-in.js"') < homeHtml.indexOf('src="https://accounts.google.com/gsi/client"'),
    'The page script adjusts the button before Google renders it.');
  const homeScript = await (await browser('/google-sign-in.js')).text();
  assert.match(homeScript, /meldresearch\.account/);
  assert.match(homeScript, /signin_with/);
  assert.equal((await browser('/api/session')).status, 204);
  const signup = await browser('/signup');
  assert.equal(signup.status, 200);
  const csp = signup.headers.get('content-security-policy');
  assert.match(csp, /script-src 'self' https:\/\/accounts\.google\.com\/gsi\/client/);
  assert.match(csp, /frame-src https:\/\/accounts\.google\.com\/gsi\//);
  assert.equal(signup.headers.get('referrer-policy'), 'strict-origin-when-cross-origin');
  const nonceCookie = signup.headers.getSetCookie().find(cookie => cookie.startsWith('MeldResearch.Remote.GoogleNonce='));
  assert.ok(nonceCookie && /path=\/auth\/google/i.test(nonceCookie) && /httponly/i.test(nonceCookie) && /samesite=strict/i.test(nonceCookie),
    'Each sign-in page binds a one-time nonce to the browser.');
  const html = await signup.text();
  const nonce = nonceCookie.split(';', 1)[0].split('=')[1];
  assert.ok(html.includes(`data-client_id="${clientId}"`) && html.includes(`data-nonce="${nonce}"`));
  assert.ok(html.includes('src="https://accounts.google.com/gsi/client"') && html.includes('src="/google-sign-in.js"'));
  assert.doesNotMatch(html, /client_secret|ClientSecret/i);
  const script = await browser('/google-sign-in.js');
  assert.equal(script.status, 200);
  assert.match(script.headers.get('content-type'), /javascript/);
  const forged = await browser('/auth/google', { method: 'POST', form: {
    __RequestVerificationToken: formToken(html), mode: 'signup', credential: 'not-a-google-token' } });
  assert.equal(forged.status, 302);
  assert.equal(forged.headers.get('location'), '/?signInFailed=1', 'An unverifiable token never creates a session.');
  assert.equal((await browser('/account')).status, 401, 'No session exists after a rejected token.');
  const noToken = await client(base)('/auth/google', { method: 'POST', form: { mode: 'signup', credential: 'x' } });
  assert.equal(noToken.status, 403, 'The sign-in form keeps antiforgery protection.');
  server.kill();
  await new Promise(resolve => server.once('exit', resolve));
}

async function verifyRemoteSession(base) {
  const browser = client(base);
  const login = await browser('/auth/dummy', { method: 'POST', form: {
    __RequestVerificationToken: formToken(await (await browser('/')).text()) } });
  assert.equal(login.status, 302);
  assert.equal((await browser('/docs.html')).status, 200);
  const home = await (await browser('/')).text();
  assert.match(home, /<a class="install-button" href="\/account">/, 'After signing up, Home offers only the install button.');
  assert.match(home, /<meta name="meld-account" content="Demo Researcher" data-email="researcher@example.invalid">/,
    'The toolbar account menu reflects the existing session after visiting Docs.');
  assert.doesNotMatch(home, /Use the synthetic test account|stat-card|Read the setup guide/);
  assert.match(home, /<footer class="meld-footer"><nav aria-label="Footer"><a href="mailto:[^"]+">Contact<\/a>\s*<a href="\/privacy.html">Privacy<\/a><\/nav><\/footer>/);
  const session = await browser('/api/session');
  assert.equal(session.status, 200, 'Static pages read the session for the account menu.');
  const sessionBody = await session.json();
  assert.equal(sessionBody.name, 'Demo Researcher');
  assert.ok(sessionBody.signOutToken);
  const account = await browser('/account');
  assert.equal(account.status, 200);
  const accountHtml = await account.text();
  assert.match(accountHtml, /<app-meld-install>/, 'The account page offers the install card.');
  assert.ok(accountHtml.includes(`irm -useb ${base}/api/install/windows | iex`), 'Without JavaScript the install command remains visible.');
  assert.doesNotMatch(accountHtml, /href="\/downloads\//);
  assert.match(accountHtml, /<form id="meld-sign-out" method="post" action="\/auth\/signout" hidden>/);
  assert.match(accountHtml, /<button form="meld-sign-out">Sign out<\/button>/, 'Without JavaScript the header can still sign out.');
  const signOut = await browser('/auth/signout', { method: 'POST', form: { __RequestVerificationToken: sessionBody.signOutToken } });
  assert.equal(signOut.status, 302);
  assert.equal(signOut.headers.get('location'), '/');
  assert.equal((await browser('/account')).status, 401, 'Signing out ends the session.');
  assert.equal((await browser('/api/session')).status, 204);
  const anonymous = await (await browser('/')).text();
  assert.match(anonymous, /class="dummy-sign-in"/);
  assert.doesNotMatch(anonymous, /meld-account|meld-sign-out/);
  assert.equal((await client(base)('/auth/signout', { method: 'POST', form: {} })).status, 403, 'Sign-out keeps antiforgery protection.');
}

async function publishSyntheticUpdate(directory) {
  await mkdir(directory, { recursive: true });
  const pkg = Buffer.from('synthetic desktop package bytes');
  const version = 'b'.repeat(64);
  const manifest = Buffer.from(JSON.stringify({
    product: 'MeldResearch.Desktop', runtime: 'win-x64', version, versionDate: '2026-10-28T00:00:00+00:00',
    length: pkg.length, sha256: createHash('sha256').update(pkg).digest('hex'),
  }));
  const { privateKey } = generateKeyPairSync('ec', { namedCurve: 'P-256' });
  const signature = sign('sha256', manifest, { key: privateKey, dsaEncoding: 'ieee-p1363' });
  const envelope = JSON.stringify({ manifest: manifest.toString('base64'), signature: signature.toString('base64') });
  await writeFile(path.join(directory, 'meldresearch-win-x64.zip'), pkg);
  await writeFile(path.join(directory, 'meldresearch-win-x64.update.json'), envelope);
  return { version, pkg, envelope };
}
async function verifyUpdateEndpoints(base, published) {
  const anonymous = client(base);
  const manifest = await anonymous('/api/updates/win-x64');
  assert.equal(manifest.status, 200, 'Installed clients read update manifests without an account session.');
  assert.match(manifest.headers.get('content-type'), /application\/json/);
  assert.equal(await manifest.text(), published.envelope, 'The signed envelope is served byte for byte.');
  assert.equal((await anonymous('/api/updates/osx-x64')).status, 404, 'Unpublished runtimes have no update.');
  assert.equal((await anonymous('/api/updates/linux-x64')).status, 404, 'Unknown runtimes are rejected.');
  const pkg = await anonymous(`/api/updates/win-x64/package/${published.version}`);
  assert.equal(pkg.status, 200);
  assert.deepEqual(Buffer.from(await pkg.arrayBuffer()), published.pkg);
  assert.equal((await anonymous(`/api/updates/win-x64/package/${'c'.repeat(64)}`)).status, 404,
    'Only the release named by the current manifest is served.');
  assert.equal((await anonymous('/downloads/win-x64')).status, 401, 'Website downloads still require an account.');
  for (const [platform, file, command] of [['windows', 'meldresearch-install.ps1', 'Install-Meld.ps1'], ['macos', 'meldresearch-install.sh', 'Install-Meld.command']]) {
    const installer = await anonymous(`/api/install/${platform}`);
    assert.equal(installer.status, 200, 'The one-line installer is fetched without a browser session.');
    assert.match(installer.headers.get('content-type'), /text\/plain/);
    assert.match(installer.headers.get('content-disposition'), new RegExp(`filename=${file.replace('.', '\\.')}`));
    const script = await installer.text();
    assert.ok(script.includes(`'${base}'`) || script.includes(`"${base}"`), 'The script is bound to this server.');
    assert.ok(!script.includes('{{') && script.includes('/api/updates/') && script.includes(command));
    if (platform === 'macos') assert.ok(!script.includes('\r'), 'bash rejects CRLF line endings.');
  }
  assert.equal((await anonymous('/api/install/linux')).status, 404);
}

try {
  const remotePort = await freePort(), localPort = await freePort();
  const remoteBase = `http://127.0.0.1:${remotePort}`, localBase = `http://127.0.0.1:${localPort}`;
  const published = await publishSyntheticUpdate(path.join(temporary, 'downloads'));
  const remoteArgs = ['--Port', String(remotePort), '--PublicOrigin', remoteBase,
    '--Dummy', 'true', '--DataDirectory', path.join(temporary, 'remote'), '--DownloadDirectory', path.join(temporary, 'downloads')];
  if (settings) remoteArgs.push('--SettingsFile', settings);
  const remoteProcess = start('RemoteBackend', remoteArgs);
  await ready(remoteProcess, remoteBase);
  const localArgs = ['--Port', String(localPort), '--Dummy', 'true', '--RemoteUrl', remoteBase,
    '--FrontendPath', path.join(repo, 'apps', 'frontend', 'dist', 'frontend', 'browser')];
  const localProcess = start('LocalBackend', localArgs);
  await ready(localProcess, localBase);
  const remote = client(remoteBase), local = client(localBase);
  await local('/api/status');
  await verifyPublicPages(local, remote);
  const ping = await local('/api/remote-ping');
  assert.equal(ping.status, 200);
  assert.equal((await ping.json()).online, true, 'The local backend reports a reachable remote service.');
  assert.equal((await remote('/api/account')).status, 401);
  assert.equal((await local('/api/research/sync', { method: 'POST', body: {}, csrf: true })).status, 401);
  const configuration = await (await remote('/api/status')).json();
  assert.equal(configuration.dummy, true);
  assert.equal(configuration.extensionId, 'pejcljoimoeklelcfbfddnkimlkjgfhe');
  if (publicPagesOnly) {
    assert.deepEqual(await (await remote('/api/development/inference-counts')).json(), { chatRequests: 0, embeddingRequests: 0 });
    await verifyUpdateEndpoints(remoteBase, published);
    await verifyRemoteSession(remoteBase);
    await verifyGoogleSignIn();
    await signInLocal(local, client(remoteBase), remoteBase, localBase);
    assert.equal((await local('/api/research/account', { method: 'DELETE', csrf: true })).status, 204);
    assert.deepEqual(await (await remote('/api/development/inference-counts')).json(), { chatRequests: 0, embeddingRequests: 0 });
    console.log('Public pages passed: local green / remote blue themes; anonymous Docs/Privacy/Project Team; Markus Mobius only; accurate provider disclosures; public signed desktop updates; remote session survives navigation and signs out; secretless Google sign-in page and token endpoint; local app Google sign-in round trip (loopback + PKCE); zero model calls.');
  } else {
    assert.equal(configuration.foundryConfigured, true);
    await verifyModelFlow(local, remote, localProcess, localArgs, localBase, remoteBase);
  }
} finally {
  for (const child of children.reverse()) {
    if (child.exitCode === null && child.signalCode === null) {
      child.kill();
      await new Promise(resolve => child.once('exit', resolve));
    }
  }
  await rm(temporary, { recursive: true });
}

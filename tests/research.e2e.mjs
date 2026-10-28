import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash, generateKeyPairSync, sign } from 'node:crypto';
import { mkdtemp, mkdir, rm, readFile, writeFile } from 'node:fs/promises';
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
        assert.match(html, /<h2>Markus Mobius<\/h2>/);
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

async function verifyModelFlow(local, remote, localProcess, localArgs, localBase) {
  const linkResponse = await local('/api/research/account/link', { method: 'POST', body: {}, csrf: true });
  assert.equal(linkResponse.status, 200);
  const link = await linkResponse.json();
  assert.equal('deviceCode' in link, false, 'The private device code must not reach the browser API.');
  const page = await (await remote('/connect?code=' + link.userCode)).text();
  const login = await remote('/auth/dummy', { method: 'POST', form: {
    code: link.userCode, __RequestVerificationToken: formToken(page),
  } });
  assert.equal(login.status, 302);
  const approval = await (await remote('/connect?code=' + link.userCode)).text();
  assert.equal((await remote('/connect', { method: 'POST', form: {
    code: link.userCode, decision: 'allow', __RequestVerificationToken: formToken(approval),
  } })).status, 200);
  let state = await statusUntil(local, state => !!state.account, 'normal browser device approval');
  assert.equal((await local(`/api/research/projects/${projectId}`, {
    method: 'PUT', body: { enabled: true, consentVersion: null }, csrf: true,
  })).status, 400, 'Monitoring requires explicit consent.');
  assert.equal((await local(`/api/research/projects/${projectId}`, {
    method: 'PUT', body: { enabled: true, consentVersion: state.consentVersion }, csrf: true,
  })).status, 204);
  state = await statusUntil(local, state => !state.busy && state.projects[0]?.revision && state.totalUpdates > 0, 'real Foundry initial import');
  assert.ok(state.updates.every(update => update.kind === 'initialImport'));
  assert.ok(state.updates.every(update => update.evidence.length > 0));
  const firstRevision = state.projects[0].revision, firstCount = state.totalUpdates;
  const counts = await (await remote('/api/development/inference-counts')).json();
  assert.ok(counts.chatRequests >= 1 && counts.embeddingRequests >= 1, 'The check must exercise real configured inference.');
  const lastCheck = state.projects[0].lastSuccessAt;
  assert.equal((await local('/api/research/sync', { method: 'POST', body: {}, csrf: true })).status, 202);
  state = await statusUntil(local, state => !state.busy && state.projects[0]?.lastSuccessAt !== lastCheck, 'unchanged content check');
  assert.equal(state.projects[0].revision, firstRevision);
  assert.equal(state.totalUpdates, firstCount);
  assert.deepEqual(await (await remote('/api/development/inference-counts')).json(), counts,
    'Unchanged file content must cause exactly zero chat and embedding calls.');
  assert.equal((await local('/api/development/overleaf/change', { method: 'POST', body: {}, csrf: true })).status, 200);
  state = await statusUntil(local, state => !state.busy && state.projects[0]?.revision !== firstRevision &&
    state.totalUpdates > firstCount, 'real Foundry delta processing');
  assert.ok(state.updates.some(update => update.kind !== 'initialImport'));
  assert.ok(state.projects[0].factCount > 0);
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
  assert.equal((await local('/api/research/account', { method: 'DELETE', csrf: true })).status, 204);
  const disconnected = await (await local('/api/research/status')).json();
  assert.equal(disconnected.account, null);
  assert.equal(disconnected.projects.length, 0, 'Disconnected accounts must not expose indexed project data.');
  assert.equal(disconnected.updates.length, 0);
  assert.equal(disconnected.pendingRevocations, 0);
  const output = logs.join('');
  assert.equal(output.includes('structured weekly feedback'), false, 'Logs must omit source text.');
  assert.equal(output.includes('Bearer '), false, 'Logs must omit bearer credentials.');
  console.log('Research E2E passed: public policy; account/proof/consent gates; real Foundry initial import and delta; exactly zero inference for unchanged content or restart; encrypted persistence; revocation and account isolation.');
}

async function verifyGoogleSignIn() {
  const port = await freePort(), base = `http://127.0.0.1:${port}`;
  const clientId = 'synthetic-client.apps.googleusercontent.com';
  const server = start('RemoteBackend', ['--Port', String(port), '--PublicOrigin', base, '--Dummy', 'false',
    '--DataDirectory', path.join(temporary, 'remote-google'), '--Google:ClientId', clientId]);
  await ready(server, base);
  const browser = client(base);
  const home = await browser('/');
  assert.doesNotMatch(home.headers.get('content-security-policy'), /accounts\.google\.com/, 'Only sign-in pages allow Google scripts.');
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
  assert.equal((await remote('/api/account')).status, 401);
  assert.equal((await local('/api/research/sync', { method: 'POST', body: {}, csrf: true })).status, 401);
  const configuration = await (await remote('/api/status')).json();
  assert.equal(configuration.dummy, true);
  assert.equal(configuration.extensionId, 'pejcljoimoeklelcfbfddnkimlkjgfhe');
  if (publicPagesOnly) {
    assert.deepEqual(await (await remote('/api/development/inference-counts')).json(), { chatRequests: 0, embeddingRequests: 0 });
    await verifyUpdateEndpoints(remoteBase, published);
    await verifyGoogleSignIn();
    console.log('Public pages passed: local green / remote blue themes; anonymous Docs/Privacy/Project Team; Markus Mobius only; accurate provider disclosures; public signed desktop updates; secretless Google sign-in page and token endpoint; zero model calls.');
  } else {
    assert.equal(configuration.foundryConfigured, true);
    await verifyModelFlow(local, remote, localProcess, localArgs, localBase);
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

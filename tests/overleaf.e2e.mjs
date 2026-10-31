import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { createServer } from 'node:net';
import { request as httpRequest, createServer as createHttpServer } from 'node:http';
import { createHash, createHmac, randomBytes } from 'node:crypto';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { browserHarness, event, extensionId, response, waitUntil } from './browser-harness.mjs';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const temporary = await mkdtemp(path.join(tmpdir(), 'meldresearch-e2e-'));
const executable = process.platform === 'win32' ? '.exe' : '';
const backendPath = path.join(repo, 'apps', 'LocalBackend', 'bin', 'Debug', 'net10.0', `MeldResearch.LocalBackend${executable}`);
const hostPath = path.join(repo, 'apps', 'BrowserHost', 'bin', 'Debug', 'net10.0', `MeldResearch.BrowserHost${executable}`);
const environment = { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', MELDRESEARCH_DATA_DIR: temporary };
const children = [];
const logs = [];
let harness;
let backend;
let baseUrl;
let cookies = new Map();

function start(file, args = []) {
  const child = spawn(file, args, { cwd: repo, env: environment, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  children.push(child);
  child.stderr.on('data', data => logs.push(data.toString()));
  return child;
}

async function stop(child) {
  if (!child || child.exitCode !== null || child.signalCode !== null) return;
  child.kill();
  await waitUntil(() => child.exitCode !== null || child.signalCode !== null, 'owned process exit', 10000);
}

async function launchBackend(port) {
  backend = start(backendPath, ['--Port', String(port),
    '--FrontendPath', path.join(repo, 'apps', 'frontend', 'dist', 'frontend', 'browser')]);
  backend.stdout.on('data', data => logs.push(data.toString()));
  baseUrl = `http://127.0.0.1:${port}`;
  let ready = false;
  const deadline = Date.now() + 15000;
  while (!ready && Date.now() < deadline) {
    if (backend.exitCode !== null) throw new Error(`The synthetic backend exited: ${logs.join('').slice(-4000)}`);
    try {
      ready = (await fetch(`${baseUrl}/api/status`)).ok;
    } catch (error) {
      if (!(error instanceof TypeError)) throw error;
    }
    if (!ready) await new Promise(resolve => setTimeout(resolve, 50));
  }
  assert.equal(ready, true, 'The synthetic backend must start.');
  cookies = new Map();
  await call('/api/status');
}

async function call(route, { method = 'GET', protectedRequest = true, headers = {} } = {}) {
  const requestHeaders = { ...headers };
  if (cookies.size) requestHeaders.Cookie = [...cookies].map(([key, value]) => `${key}=${value}`).join('; ');
  if (protectedRequest && method !== 'GET') requestHeaders['X-XSRF-TOKEN'] = decodeURIComponent(cookies.get('XSRF-TOKEN') ?? '');
  const result = await fetch(baseUrl + route, { method, headers: requestHeaders });
  for (const cookie of result.headers.getSetCookie()) {
    const pair = cookie.split(';', 1)[0];
    const equals = pair.indexOf('=');
    cookies.set(pair.slice(0, equals), pair.slice(equals + 1));
  }
  return result;
}

function nativePort() {
  const child = start(hostPath);
  const port = { child, onMessage: event(), onDisconnect: event() };
  let buffered = Buffer.alloc(0);
  child.stdout.on('data', bytes => {
    buffered = Buffer.concat([buffered, bytes]);
    while (buffered.length >= 4 && buffered.length >= 4 + buffered.readUInt32LE(0)) {
      const length = buffered.readUInt32LE(0);
      assert.ok(length <= 256 * 1024, 'The native host must honor its message-size limit.');
      const message = JSON.parse(buffered.subarray(4, 4 + length).toString('utf8'));
      buffered = buffered.subarray(4 + length);
      port.onMessage.emit(message);
    }
  });
  child.on('exit', () => port.onDisconnect.emit());
  port.postMessage = message => {
    const body = Buffer.from(JSON.stringify(message));
    const prefix = Buffer.alloc(4);
    prefix.writeUInt32LE(body.length);
    child.stdin.write(Buffer.concat([prefix, body]));
  };
  port.disconnect = () => child.stdin.end();
  return port;
}

async function rejectImpersonatedBackend(proofMode) {
  const server = createHttpServer();
  const sockets = new Set();
  const key = randomBytes(32).toString('hex');
  let receivedClientProof = false;
  const proof = (nonce, role, secret = key) => createHmac('sha256', Buffer.from(secret, 'hex'))
    .update(`meld-native-v2:${role}:${nonce.toUpperCase()}`).digest('hex');
  server.on('upgrade', (request, socket) => {
    sockets.add(socket);
    socket.on('close', () => sockets.delete(socket));
    socket.on('error', error => {
      if (error.code !== 'ECONNRESET') throw error;
    });
    const nonce = request.headers['x-meld-nonce'];
    const authorization = request.headers.authorization;
    assert.match(nonce, /^[a-f0-9]{64}$/i);
    assert.equal(authorization, `MeldNative ${proof(nonce, 'client').toUpperCase()}`);
    assert.equal(authorization.includes(key), false, 'The shared secret must not be sent to a listener.');
    receivedClientProof = true;
    const accept = createHash('sha1')
      .update(request.headers['sec-websocket-key'] + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
    const badProof = proofMode === 'reflection' ? authorization.slice('MeldNative '.length)
      : proofMode === 'wrongNonce' ? proof('0'.repeat(64), 'server')
      : proof(nonce, 'server', '0'.repeat(64));
    socket.write('HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n' +
      `Sec-WebSocket-Accept: ${accept}\r\n` +
      (proofMode === 'missing' ? '' : `X-Meld-Server-Proof: ${badProof}\r\n`) + '\r\n');
    for (const message of [
      { type: 'ready', version: 2, localUrl: 'http://127.0.0.1:6010' },
      { type: 'request', id: 'a'.repeat(32), operation: 'listProjects' },
    ]) {
      const body = Buffer.from(JSON.stringify(message));
      assert.ok(body.length < 126);
      socket.write(Buffer.concat([Buffer.from([0x81, body.length]), body]));
    }
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  try {
    await writeFile(path.join(temporary, 'bridge-connection.json'),
      JSON.stringify({ version: 2, port: server.address().port, token: key }));
    const native = nativePort();
    const messages = [];
    native.onMessage.addListener(message => messages.push(message));
    await waitUntil(() => native.child.exitCode !== null, `native host rejects ${proofMode} backend proof`);
    assert.equal(receivedClientProof, true);
    assert.equal(native.child.exitCode, 1);
    assert.equal(messages.length, 1, 'No server commands may reach the browser before authentication.');
    assert.equal(messages[0].type, 'hostError');
    assert.equal(logs.join('').includes(key), false);
  } finally {
    for (const socket of sockets) socket.destroy();
    await new Promise(resolve => server.close(resolve));
  }
}

function zipFixture() {
  const name = Buffer.from('main.tex');
  const data = Buffer.from('\\documentclass{article}\n\\begin{document}Synthetic research only.\\end{document}\n');
  let crc = 0xffffffff;
  for (const byte of data) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ ((crc & 1) ? 0xedb88320 : 0);
  }
  crc = (crc ^ 0xffffffff) >>> 0;
  const local = Buffer.alloc(30);
  local.writeUInt32LE(0x04034b50);
  local.writeUInt16LE(20, 4);
  local.writeUInt16LE(33, 12);
  local.writeUInt32LE(crc, 14);
  local.writeUInt32LE(data.length, 18);
  local.writeUInt32LE(data.length, 22);
  local.writeUInt16LE(name.length, 26);
  const central = Buffer.alloc(46);
  central.writeUInt32LE(0x02014b50);
  central.writeUInt16LE(20, 4);
  central.writeUInt16LE(20, 6);
  central.writeUInt16LE(33, 14);
  central.writeUInt32LE(crc, 16);
  central.writeUInt32LE(data.length, 20);
  central.writeUInt32LE(data.length, 24);
  central.writeUInt16LE(name.length, 28);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50);
  end.writeUInt16LE(1, 8);
  end.writeUInt16LE(1, 10);
  end.writeUInt32LE(central.length + name.length, 12);
  end.writeUInt32LE(local.length + name.length + data.length, 16);
  return Buffer.concat([local, name, data, central, name, end]);
}

try {
  const listener = createServer();
  await new Promise(resolve => listener.listen(0, '127.0.0.1', resolve));
  const port = listener.address().port;
  await new Promise(resolve => listener.close(resolve));
  await launchBackend(port);

  const page = await fetch(baseUrl);
  assert.equal(page.status, 200);
  assert.doesNotMatch(await page.text(), /\bonload\s*=/i, 'The built UI must not need inline handlers blocked by its CSP.');
  const schema = await (await call('/openapi/v1.json')).json();
  assert.ok(schema.paths['/api/overleaf/projects/{projectId}/downloads'].post);
  assert.ok(schema.paths['/api/extension'].get);
  const setup = await (await call('/api/extension')).json();
  assert.deepEqual(setup, {
    extensionId, extensionVersion: '0.5.2', protocolVersion: 2,
    developmentDirectory: path.join(repo, 'browser', 'overleaf'),
    storeUrl: `https://chromewebstore.google.com/detail/${extensionId}`, storePublished: false,
  });
  assert.deepEqual(await (await call('/api/status')).json(), {
    connected: false, busy: false, protocolVersion: 2, connectionVersion: null, dummy: false,
  });
  assert.equal((await call('/native')).status, 401);
  assert.equal((await call('/api/status', { headers: { Origin: 'https://untrusted.invalid' } })).status, 403);
  const rebindingStatus = await new Promise((resolve, reject) => {
    const request = httpRequest(baseUrl + '/api/status', { headers: { Host: `untrusted.invalid:${port}` } }, result => {
      result.resume();
      resolve(result.statusCode);
    });
    request.on('error', reject);
    request.end();
  });
  assert.equal(rebindingStatus, 400);
  assert.equal((await call('/api/overleaf/projects/refresh', { method: 'POST', protectedRequest: false })).status, 403);
  const disconnected = await call('/api/overleaf/projects/refresh', { method: 'POST' });
  assert.equal(disconnected.status, 503);
  assert.match(disconnected.headers.get('content-type'), /application\/problem\+json/);

  const projectId = '0123456789abcdef01234567';
  const projectName = 'PRIVATE-SENTINEL-SYNTHETIC-PROJECT';
  const metadata = JSON.stringify({ projects: [{ id: projectId, name: projectName, archived: false, trashed: false, lastUpdated: '2026-10-01T00:00:00Z' }] })
    .replaceAll('&', '&amp;').replaceAll('"', '&quot;');
  const html = `<html><head><meta name="ol-prefetchedProjectsBlob" content="${metadata}"></head></html>`;
  const zip = zipFixture();
  let mode = 'success';
  let clock = 0;
  harness = browserHarness({
    nativePort,
    now: () => clock,
    fetch: async url => {
      if (mode === 'rateLimit') return response('Synthetic limit', { status: 429 });
      if (mode === 'login') return response('<form action="/login"></form>', { url: 'https://www.overleaf.com/login' });
      if (mode === 'badPage') return response('<html>Synthetic unsupported page</html>');
      if (mode === 'badZip') return response('Not a ZIP', { type: 'application/zip' });
      if (mode === 'oversize') return response('Too big', { headers: { 'content-length': String(8 * 1024 * 1024 + 1) } });
      return url.endsWith('/download/zip') ? response(zip, { type: 'application/zip' }) : response(html);
    },
  });
  await waitUntil(async () => (await harness.send({ type: 'status' })).connected, 'automatic native connection');
  const firstNative = children.at(-1);
  const firstStatus = await (await call('/api/status')).json();
  assert.equal(firstStatus.connected, true);
  assert.equal(typeof firstStatus.connectionVersion, 'string');

  const secondNative = nativePort();
  const secondMessages = [];
  secondNative.onMessage.addListener(message => secondMessages.push(message));
  await waitUntil(() => secondMessages.some(message => message.type === 'hostError'), 'second-browser rejection');
  assert.equal(secondMessages.find(message => message.type === 'hostError').error, 'connection_in_use');
  assert.equal((await (await call('/api/status')).json()).connectionVersion, firstStatus.connectionVersion);

  await harness.external({ type: 'signIn' });
  await waitUntil(async () => (await (await call('/api/status')).json()).connectionVersion !== firstStatus.connectionVersion,
    'sign-in completion changes connection revision');
  const refreshed = await call('/api/overleaf/projects/refresh', { method: 'POST' });
  assert.equal(refreshed.status, 200);
  assert.equal((await refreshed.json()).projects.length, 1);

  for (const [testMode, status, code] of [
    ['login', 409, 'overleaf_sign_in_required'],
    ['rateLimit', 429, 'overleaf_rate_limited'],
    ['badPage', 502, 'overleaf_project_list_unavailable'],
    ['oversize', 413, 'response_too_large'],
  ]) {
    mode = testMode;
    const failed = await call('/api/overleaf/projects/refresh', { method: 'POST' });
    assert.equal(failed.status, status);
    assert.equal((await failed.json()).code, code);
  }
  mode = 'badZip';
  assert.equal((await call(`/api/overleaf/projects/${projectId}/downloads`, { method: 'POST' })).status, 502);
  assert.equal((await (await call('/api/overleaf/downloads')).json()).downloads.length, 0);
  mode = 'success';
  const downloaded = await call(`/api/overleaf/projects/${projectId}/downloads`, { method: 'POST' });
  assert.equal(downloaded.status, 201);
  const snapshot = await downloaded.json();
  assert.equal(downloaded.headers.get('location'), `/api/overleaf/downloads/${snapshot.id}`);
  assert.equal(snapshot.files.length, 1);
  assert.equal(snapshot.files[0].path, 'main.tex');
  const saved = await call(`/api/overleaf/downloads/${snapshot.id}/archive`);
  assert.deepEqual(Buffer.from(await saved.arrayBuffer()), zip);
  assert.equal((await readdir(path.join(temporary, 'incoming'))).length, 0);

  const connection = JSON.parse(await readFile(path.join(temporary, 'bridge-connection.json'), 'utf8'));
  assert.equal(logs.join('').includes(connection.token), false, 'Credentials must not be logged.');
  assert.equal(logs.join('').includes(projectName), false, 'Project names must not be logged.');

  await stop(backend);
  await waitUntil(() => firstNative.exitCode !== null, 'native host exits when the backend stops');
  await waitUntil(async () => !(await harness.send({ type: 'status' })).connected, 'extension sees backend stop');
  await launchBackend(port);
  const persisted = await (await call('/api/overleaf/downloads')).json();
  assert.equal(persisted.downloads.length, 1);
  assert.equal(persisted.downloads[0].id, snapshot.id);
  assert.equal((await (await call('/api/status')).json()).connected, false);
  clock = 60000;
  harness.chrome.alarms.onAlarm.emit({ name: 'meld-native-reconnect' });
  await waitUntil(async () => (await harness.send({ type: 'status' })).connected, 'automatic connection after backend restart');
  const restartedStatus = await (await call('/api/status')).json();
  assert.notEqual(restartedStatus.connectionVersion, firstStatus.connectionVersion);
  assert.equal((await call('/api/overleaf/projects/refresh', { method: 'POST' })).status, 200);
  const restartedNative = children.at(-1);
  await harness.external({ type: 'disconnect' });
  await waitUntil(() => restartedNative.exitCode !== null, 'native host exits after pause');
  await waitUntil(async () => !(await (await call('/api/status')).json()).connected, 'backend sees pause');
  assert.equal((await (await call('/api/status')).json()).connectionVersion, null);
  await stop(backend);
  for (const proofMode of ['missing', 'reflection', 'wrongNonce', 'wrongKey'])
    await rejectImpersonatedBackend(proofMode);
  console.log('PASS: actual extension scripts -> native messaging process -> C# API -> validated, persistent ZIP.');
  console.log('PASS: origin/host/CSRF protection, native authentication, error statuses, cleanup, restart persistence, and redacted logs.');
  console.log('PASS: automatic connection/reconnection, single-browser ownership, sign-in notification, setup metadata, and persistent pause.');
  console.log('PASS: native host blocks missing, reflected, wrong-nonce, and wrong-key backend proofs before forwarding commands.');
  console.log('Synthetic fixtures only; no browser profile, Overleaf account, remote backend, or LLM was contacted.');
} finally {
  if (harness) await harness.close();
  for (const child of children.reverse()) await stop(child);
  assert.ok(temporary.startsWith(path.join(tmpdir(), 'meldresearch-e2e-')));
  await rm(temporary, { recursive: true });
}

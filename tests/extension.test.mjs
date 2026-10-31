import assert from 'node:assert/strict';
import test from 'node:test';
import { browserHarness, portPair, response, waitUntil } from './browser-harness.mjs';

async function connected(fetchResponse, { acknowledge = true, ...options } = {}) {
  const [extension, host] = portPair('native');
  const messages = [];
  host.onMessage.addListener(message => {
    messages.push(message);
    if (acknowledge && message.type === 'chunk')
      host.postMessage({ type: 'ack', id: message.id, sequence: message.sequence });
  });
  const harness = browserHarness({
    nativePort: () => {
      queueMicrotask(() => host.postMessage({ type: 'ready', version: 2, localUrl: 'http://127.0.0.1:6010' }));
      return extension;
    },
    fetch: fetchResponse,
    ...options,
  });
  await waitUntil(async () => (await harness.send({ type: 'status' })).connected, 'automatic native connection');
  return { harness, host, messages };
}

const requestId = 'a'.repeat(32);
const projectId = '0123456789abcdef01234567';

test('lists through a same-origin GET and exports no page tokens or extra metadata', async () => {
  const project = { id: projectId, name: 'Synthetic & <paper>', archived: false, trashed: false, owner: { email: 'not-exported@example.invalid' } };
  const blob = JSON.stringify({ projects: [project] }).replaceAll('&', '&amp;').replaceAll('"', '&quot;');
  const html = `<meta name="ol-csrfToken" content="CSRF-SENTINEL"><meta name="ol-prefetchedProjectsBlob" content="${blob}">`;
  const { harness, host, messages } = await connected(async () => response(html));
  try {
    host.postMessage({ type: 'request', id: requestId, operation: 'listProjects' });
    await waitUntil(() => messages.some(message => message.type === 'complete'), 'project response');
    const request = harness.requests[0];
    assert.equal(request.url, 'https://www.overleaf.com/');
    assert.equal(request.options.method, 'GET');
    assert.equal(request.options.credentials, 'include');
    assert.equal(request.options.cache, 'no-store');
    const exported = Buffer.concat(messages.filter(message => message.type === 'chunk')
      .map(message => Buffer.from(message.data, 'base64'))).toString();
    assert.equal(exported.includes('CSRF-SENTINEL'), false);
    assert.equal(exported.includes('not-exported@example.invalid'), false);
    assert.deepEqual(JSON.parse(exported).projects, [{ id: projectId, name: project.name, archived: false, trashed: false }]);
  } finally { await harness.close(); }
});

test('reads only paths and last-edit times from Overleaf history, paging back, and drops user details', async () => {
  const pages = [
    { updates: [
      { meta: { end_ts: 3000, users: [{ email: 'collaborator@example.invalid' }] }, pathnames: ['main.tex'], project_ops: [] },
      { meta: { end_ts: 2000 }, pathnames: ['main.tex', 'sections/a.tex'], project_ops: [{ add: { pathname: '/fig.tex' } }] },
    ], nextBeforeTimestamp: 7 },
    { updates: [{ meta: { end_ts: 1000 }, pathnames: ['old.tex'], project_ops: [] }] },
  ];
  let page = 0;
  const { harness, host, messages } = await connected(async () =>
    response(JSON.stringify(pages[page++]), { type: 'application/json', url: `https://www.overleaf.com/project/${projectId}/updates` }));
  try {
    host.postMessage({ type: 'request', id: requestId, operation: 'projectHistory', projectId });
    await waitUntil(() => messages.some(message => message.type === 'complete'), 'history response');
    assert.deepEqual(harness.requests.map(request => request.url), [
      `https://www.overleaf.com/project/${projectId}/updates?min_count=100`,
      `https://www.overleaf.com/project/${projectId}/updates?min_count=100&before=7`,
    ]);
    const exported = Buffer.concat(messages.filter(message => message.type === 'chunk')
      .map(message => Buffer.from(message.data, 'base64'))).toString();
    assert.equal(exported.includes('collaborator@example.invalid'), false);
    assert.deepEqual(JSON.parse(exported), {
      paths: { 'main.tex': 3000, 'sections/a.tex': 2000, 'fig.tex': 2000, 'old.tex': 1000 }, complete: true,
    });
  } finally { await harness.close(); }
});

test('streams binary downloads with acknowledgement backpressure', async () => {
  const bytes = Uint8Array.from({ length: 130000 }, (_, index) => index % 251);
  const { harness, host, messages } = await connected(async () => response(bytes, { type: 'application/zip' }),
    { acknowledge: false });
  try {
    host.postMessage({ type: 'request', id: requestId, operation: 'downloadProject', projectId });
    await waitUntil(() => messages.some(message => message.type === 'chunk'), 'first chunk');
    await new Promise(resolve => setTimeout(resolve, 40));
    assert.equal(messages.filter(message => message.type === 'chunk').length, 1);
    assert.equal(messages[1].sequence, 0);
    assert.equal(Buffer.from(messages[1].data, 'base64').length, 65536);
    host.postMessage({ type: 'ack', id: requestId, sequence: 0 });
    await waitUntil(() => messages.filter(message => message.type === 'chunk').length === 2, 'second chunk');
    host.postMessage({ type: 'ack', id: requestId, sequence: 1 });
    await waitUntil(() => messages.some(message => message.type === 'complete'), 'download completion');
    const actual = Buffer.concat(messages.filter(message => message.type === 'chunk').map(message => Buffer.from(message.data, 'base64')));
    assert.deepEqual(actual, Buffer.from(bytes));
    assert.equal(harness.requests[0].url, `https://www.overleaf.com/project/${projectId}/download/zip`);
  } finally { await harness.close(); }
});

for (const [name, createResponse, expected] of [
  ['login redirect', () => response('Sign in', { url: 'https://www.overleaf.com/login' }), 'sign_in_required'],
  ['cross-origin redirect', () => response('Sign in', { url: 'https://accounts.google.com/' }), 'sign_in_required'],
  ['rate limit', () => response('Rate limited', { status: 429 }), 'http_429'],
  ['declared oversized page', () => response('Synthetic', { headers: { 'content-length': String(8 * 1024 * 1024 + 1) } }), 'response_too_large'],
  ['actual oversized page', () => response(new Uint8Array(8 * 1024 * 1024 + 1)), 'response_too_large'],
  ['empty response', () => response(''), 'invalid_response'],
]) {
  test(`reports ${name} instead of success`, async () => {
    const { harness, host, messages } = await connected(async () => createResponse());
    try {
      host.postMessage({ type: 'request', id: requestId, operation: 'listProjects' });
      await waitUntil(() => messages.some(message => message.type === 'error'), name);
      assert.equal(messages.find(message => message.type === 'error').error, expected);
      assert.equal(messages.some(message => message.type === 'complete'), false);
    } finally { await harness.close(); }
  });
}

test('rejects arbitrary project paths before any browser request', async () => {
  const { harness, host } = await connected(async () => { throw new Error('Must not fetch.'); });
  try {
    let disconnected = false;
    host.onDisconnect.addListener(() => { disconnected = true; });
    host.postMessage({ type: 'request', id: requestId, operation: 'downloadProject', projectId: '../../login' });
    await waitUntil(() => disconnected, 'invalid request disconnection');
    assert.equal(harness.requests.length, 0);
    const state = await harness.send({ type: 'status' });
    assert.equal(state.connected, false);
    assert.match(state.error, /unsupported request/);
  } finally { await harness.close(); }
});

test('does not connect without explicit Overleaf permission', async () => {
  let started = false;
  const harness = browserHarness({ nativePort: () => { started = true; }, fetch: async () => response(''), permitted: false });
  assert.equal((await harness.send({ type: 'connect' })).connected, false);
  assert.equal(started, false);
});

test('disconnect cancels an active browser fetch', async () => {
  let cancelled = false;
  const { harness, host } = await connected((_url, options) => new Promise((_resolve, reject) => {
    options.signal.addEventListener('abort', () => {
      cancelled = true;
      reject(new Error('Synthetic cancellation.'));
    });
  }));
  host.postMessage({ type: 'request', id: requestId, operation: 'listProjects' });
  await waitUntil(() => harness.requests.length === 1, 'active fetch');
  await harness.close();
  await waitUntil(() => cancelled, 'cancelled fetch');
});

test('automatically connects without a popup and creates a repeating reconnect alarm', async () => {
  const { harness } = await connected(async () => response(''));
  try {
    const state = await harness.external({ type: 'ensure' });
    assert.equal(state.connected, true);
    assert.equal(state.enabled, true);
    assert.equal(state.protocolVersion, 2);
    assert.equal(state.extensionVersion, '0.5.3');
    assert.equal(harness.alarms.get('meld-native-reconnect').periodInMinutes, 1);
    assert.equal(harness.requests.length, 0, 'Connecting alone must not read Overleaf.');
    assert.equal(harness.tabActions.length, 0, 'Passive startup must not steal focus.');
  } finally { await harness.close(); }
});

test('creates a missing Overleaf tab in the background for reads', async () => {
  const { harness, host, messages } = await connected(async () =>
    response(`<meta name="ol-prefetchedProjectsBlob" content='{"projects":[]}'>`), { tabs: [] });
  try {
    host.postMessage({ type: 'request', id: requestId, operation: 'listProjects' });
    await waitUntil(() => messages.some(message => message.type === 'complete'), 'background tab read');
    assert.deepEqual(harness.tabActions, [{ type: 'create', url: 'https://www.overleaf.com/project', active: false }]);
    assert.equal(harness.windowActions.length, 0);
  } finally { await harness.close(); }
});

test('deduplicates startup and page connection requests while native readiness is pending', async () => {
  const [extension, host] = portPair();
  let attempts = 0;
  const harness = browserHarness({ nativePort: () => { attempts++; return extension; }, fetch: async () => response('') });
  try {
    await Promise.all(Array.from({ length: 8 }, () => harness.external({ type: 'ensure' })));
    assert.equal(attempts, 1);
    assert.equal((await harness.external({ type: 'status' })).connecting, true);
    host.postMessage({ type: 'ready', version: 2, localUrl: 'http://127.0.0.1:6010' });
    await waitUntil(async () => (await harness.external({ type: 'status' })).connected, 'delayed readiness');
  } finally { await harness.close(); }
});

test('respects retry cooldown and reconnects when the periodic alarm fires', async () => {
  let time = 0;
  let attempts = 0;
  const harness = browserHarness({
    nativePort: () => {
      attempts++;
      const [extension, host] = portPair();
      queueMicrotask(() => host.postMessage(attempts === 1
        ? { type: 'hostError', error: 'backend_unavailable' }
        : { type: 'ready', version: 2, localUrl: 'http://127.0.0.1:6010' }));
      return extension;
    },
    fetch: async () => response(''), now: () => time,
  });
  try {
    await waitUntil(async () => (await harness.send({ type: 'status' })).errorCode === 'backend_unavailable', 'initial failure');
    await harness.external({ type: 'ensure' });
    assert.equal(attempts, 1);
    time = 60000;
    harness.chrome.alarms.onAlarm.emit({ name: 'meld-native-reconnect' });
    await waitUntil(async () => (await harness.send({ type: 'status' })).connected, 'alarm reconnect');
    assert.equal(attempts, 2);
  } finally { await harness.close(); }
});

test('remembers a deliberate pause across service-worker restarts and resumes only explicitly', async () => {
  const storage = {};
  const { harness } = await connected(async () => response(''), { storage });
  await harness.external({ type: 'disconnect' });
  assert.equal(storage.connectionEnabled, false);
  assert.equal(harness.alarms.size, 0);
  harness.dispose();

  let attempts = 0;
  const restarted = browserHarness({
    storage, fetch: async () => response(''),
    nativePort: () => {
      attempts++;
      const [extension, host] = portPair();
      queueMicrotask(() => host.postMessage({ type: 'ready', version: 2, localUrl: 'http://127.0.0.1:6010' }));
      return extension;
    },
  });
  try {
    await restarted.external({ type: 'ensure' });
    restarted.chrome.runtime.onStartup.emit();
    restarted.chrome.alarms.onAlarm.emit({ name: 'meld-native-reconnect' });
    await restarted.send({ type: 'status' });
    assert.equal(attempts, 0);
    assert.equal((await restarted.external({ type: 'status' })).enabled, false);
    await restarted.external({ type: 'connect' });
    await waitUntil(async () => (await restarted.send({ type: 'status' })).connected, 'explicit resume');
    assert.equal(storage.connectionEnabled, true);
  } finally { await restarted.close(); }
});

test('a pause prevents a stale permission check from opening a native connection', async () => {
  let allow;
  let attempts = 0;
  const permission = new Promise(resolve => { allow = resolve; });
  const harness = browserHarness({
    permitted: () => permission, nativePort: () => { attempts++; throw new Error('Must not connect.'); },
    fetch: async () => response(''),
  });
  try {
    await waitUntil(async () => (await harness.send({ type: 'status' })).connecting, 'pending permission check');
    await harness.external({ type: 'disconnect' });
    allow(true);
    await new Promise(resolve => setTimeout(resolve, 20));
    assert.equal(attempts, 0);
    assert.equal((await harness.external({ type: 'status' })).enabled, false);
  } finally { await harness.close(); }
});

for (const sender of [
  { url: 'https://untrusted.invalid/', frameId: 0 },
  { url: 'http://127.0.0.1:6050/', frameId: 0 },
  { url: 'http://localhost:6000/', frameId: 0 },
  { url: 'https://localhost:6010/', frameId: 0 },
  { url: 'http://localhost:6010/', frameId: 2 },
  { url: 'http://localhost:6010/', id: 'another-extension', frameId: 0 },
  { url: 'http://localhost.evil.invalid:6010/', frameId: 0 },
]) {
  test(`rejects untrusted external controls: ${sender.url}, frame ${sender.frameId}, id ${sender.id ?? 'page'}`, async () => {
    const { harness } = await connected(async () => response(''));
    try {
      const result = await harness.external({ type: 'disconnect' }, sender);
      assert.equal(result.error, 'unauthorized_request');
      assert.equal(Object.keys(result).length, 1, 'Do not disclose connector state to untrusted senders.');
      assert.equal((await harness.send({ type: 'status' })).connected, true);
    } finally { await harness.close(); }
  });
}

test('accepts localhost on both application ports, but never external data requests or arbitrary URLs', async () => {
  const { harness } = await connected(async () => response(''));
  try {
    for (const port of [6001, 6010])
      assert.equal((await harness.external({ type: 'status' }, { url: `http://localhost:${port}/`, frameId: 0 })).connected, true);
    for (const message of [{ type: 'request', operation: 'listProjects' }, { type: 'signIn', url: 'https://untrusted.invalid/' }, { type: 'openApp' }])
      assert.equal((await harness.external(message)).error, 'unauthorized_request');
    assert.equal(harness.requests.length, 0);
    assert.equal(harness.tabActions.length, 0);
  } finally { await harness.close(); }
});

test('explicit sign-in focuses the normal tab and notifies Meld after login navigation completes', async () => {
  const { harness, messages } = await connected(async () => response(''), {
    tabs: [{ id: 3, windowId: 4, incognito: false, active: false, status: 'complete', url: 'https://www.overleaf.com/login' }],
  });
  try {
    const signingIn = await harness.external({ type: 'signIn' });
    assert.equal(signingIn.signInPending, true);
    assert.deepEqual(harness.tabActions, [{ type: 'update', id: 3, active: true }]);
    assert.deepEqual(harness.windowActions, [{ id: 4, focused: true }]);
    harness.updateTab(3, { status: 'complete', url: 'https://accounts.google.com/' });
    assert.equal(messages.length, 0);
    harness.updateTab(3, { status: 'complete', url: 'https://www.overleaf.com/project' });
    await waitUntil(() => messages.some(message => message.type === 'overleafChanged'), 'sign-in notification');
    harness.updateTab(3, { status: 'complete' });
    assert.equal(messages.filter(message => message.type === 'overleafChanged').length, 1);
    assert.equal((await harness.external({ type: 'status' })).signInPending, false);
  } finally { await harness.close(); }
});

test('sign-in completion also handles a tab that was already loaded', async () => {
  const { harness, messages } = await connected(async () => response(''));
  try {
    await harness.external({ type: 'signIn' });
    await waitUntil(() => messages.some(message => message.type === 'overleafChanged'), 'already-loaded tab notification');
  } finally { await harness.close(); }
});

test('reports a closed sign-in tab and never treats it as authentication success', async () => {
  const { harness, messages } = await connected(async () => response(''), {
    tabs: [{ id: 3, windowId: 1, incognito: false, status: 'complete', url: 'https://www.overleaf.com/login' }],
  });
  try {
    await harness.external({ type: 'signIn' });
    harness.chrome.tabs.onRemoved.emit(3);
    const state = await harness.external({ type: 'status' });
    assert.equal(state.errorCode, 'sign_in_closed');
    assert.equal(state.signInPending, false);
    assert.equal(messages.length, 0);
  } finally { await harness.close(); }
});

test('the popup learns Overleaf sign-in from the final URL only, caches it, and Meld pages cannot trigger it', async () => {
  let signedIn = false;
  const { harness } = await connected(async () => response(''), {
    overleafFetch: async () => signedIn ? response('<p>BODY-SENTINEL</p>')
      : response('', { url: 'https://www.overleaf.com/login?redir=%2Fproject' }),
  });
  try {
    assert.equal((await harness.send({ type: 'status' })).overleafSignedIn, null);
    assert.equal((await harness.external({ type: 'popup' })).error, 'unauthorized_request');
    assert.equal(harness.backgroundRequests.length, 0, 'Meld pages and status polling must not contact Overleaf.');
    assert.equal((await harness.send({ type: 'popup', refresh: true })).overleafSignedIn, false);
    const [request] = harness.backgroundRequests;
    assert.equal(request.url, 'https://www.overleaf.com/project');
    assert.equal(request.options.method, 'GET');
    assert.equal(request.options.credentials, 'include');
    signedIn = true;
    assert.equal((await harness.send({ type: 'popup' })).overleafSignedIn, false, 'Polling reuses a recent result.');
    assert.equal(harness.backgroundRequests.length, 1);
    const state = await harness.send({ type: 'popup', refresh: true });
    assert.equal(state.overleafSignedIn, true);
    assert.equal(JSON.stringify(state).includes('BODY-SENTINEL'), false);
    assert.equal(harness.requests.length, 0, 'The check does not read projects.');
  } finally { await harness.close(); }
});

test('an unreachable Overleaf is reported as unknown, never as signed in', async () => {
  for (const overleafFetch of [async () => { throw new TypeError('offline'); },
    async () => response('', { status: 503 })]) {
    const { harness } = await connected(async () => response(''), { overleafFetch });
    try {
      assert.equal((await harness.send({ type: 'popup', refresh: true })).overleafSignedIn, null);
    } finally { await harness.close(); }
  }
});

test('completed sign-in marks Overleaf as signed in for the popup', async () => {
  const { harness } = await connected(async () => response(''));
  try {
    await harness.external({ type: 'signIn' });
    assert.equal((await harness.send({ type: 'popup' })).overleafSignedIn, true);
    assert.equal(harness.backgroundRequests.length, 0);
  } finally { await harness.close(); }
});

test('the Meld page can ask the connector to reload into an updated copy, but untrusted pages cannot', async () => {
  const { harness } = await connected(async () => response(''));
  try {
    assert.equal((await harness.external({ type: 'reload' }, { url: 'https://evil.invalid/', frameId: 0 })).error, 'unauthorized_request');
    await new Promise(resolve => setTimeout(resolve, 200));
    assert.equal(harness.reloads.length, 0);
    const state = await harness.external({ type: 'reload' });
    assert.equal(state.extensionVersion, '0.5.3', 'The reply is sent before the extension reloads.');
    await waitUntil(() => harness.reloads.length === 1, 'extension reload');
  } finally { await harness.close(); }
});

test('surfaces another-browser conflicts without taking over that connection', async () => {
  const [extension, host] = portPair();
  const harness = browserHarness({
    nativePort: () => {
      queueMicrotask(() => host.postMessage({ type: 'hostError', error: 'connection_in_use' }));
      return extension;
    },
    fetch: async () => response(''),
  });
  try {
    await waitUntil(async () => (await harness.send({ type: 'status' })).errorCode === 'connection_in_use', 'browser conflict');
    assert.equal((await harness.external({ type: 'status' })).connected, false);
    assert.equal(harness.requests.length, 0);
  } finally { await harness.close(); }
});

test('fails closed with a visible error when persisted preferences cannot be read', async () => {
  let attempts = 0;
  const harness = browserHarness({
    storageError: true, nativePort: () => { attempts++; }, fetch: async () => response(''),
  });
  try {
    const state = await harness.external({ type: 'ensure' });
    assert.equal(state.errorCode, 'settings_unavailable');
    assert.equal(state.enabled, false);
    assert.equal(attempts, 0);
    assert.equal(harness.warnings.length, 1);
  } finally { harness.dispose(); }
});

test('a fresh installation opens Meld once, while worker startup does not open another tab', async () => {
  const { harness } = await connected(async () => response(''));
  try {
    harness.chrome.runtime.onInstalled.emit({ reason: 'install' });
    await waitUntil(() => harness.tabActions.length === 1, 'return to Meld after installation');
    assert.deepEqual(harness.tabActions[0], { type: 'create', url: 'http://127.0.0.1:6010', active: true });
    harness.chrome.runtime.onStartup.emit();
    harness.chrome.runtime.onInstalled.emit({ reason: 'update' });
    await harness.send({ type: 'status' });
    assert.equal(harness.tabActions.length, 1);
  } finally { await harness.close(); }
});

test('permission revocation stops access and restoring it reconnects without opening a tab', async () => {
  let permitted = true;
  let attempts = 0;
  const harness = browserHarness({
    permitted: () => permitted, fetch: async () => response(''),
    nativePort: () => {
      attempts++;
      const [extension, host] = portPair();
      queueMicrotask(() => host.postMessage({ type: 'ready', version: 2, localUrl: 'http://127.0.0.1:6010' }));
      return extension;
    },
  });
  try {
    await waitUntil(async () => (await harness.send({ type: 'status' })).connected, 'initial connection');
    permitted = false;
    harness.chrome.permissions.onRemoved.emit({ origins: ['https://www.overleaf.com/*'] });
    await waitUntil(async () => (await harness.send({ type: 'status' })).errorCode === 'permission_required', 'permission revocation');
    assert.equal((await harness.send({ type: 'status' })).connected, false);
    permitted = true;
    harness.chrome.permissions.onAdded.emit({ origins: ['https://www.overleaf.com/*'] });
    await waitUntil(async () => (await harness.send({ type: 'status' })).connected, 'permission restoration');
    assert.equal(attempts, 2);
    assert.equal(harness.tabActions.length, 0);
  } finally { await harness.close(); }
});

test('rejects an incompatible native protocol before any Overleaf request', async () => {
  const [extension, host] = portPair();
  const harness = browserHarness({
    nativePort: () => {
      queueMicrotask(() => host.postMessage({ type: 'ready', version: 1, localUrl: 'http://127.0.0.1:6010' }));
      return extension;
    },
    fetch: async () => { throw new Error('Must not read.'); },
  });
  try {
    await waitUntil(async () => (await harness.send({ type: 'status' })).errorCode === 'protocol_mismatch', 'version mismatch');
    assert.equal((await harness.send({ type: 'status' })).connected, false);
    assert.equal(harness.requests.length, 0);
  } finally { await harness.close(); }
});

test('clears a tab-access error after a successful explicit retry', async () => {
  const { harness, host, messages } = await connected(async () =>
    response(`<meta name="ol-prefetchedProjectsBlob" content='{"projects":[]}'>`));
  const inject = harness.chrome.scripting.executeScript;
  try {
    harness.chrome.scripting.executeScript = async () => { throw new Error('Synthetic tab navigation.'); };
    host.postMessage({ type: 'request', id: requestId, operation: 'listProjects' });
    await waitUntil(() => messages.some(message => message.type === 'error'), 'first read failure');
    assert.equal((await harness.send({ type: 'status' })).errorCode, 'tab_unavailable');
    harness.chrome.scripting.executeScript = inject;
    host.postMessage({ type: 'request', id: 'b'.repeat(32), operation: 'listProjects' });
    await waitUntil(() => messages.some(message => message.type === 'complete'), 'successful retry');
    assert.equal((await harness.send({ type: 'status' })).error, '');
  } finally { await harness.close(); }
});

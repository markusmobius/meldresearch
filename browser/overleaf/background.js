const hostName = 'org.meldresearch.overleaf';
const overleafOrigin = 'https://www.overleaf.com';
const protocolVersion = 2;
const retryAlarm = 'meld-native-reconnect';
// Reads run in the connector's own background Overleaf tab, never in a tab the user works in, so switching projects or
// reloading Overleaf cannot break a read. It is closed again once reads have been idle this long.
const readerAlarm = 'meld-reader-close';
const readerIdleMs = 2 * 60 * 1000;
let readerTabId;
let nativePort = null;
let contentPort = null;
let requestId = null;
let localUrl = null;
let errorCode = '';
let connecting = false;
let enabled = false;
let initialized = false;
let generation = 0;
let connectionTimer = null;
let retryAfter = 0;
let signInTabId = null;
let openAfterConnect = false;
let overleafSignedIn = null;
let overleafCheckedAt = 0;

const messages = {
  backend_unavailable: 'Waiting for Meld. Start or restart the local application; reconnection is automatic.',
  host_unavailable: 'The native bridge is unavailable. Run the Meld launcher or installer to register it.',
  connection_in_use: 'Another browser is connected. Pause the connector from Meld in that browser first.',
  permission_required: 'Allow this connector to access Overleaf in your browser extension settings.',
  protocol_mismatch: 'Update or reload the connector and restart Meld so their versions match.',
  invalid_request: 'Meld sent an unsupported request. Update the connector and local application together.',
  tab_unavailable: 'Could not open Overleaf in this browser. Check that Overleaf loads, then retry; Meld also retries automatically.',
  tab_closed: 'The Overleaf tab Meld reads through was closed during a read. Meld retries automatically.',
  tab_navigated: 'The Overleaf tab Meld reads through moved to another page during a read. Meld retries automatically.',
  tab_discarded: 'The browser put the Overleaf tab Meld reads through to sleep. Meld retries automatically.',
  sign_in_required: 'Overleaf asked to sign in. Choose Sign in to Overleaf and finish signing in.',
  sign_in_closed: 'The sign-in tab was closed. Choose Sign in to Overleaf to try again.',
  settings_unavailable: 'Could not save connector settings. Reload the extension; disable it in the browser if you need to stop access.',
  extension_error: 'The connector could not complete the action. Reload it and try again.',
};

function state() {
  const connected = nativePort !== null && localUrl !== null;
  return {
    extensionVersion: chrome.runtime.getManifest().version,
    protocolVersion,
    connected,
    enabled,
    connecting,
    localUrl,
    signInPending: signInTabId !== null,
    overleafSignedIn,
    errorCode: errorCode || null,
    error: messages[errorCode] ?? '',
  };
}

function stopContent() {
  requestId = null;
  const port = contentPort;
  contentPort = null;
  port?.disconnect();
}

function disconnect() {
  generation++;
  clearTimeout(connectionTimer);
  connectionTimer = null;
  connecting = false;
  stopContent();
  const port = nativePort;
  nativePort = null;
  localUrl = null;
  port?.disconnect();
}

function failConnection(code, delay = 10000) {
  disconnect();
  errorCode = code;
  retryAfter = Date.now() + delay;
}

function postNative(message) {
  if (!nativePort) return false;
  try {
    nativePort.postMessage(message);
    return true;
  } catch {
    failConnection('backend_unavailable');
    return false;
  }
}

async function configureRetry() {
  if (!enabled) await chrome.alarms.clear(retryAlarm);
  else if (!await chrome.alarms.get(retryAlarm))
    await chrome.alarms.create(retryAlarm, { periodInMinutes: 1 });
}

async function initialize() {
  try {
    const saved = await chrome.storage.local.get('connectionEnabled');
    enabled = saved.connectionEnabled !== false;
    await configureRetry();
    initialized = true;
  } catch {
    enabled = false;
    errorCode = 'settings_unavailable';
    console.warn('Meld: connector preferences or retry scheduling are unavailable.');
  }
}

async function setEnabled(value) {
  enabled = value;
  retryAfter = 0;
  errorCode = '';
  if (!value) {
    signInTabId = null;
    disconnect();
  }
  try {
    await chrome.storage.local.set({ connectionEnabled: value });
    await configureRetry();
    return true;
  } catch {
    enabled = false;
    failConnection('settings_unavailable');
    console.warn('Meld: could not persist the auto-connect preference.');
    return false;
  }
}

function waitForTab(tabId) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => finish(new Error('Open Overleaf and finish loading its page, then retry.')), 30000);
    const listener = (id, change) => {
      if (id === tabId && change.status === 'complete') finish();
    };
    const removed = (id) => {
      if (id === tabId) finish(new Error('The Overleaf tab was closed.'));
    };
    function finish(error) {
      clearTimeout(timer);
      chrome.tabs.onUpdated.removeListener(listener);
      chrome.tabs.onRemoved.removeListener(removed);
      error ? reject(error) : resolve();
    }
    chrome.tabs.onUpdated.addListener(listener);
    chrome.tabs.onRemoved.addListener(removed);
    chrome.tabs.get(tabId).then(tab => {
      if (tab.status === 'complete') finish();
    }, () => finish(new Error('The Overleaf tab is unavailable.')));
  });
}

function overleafUrl(url) {
  try { return new URL(url).origin === overleafOrigin; } catch { return false; }
}

function signInUrl(url) {
  try {
    const parsed = new URL(url);
    return parsed.origin === overleafOrigin && /^\/(?:login|register)(?:\/|$)/.test(parsed.pathname);
  } catch { return false; }
}

// The reader tab's id survives service-worker restarts for the browser session (tab ids are not reused within one).
async function savedReaderTab() {
  if (readerTabId !== undefined) return readerTabId;
  try { readerTabId = (await chrome.storage.session.get('readerTabId')).readerTabId ?? null; } catch { readerTabId = null; }
  return readerTabId;
}

async function rememberReaderTab(id) {
  readerTabId = id;
  try {
    if (id === null) await chrome.storage.session.remove('readerTabId');
    else await chrome.storage.session.set({ readerTabId: id });
  } catch { console.warn('Meld: could not remember the Overleaf reader tab.'); }
}

/** The connector's own background Overleaf tab, reused while it is idle on Overleaf, otherwise opened anew. */
async function readerTab() {
  await chrome.alarms.clear(readerAlarm);
  const id = await savedReaderTab();
  if (id !== null) {
    try {
      const tab = await chrome.tabs.get(id);
      if (!tab.incognito && !tab.active && !tab.discarded && overleafUrl(tab.pendingUrl || tab.url) && !signInUrl(tab.pendingUrl || tab.url))
        return tab;
    } catch { /* closed */ }
    await dropReader();
  }
  const tab = await chrome.tabs.create({ url: `${overleafOrigin}/project`, active: false });
  if (tab.id === undefined) throw new Error('The Overleaf tab is unavailable.');
  await rememberReaderTab(tab.id);
  // A sleeping tab would stop a read part way; the tab is closed after reads anyway.
  try { await chrome.tabs.update(tab.id, { autoDiscardable: false }); } catch { /* older browsers */ }
  return tab;
}

/** Forgets the reader tab, closing it unless the user took it over (is looking at it, or browsed away from Overleaf). */
async function dropReader() {
  const id = await savedReaderTab();
  if (id === null) return;
  await rememberReaderTab(null);
  try {
    const tab = await chrome.tabs.get(id);
    if (!tab.active && (tab.discarded || overleafUrl(tab.pendingUrl || tab.url))) await chrome.tabs.remove(id);
  } catch { /* already closed */ }
}

/** Why a read in this tab could not go on: closed, asleep, at Overleaf's sign-in, elsewhere, or otherwise unavailable. */
async function tabProblem(tabId, fallback = 'tab_navigated') {
  try {
    const tab = await chrome.tabs.get(tabId);
    if (tab.discarded) return 'tab_discarded';
    const url = tab.pendingUrl || tab.url;
    if (signInUrl(url)) return 'sign_in_required';
    return overleafUrl(url) ? fallback : 'tab_navigated';
  } catch { return 'tab_closed'; }
}

/**
 * One attempt at a read in the reader tab. Resolves null once the read ended (completed, failed in Overleaf, or cancelled)
 * or { failure, forwarded } when the tab broke it; forwarded says whether any of it already reached Meld.
 */
async function readOnce(message) {
  let tab;
  try {
    tab = await readerTab();
    await waitForTab(tab.id);
  } catch {
    return { failure: tab?.id === undefined ? 'tab_unavailable' : await tabProblem(tab.id, 'tab_unavailable'), forwarded: false };
  }
  if (requestId !== message.id || nativePort === null) return null;
  try {
    // A new tab that ends at Overleaf's sign-in page (or a single sign-on page Meld cannot see) needs the user to sign in.
    const loaded = await chrome.tabs.get(tab.id);
    const url = loaded.pendingUrl || loaded.url;
    if (signInUrl(url) || !overleafUrl(url)) return { failure: 'sign_in_required', forwarded: false };
    await chrome.scripting.executeScript({ target: { tabId: tab.id }, files: ['reader.js', 'content.js'] });
  } catch {
    return { failure: await tabProblem(tab.id, 'tab_unavailable'), forwarded: false };
  }
  if (requestId !== message.id || nativePort === null) return null;
  return new Promise(resolve => {
    let forwarded = false;
    const port = chrome.tabs.connect(tab.id, { name: 'meld-overleaf-v1', frameId: 0 });
    contentPort = port;
    port.onMessage.addListener(response => {
      if (contentPort !== port || response.id !== requestId) return;
      if (!['start', 'chunk', 'complete', 'error'].includes(response.type)) {
        postNative({ type: 'error', id: requestId, error: 'invalid_response' });
        stopContent();
        resolve(null);
        return;
      }
      forwarded = true;
      if (postNative(response) && response.type === 'complete') errorCode = '';
      if (response.type === 'complete' || response.type === 'error') {
        stopContent();
        resolve(null);
      }
    });
    port.onDisconnect.addListener(() => {
      void chrome.runtime.lastError;
      if (contentPort === port && requestId !== null) {
        contentPort = null;
        tabProblem(tab.id).then(failure => resolve({ failure, forwarded }), () => resolve({ failure: 'tab_closed', forwarded }));
      } else resolve(null);
    });
    port.postMessage(message);
  });
}

/** A relative project path without parent steps or control characters. */
function safePath(path) {
  return typeof path === 'string' && path.length > 0 && path.length <= 1024 && !path.startsWith('/') &&
    !path.split('/').some(part => part === '' || part === '.' || part === '..') && !/[\u0000-\u001f\\]/.test(path);
}

async function startRequest(message) {
  if (requestId !== null) {
    postNative({ type: 'error', id: message.id, error: 'browser_busy' });
    return;
  }
  if (!/^[a-f0-9]{32}$/.test(message.id ?? '') ||
      !['listProjects', 'downloadProject', 'projectHistory', 'fileAuthors'].includes(message.operation) ||
      (message.operation !== 'listProjects' && !/^[a-f0-9]{24}$/.test(message.projectId ?? '')) ||
      (message.operation === 'fileAuthors' && !safePath(message.path))) {
    failConnection('invalid_request');
    return;
  }

  requestId = message.id;
  try {
    for (let attempt = 1; ; attempt++) {
      const outcome = await readOnce(message);
      if (outcome === null || requestId !== message.id) return;
      // Nothing reached Meld yet: one more try in a fresh tab hides a closed, moved or sleeping tab.
      if (attempt < 2 && !outcome.forwarded && outcome.failure !== 'sign_in_required') {
        await dropReader();
        continue;
      }
      postNative({ type: 'error', id: message.id, error: outcome.failure });
      stopContent();
      errorCode = outcome.failure;
      if (outcome.failure !== 'sign_in_required') await dropReader();
      return;
    }
  } catch {
    if (requestId === message.id) {
      postNative({ type: 'error', id: message.id, error: 'tab_unavailable' });
      stopContent();
      errorCode = 'tab_unavailable';
    }
  } finally {
    // The reader tab closes once reads have been idle for a while (an alarm, so it also happens after a worker restart).
    try { await chrome.alarms.create(readerAlarm, { when: Date.now() + readerIdleMs }); } catch { /* best effort */ }
  }
}

async function tryConnect(force = false) {
  if (!initialized || !enabled || state().connected || connecting || (!force && Date.now() < retryAfter)) return;
  connecting = true;
  const attempt = generation;
  try {
    const allowed = await chrome.permissions.contains({ origins: [`${overleafOrigin}/*`] });
    if (attempt !== generation || !enabled) return;
    if (!allowed) {
      connecting = false;
      errorCode = 'permission_required';
      return;
    }
    errorCode = '';
    const port = chrome.runtime.connectNative(hostName);
    nativePort = port;
    connectionTimer = setTimeout(() => {
      if (nativePort === port) failConnection('backend_unavailable');
    }, 15000);
    port.onMessage.addListener(message => {
      if (nativePort !== port) return;
      if (message.type === 'ready') {
        try {
          const url = new URL(message.localUrl);
          if (message.version !== protocolVersion || url.protocol !== 'http:' || url.hostname !== '127.0.0.1' ||
              Number(url.port) < 1024 || Number(url.port) > 65535 || url.port === '6000' ||
              url.username || url.password || url.pathname !== '/' || url.search || url.hash) {
            failConnection('protocol_mismatch', 60000);
            return;
          }
          clearTimeout(connectionTimer);
          connectionTimer = null;
          connecting = false;
          retryAfter = 0;
          errorCode = '';
          localUrl = url.origin;
          // Meld asks which connector it talks to (an older Meld does not ask, and would not expect the answer).
          if (message.data === 'hello') postNative({ type: 'hello', data: chrome.runtime.getManifest().version });
          if (openAfterConnect) {
            openAfterConnect = false;
            background(openLocalApp);
          }
        } catch {
          failConnection('protocol_mismatch', 60000);
        }
      } else if (message.type === 'hostError') {
        failConnection(message.error === 'connection_in_use' ? 'connection_in_use' : 'backend_unavailable',
          message.error === 'connection_in_use' ? 60000 : 10000);
      } else if (message.type === 'request' && localUrl) {
        void startRequest(message);
      } else if (message.type === 'ack' && message.id === requestId) {
        contentPort?.postMessage(message);
      } else if (message.type === 'cancel' && message.id === requestId) {
        contentPort?.postMessage(message);
        stopContent();
      }
    });
    port.onDisconnect.addListener(() => {
      const channelError = chrome.runtime.lastError;
      if (nativePort === port) {
        failConnection(channelError ? 'host_unavailable' : 'backend_unavailable');
      }
    });
  } catch {
    if (attempt === generation) failConnection('host_unavailable');
  }
}

function isProjectPage(url) {
  try {
    const parsed = new URL(url);
    return parsed.origin === overleafOrigin && (parsed.pathname === '/' || /^\/project(?:\/|$)/.test(parsed.pathname));
  } catch { return false; }
}

function signInCompleted(tab) {
  if (tab.id !== signInTabId || tab.status !== 'complete' || !isProjectPage(tab.url)) return;
  signInTabId = null;
  errorCode = '';
  overleafSignedIn = true;
  overleafCheckedAt = Date.now();
  postNative({ type: 'overleafChanged' });
}

// Only for the popup: learns whether Overleaf redirects its project page to sign-in. The body is never read.
async function checkOverleaf(force) {
  if (!force && Date.now() - overleafCheckedAt < 10000) return;
  overleafCheckedAt = Date.now();
  try {
    const response = await fetch(`${overleafOrigin}/project`, {
      method: 'GET', credentials: 'include', cache: 'no-store', redirect: 'follow',
    });
    void response.body?.cancel().catch(() => {});
    const url = new URL(response.url);
    if (url.origin !== overleafOrigin || /^\/(?:login|register)(?:\/|$)/.test(url.pathname)) overleafSignedIn = false;
    else if (response.ok) overleafSignedIn = true;
    else overleafSignedIn = null;
  } catch {
    overleafSignedIn = null;
  }
}

async function openSignIn() {
  try {
    const reader = await savedReaderTab();
    const tabs = (await chrome.tabs.query({ url: `${overleafOrigin}/*` })).filter(candidate => candidate.id !== reader);
    let tab = tabs.find(candidate => !candidate.incognito && candidate.active) ??
      tabs.find(candidate => !candidate.incognito);
    if (tab) tab = await chrome.tabs.update(tab.id, { active: true });
    else tab = await chrome.tabs.create({ url: `${overleafOrigin}/project`, active: true });
    if (tab.id === undefined) throw new Error('Missing tab.');
    signInTabId = tab.id;
    if (tab.windowId !== undefined) await chrome.windows.update(tab.windowId, { focused: true });
    signInCompleted(await chrome.tabs.get(tab.id));
  } catch {
    errorCode = 'tab_unavailable';
  }
}

async function openLocalApp() {
  if (!localUrl) {
    errorCode = 'backend_unavailable';
    return;
  }
  await chrome.tabs.create({ url: localUrl, active: true });
}

async function command(message) {
  await initialization;
  if (!initialized) return state();
  switch (message.type) {
    case 'status': break;
    case 'ensure': await tryConnect(); break;
    case 'popup':
      await tryConnect();
      await checkOverleaf(message.refresh === true);
      break;
    case 'connect':
      if (await setEnabled(true)) await tryConnect(true);
      break;
    case 'disconnect': await setEnabled(false); break;
    case 'signIn':
      if (!enabled && !await setEnabled(true)) break;
      await tryConnect(true);
      await openSignIn();
      break;
    // An updated desktop app rewrites the connector's stable folder; reloading the extension picks up those files.
    case 'reload':
      background(async () => { await new Promise(resolve => setTimeout(resolve, 100)); chrome.runtime.reload(); });
      break;
    default: errorCode = 'invalid_request';
  }
  return state();
}

chrome.runtime.onMessage.addListener((message, sender, respond) => {
  if (sender.id !== chrome.runtime.id) return false;
  reply(message, respond);
  return true;
});

function isMeldPage(sender) {
  if (sender.id || sender.frameId > 0) return false;
  try {
    const url = new URL(sender.url);
    return url.protocol === 'http:' && ['localhost', '127.0.0.1'].includes(url.hostname) &&
      ['6001', '6010'].includes(url.port) && !url.username && !url.password;
  } catch { return false; }
}

chrome.runtime.onMessageExternal.addListener((message, sender, respond) => {
  if (!isMeldPage(sender) || !message || Object.keys(message).length !== 1 ||
      !['status', 'ensure', 'connect', 'disconnect', 'signIn', 'reload'].includes(message.type)) {
    respond({ error: 'unauthorized_request' });
    return false;
  }
  reply(message, respond);
  return true;
});

function reply(message, respond) {
  command(message).then(respond, () => {
    errorCode = 'extension_error';
    console.warn('Meld: a connector command failed.');
    respond(state());
  });
}

function background(action) {
  void action().catch(() => {
    errorCode = 'extension_error';
    console.warn('Meld: a connector background action failed.');
  });
}

chrome.alarms.onAlarm.addListener(alarm => {
  if (alarm.name === retryAlarm) background(async () => { await initialization; await tryConnect(); });
  if (alarm.name === readerAlarm) background(async () => { if (requestId === null) await dropReader(); });
});
chrome.runtime.onStartup.addListener(() => background(async () => { await initialization; await tryConnect(); }));
chrome.runtime.onInstalled.addListener(details => background(async () => {
  await initialization;
  if (details.reason === 'install') {
    if (state().connected) await openLocalApp();
    else openAfterConnect = true;
  }
  await tryConnect();
}));
chrome.tabs.onUpdated.addListener((id, change, tab) => {
  if (id === signInTabId && change.status === 'complete') signInCompleted(tab);
});
chrome.tabs.onRemoved.addListener(id => {
  if (id === signInTabId) {
    signInTabId = null;
    errorCode = 'sign_in_closed';
  }
});
chrome.permissions.onRemoved.addListener(() => background(async () => {
  if (!await chrome.permissions.contains({ origins: [`${overleafOrigin}/*`] })) failConnection('permission_required');
}));
chrome.permissions.onAdded.addListener(() => background(async () => { await initialization; await tryConnect(true); }));

const initialization = initialize();
background(async () => { await initialization; await tryConnect(); });

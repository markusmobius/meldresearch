const hostName = 'org.meldresearch.overleaf';
const overleafOrigin = 'https://www.overleaf.com';
const protocolVersion = 2;
const retryAlarm = 'meld-native-reconnect';
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
  tab_unavailable: 'Could not access Overleaf. Choose Sign in to Overleaf and finish loading its page.',
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

async function startRequest(message) {
  if (requestId !== null) {
    postNative({ type: 'error', id: message.id, error: 'browser_busy' });
    return;
  }
  if (!/^[a-f0-9]{32}$/.test(message.id ?? '') ||
      !['listProjects', 'downloadProject'].includes(message.operation) ||
      (message.operation === 'downloadProject' && !/^[a-f0-9]{24}$/.test(message.projectId ?? ''))) {
    failConnection('invalid_request');
    return;
  }

  requestId = message.id;
  try {
    const tabs = await chrome.tabs.query({ url: `${overleafOrigin}/*` });
    let tab = tabs.filter(candidate => !candidate.incognito)
      .sort((left, right) => Number(right.active) - Number(left.active))[0];
    if (!tab) tab = await chrome.tabs.create({ url: `${overleafOrigin}/project`, active: false });
    if (tab.id === undefined) throw new Error('The Overleaf tab is unavailable.');
    await waitForTab(tab.id);
    if (requestId !== message.id || nativePort === null) return;

    await chrome.scripting.executeScript({ target: { tabId: tab.id }, files: ['reader.js', 'content.js'] });
    if (requestId !== message.id || nativePort === null) return;
    const port = chrome.tabs.connect(tab.id, { name: 'meld-overleaf-v1', frameId: 0 });
    contentPort = port;
    port.onMessage.addListener(response => {
      if (contentPort !== port || response.id !== requestId) return;
      if (!['start', 'chunk', 'complete', 'error'].includes(response.type)) {
        postNative({ type: 'error', id: requestId, error: 'invalid_response' });
        stopContent();
        return;
      }
      if (postNative(response) && response.type === 'complete') errorCode = '';
      if (response.type === 'complete' || response.type === 'error') stopContent();
    });
    port.onDisconnect.addListener(() => {
      const channelError = chrome.runtime.lastError;
      if (contentPort === port && requestId !== null) {
        postNative({ type: 'error', id: requestId, error: 'tab_closed' });
        requestId = null;
        contentPort = null;
        errorCode = 'tab_unavailable';
      }
    });
    port.postMessage(message);
  } catch {
    if (requestId === message.id) {
      postNative({ type: 'error', id: message.id, error: 'tab_closed' });
      stopContent();
      errorCode = 'tab_unavailable';
    }
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
    const tabs = await chrome.tabs.query({ url: `${overleafOrigin}/*` });
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

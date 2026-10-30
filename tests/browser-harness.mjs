import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import { createRequire } from 'node:module';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const { JSDOM } = createRequire(new URL('../apps/frontend/package.json', import.meta.url))('jsdom');
const { DOMParser } = new JSDOM('').window;
const extensionRoot = process.env.MELD_EXTENSION_TEST_ROOT
  ? pathToFileURL(path.resolve(process.env.MELD_EXTENSION_TEST_ROOT) + path.sep)
  : new URL('../browser/overleaf/', import.meta.url);
const backgroundSource = await readFile(new URL('background.js', extensionRoot), 'utf8');
const contentSource = await readFile(new URL('content.js', extensionRoot), 'utf8');
const readerSource = await readFile(new URL('reader.js', extensionRoot), 'utf8');
const manifest = JSON.parse(await readFile(new URL('manifest.json', extensionRoot), 'utf8'));
export const extensionId = manifest.key
  ? [...createHash('sha256').update(Buffer.from(manifest.key, 'base64')).digest('hex').slice(0, 32)]
    .map(character => 'abcdefghijklmnop'[parseInt(character, 16)]).join('')
  : 'abcdefghijklmnopabcdefghijklmnop';

export function event() {
  const listeners = new Set();
  return {
    addListener: listener => listeners.add(listener),
    removeListener: listener => listeners.delete(listener),
    emit: (...args) => { for (const listener of [...listeners]) listener(...args); },
  };
}

export function portPair(name = 'test') {
  let closed = false;
  const first = { name, sender: { id: extensionId }, onMessage: event(), onDisconnect: event() };
  const second = { name, sender: { id: extensionId }, onMessage: event(), onDisconnect: event() };
  for (const [source, target] of [[first, second], [second, first]]) {
    source.postMessage = message => {
      if (closed) throw new Error('The test port is disconnected.');
      const copy = structuredClone(message);
      queueMicrotask(() => { if (!closed) target.onMessage.emit(copy); });
    };
    source.disconnect = () => {
      if (closed) return;
      closed = true;
      queueMicrotask(() => { first.onDisconnect.emit(); second.onDisconnect.emit(); });
    };
  }
  return [first, second];
}

export function response(body, { status = 200, type = 'text/html', url = 'https://www.overleaf.com/project', headers = {} } = {}) {
  const result = new Response(body, { status, headers: { 'content-type': type, ...headers } });
  Object.defineProperty(result, 'url', { value: url });
  return result;
}

export function browserHarness({
  nativePort, fetch: fetchResponse, overleafFetch, permitted = true, storage = {}, storageError = false,
  tabs = [{ id: 1, windowId: 1, active: true, incognito: false, status: 'complete', url: 'https://www.overleaf.com/project' }],
  now = () => Date.now(),
}) {
  const requests = [];
  const backgroundRequests = [];
  const warnings = [];
  const tabActions = [];
  const windowActions = [];
  const alarms = new Map();
  const nativePorts = [];
  const reloads = [];
  const timers = new Set();
  const runtimeMessages = event();
  const externalMessages = event();
  const contentConnections = event();
  const contentContext = vm.createContext({
    chrome: { runtime: { id: extensionId, onConnect: contentConnections } },
    location: { origin: 'https://www.overleaf.com' },
    fetch: (url, options) => {
      requests.push({ url, options });
      return fetchResponse(url, options);
    },
    URL, AbortController, setTimeout, clearTimeout, btoa, DOMParser, TextDecoder, TextEncoder,
    console: { warn: message => warnings.push(message) },
  });
  const chrome = {
    runtime: {
      id: extensionId, lastError: undefined, onMessage: runtimeMessages, onMessageExternal: externalMessages,
      onStartup: event(), onInstalled: event(), getManifest: () => manifest, reload: () => { reloads.push(Date.now()); },
      connectNative: name => { const port = nativePort(name); nativePorts.push(port); return port; },
    },
    permissions: {
      contains: async () => typeof permitted === 'function' ? permitted() : permitted,
      onAdded: event(), onRemoved: event(),
    },
    storage: { local: {
      get: async () => {
        if (storageError) throw new Error('Synthetic storage failure.');
        return { ...storage };
      },
      set: async values => {
        if (storageError) throw new Error('Synthetic storage failure.');
        Object.assign(storage, values);
      },
    } },
    alarms: {
      get: async name => alarms.get(name),
      create: async (name, options) => { alarms.set(name, options); },
      clear: async name => alarms.delete(name),
      onAlarm: event(),
    },
    windows: { update: async (id, options) => { windowActions.push({ id, ...options }); } },
    scripting: {
      executeScript: async () => {
        vm.runInContext(readerSource, contentContext, { timeout: 1000 });
        vm.runInContext(contentSource, contentContext, { timeout: 1000 });
        return [];
      },
    },
    tabs: {
      query: async () => tabs.filter(tab => tab.url?.startsWith('https://www.overleaf.com/')),
      get: async id => {
        const tab = tabs.find(tab => tab.id === id);
        if (!tab) throw new Error('Synthetic missing tab.');
        return { ...tab };
      },
      create: async options => {
        tabActions.push({ type: 'create', ...options });
        const tab = { id: Math.max(0, ...tabs.map(tab => tab.id)) + 1, windowId: 1,
          incognito: false, status: 'complete', ...options };
        tabs.push(tab);
        return { ...tab };
      },
      update: async (id, options) => {
        tabActions.push({ type: 'update', id, ...options });
        const tab = tabs.find(tab => tab.id === id);
        if (!tab) throw new Error('Synthetic missing tab.');
        Object.assign(tab, options);
        return { ...tab };
      },
      onUpdated: event(),
      onRemoved: event(),
      connect: (_tabId, options) => {
        const [background, content] = portPair(options.name);
        contentConnections.emit(content);
        return background;
      },
    },
  };
  vm.runInNewContext(backgroundSource, {
    chrome, URL, clearTimeout,
    fetch: async (url, options) => {
      backgroundRequests.push({ url, options });
      if (!overleafFetch) throw new Error('Synthetic background fetch is not configured.');
      return overleafFetch(url, options);
    },
    Date: class extends Date { static now() { return now(); } },
    setTimeout: (callback, delay) => {
      const timer = setTimeout(callback, delay);
      timers.add(timer);
      return timer;
    },
    console: { warn: message => warnings.push(message) },
  }, { timeout: 1000 });
  function send(message) {
    return new Promise(resolve => runtimeMessages.emit(message, { id: extensionId }, resolve));
  }
  function external(message, sender = { url: 'http://127.0.0.1:6010/', frameId: 0 }) {
    return new Promise(resolve => externalMessages.emit(message, sender, resolve));
  }
  function updateTab(id, changes) {
    const tab = tabs.find(tab => tab.id === id);
    Object.assign(tab, changes);
    chrome.tabs.onUpdated.emit(id, changes, { ...tab });
  }
  return {
    send, external, requests, backgroundRequests, reloads, warnings, chrome, tabs, tabActions, windowActions, alarms, storage, updateTab,
    close: () => send({ type: 'disconnect' }),
    dispose: () => { for (const port of nativePorts) port.disconnect(); for (const timer of timers) clearTimeout(timer); },
  };
}

export async function waitUntil(predicate, description, timeout = 10000) {
  const deadline = Date.now() + timeout;
  while (!await predicate()) {
    if (Date.now() > deadline) throw new Error(`Timed out: ${description}`);
    await new Promise(resolve => setTimeout(resolve, 10));
  }
}

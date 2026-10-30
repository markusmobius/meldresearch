// Meld Research local app service worker. It exists so Chrome and Edge can install the app; it caches nothing,
// because the app is served by the local backend on this computer and must always show its current state.
self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));
self.addEventListener('fetch', () => {});

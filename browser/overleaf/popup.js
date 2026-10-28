const status = document.getElementById('status');
const connect = document.getElementById('connect');
const openApp = document.getElementById('open-app');
const disconnect = document.getElementById('disconnect');

function render(state) {
  status.textContent = state.error || (!state.enabled ? 'Auto-connect is paused.' :
    state.connected ? 'Browser bridge connected. Meld checks Overleaf sign-in when it loads projects.' :
    state.connecting ? 'Connecting to Meld...' : 'Waiting for Meld. Start the local application; reconnection is automatic.');
  connect.disabled = state.connected || state.connecting;
  openApp.disabled = !state.connected;
  disconnect.disabled = !state.enabled;
}

connect.addEventListener('click', async () => {
  connect.disabled = true;
  try {
    status.textContent = 'Connecting to the registered native bridge...';
    render(await chrome.runtime.sendMessage({ type: 'connect' }));
  } catch {
    render({ connected: false, error: 'The extension could not connect. Restart the local backend and reload this extension.' });
  }
});

openApp.addEventListener('click', async () => {
  try {
    render(await chrome.runtime.sendMessage({ type: 'openApp' }));
  } catch {
    status.textContent = 'Could not open the local application. Open http://127.0.0.1:6010 manually.';
  }
});

document.getElementById('overleaf').addEventListener('click', async () => {
  try {
    render(await chrome.runtime.sendMessage({ type: 'signIn' }));
  } catch {
    status.textContent = 'Could not open Overleaf. Open https://www.overleaf.com/project manually.';
  }
});

disconnect.addEventListener('click', async () => {
  try {
    render(await chrome.runtime.sendMessage({ type: 'disconnect' }));
  } catch {
    status.textContent = 'Could not confirm disconnection. Reload the extension.';
  }
});

function updateStatus() {
  chrome.runtime.sendMessage({ type: 'ensure' }).then(render, () => {
    render({ connected: false, error: 'The extension background process is unavailable. Reload the extension.' });
  });
}

updateStatus();
const timer = setInterval(updateStatus, 2000);
window.addEventListener('pagehide', () => clearInterval(timer));

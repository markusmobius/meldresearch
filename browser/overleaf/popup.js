const status = document.getElementById('status');
const overleaf = document.getElementById('overleaf');
const connect = document.getElementById('connect');
const disconnect = document.getElementById('disconnect');

function message(state) {
  if (state.signInPending) return 'Finish signing in to Overleaf in the opened tab.';
  if (state.overleafSignedIn === false)
    return state.errorCode === 'sign_in_closed' ? state.error : 'Sign in to Overleaf to connect your projects to Meld Research.';
  if (state.overleafSignedIn !== true) return 'Could not reach Overleaf. Sign in to Overleaf, then reopen this window.';
  if (state.error) return state.error;
  if (!state.enabled) return 'Disconnected. Meld cannot read Overleaf until you connect.';
  if (state.connected) return 'Connected to Meld Research.';
  if (state.connecting) return 'Connecting to Meld Research...';
  return 'Waiting for the Meld Research desktop application. It connects automatically once it is running.';
}

function render(state) {
  status.textContent = message(state);
  const signedIn = state.overleafSignedIn === true;
  overleaf.hidden = signedIn;
  overleaf.disabled = state.signInPending === true;
  connect.hidden = !signedIn || state.enabled;
  disconnect.hidden = !signedIn || !state.enabled;
}

async function send(request, failure) {
  try {
    render(await chrome.runtime.sendMessage(request));
  } catch {
    status.textContent = failure;
  }
}

overleaf.addEventListener('click', () => {
  overleaf.disabled = true;
  void send({ type: 'signIn' }, 'Could not open Overleaf. Open https://www.overleaf.com/project manually.');
});

connect.addEventListener('click', () => {
  connect.hidden = true;
  status.textContent = 'Connecting to Meld Research...';
  void send({ type: 'connect' }, 'The extension could not connect. Restart Meld Research and reload this extension.');
});

disconnect.addEventListener('click', () => {
  disconnect.hidden = true;
  void send({ type: 'disconnect' }, 'Could not confirm disconnection. Reload the extension.');
});

function updateStatus(refresh = false) {
  chrome.runtime.sendMessage({ type: 'popup', refresh }).then(render, () => {
    status.textContent = 'The extension background process is unavailable. Reload the extension.';
    for (const button of [overleaf, connect, disconnect]) button.hidden = true;
  });
}

updateStatus(true);
const timer = setInterval(updateStatus, 2000);
window.addEventListener('pagehide', () => clearInterval(timer));

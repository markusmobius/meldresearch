'use strict';
// Sign in with Google for Meld Research. Loaded before Google's script so the home page button can say
// "Sign up" or "Sign in": a browser that has seen a Meld account before (meldresearch.account, set by the
// install page) gets "Sign in". Signing up again with an existing account simply signs in.
(function () {
  var button = document.querySelector('.g_id_signin[data-meld-auto-mode]');
  var form = document.getElementById('google-sign-in');
  if (!button || !form) return;
  var known = false;
  try { known = window.localStorage.getItem('meldresearch.account') === '1'; } catch (e) { known = false; }
  if (!known) return;
  button.setAttribute('data-text', 'signin_with');
  var onload = document.getElementById('g_id_onload');
  if (onload) onload.setAttribute('data-context', 'signin');
  form.elements.namedItem('mode').value = 'login';
})();

// Called by Google's button. Submits the Google-signed ID token through this site's own same-origin form
// (with its antiforgery token); the server verifies the token.
window.meldGoogleCredential = function (response) {
  var form = document.getElementById('google-sign-in');
  if (!form || !response || typeof response.credential !== 'string' || response.credential.length === 0) return;
  form.elements.namedItem('credential').value = response.credential;
  form.submit();
};

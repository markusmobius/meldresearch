'use strict';
// Called by Google's Sign in with Google button. Submits the Google-signed ID token through this site's own
// same-origin form (with its antiforgery token); the server verifies the token. Nothing is stored here.
window.meldGoogleCredential = function (response) {
  var form = document.getElementById('google-sign-in');
  if (!form || !response || typeof response.credential !== 'string' || response.credential.length === 0) return;
  form.elements.namedItem('credential').value = response.credential;
  form.submit();
};

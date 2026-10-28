import { provideHttpClient } from '@angular/common/http';
import { provideBrowserGlobalErrorListeners } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { MeldToolbar } from './app/meld-toolbar';

async function start(): Promise<void> {
  const component = document.querySelector('app-root') ? (await import('./app/app')).App : MeldToolbar;
  await bootstrapApplication(component, {
    providers: [provideHttpClient(), provideBrowserGlobalErrorListeners(), provideAnimationsAsync()],
  });
}

void start().catch(() => {
  const message = document.createElement('p');
  message.setAttribute('role', 'alert');
  message.textContent = 'Meld Research could not start. Reload this page or restart the backend.';
  document.body.prepend(message);
  console.error('Meld Research frontend initialization failed.');
});

import { provideHttpClient } from '@angular/common/http';
import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { bootstrapApplication, createApplication } from '@angular/platform-browser';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { MeldToolbar } from './app/meld-toolbar';

const config: ApplicationConfig = {
  providers: [provideHttpClient(), provideBrowserGlobalErrorListeners(), provideAnimationsAsync()],
};

async function start(): Promise<void> {
  if (document.querySelector('app-root')) {
    await bootstrapApplication((await import('./app/app')).App, config);
    return;
  }
  // Server-rendered and public pages: the shared toolbar, plus the install card where the page provides one.
  const application = await createApplication(config);
  application.bootstrap(MeldToolbar);
  if (document.querySelector('app-meld-install')) application.bootstrap((await import('./app/meld-install')).MeldInstall);
  if (document.querySelector('app-meld-usage')) application.bootstrap((await import('./app/meld-usage')).MeldUsage);
}

void start().catch(() => {
  const message = document.createElement('p');
  message.setAttribute('role', 'alert');
  message.textContent = 'Meld Research could not start. Reload this page or restart the backend.';
  document.body.prepend(message);
  console.error('Meld Research frontend initialization failed.');
});

import { Component, computed, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatToolbarModule } from '@angular/material/toolbar';

export type LocalPage = 'home' | 'about-me' | 'account';

export interface RemoteAccount { name: string; email: string; signOutToken?: string; }

/** The signed-in remote account, published by the server-rendered page; null on local and anonymous pages. */
export function readRemoteAccount(): RemoteAccount | null {
  const meta = document.querySelector<HTMLMetaElement>('meta[name="meld-account"]');
  return meta ? { name: meta.content, email: meta.dataset['email'] ?? '' } : null;
}

const isRemoteSite = () =>
  getComputedStyle(document.documentElement).getPropertyValue('--meld-role').trim() === 'remote';

@Component({
  selector: 'app-meld-toolbar',
  imports: [MatButtonModule, MatIconModule, MatMenuModule, MatToolbarModule],
  templateUrl: './meld-toolbar.html',
  styleUrl: './meld-toolbar.css',
})
export class MeldToolbar {
  readonly page = input<string>(location.pathname);
  readonly accountName = input<string | null>(null);
  readonly dummy = input(false);
  readonly updateCount = input(0);
  readonly remoteAccount = signal(readRemoteAccount());
  readonly remoteInitial = computed(() => {
    const account = this.remoteAccount();
    return (account?.name || account?.email || '?').trim().charAt(0).toUpperCase();
  });

  constructor() {
    // Static remote pages (Docs, Privacy, Team) cannot embed the session, so they ask for it.
    if (!this.remoteAccount() && !document.querySelector('app-root') && isRemoteSite()) void this.loadSession();
  }

  private async loadSession(): Promise<void> {
    try {
      const response = await fetch('/api/session', { credentials: 'same-origin', headers: { Accept: 'application/json' } });
      if (response.status === 200) this.remoteAccount.set(await response.json() as RemoteAccount);
    } catch {
      // Anonymous navigation still works.
    }
  }

  /** Submits the antiforgery-protected sign-out form: the page's own, or one built from the session token. */
  signOut(): void {
    let form = document.querySelector<HTMLFormElement>('form#meld-sign-out');
    const token = this.remoteAccount()?.signOutToken;
    if (!form && token) {
      form = document.createElement('form');
      form.method = 'post';
      form.action = '/auth/signout';
      form.hidden = true;
      const input = document.createElement('input');
      input.type = 'hidden';
      input.name = '__RequestVerificationToken';
      input.value = token;
      form.append(input);
      document.body.append(form);
    }
    form?.submit();
  }
}

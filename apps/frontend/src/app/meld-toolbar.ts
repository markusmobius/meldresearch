import { Component, DestroyRef, computed, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatToolbarModule } from '@angular/material/toolbar';

export type LocalPage = 'home' | 'about-me' | 'account';

export interface RemoteAccount { name: string; email: string; signOutToken?: string; superuser?: boolean; }

/** The signed-in remote account, published by the server-rendered page; null on local and anonymous pages. */
export function readRemoteAccount(): RemoteAccount | null {
  const meta = document.querySelector<HTMLMetaElement>('meta[name="meld-account"]');
  return meta ? { name: meta.content, email: meta.dataset['email'] ?? '', superuser: meta.dataset['superuser'] === 'true' } : null;
}

const isRemoteSite = () =>
  getComputedStyle(document.documentElement).getPropertyValue('--meld-role').trim() === 'remote';

export interface LocalVersion { version: string; versionDate: string | null; updateStatus: string; pendingVersion?: string | null; }

/** Updater states in which a newer release is installed or being installed and Meld restarts into it shortly. */
const UPDATING = new Set(['installing', 'ready', 'restarting']);
export const shortVersion = (version: string | null | undefined) =>
  !version ? '' : /^[0-9a-f]{64}$/.test(version) ? 'v' + version.slice(0, 8) : version;

/** Release label for the brand area: the first 8 characters of the content-hash release and its commit time. */
export function versionLabel(local: LocalVersion | null): string {
  if (!local) return '';
  if (!/^[0-9a-f]{64}$/.test(local.version)) return 'development build';
  const date = commitDate(local.versionDate);
  return `v${local.version.slice(0, 8)}${date ? ' \u00b7 ' + date : ''}`;
}

function commitDate(value: string | null): string {
  const date = value ? new Date(value) : null;
  return date && !isNaN(date.getTime())
    ? date.toLocaleString(undefined, { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', hour12: false })
    : '';
}

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
  /** Local app: Feed and About me appear only after the Meld account and Overleaf are connected. */
  readonly setupComplete = input(true);
  readonly remoteAccount = signal(readRemoteAccount());
  readonly remoteInitial = computed(() => {
    const account = this.remoteAccount();
    return (account?.name || account?.email || '?').trim().charAt(0).toUpperCase();
  });
  /** Local app only: the installed release, and whether the remote service is reachable (null until checked). */
  readonly localVersion = signal<LocalVersion | null>(null);
  readonly remoteOnline = signal<boolean | null>(null);
  readonly versionText = computed(() => versionLabel(this.localVersion()));
  /** The release Meld is restarting into; set from the first "installing" report until the new version answers. */
  readonly updateTarget = signal<string | null>(null);
  readonly shortVersion = shortVersion;
  private loadedVersion: string | null = null;
  /** Replaced in tests; reloading picks up the new release's frontend. */
  reload: () => void = () => location.reload();
  readonly versionTitle = computed(() => {
    const local = this.localVersion();
    if (!local) return '';
    const date = commitDate(local.versionDate);
    return `Release ${local.version}${date ? '\nCommitted ' + date : ''}\nAutomatic updates: ${local.updateStatus}`;
  });

  constructor() {
    // Static remote pages (Docs, Privacy, Team) cannot embed the session, so they ask for it.
    if (!this.remoteAccount() && !document.querySelector('app-root') && isRemoteSite()) void this.loadSession();
    if (!isRemoteSite() && location.protocol.startsWith('http')) {
      // Like Project Meld: watch for an update every few seconds while one is underway, otherwise every 20 seconds.
      let timer: ReturnType<typeof setTimeout> | undefined;
      let stopped = false;
      const tick = async () => {
        await Promise.all([this.loadVersion(), this.ping()]);
        if (!stopped) timer = setTimeout(() => void tick(), this.updateTarget() ? 3000 : 20000);
      };
      void tick();
      inject(DestroyRef).onDestroy(() => { stopped = true; clearTimeout(timer); });
    }
  }

  private async loadVersion(): Promise<void> {
    try {
      const response = await fetch('/api/version', { headers: { Accept: 'application/json' } });
      if (!response.ok) return;
      const local = await response.json() as LocalVersion;
      if (this.loadedVersion === null) this.loadedVersion = local.version;
      else if (local.version !== this.loadedVersion) {
        this.reload();
        return;
      }
      this.localVersion.set(local);
      if (UPDATING.has(local.updateStatus) && local.pendingVersion) this.updateTarget.set(local.pendingVersion);
      else if (!UPDATING.has(local.updateStatus)) this.updateTarget.set(null);
    } catch {
      // While Meld restarts into an update it does not answer; the overlay stays until the new version does.
    }
  }

  private async ping(): Promise<void> {
    try {
      const response = await fetch('/api/remote-ping', { headers: { Accept: 'application/json' } });
      this.remoteOnline.set(response.ok && (await response.json() as { online: boolean }).online === true);
    } catch {
      this.remoteOnline.set(false);
    }
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

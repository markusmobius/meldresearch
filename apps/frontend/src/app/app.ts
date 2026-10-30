import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, DestroyRef, HostListener, OnInit, computed, effect, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSidenavModule } from '@angular/material/sidenav';
import { BrowserConnector, ConnectorResult } from './browser-connector';
import { BridgeStatus, Download, ExtensionSetup, OverleafApi, OverleafProject, describeFailure, needsOverleafSignIn } from './overleaf-api';
import { ResearchPanel } from './research-panel';
import { ResearchApi, ResearchStatus } from './research-api';
import { LocalPage, MeldToolbar } from './meld-toolbar';

export type SetupTab = 'meld' | 'overleaf';
/** The account ID for which this browser has connected Overleaf at least once; later Overleaf outages keep the feed. */
const overleafConfirmedKey = 'meldresearch.overleafConnectedFor';
function readOverleafConfirmed(): string | null {
  try { return localStorage.getItem(overleafConfirmedKey); } catch { return null; }
}

@Component({
  selector: 'app-root',
  imports: [DatePipe, DecimalPipe, ResearchPanel, MeldToolbar, MatButtonModule, MatCardModule,
    MatExpansionModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSidenavModule],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App implements OnInit {
  readonly page = signal<LocalPage>(this.pageFromHash());
  readonly research = signal<ResearchStatus | null>(null);
  private readonly api = inject(OverleafApi);
  private readonly browser = inject(BrowserConnector);
  private readonly destroyRef = inject(DestroyRef);
  private polling = false;
  private stopped = false;
  private commandVersion = 0;
  private attemptedVersion: string | null = null;
  private readonly loadedVersion = signal<string | null>(null);
  readonly status = signal<BridgeStatus | null>(null);
  readonly setup = signal<ExtensionSetup | null>(null);
  readonly connector = signal<ConnectorResult | null>(null);
  readonly projects = signal<ReadonlyArray<OverleafProject>>([]);
  readonly downloads = signal<ReadonlyArray<Download>>([]);
  readonly error = signal('');
  readonly connectionError = signal('');
  readonly busy = signal('');
  readonly connectorBusy = signal(false);
  readonly showSetup = signal(false);
  readonly copyMessage = signal('');
  readonly signInRequired = signal(false);
  readonly refreshed = signal(false);
  readonly extensionsPage = /Edg\//.test(navigator.userAgent) ? 'edge://extensions' : 'chrome://extensions';
  readonly browserState = computed(() => {
    const connector = this.connector();
    return connector?.kind === 'available' ? connector.state : null;
  });
  readonly connected = computed(() => !!this.status()?.connected && !!this.browserState()?.connected);
  readonly verified = computed(() => this.connected() && this.loadedVersion() !== null &&
    this.loadedVersion() === this.status()?.connectionVersion);
  readonly setupNeeded = computed(() => {
    const kind = this.connector()?.kind;
    return this.showSetup() || kind === 'missing' || kind === 'outdated' || kind === 'unsupported';
  });
  readonly connectorMessage = computed(() => {
    const connector = this.connector();
    return connector?.kind === 'available' ? connector.state.error : connector?.message ?? '';
  });
  readonly connectionLabel = computed(() => {
    if (this.status()?.dummy) return 'Synthetic Overleaf source';
    if (!this.status()) return 'Local backend unavailable';
    if (!this.connector()) return 'Checking browser connector';
    if (this.connector()?.kind === 'missing') return 'One-time setup needed';
    if (this.connector()?.kind === 'outdated') return 'Connector update needed';
    if (this.connector()?.kind === 'unsupported') return 'Chrome or Edge required';
    if (this.browserState()?.enabled === false) return 'Auto-connect paused';
    if (this.browserState()?.signInPending) return 'Finish signing in to Overleaf';
    if (this.verified()) return 'Overleaf connected';
    if (this.signInRequired() && this.connected()) return 'Overleaf sign-in needed';
    if (this.connected()) return 'Browser connected';
    if (this.status()?.connected) return 'Connected in another browser';
    if (this.browserState()?.connecting) return 'Connecting browser';
    return 'Browser connector disconnected';
  });

  @HostListener('window:hashchange')
  locationChanged(): void { this.page.set(this.pageFromHash()); }

  private pageFromHash(): LocalPage {
    return location.hash === '#about-me' ? 'about-me' : location.hash === '#account' ? 'account' : 'home';
  }

  // Setup: the Meld account (Google) first, then Overleaf. Until both are done, only the Account view exists.
  private readonly researchApi = inject(ResearchApi);
  readonly accountAction = signal('');
  private readonly chosenTab = signal<SetupTab | null>(null);
  private readonly overleafConfirmedFor = signal(readOverleafConfirmed());
  readonly setupKnown = computed(() => this.research() !== null);
  readonly accountLinked = computed(() => !!this.research()?.account);
  readonly overleafReady = computed(() => this.verified() ||
    (!!this.research()?.account && this.overleafConfirmedFor() === this.research()?.account?.id));
  readonly setupComplete = computed(() => this.accountLinked() && this.overleafReady());
  readonly setupTab = computed<SetupTab>(() => this.chosenTab() ?? (this.accountLinked() ? 'overleaf' : 'meld'));
  readonly view = computed<LocalPage>(() => this.setupKnown() && this.setupComplete() ? this.page() : 'account');

  constructor() {
    effect(() => {
      const account = this.research()?.account?.id;
      if (account && this.verified() && this.overleafConfirmedFor() !== account) {
        try { localStorage.setItem(overleafConfirmedKey, account); } catch { /* storage disabled: setup is re-checked next time */ }
        this.overleafConfirmedFor.set(account);
      }
    });
    // Finishing setup opens the feed.
    let wasComplete: boolean | null = null;
    effect(() => {
      if (!this.setupKnown()) return;
      const complete = this.setupComplete();
      if (wasComplete === false && complete && this.page() === 'account') location.hash = '#feed';
      wasComplete = complete;
    });
  }

  chooseTab(tab: SetupTab): void { this.chosenTab.set(tab); }

  /** Signs in on the Meld website in this tab; the website returns to /account/callback and then to this app. */
  async signIn(): Promise<void> {
    this.accountAction.set('Opening Google sign-in...');
    try {
      this.navigate((await this.researchApi.link()).signInUrl);
    } catch (error: unknown) {
      this.error.set(describeFailure(error));
      this.accountAction.set('');
    }
  }
  navigate(url: string): void { window.location.assign(url); }

  async disconnect(): Promise<void> {
    this.accountAction.set('Disconnecting...');
    try {
      await this.researchApi.unlink();
      this.research.update(state => state ? { ...state, account: null } : state);
      this.chosenTab.set(null);
    } catch (error: unknown) {
      this.error.set(describeFailure(error));
    } finally {
      this.accountAction.set('');
    }
  }

  ngOnInit(): void {
    void this.loadDownloads();
    void this.checkStatus();
    const timer = setInterval(() => { void this.checkStatus(); }, 3000);
    this.destroyRef.onDestroy(() => { this.stopped = true; clearInterval(timer); });
  }

  private async loadDownloads(): Promise<void> {
    try {
      this.downloads.set(await this.api.downloads());
    } catch (error: unknown) {
      this.error.set(describeFailure(error));
    }
  }

  private async checkStatus(): Promise<void> {
    if (this.polling || this.stopped) return;
    this.polling = true;
    try {
      this.status.set(await this.api.status());
      if (!this.setup()) this.setup.set(await this.api.extension());
      const setup = this.setup();
      if (setup && this.status()?.dummy) {
        this.connector.set({ kind: 'available', state: {
          connected: true, enabled: true, connecting: false, extensionVersion: setup.extensionVersion,
          protocolVersion: setup.protocolVersion, localUrl: 'http://127.0.0.1:6010',
          signInPending: false, errorCode: null, error: '',
        } });
      } else if (setup && !this.connectorBusy()) {
        const version = this.commandVersion;
        const result = await this.browser.send(setup, 'ensure');
        if (version === this.commandVersion) this.connector.set(result);
      }
      this.connectionError.set('');
      const version = this.status()?.connectionVersion;
      if (!this.stopped && this.connected() && version && version !== this.attemptedVersion &&
          !this.busy() && !this.status()?.busy && !this.connectorBusy() && !this.browserState()?.signInPending) {
        await this.refresh();
      }
    } catch (error: unknown) {
      this.status.set(null);
      this.connectionError.set(describeFailure(error));
    } finally {
      this.polling = false;
    }
  }

  async connect(): Promise<void> {
    await this.connectorAction(this.connected() && this.signInRequired() ? 'signIn' : 'connect');
    if (this.connector()?.kind !== 'available') this.showSetup.set(true);
  }

  async connectorAction(command: 'connect' | 'disconnect' | 'signIn'): Promise<void> {
    const setup = this.setup();
    if (!setup || this.connectorBusy()) return;
    this.commandVersion++;
    this.connectorBusy.set(true);
    try {
      this.connector.set(await this.browser.send(setup, command));
    } finally {
      this.connectorBusy.set(false);
      await this.checkStatus();
    }
  }

  async copy(text: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(text);
      this.copyMessage.set('Copied.');
    } catch {
      this.copyMessage.set('Automatic copy is unavailable. Select the text in the field and copy it manually.');
    }
  }

  async refresh(): Promise<void> {
    if (this.busy() || this.status()?.busy || !this.connected()) return;
    const version = this.status()?.connectionVersion ?? null;
    this.attemptedVersion = version;
    this.busy.set('Reading your Overleaf project list...');
    this.error.set('');
    try {
      this.projects.set(await this.api.refresh());
      this.loadedVersion.set(version);
      this.signInRequired.set(false);
      this.refreshed.set(true);
    } catch (error: unknown) {
      this.error.set(describeFailure(error));
      this.signInRequired.set(needsOverleafSignIn(error));
      if (this.signInRequired()) this.loadedVersion.set(null);
    } finally {
      this.busy.set('');
    }
  }

  async download(project: OverleafProject): Promise<void> {
    if (this.busy() || this.status()?.busy || !this.verified()) return;
    this.busy.set('Downloading and validating the selected project...');
    this.error.set('');
    try {
      const result = await this.api.download(project.id);
      this.downloads.update(downloads => [result, ...downloads]);
    } catch (error: unknown) {
      this.error.set(describeFailure(error));
      if (needsOverleafSignIn(error)) {
        this.signInRequired.set(true);
        this.loadedVersion.set(null);
      }
    } finally {
      this.busy.set('');
    }
  }
}

import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, DestroyRef, HostListener, OnInit, computed, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatIconModule } from '@angular/material/icon';
import { MatSidenavModule } from '@angular/material/sidenav';
import { BrowserConnector, ConnectorResult, olderVersion } from './browser-connector';
import { BridgeStatus, Download, ExtensionSetup, OverleafApi, OverleafProject, describeFailure, needsOverleafSignIn } from './overleaf-api';
import { ResearchPanel } from './research-panel';
import { ResearchApi, ResearchStatus } from './research-api';
import { LocalPage, MeldToolbar } from './meld-toolbar';
import { ProjectSelection } from './project-selection';
import { ResearcherSetup } from './researcher-setup';
import { PublicationsList } from './publications';
import { PrivateProfile } from './private-profile';
import { includedCount, researcherDone, undecidedRecent } from './project-decisions';

export type SetupTab = 'meld' | 'overleaf' | 'openalex';
export type OverleafTab = 'connection' | 'projects';
/** The account ID for which this browser has connected Overleaf at least once; later Overleaf outages keep the feed. */
const overleafConfirmedKey = 'meldresearch.overleafConnectedFor';
function readOverleafConfirmed(): string | null {
  try { return localStorage.getItem(overleafConfirmedKey); } catch { return null; }
}
const installDismissedKey = 'meldresearch.installTipDismissed';
/** The connector version this tab already asked an older connector to reload into, so a failed reload is not repeated. */
const reloadRequestedKey = 'meldresearch.connectorReloadRequested';
/** Chrome/Edge's deferred install prompt (not in the TypeScript DOM library). */
interface InstallPromptEvent extends Event {
  prompt(): Promise<void>;
  readonly userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }>;
}

@Component({
  selector: 'app-root',
  imports: [DatePipe, DecimalPipe, ResearchPanel, MeldToolbar, ProjectSelection, ResearcherSetup, PublicationsList, PrivateProfile, MatButtonModule, MatCardModule,
    MatExpansionModule, MatIconModule, MatSidenavModule],
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
  /** The browser still runs an older connector than this Meld release ships. */
  readonly connectorStale = computed(() => {
    const state = this.browserState(), setup = this.setup();
    return !!state && !!setup && !this.status()?.dummy && olderVersion(state.extensionVersion, setup.extensionVersion);
  });
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
    if (this.connector()?.kind === 'outdated' || this.connectorStale()) return 'Connector update needed';
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

  // Setup: the Meld account (Google), then Overleaf (connection, then an active choice per recent project), then
  // OpenAlex. Only the Account view exists until a project is included; OpenAlex never blocks the feed.
  private readonly researchApi = inject(ResearchApi);
  private readonly panel = viewChild(ResearchPanel);
  readonly accountAction = signal('');
  private readonly chosenTab = signal<SetupTab | null>(null);
  private readonly chosenOverleafTab = signal<OverleafTab | null>(null);
  private readonly overleafConfirmedFor = signal(readOverleafConfirmed());
  readonly setupKnown = computed(() => this.research() !== null);
  readonly accountLinked = computed(() => !!this.research()?.account);
  readonly overleafReady = computed(() => this.verified() ||
    (!!this.research()?.account && this.overleafConfirmedFor() === this.research()?.account?.id));
  /** The freshest Overleaf project list: the backend's last read (also by monitoring), else this page's own refresh. */
  readonly overleafProjects = computed<ReadonlyArray<OverleafProject>>(() => this.research()?.overleafProjects?.projects ?? this.projects());
  readonly undecided = computed(() => undecidedRecent(this.research(), this.overleafProjects()));
  readonly includedProjects = computed(() => includedCount(this.research()));
  readonly openAlexDone = computed(() => researcherDone(this.research()));
  readonly overleafDone = computed(() => this.overleafReady() && !this.connectorStale() && this.includedProjects() > 0 && !this.undecided().length);
  readonly setupComplete = computed(() => this.accountLinked() && this.overleafReady() && !this.connectorStale() && this.includedProjects() > 0);
  readonly setupTab = computed<SetupTab>(() => this.chosenTab() ?? (this.accountLinked() ? 'overleaf' : 'meld'));
  readonly overleafTab = computed<OverleafTab>(() => this.chosenOverleafTab() ??
    (this.overleafReady() && !this.connectorStale() && !this.setupNeeded() && this.connector()?.kind === 'available' ? 'projects' : 'connection'));
  readonly view = computed<LocalPage>(() => this.setupKnown() && this.setupComplete() ? this.page() : 'account');
  readonly overleafLabel = computed(() => {
    if (!this.verified() || this.connectorStale()) return this.connectionLabel();
    const processing = this.research()?.processing;
    if (processing) return `${processing.kind === 'import' ? 'Importing' : 'Updating'} ${processing.filesDone}/${processing.filesTotal} files`;
    const undecided = this.undecided().length;
    if (undecided) return `${undecided} ${undecided === 1 ? 'project' : 'projects'} to decide`;
    const included = this.includedProjects();
    return included ? `${included} ${included === 1 ? 'project' : 'projects'} included` : 'Choose your projects';
  });
  readonly researcherLabel = computed(() => {
    const researcher = this.research()?.researcher;
    switch (researcher?.status) {
      case 'searching': return 'Searching...';
      case 'choice': return 'Confirm your profile';
      case 'unmatched': return 'No match yet';
      case 'rejected': return 'Not linked';
      case 'importing': return 'Importing publications';
      case 'ready': return `${researcher.workCount} publications`;
      default: return 'Find your publications';
    }
  });
  /** Set after a while, so the OpenAlex nudge does not wait forever for an Overleaf list that never loads. */
  private readonly listWaitOver = signal(false);

  constructor() {
    effect(() => {
      const account = this.research()?.account?.id;
      if (account && this.verified() && this.overleafConfirmedFor() !== account) {
        try { localStorage.setItem(overleafConfirmedKey, account); } catch { /* storage disabled: setup is re-checked next time */ }
        this.overleafConfirmedFor.set(account);
      }
    });
    // Every recent project needs an active choice: each page load, and each newly appearing project, opens
    // Account → Overleaf → Projects until the choice is made.
    const nudged = new Set<string>();
    effect(() => {
      if (!this.setupKnown() || !this.accountLinked() || !this.overleafReady() || this.connectorStale()) return;
      const fresh = this.undecided().filter(project => !nudged.has(project.id));
      if (!fresh.length) return;
      for (const project of fresh) nudged.add(project.id);
      untracked(() => this.open('overleaf', 'projects'));
    });
    // Once per page load, OpenAlex is offered after the projects are settled (projects come first).
    let openAlexNudged = false;
    effect(() => {
      if (openAlexNudged || !this.setupKnown() || !this.setupComplete() || this.undecided().length || this.openAlexDone()) return;
      if (!this.research()?.overleafProjects && !this.listWaitOver()) return;
      openAlexNudged = true;
      untracked(() => this.open('openalex'));
    });
    // Finishing everything, including OpenAlex, opens the feed.
    let wasDone: boolean | null = null;
    effect(() => {
      if (!this.setupKnown()) return;
      const done = this.setupComplete() && !this.undecided().length && this.openAlexDone();
      if (wasDone === false && done && untracked(() => this.page()) === 'account') {
        this.chosenTab.set(null);
        location.hash = '#feed';
        this.page.set('home');
      }
      wasDone = done;
    });
  }

  chooseTab(tab: SetupTab): void { this.chosenTab.set(tab); }
  chooseOverleafTab(tab: OverleafTab): void { this.chosenOverleafTab.set(tab); }
  private open(tab: SetupTab, overleafTab?: OverleafTab): void {
    this.chosenTab.set(tab);
    if (overleafTab) this.chosenOverleafTab.set(overleafTab);
    if (location.hash !== '#account') location.hash = '#account';
    this.page.set('account');
  }
  /** A project or researcher action changed the research state; show it without waiting for the next poll. */
  researchChanged(): void { void this.panel()?.refresh(); }

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
    if ('serviceWorker' in navigator) void navigator.serviceWorker.register('/sw.js').catch(() => undefined);
    if (this.installTipAllowed()) this.showInstallTip.set(true);
    void this.loadDownloads();
    void this.checkStatus();
    const timer = setInterval(() => { void this.checkStatus(); }, 3000);
    const wait = setTimeout(() => this.listWaitOver.set(true), 15000);
    this.destroyRef.onDestroy(() => { this.stopped = true; clearInterval(timer); clearTimeout(wait); });
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
        this.requestConnectorReload(setup);
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

  /**
   * A connector loaded from Meld's stable connector folder updates by reloading itself. Asked once per tab and version:
   * if it is still older afterwards (loaded from elsewhere, or too old to reload), the Overleaf tab shows update steps.
   */
  private requestConnectorReload(setup: ExtensionSetup): void {
    if (!this.connectorStale()) return;
    try {
      if (sessionStorage.getItem(reloadRequestedKey) === setup.extensionVersion) return;
      sessionStorage.setItem(reloadRequestedKey, setup.extensionVersion);
    } catch { return; }
    void this.browser.send(setup, 'reload');
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

  /** The text most recently copied, so its button can show a check mark. */
  readonly copied = signal<string | null>(null);
  readonly setupNote = signal('');

  async copy(text: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(text);
      this.copied.set(text);
      setTimeout(() => { if (this.copied() === text) this.copied.set(null); }, 2000);
    } catch {
      this.setupNote.set('Copying is blocked in this browser. Select the text and copy it manually: ' + text);
    }
  }

  reload(): void { location.reload(); }

  // Install as an app (Chrome/Edge), with the same top-right nudge as Project Meld.
  private readonly installEvent = signal<InstallPromptEvent | null>(null);
  readonly showInstallTip = signal(false);
  readonly canInstallNow = computed(() => this.installEvent() !== null);

  private installTipAllowed(): boolean {
    const standalone = typeof matchMedia === 'function' && matchMedia('(display-mode: standalone)').matches;
    let dismissed = false;
    try { dismissed = localStorage.getItem(installDismissedKey) === 'true'; } catch { /* storage disabled */ }
    return !standalone && /Chrome|Edg\//.test(navigator.userAgent) && !dismissed;
  }

  @HostListener('window:beforeinstallprompt', ['$event'])
  onBeforeInstallPrompt(event: Event): void {
    event.preventDefault();
    this.installEvent.set(event as InstallPromptEvent);
    if (this.installTipAllowed()) this.showInstallTip.set(true);
  }

  @HostListener('window:appinstalled')
  onAppInstalled(): void {
    this.showInstallTip.set(false);
    this.installEvent.set(null);
  }

  async installNow(): Promise<void> {
    const prompt = this.installEvent();
    if (!prompt) return;
    await prompt.prompt();
    if ((await prompt.userChoice).outcome === 'accepted') this.showInstallTip.set(false);
    this.installEvent.set(null);
  }

  dismissInstallTip(): void {
    this.showInstallTip.set(false);
    try { localStorage.setItem(installDismissedKey, 'true'); } catch { /* storage disabled */ }
  }

  async refresh(): Promise<void> {
    // Browser reads queue behind monitoring transfers, so a busy browser never blocks this request.
    if (this.busy() || !this.connected()) return;
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
    if (this.busy() || !this.verified()) return;
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

import { Component, DestroyRef, ElementRef, HostListener, OnInit, computed, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatTabsModule } from '@angular/material/tabs';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatIconModule } from '@angular/material/icon';
import { MatSidenavModule } from '@angular/material/sidenav';
import { BrowserConnector, ConnectorResult, olderVersion } from './browser-connector';
import { BridgeStatus, ExtensionSetup, OverleafApi, OverleafProject, describeFailure, needsOverleafSignIn } from './overleaf-api';
import { ResearchPanel } from './research-panel';
import { LeaveResult, ResearchApi, ResearchStatus } from './research-api';
import { LocalPage, MeldToolbar } from './meld-toolbar';
import { ProjectSelection } from './project-selection';
import { ResearcherSetup } from './researcher-setup';
import { PublicationsList } from './publications';
import { PrivateProfile, skippedCount } from './private-profile';
import { MeldLog } from './meld-log';
import { EncryptedSync } from './encrypted-sync';
import { BackupData } from './backup-data';
import { LeaveMeld } from './leave-meld';
import { GmailSetup } from './gmail-setup';
import { InterfaceLock } from './interface-lock';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { includedCount, researcherDone, undecidedRecent } from './project-decisions';

export type SetupTab = 'meld' | 'sync' | 'backup' | 'overleaf' | 'gmail' | 'openalex';
export type OverleafTab = 'connection' | 'projects';
export type AboutTab = 'private' | 'publications' | 'skipped';
/** The account ID for which this browser has connected Overleaf at least once; later Overleaf outages keep the feed. */
const overleafConfirmedKey = 'meldresearch.overleafConnectedFor';
function readOverleafConfirmed(): string | null {
  try { return localStorage.getItem(overleafConfirmedKey); } catch { return null; }
}
const installDismissedKey = 'meldresearch.installTipDismissed';
/** The account ID for which this browser has finished (restored or skipped) the optional restore step of setup. */
const restoreSettledKey = 'meldresearch.restoreSettledFor';
function readRestoreSettled(): string | null {
  try { return localStorage.getItem(restoreSettledKey); } catch { return null; }
}
/** The connector version this tab already asked an older connector to reload into, so a failed reload is not repeated. */
const reloadRequestedKey = 'meldresearch.connectorReloadRequested';
/** Chrome/Edge's deferred install prompt (not in the TypeScript DOM library). */
interface InstallPromptEvent extends Event {
  prompt(): Promise<void>;
  readonly userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }>;
}

@Component({
  selector: 'app-root',
  imports: [ResearchPanel, MeldToolbar, ProjectSelection, ResearcherSetup, PublicationsList, PrivateProfile, MeldLog, EncryptedSync, BackupData, LeaveMeld, GmailSetup, MatProgressBarModule, MatButtonModule, MatCardModule, MatTabsModule,
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

  /** An upload (or similar) locks the whole interface; the overlay shows its progress and then its outcome. */
  readonly lock = inject(InterfaceLock);
  private readonly lockClose = viewChild<unknown, ElementRef<HTMLElement>>('lockClose', { read: ElementRef });
  private readonly focusOutcome = effect(() => this.lockClose()?.nativeElement.focus());

  /** Leaving or reloading during an upload would abandon it, so the browser asks first. */
  @HostListener('window:beforeunload', ['$event'])
  confirmLeavingPage(event: BeforeUnloadEvent): void {
    if (this.lock.running()) { event.preventDefault(); event.returnValue = ''; }
  }

  @HostListener('window:hashchange')
  locationChanged(): void {
    this.page.set(this.pageFromHash());
    if (location.hash === '#gmail') this.chosenTab.set('gmail');
  }

  private pageFromHash(): LocalPage {
    // #gmail: Account → Gmail (Google's consent page returns there).
    if (location.hash === '#gmail') return 'account';
    return location.hash === '#about-me' ? 'about-me' : location.hash === '#account' ? 'account' : location.hash === '#log' ? 'log' : 'home';
  }

  // Setup: the Meld account (Google), Remote Sync (required when the server offers it: choose or enter the passphrase),
  // an optional restore from a backup, Overleaf (connection, then an active choice per recent project), then OpenAlex and,
  // when offered, the optional Gmail step.
  // Only the Account view exists until a project is included; the restore and OpenAlex never block the feed.
  private readonly researchApi = inject(ResearchApi);
  private readonly panel = viewChild(ResearchPanel);
  readonly accountAction = signal('');
  private readonly chosenTab = signal<SetupTab | null>(location.hash === '#gmail' ? 'gmail' : null);
  private readonly chosenOverleafTab = signal<OverleafTab | null>(null);
  private readonly overleafConfirmedFor = signal(readOverleafConfirmed());
  readonly setupKnown = computed(() => this.research() !== null);
  readonly accountLinked = computed(() => !!this.research()?.account);
  /** Remote Sync is a step while the server offers it (also before signing in, so the steps do not change afterwards). */
  readonly syncOffered = computed(() => { const state = this.research()?.sync?.state; return !!state && state !== 'off'; });
  /**
   * With encrypted sync offered, this computer needs the passphrase (new or existing) before anything is processed, and
   * setup waits while that is still being checked (so it does not move on before the answer).
   */
  readonly recoveryKeyShown = signal(false);
  readonly syncPending = computed(() => this.recoveryKeyShown() || ['setup', 'locked', 'checking'].includes(this.research()?.sync?.state ?? ''));
  readonly syncDone = computed(() => this.accountLinked() && !this.syncPending());
  /** The optional restore was done or skipped here, or there is research already (then there is nothing to restore into). */
  private readonly restoreSettledFor = signal(readRestoreSettled());
  readonly restoreSettled = computed(() => this.syncDone() &&
    (this.restoreSettledFor() === this.research()?.account?.id || this.includedProjects() > 0));
  /** Gmail is an optional step when Meld's server offers it; connected accounts mark it done. */
  readonly gmailOffered = computed(() => !!this.research()?.gmail?.configured || !!this.research()?.gmail?.accounts.length);
  readonly gmailConnected = computed(() => this.research()?.gmail?.accounts.filter(account => account.status === 'connected').length ?? 0);
  readonly gmailLabel = computed(() => {
    const accounts = this.research()?.gmail?.accounts ?? [];
    if (accounts.some(account => account.status !== 'connected')) return 'Reconnect needed';
    return accounts.length ? `${accounts.length} ${accounts.length === 1 ? 'account' : 'accounts'} connected` : 'Optional';
  });
  // Gmail is optional, so it comes last, after OpenAlex (which needs a decision).
  readonly steps = computed<SetupTab[]>(() => ['meld', ...(this.syncOffered() ? ['sync' as const] : []), 'backup', 'overleaf',
    'openalex', ...(this.gmailOffered() ? ['gmail' as const] : [])]);
  stepLabel(tab: SetupTab): string { return `Step ${this.steps().indexOf(tab) + 1} of ${this.steps().length}`; }
  readonly overleafReady = computed(() => this.verified() ||
    (!!this.research()?.account && this.overleafConfirmedFor() === this.research()?.account?.id));
  /** The freshest Overleaf project list: the backend's last read (also by monitoring), else this page's own refresh. */
  readonly overleafProjects = computed<ReadonlyArray<OverleafProject>>(() => this.research()?.overleafProjects?.projects ?? this.projects());
  readonly undecided = computed(() => undecidedRecent(this.research(), this.overleafProjects()));
  readonly includedProjects = computed(() => includedCount(this.research()));
  readonly openAlexDone = computed(() => researcherDone(this.research()));
  readonly overleafDone = computed(() => this.overleafReady() && !this.connectorStale() && this.includedProjects() > 0 && !this.undecided().length);
  readonly setupComplete = computed(() => this.syncDone() && this.overleafReady() && !this.connectorStale() && this.includedProjects() > 0);
  /**
   * About me tabs, as in Project Meld: Private profile, Public profile (once there is one) and Publications, then a quieter
   * Skipped tab while anything is skipped.
   */
  readonly aboutTab = signal<AboutTab>('private');
  readonly skippedCount = computed(() => skippedCount(this.research()));
  readonly aboutTabs = computed<ReadonlyArray<AboutTab>>(() => this.skippedCount() ? ['private', 'publications', 'skipped'] : ['private', 'publications']);
  readonly aboutIndex = computed(() => Math.max(0, this.aboutTabs().indexOf(this.aboutTab())));
  selectAbout(index: number): void { this.aboutTab.set(this.aboutTabs().at(index) ?? 'private'); }
  /**
   * During setup a step opens only when every step before it is finished (later steps are greyed out); once setup is
   * complete every step stays open.
   */
  readonly stepProgress = computed(() => {
    const done: Record<SetupTab, boolean> = { meld: this.accountLinked(), sync: this.syncDone(), backup: this.restoreSettled(),
      overleaf: this.overleafDone(), gmail: this.gmailConnected() > 0, openalex: this.openAlexDone() };
    const open = {} as Record<SetupTab, boolean>;
    let previousDone = true;
    for (const tab of this.steps()) {
      open[tab] = previousDone || this.setupComplete();
      previousDone &&= done[tab];
    }
    return { done, open };
  });
  stepOpen(tab: SetupTab): boolean { return this.stepProgress().open[tab] ?? false; }
  stepDone(tab: SetupTab): boolean { return this.stepProgress().done[tab]; }
  stepIcon(tab: SetupTab): string { return this.stepDone(tab) ? 'check_circle' : this.stepOpen(tab) ? 'radio_button_unchecked' : 'lock'; }
  readonly setupTab = computed<SetupTab>(() => {
    const chosen = this.chosenTab();
    if (chosen && this.stepOpen(chosen)) return chosen;
    return !this.accountLinked() ? 'meld' : this.syncPending() ? 'sync' : !this.restoreSettled() ? 'backup' : 'overleaf';
  });
  /** The step after the Meld account, for its Next button. */
  readonly nextStep = computed<{ tab: SetupTab; label: string } | null>(() =>
    this.syncPending() && this.syncOffered() ? { tab: 'sync', label: 'Remote Sync' }
      : !this.restoreSettled() ? { tab: 'backup', label: 'restore from a backup (optional)' }
      : !this.verified() ? { tab: 'overleaf', label: 'connect Overleaf' } : null);
  readonly syncLabel = computed(() => {
    const sync = this.research()?.sync;
    switch (sync?.state) {
      case 'setup': return 'Choose a passphrase';
      case 'locked': return 'Enter your passphrase';
      case 'checking': return 'Checking...';
      case 'signed-out': return 'Sign in first';
      case 'ready': if (this.recoveryKeyShown()) return 'Save your recovery key';
        return `On · ${sync.syncedProjects} ${sync.syncedProjects === 1 ? 'project' : 'projects'} synced`;
      default: return '';
    }
  });
  readonly backupLabel = computed(() => this.setupComplete() ? 'Download or restore' : this.restoreSettled() ? 'Done' : 'Optional restore');
  readonly overleafTab = computed<OverleafTab>(() => this.chosenOverleafTab() ??
    (this.overleafReady() && !this.connectorStale() && !this.setupNeeded() && this.connector()?.kind === 'available' ? 'projects' : 'connection'));
  // The Log stays reachable during setup: it is where setup problems are explained.
  readonly view = computed<LocalPage>(() => this.page() === 'log' ? 'log' : this.setupKnown() && this.setupComplete() ? this.page() : 'account');
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
    // Account → Overleaf → Projects until the choice is made. With encrypted sync, choices made on another computer arrive
    // once this one is unlocked, and from a backup once restored, so the nudge waits for both steps.
    const nudged = new Set<string>();
    effect(() => {
      if (!this.setupKnown() || !this.restoreSettled() || !this.overleafReady() || this.connectorStale()) return;
      const fresh = this.undecided().filter(project => !nudged.has(project.id));
      if (!fresh.length) return;
      for (const project of fresh) nudged.add(project.id);
      untracked(() => this.open('overleaf', 'projects'));
    });
    // Once per page load, OpenAlex is offered after the projects are settled (projects come first).
    // Returning from Google to Account → Gmail stays there (the OpenAlex offer waits for the next page load).
    let openAlexNudged = location.hash === '#gmail';
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

  chooseTab(tab: SetupTab): void { if (this.stepOpen(tab)) this.chosenTab.set(tab); }
  /** A step finished (passphrase saved or unlocked): setup continues with the next open step. */
  stepFinished(): void { this.chosenTab.set(null); }
  /** The optional restore was done or skipped; it is not offered again for this account in this browser. */
  settleRestore(): void {
    const account = this.research()?.account?.id;
    if (!account) return;
    try { localStorage.setItem(restoreSettledKey, account); } catch { /* storage disabled: offered again next time */ }
    this.restoreSettledFor.set(account);
    this.stepFinished();
  }
  chooseOverleafTab(tab: OverleafTab): void { this.chosenOverleafTab.set(tab); }
  private open(tab: SetupTab, overleafTab?: OverleafTab): void {
    this.chosenTab.set(tab);
    if (overleafTab) this.chosenOverleafTab.set(overleafTab);
    if (location.hash !== '#account') location.hash = '#account';
    this.page.set('account');
  }
  /** A project or researcher action changed the research state; show it without waiting for the next poll. */
  researchChanged(): void { void this.panel()?.refresh(); }
  /** After leaving, only the farewell remains: every poll stops (the app is erased or already shutting down). */
  readonly farewell = signal<LeaveResult | null>(null);
  leave(result: LeaveResult): void {
    this.stopped = true;
    this.farewell.set(result);
  }

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
    void this.checkStatus();
    const timer = setInterval(() => { void this.checkStatus(); }, 3000);
    const wait = setTimeout(() => this.listWaitOver.set(true), 15000);
    this.destroyRef.onDestroy(() => { this.stopped = true; clearInterval(timer); clearTimeout(wait); });
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

}

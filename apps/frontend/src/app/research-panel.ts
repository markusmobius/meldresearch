import { DatePipe, NgTemplateOutlet } from '@angular/common';
import { Component, DestroyRef, OnInit, TemplateRef, computed, inject, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { describeFailure } from './overleaf-api';
import { GmailThread, GmailThreadAction, ResearchApi, ResearchStatus, ResearchUpdate } from './research-api';
import { LocalPage } from './meld-toolbar';

/** One card of the Updates column: an Update of an Overleaf project, or an email thread with newsworthy progress. */
export type FeedItem = { key: string; at: string; update: ResearchUpdate; thread?: undefined } | { key: string; at: string; thread: GmailThread; update?: undefined };
/** Bullets shown on a thread card; the rest open in Review. */
export const THREAD_BULLETS_SHOWN = 4;

/**
 * Home: a TweetDeck-style feed ported from Project Meld. Updates holds Overleaf Updates and email threads with newsworthy
 * progress (each thread one card whose bullet list grows); Private updates holds threads Meld could not place on a
 * research project, where the user skips them, adds them to a project or starts a new one. On narrow screens one column
 * shows at a time.
 */
@Component({
  selector: 'app-research-panel',
  imports: [DatePipe, NgTemplateOutlet, MatButtonModule, MatCardModule, MatDialogModule, MatFormFieldModule, MatIconModule, MatInputModule, MatMenuModule,
    MatProgressBarModule],
  templateUrl: './research-panel.html',
  styleUrl: './research-panel.css',
  host: { '[class.feed-view]': "page() === 'home'" },
})
export class ResearchPanel implements OnInit {
  readonly page = input<LocalPage>('home');
  readonly statusChange = output<ResearchStatus>();
  private readonly api = inject(ResearchApi);
  private readonly destroy = inject(DestroyRef);
  private readonly dialog = inject(MatDialog);
  private evidenceDialog: MatDialogRef<unknown> | null = null;
  private polling = false;
  private stopped = false;
  private failedPoll = false;
  readonly state = signal<ResearchStatus | null>(null);
  readonly error = signal('');
  readonly action = signal('');
  readonly selectedUpdate = signal<ResearchUpdate | null>(null);
  readonly selectedThread = signal<GmailThread | null>(null);
  /** The column shown on narrow screens. */
  readonly column = signal<'updates' | 'private'>('updates');
  /** The thread being decided (address|id), and the thread whose new project is being named. */
  readonly deciding = signal<string | null>(null);
  readonly naming = signal<string | null>(null);
  readonly projectName = signal('');
  readonly threadError = signal('');
  readonly threadBulletsShown = THREAD_BULLETS_SHOWN;
  /** Whether Private updates is offered: Meld's server offers Gmail, or an account is connected. */
  readonly privateColumn = computed(() => !!this.state()?.gmail?.configured || !!this.state()?.gmail?.accounts.length);
  readonly feedItems = computed<ReadonlyArray<FeedItem>>(() => {
    const state = this.state();
    if (!state) return [];
    const items: FeedItem[] = state.updates.map(update => ({ key: 'u:' + update.id, at: update.observedAt, update }));
    for (const thread of state.gmail?.threads ?? [])
      if (thread.status === 'assigned' && thread.newsAt && !thread.skipped) items.push({ key: `t:${thread.address}|${thread.id}`, at: thread.newsAt, thread });
    return items.sort((a, b) => Date.parse(b.at) - Date.parse(a.at));
  });
  readonly pending = computed(() => (this.state()?.gmail?.threads ?? []).filter(thread => thread.status === 'pending' && !thread.skipped)
    .slice().sort((a, b) => Date.parse(b.lastSentAt) - Date.parse(a.lastSentAt)));
  /** Research cards a thread can be put on, by title. */
  readonly cards = computed(() => (this.state()?.themes ?? []).filter(theme => !!theme.profile && !theme.skipped)
    .map(theme => ({ id: theme.id, title: theme.profile!.title })).sort((a, b) => a.title.localeCompare(b.title)));
  /** Included projects with an error or a changed notice, named in the feed so it is clear which ones need attention. */
  readonly attentionProjects = computed(() => (this.state()?.projects ?? [])
    .filter(project => (!!project.error && project.phase !== 'retrying') || project.consentRequired).map(project => project.name));
  readonly needsProjectAttention = computed(() => this.attentionProjects().length > 0);
  readonly progressPercent = computed(() => {
    const running = this.state()?.processing;
    return running?.filesTotal ? Math.round(100 * running.filesDone / running.filesTotal) : 0;
  });
  /** The file name without its folders, for the compact processing strip; the full path is in the tooltip. */
  fileName(path: string): string { return path.split('/').pop() ?? path; }

  ngOnInit(): void {
    void this.refresh();
    const timer = setInterval(() => { void this.refresh(); }, 3000);
    this.destroy.onDestroy(() => { this.stopped = true; clearInterval(timer); this.evidenceDialog?.close(); });
  }

  async refresh(): Promise<void> {
    if (this.polling || this.stopped) return;
    this.polling = true;
    try {
      const next = await this.api.status();
      if (next.account?.id !== this.state()?.account?.id) {
        this.evidenceDialog?.close();
        this.selectedUpdate.set(null);
      }
      if (this.selectedUpdate() && !next.updates.some(update => update.id === this.selectedUpdate()?.id)) {
        this.evidenceDialog?.close();
        this.selectedUpdate.set(null);
      }
      const shown = this.selectedThread();
      if (shown) {
        const current = next.gmail?.threads?.find(thread => thread.address === shown.address && thread.id === shown.id);
        if (current) this.selectedThread.set(current);
        else { this.evidenceDialog?.close(); this.selectedThread.set(null); }
      }
      this.state.set(next);
      this.statusChange.emit(next);
      if (this.failedPoll) { this.error.set(''); this.failedPoll = false; }
    } catch (error: unknown) {
      this.failedPoll = true;
      this.error.set(describeFailure(error));
    } finally { this.polling = false; }
  }

  /** An Update from a Gmail workstream (its evidence is email, not an Overleaf project). */
  fromGmail(update: ResearchUpdate): boolean { return update.projectId.startsWith('gmail-'); }
  threadKey(thread: Pick<GmailThread, 'address' | 'id'>): string { return `${thread.address}|${thread.id}`; }
  /** The latest bullets of a thread card, oldest of them first. */
  latestBullets(thread: GmailThread): GmailThread['bullets'] { return thread.bullets.slice(-THREAD_BULLETS_SHOWN); }

  showUpdate(update: ResearchUpdate, template: TemplateRef<unknown>): void {
    this.selectedThread.set(null);
    this.open(template, () => this.selectedUpdate.set(update));
  }
  showThread(thread: GmailThread, template: TemplateRef<unknown>): void {
    this.selectedUpdate.set(null);
    this.open(template, () => this.selectedThread.set(thread));
  }
  private open(template: TemplateRef<unknown>, select: () => void): void {
    this.evidenceDialog?.close();
    select();
    const dialog = this.dialog.open(template, { width: '680px', maxWidth: 'calc(100vw - 32px)', maxHeight: 'calc(100dvh - 32px)' });
    this.evidenceDialog = dialog;
    dialog.afterClosed().subscribe(() => {
      if (this.evidenceDialog === dialog) {
        this.evidenceDialog = null;
        this.selectedUpdate.set(null);
        this.selectedThread.set(null);
      }
    });
  }

  /** Starts naming a new research project for a thread, with Meld's suggestion as the default name. */
  startNaming(thread: GmailThread): void {
    this.threadError.set('');
    this.projectName.set(thread.suggested ?? thread.title);
    this.naming.set(this.threadKey(thread));
  }
  async decide(thread: GmailThread, action: GmailThreadAction, cardId?: string): Promise<void> {
    const key = this.threadKey(thread);
    if (this.deciding()) return;
    const name = action === 'new' ? this.projectName().trim() : undefined;
    if (action === 'new' && !name) { this.threadError.set('Give the new project a name.'); return; }
    this.deciding.set(key);
    this.threadError.set('');
    try {
      await this.api.decideGmailThread(thread, action, cardId, name);
      if (this.naming() === key) this.naming.set(null);
      await this.refresh();
    } catch (error: unknown) { this.threadError.set(describeFailure(error)); }
    finally { this.deciding.set(null); }
  }
  async sync(): Promise<void> { await this.run('Requesting a content check...', () => this.api.sync()); }
  private async run(label: string, operation: () => Promise<void>): Promise<void> {
    if (this.action()) return;
    this.action.set(label);
    this.failedPoll = false;
    this.error.set('');
    try {
      await operation();
      await this.refresh();
    } catch (error: unknown) { this.error.set(describeFailure(error)); }
    finally { this.action.set(''); }
  }
}

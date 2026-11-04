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
import { GmailThread, HousekeepingGroup, PendingFile, ResearchApi, ResearchChange, ResearchStatus } from './research-api';
import { LocalPage } from './meld-toolbar';
import { ChooserData, ChooserResult, ProjectChooser } from './project-chooser';
import { firstValueFrom } from 'rxjs';

/**
 * One card of Housekeeping: a group of waiting email threads (group null while a thread is still being grouped: a card of
 * its own), or the files removed from one research project (files).
 */
export interface HousekeepingCard {
  key: string; at: string; name: string; group: HousekeepingGroup | null; threads: ReadonlyArray<GmailThread>;
  files: ReadonlyArray<PendingFile> | null; fromCardId: string | null;
}
/** Bullets shown on a single thread, threads shown in a group, and detail changes on an Update card; the rest open on request. */
export const THREAD_BULLETS_SHOWN = 4;
export const GROUP_THREADS_SHOWN = 4;
export const CHANGES_SHOWN = 3;

/**
 * Home: a TweetDeck-style feed ported from Project Meld. Updates holds the changelog entries that changed a research
 * project's details (from Overleaf or email); Housekeeping holds email threads Meld could not place on a research project,
 * grouped by topic, and files the user removed from one, where the user decides on a whole group at once: a new project,
 * an existing one, or skip. On narrow screens one column shows at a time.
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
  readonly selectedChange = signal<ResearchChange | null>(null);
  readonly selectedThread = signal<GmailThread | null>(null);
  /** The column shown on narrow screens. */
  readonly column = signal<'updates' | 'private'>('updates');
  /** The Housekeeping card being decided, the card whose new project is being named, and the group being renamed (card keys). */
  readonly deciding = signal<string | null>(null);
  readonly naming = signal<string | null>(null);
  readonly projectName = signal('');
  readonly renaming = signal<string | null>(null);
  readonly groupName = signal('');
  /** Groups showing all their threads. */
  readonly expanded = signal<ReadonlySet<string>>(new Set());
  readonly threadError = signal('');
  readonly threadBulletsShown = THREAD_BULLETS_SHOWN;
  readonly groupThreadsShown = GROUP_THREADS_SHOWN;
  readonly changesShown = CHANGES_SHOWN;
  /** Whether Housekeeping is offered: Meld's server offers Gmail, an account is connected, or a removed file waits there. */
  readonly privateColumn = computed(() => !!this.state()?.gmail?.configured || !!this.state()?.gmail?.accounts.length || !!this.state()?.pendingFiles?.length);
  /** Updates: changelog entries that changed details, newest first. */
  readonly feedItems = computed<ReadonlyArray<ResearchChange>>(() => this.state()?.changes ?? []);
  /** Housekeeping: groups of waiting threads, threads still being grouped, and removed files by project; newest first. */
  readonly housekeeping = computed<ReadonlyArray<HousekeepingCard>>(() => {
    const state = this.state();
    const groups = new Map((state?.gmail?.groups ?? []).map(group => [group.id, group] as const));
    const byGroup = new Map<string, GmailThread[]>();
    const cards: HousekeepingCard[] = [];
    const newest = (threads: ReadonlyArray<GmailThread>) => [...threads].sort((a, b) => Date.parse(b.lastSentAt) - Date.parse(a.lastSentAt));
    for (const thread of (state?.gmail?.threads ?? []).filter(item => item.status === 'pending' && !item.skipped)) {
      if (thread.groupId && groups.has(thread.groupId)) byGroup.set(thread.groupId, [...byGroup.get(thread.groupId) ?? [], thread]);
      else cards.push({ key: 't:' + this.threadKey(thread), at: thread.lastSentAt, name: thread.suggested ?? thread.title, group: null,
        threads: [thread], files: null, fromCardId: null });
    }
    for (const [id, threads] of byGroup) {
      const sorted = newest(threads);
      cards.push({ key: 'g:' + id, at: sorted[0].lastSentAt, name: groups.get(id)!.name, group: groups.get(id)!, threads: sorted, files: null, fromCardId: null });
    }
    const byCard = new Map<string, PendingFile[]>();
    for (const file of state?.pendingFiles ?? []) byCard.set(file.fromCardId, [...byCard.get(file.fromCardId) ?? [], file]);
    for (const [fromCardId, files] of byCard) {
      const sorted = [...files].sort((a, b) => Date.parse(b.removedAt) - Date.parse(a.removedAt));
      cards.push({ key: 'f:' + fromCardId, at: sorted[0].removedAt, name: 'Removed from ' + (sorted[0].fromCardTitle ?? 'a research project'),
        group: null, threads: [], files: sorted, fromCardId });
    }
    return cards.sort((a, b) => Date.parse(b.at) - Date.parse(a.at));
  });
  /** Groups a thread can be moved to: every other group shown. */
  readonly groups = computed(() => this.housekeeping().filter(card => card.group).map(card => card.group!));
  /** Research cards a thread can be put on, by title. */
  readonly cards = computed(() => (this.state()?.themes ?? []).filter(theme => !!theme.profile && !theme.skipped)
    .map(theme => ({ id: theme.id, title: theme.name || theme.profile!.title })).sort((a, b) => a.title.localeCompare(b.title)));
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
        this.selectedChange.set(null);
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

  kindLabel(change: ResearchChange): string {
    return { overleaf: 'Overleaf', gmail: 'Email', removed: 'Source removed', user: 'Your change', merge: 'Merged', review: 'Review' }[change.kind] ?? 'Update';
  }
  kindIcon(change: ResearchChange): string {
    return { overleaf: 'description', gmail: 'mail', removed: 'remove_circle_outline', user: 'person', merge: 'call_merge', review: 'fact_check' }[change.kind] ?? 'update';
  }
  opLabel(op: string): string { return op === 'add' ? 'Added' : op === 'remove' ? 'Removed' : 'Revised'; }
  threadKey(thread: Pick<GmailThread, 'address' | 'id'>): string { return `${thread.address}|${thread.id}`; }
  /** The latest bullets of a thread card, oldest of them first. */
  latestBullets(thread: GmailThread): GmailThread['bullets'] { return thread.bullets.slice(-THREAD_BULLETS_SHOWN); }

  showChange(change: ResearchChange, template: TemplateRef<unknown>): void {
    this.selectedThread.set(null);
    this.open(template, () => this.selectedChange.set(change));
  }
  showThread(thread: GmailThread, template: TemplateRef<unknown>): void {
    this.selectedChange.set(null);
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
        this.selectedChange.set(null);
        this.selectedThread.set(null);
      }
    });
  }

  /** The threads a group shows: its newest few, or all of them once expanded. */
  shownThreads(card: HousekeepingCard): ReadonlyArray<GmailThread> {
    return this.expanded().has(card.key) ? card.threads : card.threads.slice(0, GROUP_THREADS_SHOWN);
  }
  expand(card: HousekeepingCard): void { this.expanded.update(keys => new Set(keys).add(card.key)); }
  /** Projects a card's items can be added to (removed files not back to the project they came from). */
  targets(card: HousekeepingCard): ReturnType<ResearchPanel['cards']> {
    return this.cards().filter(item => item.id !== card.fromCardId);
  }
  /** Groups a thread can move to: all but its own. */
  otherGroups(thread: GmailThread): ReadonlyArray<HousekeepingGroup> { return this.groups().filter(group => group.id !== thread.groupId); }

  /** Starts naming a new research project for a group (or a thread), with the group's name as the default. */
  startNaming(card: HousekeepingCard): void {
    this.threadError.set('');
    this.projectName.set(card.name);
    this.naming.set(card.key);
  }
  startRenaming(card: HousekeepingCard): void {
    this.threadError.set('');
    this.groupName.set(card.name);
    this.renaming.set(card.key);
  }
  /** One new research project from all of a card's threads. */
  async createProject(card: HousekeepingCard): Promise<void> {
    const name = this.projectName().trim();
    if (!name) { this.threadError.set('Give the new project a name.'); return; }
    if (await this.act(card.key, () => card.group ? this.api.decideHousekeepingGroup(card.group.id, 'new', undefined, name)
      : this.api.decideGmailThread(card.threads[0], 'new', undefined, name))) this.naming.set(null);
  }
  /** Adds all of a card's threads, or removed files, to a research project. */
  async addToProject(card: HousekeepingCard, cardId: string): Promise<void> {
    await this.act(card.key, async () => {
      if (card.files) for (const file of card.files) await this.api.decidePendingFile(file, 'project', cardId);
      else if (card.group) await this.api.decideHousekeepingGroup(card.group.id, 'project', cardId);
      else await this.api.decideGmailThread(card.threads[0], 'project', cardId);
    });
  }
  /** Skips everything on a card: threads become one skipped project, removed files are left out. */
  async skipCard(card: HousekeepingCard): Promise<void> {
    await this.act(card.key, async () => {
      if (card.files) for (const file of card.files) await this.api.decidePendingFile(file, 'dismiss');
      else if (card.group) await this.api.decideHousekeepingGroup(card.group.id, 'dismiss');
      else await this.api.decideGmailThread(card.threads[0], 'dismiss');
    });
  }
  async rename(card: HousekeepingCard): Promise<void> {
    const name = this.groupName().trim();
    if (!card.group || !name) { this.threadError.set('Give the group a name.'); return; }
    if (await this.act(card.key, () => this.api.renameHousekeepingGroup(card.group!.id, name))) this.renaming.set(null);
  }
  /** Moves one thread to another group, or to a new group of its own (groupId null); it stays there. */
  async moveThread(card: HousekeepingCard, thread: GmailThread, groupId: string | null): Promise<void> {
    await this.act(card.key, () => this.api.decideGmailThread(thread, 'group', undefined, undefined, groupId));
  }
  /** Opens the project chooser (wide, closest first; select, then confirm). */
  private choose(data: ChooserData): Promise<ChooserResult> {
    return firstValueFrom(this.dialog.open<ProjectChooser, ChooserData, ChooserResult>(ProjectChooser,
      { data, width: '680px', maxWidth: '100vw', maxHeight: '92dvh', autoFocus: 'first-tabbable' }).afterClosed());
  }
  /** Add to project for a whole card: its threads (or removed files) go to the chosen project; "a new project" starts naming one. */
  async chooseProject(card: HousekeepingCard): Promise<void> {
    const count = card.files?.length ?? card.threads.length;
    const choice = await this.choose({
      heading: 'Add to a research project', confirm: 'Add',
      subject: card.files ? `${count} ${count === 1 ? 'file' : 'files'} removed from ${card.files[0].fromCardTitle ?? 'a research project'}`
        : `“${card.name}” · ${count} email ${count === 1 ? 'thread' : 'threads'}`,
      kind: card.files ? 'files' : card.group ? 'group' : 'thread',
      id: card.files ? card.fromCardId! : card.group ? card.group.id : this.threadKey(card.threads[0]),
      extra: card.files ? null : { label: 'Create a new project instead', hint: 'Name a new research project for these threads.' },
    });
    if (choice === 'new') this.startNaming(card);
    else if (choice) await this.addToProject(card, choice);
  }
  /** Add to project for one thread of a group. */
  async chooseThreadProject(card: HousekeepingCard, thread: GmailThread): Promise<void> {
    const choice = await this.choose({ heading: 'Add this thread to a research project', confirm: 'Add', subject: `“${thread.title}”`,
      kind: 'thread', id: this.threadKey(thread) });
    if (choice) await this.decideThread(card, thread, 'project', choice);
  }
  /** One thread of a group: to a research project, or skipped. */
  async decideThread(card: HousekeepingCard, thread: GmailThread, action: 'project' | 'dismiss', cardId?: string): Promise<void> {
    await this.act(card.key, () => this.api.decideGmailThread(thread, action, cardId));
  }
  /** One removed file left out. */
  async skipFile(card: HousekeepingCard, file: PendingFile): Promise<void> {
    await this.act(card.key, () => this.api.decidePendingFile(file, 'dismiss'));
  }
  private async act(key: string, operation: () => Promise<void>): Promise<boolean> {
    if (this.deciding()) return false;
    this.deciding.set(key);
    this.threadError.set('');
    try {
      await operation();
      await this.refresh();
      return true;
    } catch (error: unknown) {
      this.threadError.set(describeFailure(error));
      await this.refresh();
      return false;
    } finally { this.deciding.set(null); }
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

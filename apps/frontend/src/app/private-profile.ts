import { DatePipe } from '@angular/common';
import { Component, Directive, ElementRef, OnDestroy, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatDialog } from '@angular/material/dialog';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTabsModule } from '@angular/material/tabs';
import { firstValueFrom } from 'rxjs';
import { describeFailure } from './overleaf-api';
import { ChooserData, ChooserResult, ProjectChooser } from './project-chooser';
import {
  Collaborator, CollaboratorKind, DetailSource, GmailThread, ResearchApi, ResearchChange, ResearchDetail, ResearchProject, ResearchStatus,
} from './research-api';

/** Emits once when the element scrolls into view (the changelog's "earlier entries" marker). */
@Directive({ selector: '[appInView]' })
export class InView implements OnInit, OnDestroy {
  readonly appInView = output<void>();
  private readonly element = inject(ElementRef<HTMLElement>);
  private observer: IntersectionObserver | null = null;
  ngOnInit(): void {
    if (typeof IntersectionObserver === 'undefined') return;
    this.observer = new IntersectionObserver(entries => {
      if (entries.some(entry => entry.isIntersecting)) { this.observer?.disconnect(); this.appInView.emit(); }
    }, { rootMargin: '200px' });
    this.observer.observe(this.element.nativeElement);
  }
  ngOnDestroy(): void { this.observer?.disconnect(); }
}

/** One research project: a card with its Overleaf projects, or a project still being read before it gets a card. */
interface ProfileCard {
  /** title: the short name (or the user's name); fullTitle: the main paper's full title when it differs. */
  id: string; themeId: string | null; title: string; fullTitle: string | null; nameChosen: boolean;
  /** The two-sentence summary, written from the details. */
  summary: string | null; details: ReadonlyArray<ResearchDetail>; members: ReadonlyArray<ResearchProject>;
  /** The member the card's title comes from (its main paper), and why its projects were grouped. */
  anchorId: string | null; reason: string | null;
  /** Skipped as not the user's own research; threads: the skipped email threads on it. */
  skipped: boolean; threads: ReadonlyArray<GmailThread>;
  /** The people the research is done with; how many current details have known people, and the user's share of them. */
  collaborators: ReadonlyArray<Collaborator>; knownDetails: number; yourShare: number | null;
  updating: boolean; progress: ResearchProject | null;
  /** The latest change of the research: a source's date or an Overleaf edit of a member; orders the cards. */
  lastChanged: number | null;
}

/** A card's changelog as loaded so far (pages of 30, newest first). */
interface ChangelogView { entries: ReadonlyArray<ResearchChange>; more: boolean; loading: boolean; error: string; }

const BUSY = new Set(['importing', 'queued', 'waiting', 'checking']);
type Tab = 'details' | 'changelog' | 'projects' | 'collaborators';
const KINDS: Record<CollaboratorKind, { icon: string; label: string }> = {
  author: { icon: 'article', label: 'Author' }, owner: { icon: 'key', label: 'Owns the Overleaf project' },
  editor: { icon: 'edit_note', label: 'Edits in Overleaf' }, from: { icon: 'mail', label: 'Writes email' },
  to: { icon: 'forward_to_inbox', label: 'Receives email' }, cc: { icon: 'forward_to_inbox', label: 'Copied on email' },
};
/** Cards, and the threads of an open skipped card, are shown in pages of this size; more load while scrolling. */
const PAGE = 20;

/** The entries of About me → Skipped: skipped cards with something on them, plus one for skipped threads still being grouped. */
export function skippedCount(status: ResearchStatus | null): number {
  if (!status) return 0;
  const enabled = new Set(status.projects.filter(project => project.enabled).map(project => project.projectId));
  const threads = (status.gmail?.threads ?? []).filter(thread => thread.skipped);
  const cards = (status.themes ?? []).filter(theme => theme.skipped && (theme.projectIds.some(id => enabled.has(id))
    || (theme.details ?? []).length || threads.some(thread => thread.cardId === theme.id))).length;
  return cards + (threads.some(thread => !thread.cardId) ? 1 : 0);
}
const mailTime = (thread: GmailThread): number => (thread.lastSentAt ? Date.parse(thread.lastSentAt) : NaN) || 0;

/**
 * About me → My projects, ported from Project Meld's overview: one card per research project with its title, a two-sentence
 * summary and its Overleaf projects. A card expands in place to its details (at most 30 sentences, each with its sources; a
 * source can be removed, which sends it to Housekeeping), its changelog (every source that updated it, newest first,
 * loaded while scrolling) and its projects (main paper, move, merge, skip). Stored on this computer only.
 */
@Component({
  selector: 'app-private-profile',
  imports: [DatePipe, InView, MatButtonModule, MatIconModule, MatProgressBarModule, MatTabsModule],
  templateUrl: './private-profile.html',
  styleUrl: './private-profile.css',
})
export class PrivateProfile {
  readonly status = input<ResearchStatus | null>(null);
  /** About me → Private profile (the user's research) or About me → Skipped. */
  readonly section = input<'projects' | 'skipped'>('projects');
  readonly changed = output<void>();
  private readonly api = inject(ResearchApi);
  private readonly dialog = inject(MatDialog);
  readonly expandedId = signal<string | null>(null);
  readonly tab = signal<Tab>('details');
  /** The detail whose sources are shown (one at a time). */
  readonly openDetail = signal<string | null>(null);
  readonly changelogs = signal<ReadonlyMap<string, ChangelogView>>(new Map());
  readonly working = signal('');
  /** The card being renamed, and the name being typed. */
  readonly renaming = signal<string | null>(null);
  readonly nameDraft = signal('');
  readonly error = signal('');
  /** The cards of the profile: the user's own research. */
  readonly cards = computed(() => this.allCards().filter(card => !card.skipped));
  /** Work skipped as not the user's own (About me → Skipped), grouped like projects. */
  readonly skippedCards = computed(() => this.allCards().filter(card => card.skipped));
  /** Skipped threads still being grouped with related skipped work. */
  readonly ungrouped = computed(() => (this.status()?.gmail?.threads ?? []).filter(thread => thread.skipped && !thread.cardId));
  /** How many cards are shown; grows by a page while the user scrolls. */
  readonly cardLimit = signal(PAGE);
  readonly shownCards = computed(() => this.cards().slice(0, this.cardLimit()));
  /** About me → Skipped: skipped cards and the threads still being grouped, the most recent email first. */
  readonly skippedList = computed<ReadonlyArray<ProfileCard>>(() => {
    const list = [...this.skippedCards()];
    const loose = this.ungrouped();
    if (loose.length) {
      list.push({ id: 'ungrouped', themeId: null, title: 'Being grouped\u2026', fullTitle: null, nameChosen: false, summary: null, details: [],
        members: [], anchorId: null, reason: null, skipped: true, threads: loose, updating: false, progress: null, lastChanged: null, collaborators: [],
        knownDetails: 0, yourShare: null });
    }
    const latest = (card: ProfileCard) => Math.max(card.lastChanged ?? 0, ...card.threads.map(mailTime));
    return list.map(card => ({ card, at: latest(card) })).sort((a, b) => b.at - a.at).map(item => item.card);
  });
  readonly shownSkipped = computed(() => this.skippedList().slice(0, this.cardLimit()));
  /** The skipped card opened to its threads (one at a time), and how many of its threads are shown. */
  readonly openSkipped = signal<string | null>(null);
  readonly threadLimit = signal(PAGE);
  private readonly allCards = computed<ReadonlyArray<ProfileCard>>(() => {
    const status = this.status();
    if (!status) return [];
    const shown = status.projects.filter(project => project.enabled);
    const byId = new Map(shown.map(project => [project.projectId, project] as const));
    const edited = new Map((status.overleafProjects?.projects ?? []).map(project => [project.id, project.updatedAt ? Date.parse(project.updatedAt) : NaN] as const));
    const latest = (ids: ReadonlyArray<string>, details: ReadonlyArray<ResearchDetail>): number | null => {
      const times = [...ids.map(id => edited.get(id) ?? NaN),
        ...details.flatMap(detail => detail.sources).map(source => source.at ? Date.parse(source.at) : NaN)].filter(Number.isFinite);
      return times.length ? Math.max(...times) : null;
    };
    const cards: ProfileCard[] = [];
    const placed = new Set<string>();
    for (const theme of status.themes ?? []) {
      const members = theme.projectIds.map(id => byId.get(id)).filter((project): project is ResearchProject => !!project);
      const details = theme.details ?? [];
      const threads = (status.gmail?.threads ?? []).filter(thread => thread.skipped && thread.cardId === theme.id);
      if (!members.length && !details.length && !threads.length) continue;
      members.forEach(member => placed.add(member.projectId));
      cards.push({
        id: theme.id, themeId: theme.id, title: theme.name || theme.profile?.title || members[0]?.name || 'Research project',
        fullTitle: theme.name && theme.profile?.title && theme.profile.title !== theme.name ? theme.profile.title : null, nameChosen: !!theme.nameChosen,
        summary: theme.profile?.description || null, details, members, anchorId: theme.anchorId ?? null, reason: theme.reason ?? null,
        skipped: !!theme.skipped, threads, updating: theme.updating, progress: members.find(member => BUSY.has(member.phase)) ?? null,
        lastChanged: latest(theme.projectIds, details), collaborators: theme.collaborators ?? [],
        knownDetails: theme.knownDetails ?? 0, yourShare: theme.yourShare ?? null,
      });
    }
    // Projects still being read, before their first details give them a card.
    for (const project of shown.filter(item => !placed.has(item.projectId))) {
      cards.push({
        id: project.projectId, themeId: null, title: project.name, fullTitle: null, nameChosen: false, summary: null, details: [], members: [project], anchorId: null, reason: null,
        skipped: false, threads: [], updating: false, progress: BUSY.has(project.phase) ? project : null, lastChanged: latest([project.projectId], []),
        collaborators: [], knownDetails: 0, yourShare: null,
      });
    }
    return cards.sort((a, b) => (b.lastChanged ?? 0) - (a.lastChanged ?? 0));
  });
  /** Cards a project can be moved to, or a card merged into: every other card of the profile. */
  readonly targets = computed(() => this.cards().filter(card => card.themeId && card.id !== this.expandedId()));

  /** Opens a card in place (closing the one open before), or closes it. */
  toggle(card: ProfileCard): void {
    const opening = this.expandedId() !== card.id;
    this.expandedId.set(opening ? card.id : null);
    this.tab.set('details');
    this.openDetail.set(null);
    this.error.set('');
  }
  selectTab(card: ProfileCard, index: number): void {
    const tab: Tab = index === 1 ? 'changelog' : index === 2 ? 'projects' : index === 3 ? 'collaborators' : 'details';
    this.tab.set(tab);
    if (tab === 'changelog' && card.themeId) void this.loadChangelog(card.themeId, true);
  }
  /** A share of the current details as a whole percentage ("20%"), or "" when unknown. */
  percent(share: number | null | undefined): string { return share == null ? '' : `${Math.round(share * 100)}%`; }
  /** How a collaborator appears on the project, as an icon and a label. */
  kindInfo(kind: CollaboratorKind): { icon: string; label: string } { return KINDS[kind] ?? { icon: 'person', label: kind }; }
  toggleDetail(detail: ResearchDetail): void { this.openDetail.update(open => open === detail.id ? null : detail.id); }
  showMoreCards(): void { this.cardLimit.update(limit => limit + PAGE); }
  showMoreThreads(): void { this.threadLimit.update(limit => limit + PAGE); }
  toggleSkipped(card: ProfileCard): void {
    this.openSkipped.update(open => open === card.id ? null : card.id);
    this.threadLimit.set(PAGE);
  }
  /** A skipped card's threads, newest first, as far as they are shown. */
  shownThreads(card: ProfileCard): ReadonlyArray<GmailThread> {
    return [...card.threads].sort((a, b) => mailTime(b) - mailTime(a)).slice(0, this.threadLimit());
  }
  /** What a collapsed skipped card holds, e.g. "1 Overleaf project · 12 email threads". */
  skippedMeta(card: ProfileCard): string {
    const count = (n: number, one: string) => `${n} ${one}${n === 1 ? '' : 's'}`;
    return [card.members.length ? count(card.members.length, 'Overleaf project') : '', card.threads.length ? count(card.threads.length, 'email thread') : '']
      .filter(part => !!part).join(' \u00b7 ');
  }

  /** The card's projects, its main paper first. */
  memberNames(card: ProfileCard): string {
    return [...card.members].sort((a, b) => Number(b.projectId === card.anchorId) - Number(a.projectId === card.anchorId))
      .map(member => member.name).join(' \u00b7 ');
  }
  /** Distinct sources the card's details rest on. */
  sourceCount(card: ProfileCard): number { return new Set(card.details.flatMap(detail => detail.sources.map(source => source.key))).size; }
  /** Distinct sources of one detail (a document cited at two lines counts once). */
  sources(detail: ResearchDetail): ReadonlyArray<DetailSource> {
    const seen = new Map<string, DetailSource>();
    for (const source of detail.sources) if (!seen.has(source.key)) seen.set(source.key, source);
    return [...seen.values()];
  }
  quotes(detail: ResearchDetail, source: DetailSource): ReadonlyArray<string> {
    return detail.sources.filter(item => item.key === source.key).map(item => item.quote).filter(quote => !!quote);
  }
  changelog(themeId: string | null): ChangelogView | null { return themeId ? this.changelogs().get(themeId) ?? null : null; }
  kindIcon(entry: ResearchChange): string {
    return { overleaf: 'description', gmail: 'mail', removed: 'remove_circle_outline', user: 'person', merge: 'call_merge', review: 'fact_check' }[entry.kind] ?? 'history';
  }
  kindLabel(entry: ResearchChange): string {
    return { overleaf: 'Overleaf', gmail: 'Email', removed: 'Source removed', user: 'Your change', merge: 'Merged', review: 'Review' }[entry.kind] ?? 'Change';
  }

  /** Loads the first page of a card's changelog (fresh), or the next page while the user scrolls. */
  async loadChangelog(themeId: string, fresh = false): Promise<void> {
    const current = this.changelogs().get(themeId);
    if (current?.loading || (!fresh && current && !current.more)) return;
    const shown = fresh ? [] : current?.entries ?? [];
    this.setLog(themeId, { entries: shown, more: current?.more ?? true, loading: true, error: '' });
    try {
      const page = await this.api.changelog(themeId, shown.at(-1) ?? null);
      const ids = new Set(shown.map(entry => entry.id));
      this.setLog(themeId, { entries: [...shown, ...page.entries.filter(entry => !ids.has(entry.id))], more: page.more, loading: false, error: '' });
    } catch (error: unknown) {
      this.setLog(themeId, { entries: shown, more: true, loading: false, error: describeFailure(error) });
    }
  }
  private setLog(themeId: string, view: ChangelogView): void {
    this.changelogs.update(map => new Map(map).set(themeId, view));
  }

  /** Removes a source from the whole research project; it waits in Housekeeping. */
  async removeSource(card: ProfileCard, source: DetailSource): Promise<void> {
    if (!card.themeId || this.working()) return;
    await this.act(`source:${source.key}`, () => this.api.removeSource(card.themeId!, source.key));
  }
  /** Skips a card as not the user's own research, or brings a skipped card back into the profile. */
  async skip(card: ProfileCard, skipped: boolean): Promise<void> {
    if (!card.themeId) return;
    if (await this.act(card.id, () => this.api.skipTheme(card.themeId!, skipped)) && skipped) this.expandedId.set(null);
  }
  /** Brings one skipped email thread back to Housekeeping. */
  async unskip(thread: GmailThread): Promise<void> {
    await this.act(`${thread.address}|${thread.id}`, () => this.api.decideGmailThread(thread, 'unskip'));
  }
  /** Makes a project the card's main paper (its title comes from that paper). */
  async makeMain(card: ProfileCard, project: ResearchProject): Promise<void> {
    if (!card.themeId) return;
    await this.act(card.id, () => this.api.setThemeAnchor(card.themeId!, project.projectId));
  }
  startRenaming(card: ProfileCard): void {
    this.error.set('');
    this.nameDraft.set(card.title);
    this.renaming.set(card.id);
  }
  /** Saves the user's own name for a project (empty: back to its short name). */
  async rename(card: ProfileCard, name = this.nameDraft()): Promise<void> {
    if (!card.themeId) return;
    if (await this.act(card.id, () => this.api.renameTheme(card.themeId!, name.trim()))) this.renaming.set(null);
  }
  /** Opens the project chooser (wide, closest first; select, then confirm). */
  private choose(data: ChooserData): Promise<ChooserResult> {
    return firstValueFrom(this.dialog.open<ProjectChooser, ChooserData, ChooserResult>(ProjectChooser,
      { data, width: '680px', maxWidth: '100vw', maxHeight: '92dvh', autoFocus: 'first-tabbable' }).afterClosed());
  }
  /** Move: the chooser lists the other projects closest to this Overleaf project first, and a project of its own. */
  async chooseMove(card: ProfileCard, project: ResearchProject): Promise<void> {
    const choice = await this.choose({ heading: `Move ${project.name}`, confirm: 'Move', kind: 'project', id: project.projectId,
      subject: `Choose the research project that ${project.name} belongs to. Its sources leave “${card.title}”.`,
      extra: card.members.length > 1 ? { label: 'A project of its own', hint: 'Start a separate research project from this Overleaf project.' } : null });
    if (choice) await this.move(project, choice === 'new' ? null : choice);
  }
  /** Merge into: the chooser lists the projects closest to this one first. */
  async chooseMerge(card: ProfileCard): Promise<void> {
    if (!card.themeId) return;
    const choice = await this.choose({ heading: `Merge “${card.title}” into…`, confirm: 'Merge', kind: 'card', id: card.themeId,
      subject: 'Both become one research project; their details are combined.' });
    if (choice) await this.mergeInto(card, choice);
  }
  /** Combines the whole card into another card. */
  async mergeInto(card: ProfileCard, targetId: string): Promise<void> {
    if (!card.themeId) return;
    if (await this.act(card.id, () => this.api.mergeTheme(card.themeId!, targetId))) this.expandedId.set(targetId);
  }
  /** Moves one Overleaf project to another card, or to a card of its own (targetId null). */
  async move(project: ResearchProject, targetId: string | null): Promise<void> {
    await this.act(project.projectId, () => this.api.moveToTheme(project.projectId, targetId));
  }
  private async act(key: string, operation: () => Promise<void>): Promise<boolean> {
    if (this.working()) return false;
    this.working.set(key);
    this.error.set('');
    try {
      await operation();
      this.changed.emit();
      return true;
    } catch (error: unknown) {
      this.error.set(describeFailure(error));
      return false;
    } finally { this.working.set(''); }
  }
}

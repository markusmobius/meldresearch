import { DatePipe } from '@angular/common';
import { Component, TemplateRef, computed, inject, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTabsModule } from '@angular/material/tabs';
import { describeFailure } from './overleaf-api';
import { ProjectProfile, ResearchApi, ResearchProject, ResearchStatus, ResearchUpdate, isOverleafProject } from './research-api';

/** One card: a research line with one or more Overleaf projects, or a project still being imported or grouped. */
interface ProfileCard {
  id: string; themeId: string | null; profile: ProfileOrNull; members: ReadonlyArray<ResearchProject>;
  /** The member the card is described from (its main paper), and why its projects were grouped. */
  anchorId?: string | null; reason?: string | null;
  recent: ReadonlyArray<ResearchUpdate>; updating: boolean; progress: ResearchProject | null;
  /** The card's 4-5 sentence description; until it is written, the full description is shown. */
  blurb: string | null;
  /** The most recent Overleaf edit of any of the card's projects (or, failing that, its latest Update); orders the cards. */
  lastEdited: number | null;
}
type ProfileOrNull = ProjectProfile | null;

/** Updates shown with a card: those whose source changed in roughly the last three months. */
export const RECENT_UPDATE_DAYS = 92;
const BUSY = new Set(['importing', 'queued', 'waiting', 'checking']);

/**
 * About me → Private profile: one card per line of research. Overleaf projects that belong together (a paper, its slides,
 * drafts, follow-ups) share a card with a combined summary. A card shows the title, its projects, the one-paragraph
 * description and topics; it opens the contributions, its projects (which can be moved between cards) and the Updates of
 * the last three months. Stored on this computer only.
 */
@Component({
  selector: 'app-private-profile',
  imports: [DatePipe, MatButtonModule, MatDialogModule, MatIconModule, MatProgressBarModule, MatTabsModule],
  templateUrl: './private-profile.html',
  styleUrl: './private-profile.css',
})
export class PrivateProfile {
  /** Whether every member is an Overleaf project (cards may also hold Gmail workstreams). */
  allOverleaf(members: ReadonlyArray<ResearchProject>): boolean { return members.every(isOverleafProject); }
  readonly status = input<ResearchStatus | null>(null);
  readonly changed = output<void>();
  private readonly dialog = inject(MatDialog);
  private readonly api = inject(ResearchApi);
  readonly selectedId = signal<string | null>(null);
  readonly moving = signal(false);
  readonly moveError = signal('');
  readonly cards = computed<ReadonlyArray<ProfileCard>>(() => {
    const status = this.status();
    if (!status) return [];
    const since = Date.now() - RECENT_UPDATE_DAYS * 86400000;
    const shown = status.projects.filter(project => project.enabled || project.profile);
    const byId = new Map(shown.map(project => [project.projectId, project] as const));
    const edited = new Map((status.overleafProjects?.projects ?? []).map(project => [project.id, project.updatedAt ? Date.parse(project.updatedAt) : NaN] as const));
    const lastEdited = (ids: ReadonlyArray<string>): number | null => {
      const times = ids.map(id => edited.get(id) ?? NaN).concat(status.updates.filter(update => ids.includes(update.projectId))
        .map(update => Date.parse(update.sourceUpdatedAt ?? update.observedAt))).filter(time => Number.isFinite(time));
      return times.length ? Math.max(...times) : null;
    };
    // Email threads on a card count as its Updates too: their newsworthy bullets, dated by the mail.
    const threads = (cardId: string): ResearchUpdate[] => (status.gmail?.threads ?? [])
      .filter(thread => thread.status === 'assigned' && thread.cardId === cardId && !!thread.newsAt && Date.parse(thread.newsAt) >= since)
      .map(thread => ({ id: `thread:${thread.address}|${thread.id}`, projectId: '', projectName: 'Email', kind: 'change', title: thread.title,
        summary: thread.bullets.filter(bullet => bullet.newsworthy).slice(-3).map(bullet => bullet.text).join(' '),
        observedAt: thread.newsAt!, sourceUpdatedAt: thread.newsAt, revision: '', previousRevision: null, previousSummary: null,
        evidence: thread.bullets.flatMap(bullet => bullet.evidence) }));
    const recent = (ids: ReadonlyArray<string>, cardId?: string) => [...status.updates.filter(update => ids.includes(update.projectId) &&
      update.kind !== 'initialImport' && Date.parse(update.sourceUpdatedAt ?? update.observedAt) >= since), ...(cardId ? threads(cardId) : [])]
      .sort((a, b) => Date.parse(b.sourceUpdatedAt ?? b.observedAt) - Date.parse(a.sourceUpdatedAt ?? a.observedAt));
    const cards: ProfileCard[] = [];
    const placed = new Set<string>();
    for (const theme of status.themes ?? []) {
      const members = theme.projectIds.map(id => byId.get(id)).filter((project): project is ResearchProject => !!project);
      if (!members.length) continue;
      members.forEach(member => placed.add(member.projectId));
      cards.push({ id: theme.id, themeId: theme.id, profile: theme.profile ?? members[0].profile, members, recent: recent(theme.projectIds, theme.id),
        updating: theme.updating, progress: members.find(member => BUSY.has(member.phase)) ?? null, lastEdited: lastEdited(theme.projectIds),
        blurb: theme.blurb ?? null, anchorId: theme.anchorId ?? null, reason: theme.reason ?? null });
    }
    // Projects still importing, or imported and about to be placed on a card.
    for (const project of shown.filter(item => !placed.has(item.projectId))) {
      cards.push({ id: project.projectId, themeId: null, profile: project.profile, members: [project], recent: recent([project.projectId]),
        updating: !!project.profile, progress: BUSY.has(project.phase) ? project : null, lastEdited: lastEdited([project.projectId]), blurb: null });
    }
    // Most recently edited research first.
    return cards.sort((a, b) => (b.lastEdited ?? 0) - (a.lastEdited ?? 0));
  });
  /** The opened card, kept current while the dialog is open (status refreshes every few seconds). */
  readonly selected = computed(() => this.cards().find(card => card.id === this.selectedId()) ?? null);
  /** Cards a project can be moved to: every other card with a summary. */
  readonly targets = computed(() => this.cards().filter(card => card.themeId && card.profile && card.id !== this.selectedId()));

  open(card: ProfileCard, details: TemplateRef<unknown>): void {
    this.selectedId.set(card.id);
    this.moveError.set('');
    this.dialog.open(details, { width: '760px', maxWidth: '94vw', autoFocus: 'dialog', ariaLabel: card.profile?.title ?? card.members[0].name })
      .afterClosed().subscribe(() => this.selectedId.set(null));
  }

  /** The card's projects, its main paper first. */
  memberNames(card: ProfileCard): string {
    return [...card.members].sort((a, b) => Number(b.projectId === card.anchorId) - Number(a.projectId === card.anchorId))
      .map(member => member.name).join(' \u00b7 ');
  }

  /** Makes a project the one the card is described from. */
  async makeMain(card: ProfileCard, project: ResearchProject): Promise<void> {
    if (!card.themeId || this.moving()) return;
    this.moving.set(true);
    this.moveError.set('');
    try {
      await this.api.setThemeAnchor(card.themeId, project.projectId);
      this.changed.emit();
    } catch (error: unknown) { this.moveError.set(describeFailure(error)); }
    finally { this.moving.set(false); }
  }

  /** Combines the whole card into another card ('keep' does nothing). */
  async mergeInto(card: ProfileCard, event: Event): Promise<void> {
    const select = event.target as HTMLSelectElement;
    if (!card.themeId || select.value === 'keep') return;
    this.moving.set(true);
    this.moveError.set('');
    try {
      await this.api.mergeTheme(card.themeId, select.value);
      this.dialog.closeAll();
      this.changed.emit();
    } catch (error: unknown) {
      this.moveError.set(describeFailure(error));
      select.value = 'keep';
    } finally {
      this.moving.set(false);
    }
  }

  /** Moves one Overleaf project to the chosen card ('' = a card of its own); the dialog closes as the cards change. */
  async move(project: ResearchProject, event: Event): Promise<void> {
    const select = event.target as HTMLSelectElement;
    const target = select.value;
    if (target === 'keep') return;
    this.moving.set(true);
    this.moveError.set('');
    try {
      await this.api.moveToTheme(project.projectId, target || null);
      this.dialog.closeAll();
      this.changed.emit();
    } catch (error: unknown) {
      this.moveError.set(describeFailure(error));
      select.value = 'keep';
    } finally {
      this.moving.set(false);
    }
  }
}

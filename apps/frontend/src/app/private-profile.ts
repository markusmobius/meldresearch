import { DatePipe } from '@angular/common';
import { Component, TemplateRef, computed, inject, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTabsModule } from '@angular/material/tabs';
import { describeFailure } from './overleaf-api';
import { ProjectProfile, ResearchApi, ResearchProject, ResearchStatus, ResearchUpdate } from './research-api';

/** One card: a research line with one or more Overleaf projects, or a project still being imported or grouped. */
interface ProfileCard {
  id: string; themeId: string | null; profile: ProfileOrNull; members: ReadonlyArray<ResearchProject>;
  recent: ReadonlyArray<ResearchUpdate>; updating: boolean; progress: ResearchProject | null;
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
    const recent = (ids: ReadonlyArray<string>) => status.updates.filter(update => ids.includes(update.projectId) &&
      update.kind !== 'initialImport' && Date.parse(update.sourceUpdatedAt ?? update.observedAt) >= since);
    const cards: ProfileCard[] = [];
    const placed = new Set<string>();
    for (const theme of status.themes ?? []) {
      const members = theme.projectIds.map(id => byId.get(id)).filter((project): project is ResearchProject => !!project);
      if (!members.length) continue;
      members.forEach(member => placed.add(member.projectId));
      cards.push({ id: theme.id, themeId: theme.id, profile: theme.profile ?? members[0].profile, members, recent: recent(theme.projectIds),
        updating: theme.updating, progress: members.find(member => BUSY.has(member.phase)) ?? null });
    }
    // Projects still importing, or imported and about to be placed on a card.
    for (const project of shown.filter(item => !placed.has(item.projectId))) {
      cards.push({ id: project.projectId, themeId: null, profile: project.profile, members: [project], recent: recent([project.projectId]),
        updating: !!project.profile, progress: BUSY.has(project.phase) ? project : null });
    }
    return cards;
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

  memberNames(card: ProfileCard): string { return card.members.map(member => member.name).join(' \u00b7 '); }

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

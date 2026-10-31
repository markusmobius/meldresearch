import { DatePipe } from '@angular/common';
import { Component, TemplateRef, computed, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTabsModule } from '@angular/material/tabs';
import { ResearchProject, ResearchStatus, ResearchUpdate } from './research-api';

interface ProfileEntry { project: ResearchProject; recent: ReadonlyArray<ResearchUpdate>; }

/** Updates shown with a project: those whose source changed in roughly the last three months. */
export const RECENT_UPDATE_DAYS = 92;

/**
 * About me → Private profile: one compact card per included project (title, a two-line description, topics). A card opens
 * the full summary and, when there are any, its Updates from the last three months. Stored on this computer only.
 */
@Component({
  selector: 'app-private-profile',
  imports: [DatePipe, MatButtonModule, MatDialogModule, MatIconModule, MatProgressBarModule, MatTabsModule],
  templateUrl: './private-profile.html',
  styleUrl: './private-profile.css',
})
export class PrivateProfile {
  readonly status = input<ResearchStatus | null>(null);
  private readonly dialog = inject(MatDialog);
  readonly selectedId = signal<string | null>(null);
  readonly entries = computed<ReadonlyArray<ProfileEntry>>(() => {
    const status = this.status();
    if (!status) return [];
    const since = Date.now() - RECENT_UPDATE_DAYS * 86400000;
    return status.projects.filter(project => project.enabled || project.profile).map(project => ({
      project,
      recent: status.updates.filter(update => update.projectId === project.projectId && update.kind !== 'initialImport' &&
        Date.parse(update.sourceUpdatedAt ?? update.observedAt) >= since),
    }));
  });
  /** The opened project, kept current while the dialog is open (status refreshes every few seconds). */
  readonly selected = computed(() => this.entries().find(entry => entry.project.projectId === this.selectedId()) ?? null);

  open(entry: ProfileEntry, details: TemplateRef<unknown>): void {
    this.selectedId.set(entry.project.projectId);
    this.dialog.open(details, { width: '720px', maxWidth: '94vw', autoFocus: 'dialog', ariaLabel: entry.project.profile?.title ?? entry.project.name })
      .afterClosed().subscribe(() => this.selectedId.set(null));
  }
}

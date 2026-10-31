import { DatePipe } from '@angular/common';
import { Component, computed, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ResearchProject, ResearchStatus, ResearchUpdate } from './research-api';

interface ProfileEntry { project: ResearchProject; updates: ReadonlyArray<ResearchUpdate>; }

/** About me → Private profile: each included project's running summary and its dated Updates. Stored on this computer only. */
@Component({
  selector: 'app-private-profile',
  imports: [DatePipe, MatIconModule, MatProgressBarModule],
  templateUrl: './private-profile.html',
  styleUrl: './private-profile.css',
})
export class PrivateProfile {
  readonly status = input<ResearchStatus | null>(null);
  readonly entries = computed<ReadonlyArray<ProfileEntry>>(() => {
    const status = this.status();
    if (!status) return [];
    return status.projects.filter(project => project.enabled || project.profile).map(project => ({
      project,
      updates: status.updates.filter(update => update.projectId === project.projectId && update.kind !== 'initialImport').slice(0, 8),
    }));
  });
}

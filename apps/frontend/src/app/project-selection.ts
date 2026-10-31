import { DatePipe, NgTemplateOutlet } from '@angular/common';
import { Component, computed, inject, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { OverleafApi, OverleafProject, describeFailure } from './overleaf-api';
import { ProjectSummary, ResearchApi, ResearchProject, ResearchStatus } from './research-api';
import { ProjectChoice, isRecent, projectChoice } from './project-decisions';

/** Account → Overleaf → Projects: an active include/exclude choice per recent project, optional choices for older ones. */
@Component({
  selector: 'app-project-selection',
  imports: [DatePipe, NgTemplateOutlet, MatButtonModule, MatExpansionModule, MatIconModule, MatProgressBarModule],
  templateUrl: './project-selection.html',
  styleUrl: './project-selection.css',
})
export class ProjectSelection {
  readonly status = input<ResearchStatus | null>(null);
  readonly projects = input<ReadonlyArray<OverleafProject>>([]);
  readonly sourceReady = input(false);
  readonly changed = output<void>();
  private readonly api = inject(ResearchApi);
  private readonly overleaf = inject(OverleafApi);
  readonly action = signal('');
  readonly error = signal('');
  readonly confirmForget = signal<string | null>(null);
  readonly recent = computed(() => this.projects().filter(project => isRecent(project)));
  readonly older = computed(() => this.projects().filter(project => !isRecent(project)));
  readonly undecided = computed(() => this.recent().filter(project => this.choice(project.id) === null));
  readonly includedOlder = computed(() => this.older().filter(project => this.choice(project.id) === 'included').length);

  choice(id: string): ProjectChoice { return projectChoice(this.status(), id); }
  isRecentProject(project: OverleafProject): boolean { return isRecent(project); }
  monitored(id: string): ResearchProject | undefined { return this.status()?.projects.find(project => project.projectId === id); }
  summaryFor(id: string): ProjectSummary | undefined { return this.status()?.summaries?.find(summary => summary.projectId === id); }
  /** Overleaf reports a newer edit than the one the summary was made from. */
  summaryStale(project: OverleafProject, summary: ProjectSummary): boolean {
    return !!project.updatedAt && !!summary.sourceUpdatedAt && Date.parse(project.updatedAt) > Date.parse(summary.sourceUpdatedAt);
  }
  fileName(path: string | null): string { return path?.split('/').pop() || 'the main .tex file'; }

  phaseLabel(project: ResearchProject): string {
    if (!project.enabled) return 'Paused';
    switch (project.phase) {
      case 'waiting': return 'Waiting for its first read';
      case 'checking': return 'Reading Overleaf...';
      case 'queued': return `Queued \u00b7 ${project.filesTotal} ${project.filesTotal === 1 ? 'file' : 'files'}`;
      case 'importing': return `Importing ${project.filesDone} of ${project.filesTotal} files`;
      case 'updating': return `Processing changes ${project.filesDone} of ${project.filesTotal}`;
      case 'error': return 'Retrying after an error';
      default: return project.profile ? 'Up to date' : 'Imported';
    }
  }

  /** Choosing Summarize is the consent to send only this project's main .tex file. */
  async summarize(project: OverleafProject): Promise<void> {
    await this.run(`Queuing a summary of ${project.name}...`, () => this.api.summarize(project.id));
  }

  async checkNow(): Promise<void> { await this.run('Checking included projects...', () => this.api.sync()); }

  /** Choosing Include is the explicit consent to process that project under the current notice. */
  async decide(project: OverleafProject, included: boolean): Promise<void> {
    await this.run(included ? `Including ${project.name}...` : `Excluding ${project.name}...`,
      () => this.api.decide(project.id, included, included ? this.status()?.consentVersion ?? null : null));
  }
  async resume(id: string): Promise<void> {
    await this.run('Resuming monitoring...', () => this.api.monitor(id, true, this.status()?.consentVersion ?? null));
  }
  async pause(id: string): Promise<void> { await this.run('Pausing monitoring...', () => this.api.monitor(id, false, null)); }
  async forget(id: string): Promise<void> {
    await this.run('Removing local research...', () => this.api.forget(id));
    this.confirmForget.set(null);
  }
  async demoChange(): Promise<void> { await this.run('Applying a synthetic revision...', async () => { await this.api.demoChange(); }); }

  private async run(label: string, operation: () => Promise<void>): Promise<void> {
    if (this.action()) return;
    this.action.set(label);
    this.error.set('');
    try {
      await this.overleaf.status();
      await operation();
      this.changed.emit();
    } catch (error: unknown) { this.error.set(describeFailure(error)); }
    finally { this.action.set(''); }
  }
}

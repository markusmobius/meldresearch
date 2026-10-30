import { DatePipe, NgTemplateOutlet } from '@angular/common';
import { Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatIconModule } from '@angular/material/icon';
import { OverleafApi, OverleafProject, describeFailure } from './overleaf-api';
import { ResearchApi, ResearchProject, ResearchStatus } from './research-api';
import { ProjectChoice, isRecent, projectChoice } from './project-decisions';

/** Account → Overleaf → Projects: an active include/exclude choice per recent project, optional choices for older ones. */
@Component({
  selector: 'app-project-selection',
  imports: [DatePipe, NgTemplateOutlet, MatButtonModule, MatCheckboxModule, MatExpansionModule, MatIconModule],
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
  readonly consent = signal(false);
  readonly action = signal('');
  readonly error = signal('');
  readonly confirmForget = signal<string | null>(null);
  readonly recent = computed(() => this.projects().filter(project => isRecent(project)));
  readonly older = computed(() => this.projects().filter(project => !isRecent(project)));
  readonly undecided = computed(() => this.recent().filter(project => this.choice(project.id) === null));
  readonly includedOlder = computed(() => this.older().filter(project => this.choice(project.id) === 'included').length);

  constructor() {
    // Consent belongs to the signed-in account; another account must give it again.
    let account: string | null | undefined;
    effect(() => {
      const next = this.status()?.account?.id ?? null;
      if (account !== undefined && next !== account) this.consent.set(false);
      account = next;
    });
  }

  choice(id: string): ProjectChoice { return projectChoice(this.status(), id); }
  isRecentProject(project: OverleafProject): boolean { return isRecent(project); }
  monitored(id: string): ResearchProject | undefined { return this.status()?.projects.find(project => project.projectId === id); }

  async decide(project: OverleafProject, included: boolean): Promise<void> {
    if (included && !this.consent()) {
      this.error.set('Approve processing of included project text first.');
      return;
    }
    await this.run(included ? `Including ${project.name}...` : `Excluding ${project.name}...`,
      () => this.api.decide(project.id, included, included ? this.status()?.consentVersion ?? null : null));
  }
  async resume(id: string): Promise<void> {
    if (!this.consent()) {
      this.error.set('Approve processing of included project text first.');
      return;
    }
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

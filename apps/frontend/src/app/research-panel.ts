import { DatePipe } from '@angular/common';
import { Component, DestroyRef, OnInit, TemplateRef, computed, inject, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { OverleafApi, OverleafProject, describeFailure } from './overleaf-api';
import { ResearchApi, ResearchProject, ResearchStatus, ResearchUpdate } from './research-api';
import { LocalPage } from './meld-toolbar';

@Component({
  selector: 'app-research-panel',
  imports: [DatePipe, MatButtonModule, MatCardModule, MatCheckboxModule, MatDialogModule,
    MatExpansionModule, MatIconModule, MatProgressBarModule],
  templateUrl: './research-panel.html',
  styleUrl: './research-panel.css',
  host: { '[class.feed-view]': "page() === 'home'" },
})
export class ResearchPanel implements OnInit {
  readonly page = input<LocalPage>('home');
  readonly statusChange = output<ResearchStatus>();
  readonly projects = input<ReadonlyArray<OverleafProject>>([]);
  readonly sourceReady = input(false);
  private readonly api = inject(ResearchApi);
  private readonly overleaf = inject(OverleafApi);
  private readonly destroy = inject(DestroyRef);
  private readonly dialog = inject(MatDialog);
  private evidenceDialog: MatDialogRef<unknown> | null = null;
  private polling = false;
  private stopped = false;
  private failedPoll = false;
  readonly state = signal<ResearchStatus | null>(null);
  readonly error = signal('');
  readonly action = signal('');
  readonly consent = signal(false);
  readonly confirmForget = signal<string | null>(null);
  readonly selectedUpdate = signal<ResearchUpdate | null>(null);
  readonly needsProjectAttention = computed(() => this.state()?.projects.some(project =>
    !!project.error || project.consentRequired) ?? false);

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
        this.consent.set(false);
        this.evidenceDialog?.close();
        this.selectedUpdate.set(null);
      }
      if (this.selectedUpdate() && !next.updates.some(update => update.id === this.selectedUpdate()?.id)) {
        this.evidenceDialog?.close();
        this.selectedUpdate.set(null);
      }
      this.state.set(next);
      this.statusChange.emit(next);
      if (this.failedPoll) { this.error.set(''); this.failedPoll = false; }
    } catch (error: unknown) {
      this.failedPoll = true;
      this.error.set(describeFailure(error));
    } finally { this.polling = false; }
  }

  watched(id: string): ResearchProject | undefined { return this.state()?.projects.find(project => project.projectId === id); }
  showUpdate(update: ResearchUpdate, template: TemplateRef<unknown>): void {
    this.evidenceDialog?.close();
    this.selectedUpdate.set(update);
    const dialog = this.dialog.open(template, { width: '680px', maxWidth: 'calc(100vw - 32px)', maxHeight: 'calc(100dvh - 32px)' });
    this.evidenceDialog = dialog;
    dialog.afterClosed().subscribe(() => {
      if (this.evidenceDialog === dialog) {
        this.evidenceDialog = null;
        this.selectedUpdate.set(null);
      }
    });
  }
  async monitor(id: string, enabled: boolean): Promise<void> {
    if (enabled && !this.consent()) {
      this.error.set('Approve processing of selected project text before enabling monitoring.');
      return;
    }
    await this.run(enabled ? 'Enabling monitoring...' : 'Pausing monitoring...',
      () => this.api.monitor(id, enabled, enabled ? this.state()?.consentVersion ?? null : null));
  }
  async forget(id: string): Promise<void> {
    await this.run('Removing local research...', () => this.api.forget(id));
    this.confirmForget.set(null);
  }
  async sync(): Promise<void> { await this.run('Requesting a content check...', () => this.api.sync()); }
  async demoChange(): Promise<void> { await this.run('Applying a synthetic revision...', async () => { await this.api.demoChange(); }); }
  private async run(label: string, operation: () => Promise<void>): Promise<void> {
    if (this.action()) return;
    this.action.set(label);
    this.failedPoll = false;
    this.error.set('');
    try {
      await this.overleaf.status();
      await operation();
      await this.refresh();
    } catch (error: unknown) { this.error.set(describeFailure(error)); }
    finally { this.action.set(''); }
  }
}

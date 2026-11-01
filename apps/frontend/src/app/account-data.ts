import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, DestroyRef, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { InterfaceLock } from './interface-lock';
import { describeFailure } from './overleaf-api';
import { ExportStatus, ImportResult, LeaveResult, ResearchApi } from './research-api';

export const LEAVE_CONFIRMATION = 'DELETE';

/** What an upload restored, in one sentence. */
export function importSummary(result: ImportResult, synced: boolean): string {
  const plural = (count: number, one: string, many: string) => `${count} ${count === 1 ? one : many}`;
  let text = `Imported ${plural(result.projects, 'project', 'projects')} (${result.included} included), ` +
    `${plural(result.cards, 'research card', 'research cards')} and ${plural(result.summaries, 'summary', 'summaries')}` +
    `${result.publications ? ', and your OpenAlex publications' : ''}.`;
  if (result.otherAccount) text += ` It came from ${result.exportedEmail}'s earlier account and now belongs to this one.`;
  if (synced) text += ' Encrypted sync uploads it on its next check.';
  return text;
}

/** Account → Meld account → Your data: download it, upload a download, or leave Meld Research. */
@Component({
  selector: 'app-account-data',
  imports: [DatePipe, DecimalPipe, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatProgressSpinnerModule],
  templateUrl: './account-data.html',
  styleUrl: './account-data.css',
})
export class AccountData implements OnInit {
  /** Whether encrypted sync is on: then the cloud copy is mentioned in the leave warning. */
  readonly synced = input(false);
  readonly changed = output<void>();
  readonly left = output<LeaveResult>();
  private readonly api = inject(ResearchApi);
  private readonly lock = inject(InterfaceLock);
  private readonly destroy = inject(DestroyRef);
  readonly download = signal<ExportStatus | null>(null);
  readonly preparing = computed(() => this.download()?.state === 'preparing');
  readonly imported = signal<ImportResult | null>(null);
  readonly leaving = signal(false);
  readonly confirmation = signal('');
  readonly working = signal(false);
  readonly error = signal('');
  readonly confirmWord = LEAVE_CONFIRMATION;
  private timer: ReturnType<typeof setTimeout> | undefined;
  private stopped = false;
  /** The download this page saw being prepared: it starts by itself when ready (not one that was already ready on load). */
  private awaiting: string | null = null;

  ngOnInit(): void {
    this.destroy.onDestroy(() => { this.stopped = true; clearTimeout(this.timer); });
    void this.check();
  }

  async startDownload(): Promise<void> {
    if (this.preparing()) return;
    this.error.set('');
    try {
      const status = await this.api.startExport();
      // A small export can be ready before this answer arrives; it still starts, because this page asked for it.
      this.awaiting = status.id;
      this.show(status);
    }
    catch (error: unknown) { this.error.set(describeFailure(error)); }
  }

  async upload(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file || this.lock.locked()) return;
    this.error.set('');
    this.imported.set(null);
    this.lock.begin('Uploading your data', `Restoring ${file.name}. Everything else waits until this is finished.`);
    try {
      const result = await this.api.importData(file);
      this.imported.set(result);
      this.lock.finish(importSummary(result, this.synced()));
      this.changed.emit();
    } catch (error: unknown) {
      const message = describeFailure(error);
      this.error.set(message);
      this.lock.fail(message);
    }
  }

  summary(result: ImportResult): string { return importSummary(result, this.synced()); }
  toggleLeave(): void { this.leaving.update(value => !value); this.confirmation.set(''); this.error.set(''); }
  value(event: Event): string { return (event.target as HTMLInputElement).value; }

  async leave(): Promise<void> {
    if (this.confirmation().trim() !== LEAVE_CONFIRMATION || this.working()) return;
    this.working.set(true);
    this.error.set('');
    try { this.left.emit(await this.api.leave(LEAVE_CONFIRMATION)); }
    catch (error: unknown) { this.error.set(describeFailure(error)); }
    finally { this.working.set(false); }
  }

  private async check(): Promise<void> {
    if (this.stopped) return;
    try { this.show(await this.api.exportStatus()); }
    catch { this.schedule(3000); }
  }

  private show(status: ExportStatus | null): void {
    clearTimeout(this.timer);
    if (this.stopped) return;
    this.download.set(status);
    if (!status) return;
    if (status.state === 'preparing') {
      this.awaiting = status.id;
      this.schedule(1000);
      return;
    }
    if (status.state === 'ready' && status.url && this.awaiting === status.id) this.save(status.url, status.fileName);
    this.awaiting = null;
    // The link disappears when the server forgets the download.
    if (status.expiresAt) this.schedule(Math.max(0, Date.parse(status.expiresAt) - Date.now()) + 500);
  }

  private schedule(delay: number): void {
    clearTimeout(this.timer);
    if (!this.stopped) this.timer = setTimeout(() => { void this.check(); }, delay);
  }

  /** Starts the browser's download of a prepared file. */
  save(url: string, fileName: string | null): void {
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName ?? '';
    link.rel = 'noopener';
    document.body.appendChild(link);
    link.click();
    link.remove();
  }
}

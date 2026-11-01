import { DatePipe } from '@angular/common';
import { Component, inject, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { describeFailure } from './overleaf-api';
import { ImportResult, LeaveResult, ResearchApi } from './research-api';

export const LEAVE_CONFIRMATION = 'DELETE';

/** Account → Meld account → Your data: download it, upload a download, or leave Meld Research. */
@Component({
  selector: 'app-account-data',
  imports: [DatePipe, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule],
  templateUrl: './account-data.html',
  styleUrl: './account-data.css',
})
export class AccountData {
  /** Whether encrypted sync is on: then the cloud copy is mentioned in the leave warning. */
  readonly synced = input(false);
  readonly changed = output<void>();
  readonly left = output<LeaveResult>();
  private readonly api = inject(ResearchApi);
  readonly imported = signal<ImportResult | null>(null);
  readonly importing = signal(false);
  readonly leaving = signal(false);
  readonly confirmation = signal('');
  readonly working = signal(false);
  readonly error = signal('');
  readonly confirmWord = LEAVE_CONFIRMATION;

  async upload(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file || this.importing()) return;
    this.importing.set(true);
    this.error.set('');
    this.imported.set(null);
    try {
      this.imported.set(await this.api.importData(file));
      this.changed.emit();
    } catch (error: unknown) { this.error.set(describeFailure(error)); }
    finally { this.importing.set(false); }
  }

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
}

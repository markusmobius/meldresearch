import { Component, inject, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { describeFailure } from './overleaf-api';
import { LeaveResult, ResearchApi } from './research-api';

export const LEAVE_CONFIRMATION = 'DELETE';

/** Account → Meld account → Leave Meld Research: deletes the account everywhere and uninstalls the app, after typing DELETE. */
@Component({
  selector: 'app-leave-meld',
  imports: [MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule],
  templateUrl: './leave-meld.html',
  styleUrl: './leave-meld.css',
})
export class LeaveMeld {
  /** Whether encrypted sync is on: then the cloud copy is mentioned in the warning. */
  readonly synced = input(false);
  readonly left = output<LeaveResult>();
  private readonly api = inject(ResearchApi);
  readonly open = signal(false);
  readonly confirmation = signal('');
  readonly working = signal(false);
  readonly error = signal('');
  readonly confirmWord = LEAVE_CONFIRMATION;

  toggle(): void { this.open.update(value => !value); this.confirmation.set(''); this.error.set(''); }
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

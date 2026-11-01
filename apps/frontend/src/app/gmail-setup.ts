import { DatePipe } from '@angular/common';
import { Component, inject, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { describeFailure } from './overleaf-api';
import { GmailAccount, GmailStatus, ResearchApi } from './research-api';

/**
 * Account → Gmail: connect one or more Gmail accounts for read-only access. Google's consent page opens in this tab and
 * returns to the app. Each account shows its import: threads analyzed, workstreams found, progress and the next import.
 */
@Component({
  selector: 'app-gmail-setup',
  imports: [DatePipe, MatButtonModule, MatCheckboxModule, MatIconModule, MatProgressBarModule],
  templateUrl: './gmail-setup.html',
  styleUrl: './gmail-setup.css',
})
export class GmailSetup {
  readonly status = input<GmailStatus | null>(null);
  readonly changed = output<void>();
  private readonly api = inject(ResearchApi);
  readonly agreed = signal(false);
  readonly working = signal<string | null>(null);
  readonly confirmRemove = signal<string | null>(null);
  readonly error = signal('');
  readonly needsReconnect = (account: GmailAccount) => account.status === 'reconnect';

  async connect(): Promise<void> {
    const status = this.status();
    if (!status || !this.agreed() || this.working()) return;
    this.working.set('connect');
    this.error.set('');
    try { this.navigate((await this.api.connectGmail(status.consentVersion)).authorizationUrl); }
    catch (error: unknown) { this.error.set(describeFailure(error)); this.working.set(null); }
  }
  navigate(url: string): void { window.location.assign(url); }

  importNow(account: GmailAccount): Promise<void> { return this.run(account.address, () => this.api.importGmail(account.address)); }
  remove(account: GmailAccount): Promise<void> {
    if (this.confirmRemove() !== account.address) { this.confirmRemove.set(account.address); return Promise.resolve(); }
    return this.run(account.address, () => this.api.removeGmail(account.address)).then(() => this.confirmRemove.set(null));
  }
  statusLabel(account: GmailAccount): string {
    return account.status === 'connected' ? 'Connected' : account.status === 'reconnect' ? 'Reconnect needed' : 'Problem';
  }

  private async run(address: string, operation: () => Promise<unknown>): Promise<void> {
    if (this.working()) return;
    this.working.set(address);
    this.error.set('');
    try { await operation(); this.changed.emit(); }
    catch (error: unknown) { this.error.set(describeFailure(error)); this.changed.emit(); }
    finally { this.working.set(null); }
  }
}

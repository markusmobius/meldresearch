import { DatePipe } from '@angular/common';
import { Component, computed, inject, input, OnInit, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { describeFailure } from './overleaf-api';
import { ResearchApi, SyncStatus } from './research-api';

export const MIN_PASSPHRASE = 10;

/**
 * Account → Remote Sync: end-to-end encrypted sync. The first computer chooses a passphrase (and receives the recovery key);
 * other computers unlock with either. A forgotten passphrase and recovery key means starting over. Once set up, the only
 * action is showing the recovery key again.
 */
@Component({
  selector: 'app-encrypted-sync',
  imports: [DatePipe, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatIconModule, MatInputModule],
  templateUrl: './encrypted-sync.html',
  styleUrl: './encrypted-sync.css',
})
export class EncryptedSync implements OnInit {
  readonly status = input<SyncStatus | null>(null);
  readonly changed = output<void>();
  /** True while a new recovery key is on screen: setup must not move on before the user saved it. */
  readonly keyShown = output<boolean>();
  /** This computer finished the step (key saved after setting up, or unlocked): setup moves on. */
  readonly done = output<void>();
  private readonly api = inject(ResearchApi);
  /** The latest answer from the explicit check; the research status takes over once it has caught up. */
  private readonly checked = signal<SyncStatus | null>(null);
  readonly sync = computed(() => {
    const status = this.status(), checked = this.checked();
    return !status || status.state === 'checking' ? checked ?? status : status;
  });
  readonly passphrase = signal('');
  readonly confirmation = signal('');
  readonly recoveryInput = signal('');
  readonly useRecovery = signal(false);
  readonly startingOver = signal(false);
  /** After creating or starting over (new: the user must confirm it is saved), or shown again on request. */
  readonly recoveryKey = signal<string | null>(null);
  readonly keyIsNew = signal(false);
  readonly keyReplaced = signal(false);
  readonly saved = signal(false);
  readonly copied = signal(false);
  readonly saving = signal(false);
  readonly error = signal('');

  ngOnInit(): void {
    this.api.encryptedSync().then(status => this.checked.set(status), () => undefined);
  }

  /** A new passphrase: long enough and typed the same twice. */
  readonly newPassphraseProblem = computed(() => {
    const value = this.passphrase();
    if (value.trim().length < MIN_PASSPHRASE) return `Use at least ${MIN_PASSPHRASE} characters, for example four unrelated words.`;
    if (value !== this.confirmation()) return 'The two passphrases do not match.';
    return null;
  });

  create(): Promise<unknown> {
    return this.newPassphraseProblem() ? Promise.resolve() :
      this.run(async () => this.showKey((await this.api.createSync(this.passphrase())).recoveryKey));
  }
  async unlock(): Promise<void> {
    if (await this.run(() => this.api.unlockSync(this.useRecovery() ? { recoveryKey: this.recoveryInput() } : { passphrase: this.passphrase() })))
      this.done.emit();
  }
  /** The only action once sync is set up. */
  showRecoveryKey(): Promise<boolean> {
    return this.run(async () => {
      const result = await this.api.recoveryKey();
      this.recoveryKey.set(result.recoveryKey);
      this.keyIsNew.set(false);
      this.keyReplaced.set(result.replaced);
    });
  }
  startOver(): Promise<unknown> {
    return this.newPassphraseProblem() ? Promise.resolve() : this.run(async () => {
      this.showKey((await this.api.startSyncOver(this.passphrase())).recoveryKey);
      this.startingOver.set(false);
    });
  }
  async copy(): Promise<void> {
    const key = this.recoveryKey();
    if (!key) return;
    try { await navigator.clipboard.writeText(key); this.copied.set(true); }
    catch { this.error.set('Copying is not available here. Select the key and copy it, or download it.'); }
  }
  download(): void {
    const key = this.recoveryKey();
    if (!key) return;
    const text = `Meld Research recovery key\n\n${key}\n\nThis key unlocks your encrypted research if you forget your passphrase.\n` +
      'Keep it somewhere safe and private. Meld cannot recover it for you.\n';
    const link = document.createElement('a');
    link.href = URL.createObjectURL(new Blob([text], { type: 'text/plain' }));
    link.download = 'meld-research-recovery-key.txt';
    link.click();
    URL.revokeObjectURL(link.href);
  }
  closeKey(): void {
    const wasNew = this.keyIsNew();
    this.recoveryKey.set(null);
    this.keyIsNew.set(false);
    this.keyReplaced.set(false);
    this.saved.set(false);
    this.copied.set(false);
    if (wasNew) { this.keyShown.emit(false); this.done.emit(); }
  }
  toggle(mode: 'recovery' | 'start-over'): void {
    this.error.set('');
    this.clear();
    if (mode === 'recovery') this.useRecovery.update(value => !value);
    else this.startingOver.update(value => !value);
  }
  value(event: Event): string { return (event.target as HTMLInputElement).value; }

  private showKey(key: string): void { this.recoveryKey.set(key); this.keyIsNew.set(true); this.keyShown.emit(true); }
  private clear(): void { this.passphrase.set(''); this.confirmation.set(''); this.recoveryInput.set(''); }

  private async run(operation: () => Promise<void>): Promise<boolean> {
    if (this.saving()) return false;
    this.saving.set(true);
    this.error.set('');
    try {
      await operation();
      this.clear();
      this.useRecovery.set(false);
      this.checked.set(await this.api.encryptedSync().catch(() => this.checked()));
      this.changed.emit();
      return true;
    } catch (error: unknown) { this.error.set(describeFailure(error)); return false; }
    finally { this.saving.set(false); }
  }
}

import { Component, signal } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';

export type InstallOs = 'windows' | 'mac';

/** Install card on the remote account page: one-line installers for Windows and macOS. */
@Component({
  selector: 'app-meld-install',
  imports: [MatButtonModule, MatCardModule, MatIconModule, NgTemplateOutlet],
  templateUrl: './meld-install.html',
  styleUrl: './meld-install.css',
})
export class MeldInstall {
  readonly os = signal<InstallOs>(/mac|os x/i.test(navigator.userAgent) ? 'mac' : 'windows');
  readonly tab = signal<'automatic' | 'manual'>('automatic');
  readonly copied = signal<string | null>(null);
  readonly windowsCommand = `irm -useb ${location.origin}/api/install/windows | iex`;
  readonly macCommand = `curl -fsSL ${location.origin}/api/install/macos | bash`;
  readonly windowsManualCommand = 'powershell -ExecutionPolicy Bypass -File .\\meldresearch-install.ps1';
  readonly macManualCommand = 'bash meldresearch-install.sh';

  constructor() {
    // Only account holders see this card; the home page then offers "Sign in" instead of "Sign up" (google-sign-in.js).
    try { localStorage.setItem('meldresearch.account', '1'); } catch { /* storage disabled */ }
  }

  async copy(text: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(text);
      this.copied.set(text);
      setTimeout(() => { if (this.copied() === text) this.copied.set(null); }, 2000);
    } catch {
      this.copied.set(null);
    }
  }
}

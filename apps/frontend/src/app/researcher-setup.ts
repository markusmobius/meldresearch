import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatRadioModule } from '@angular/material/radio';
import { describeFailure } from './overleaf-api';
import { ResearchApi, ResearcherStatus } from './research-api';

/** Account → OpenAlex: find the user's public author profile by name and let them confirm it (from Project Meld). */
@Component({
  selector: 'app-researcher-setup',
  imports: [DatePipe, DecimalPipe, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule,
    MatProgressBarModule, MatRadioModule],
  templateUrl: './researcher-setup.html',
  styleUrl: './researcher-setup.css',
})
export class ResearcherSetup {
  readonly researcher = input<ResearcherStatus | null>(null);
  readonly accountName = input('');
  readonly changed = output<void>();
  private readonly api = inject(ResearchApi);
  readonly searchName = signal('');
  readonly selectedId = signal<string | null>(null);
  readonly editing = signal(false);
  readonly saving = signal(false);
  /** The name being searched right now; the search runs while the user waits. */
  readonly searching = signal<string | null>(null);
  readonly error = signal('');
  readonly phase = computed(() => this.researcher()?.status ?? null);
  readonly showSearch = computed(() => this.editing() || this.phase() === null || this.phase() === 'unmatched' || this.phase() === 'rejected');

  constructor() {
    // Prefill the name once: the last search, otherwise the Google account name.
    let filled = false;
    effect(() => {
      const name = this.researcher()?.searchName || this.accountName();
      if (!filled && name) { this.searchName.set(name); filled = true; }
    });
    effect(() => { if (this.phase() !== 'choice') this.selectedId.set(null); });
  }

  search(): Promise<void> {
    const name = this.searchName().trim();
    if (name.length < 2) {
      this.error.set('Enter your name as it appears on your publications.');
      return Promise.resolve();
    }
    this.searching.set(name);
    return this.save(() => this.api.researcher('search', { searchName: name, baseVersion: this.researcher()?.version }))
      .finally(() => this.searching.set(null));
  }
  confirm(): Promise<void> {
    const id = this.selectedId();
    return id ? this.save(() => this.api.researcher('confirm', { authorId: id, baseVersion: this.researcher()?.version })) : Promise.resolve();
  }
  none(): Promise<void> { return this.save(() => this.api.researcher('none', { baseVersion: this.researcher()?.version })); }
  retry(): Promise<void> { return this.save(() => this.api.researcher('retry', { baseVersion: this.researcher()?.version })); }
  nameChanged(event: Event): void { this.searchName.set((event.target as HTMLInputElement).value); }

  private async save(operation: () => Promise<void>): Promise<void> {
    if (this.saving()) return;
    this.saving.set(true);
    this.error.set('');
    try {
      await operation();
      this.editing.set(false);
      this.changed.emit();
    } catch (error: unknown) { this.error.set(describeFailure(error)); }
    finally { this.saving.set(false); }
  }
}

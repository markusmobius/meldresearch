import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { describeFailure } from './overleaf-api';
import { Publications, PublicationWork, ResearchApi, ResearcherStatus } from './research-api';

/** About me: the confirmed OpenAlex author's public works, stored on this computer (list design from Project Meld). */
@Component({
  selector: 'app-publications',
  imports: [MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule],
  templateUrl: './publications.html',
  styleUrl: './publications.css',
})
export class PublicationsList {
  readonly researcher = input<ResearcherStatus | null>(null);
  private readonly api = inject(ResearchApi);
  readonly data = signal<Publications | null>(null);
  readonly error = signal('');
  readonly query = signal('');
  readonly shown = signal(20);
  readonly expanded = signal<string | null>(null);
  readonly filtered = computed<ReadonlyArray<PublicationWork>>(() => {
    const works = this.data()?.works ?? [];
    const terms = this.query().trim().toLowerCase().split(/\s+/).filter(Boolean);
    return terms.length ? works.filter(work => {
      const text = `${work.title} ${work.authors.join(' ')} ${work.venue ?? ''} ${work.year}`.toLowerCase();
      return terms.every(term => text.includes(term));
    }) : works;
  });
  readonly visible = computed(() => this.filtered().slice(0, this.shown()));

  constructor() {
    // Reload whenever a different author or a newer import is reported.
    effect(() => {
      const researcher = this.researcher();
      const key = researcher?.status === 'ready' ? `${researcher.researcher?.id}|${researcher.updatedAt}` : null;
      void this.load(key);
    });
  }

  private loadedKey: string | null = null;
  private async load(key: string | null): Promise<void> {
    if (key === this.loadedKey) return;
    this.loadedKey = key;
    if (!key) { this.data.set(null); return; }
    try {
      this.data.set(await this.api.publications());
      this.error.set('');
    } catch (error: unknown) { this.error.set(describeFailure(error)); }
  }

  search(event: Event): void {
    this.query.set((event.target as HTMLInputElement).value);
    this.shown.set(20);
  }
}

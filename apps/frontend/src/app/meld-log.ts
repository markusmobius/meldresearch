import { Component, DestroyRef, OnInit, computed, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { ResearchProject } from './research-api';

export interface AppLogEntry {
  id: number; timestamp: string; level: 'info' | 'warn' | 'error'; source: string; message: string; detail: string | null;
  projectId: string | null; repeats: number;
}

/**
 * More → Log (as in Project Meld): the local app's activity, newest first, refreshed every few seconds. Entries name a
 * project by its Overleaf id; its name comes from the app's own status. No research content or credentials are logged.
 */
@Component({
  selector: 'app-meld-log',
  imports: [MatButtonModule, MatIconModule],
  templateUrl: './meld-log.html',
  styleUrl: './meld-log.css',
})
export class MeldLog implements OnInit {
  readonly projects = input<ReadonlyArray<ResearchProject>>([]);
  private readonly destroyRef = inject(DestroyRef);
  readonly entries = signal<ReadonlyArray<AppLogEntry>>([]);
  readonly sources = signal<ReadonlyArray<string>>([]);
  readonly level = signal('all');
  readonly source = signal('all');
  readonly error = signal('');
  readonly loaded = signal(false);
  private loading = false;
  private readonly names = computed(() => new Map(this.projects().map(project => [project.projectId, project.name] as const)));

  ngOnInit(): void {
    void this.load();
    const timer = setInterval(() => { if (!this.loading) void this.load(); }, 4000);
    this.destroyRef.onDestroy(() => clearInterval(timer));
  }

  async load(): Promise<void> {
    this.loading = true;
    try {
      const query = new URLSearchParams({ level: this.level(), source: this.source(), limit: '300' });
      const response = await fetch(`/api/logs?${query}`, { headers: { Accept: 'application/json' } });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const body = await response.json() as { entries: AppLogEntry[]; sources: string[] };
      this.entries.set(body.entries);
      this.sources.set(body.sources);
      this.error.set('');
    } catch {
      // An unreachable backend must not look like an empty log.
      this.error.set('Cannot reach the local Meld Research backend. Is it running?');
    } finally {
      this.loading = false;
      this.loaded.set(true);
    }
  }

  filter(kind: 'level' | 'source', event: Event): void {
    (kind === 'level' ? this.level : this.source).set((event.target as HTMLSelectElement).value);
    void this.load();
  }

  project(entry: AppLogEntry): string | null {
    return entry.projectId ? this.names().get(entry.projectId) ?? `project ${entry.projectId.slice(0, 6)}` : null;
  }

  time(iso: string): string {
    const date = new Date(iso);
    const today = new Date().toDateString() === date.toDateString();
    return date.toLocaleString([], today ? { hour: '2-digit', minute: '2-digit', second: '2-digit' }
      : { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', second: '2-digit' });
  }
}

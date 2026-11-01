import { Injectable, computed, signal } from '@angular/core';

/** A long operation that blocks the whole interface; outcome is set when it finished (done) or stopped (failed). */
export interface LockState { title: string; detail: string; outcome?: 'done' | 'failed'; message?: string; }

/**
 * Locks the entire app (rendered by the root component as a modal overlay; everything behind it is inert) while an
 * operation such as uploading data runs. It stays until the user closes the outcome.
 */
@Injectable({ providedIn: 'root' })
export class InterfaceLock {
  readonly state = signal<LockState | null>(null);
  readonly locked = computed(() => this.state() !== null);
  readonly running = computed(() => { const state = this.state(); return !!state && !state.outcome; });

  begin(title: string, detail: string): void { this.state.set({ title, detail }); }
  finish(message: string): void { this.state.update(state => state && { ...state, outcome: 'done', message }); }
  fail(message: string): void { this.state.update(state => state && { ...state, outcome: 'failed', message }); }
  close(): void { if (!this.running()) this.state.set(null); }
}

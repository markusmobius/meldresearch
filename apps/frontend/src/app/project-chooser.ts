import { DatePipe, NgTemplateOutlet } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { describeFailure } from './overleaf-api';
import { ChoiceKind, ProjectChoice, ResearchApi } from './research-api';

/** What the chooser is for. extra: an additional choice below the projects (for example "A project of its own"), returned as 'new'. */
export interface ChooserData {
  heading: string; subject: string; kind: ChoiceKind; id: string; confirm: string;
  extra?: { label: string; hint: string } | null;
}
/** The chosen research project's id, 'new' for the extra choice, or undefined when cancelled. */
export type ChooserResult = string | undefined;

/**
 * Chooses a research project to add something to, or move it to: a wide list with each project's title, summary, its
 * Overleaf projects and latest change, closest first (the closest few marked as best matches), with a search field. Choosing
 * takes two steps (select a project, then confirm), so a mistaken tap does nothing.
 */
@Component({
  selector: 'app-project-chooser',
  imports: [DatePipe, NgTemplateOutlet, MatButtonModule, MatDialogModule, MatFormFieldModule, MatIconModule, MatInputModule, MatProgressBarModule],
  templateUrl: './project-chooser.html',
  styleUrl: './project-chooser.css',
})
export class ProjectChooser implements OnInit {
  readonly data = inject<ChooserData>(MAT_DIALOG_DATA);
  private readonly api = inject(ResearchApi);
  private readonly dialog = inject(MatDialogRef<ProjectChooser, ChooserResult>);
  readonly choices = signal<ReadonlyArray<ProjectChoice>>([]);
  readonly ranked = signal(false);
  readonly loading = signal(true);
  readonly error = signal('');
  readonly query = signal('');
  readonly selected = signal<string | null>(null);
  readonly shown = computed(() => {
    const words = this.query().toLowerCase().split(/\s+/).filter(word => word.length);
    return this.choices().filter(choice => {
      const text = [choice.title, choice.summary, ...choice.projects].join(' ').toLowerCase();
      return words.every(word => text.includes(word));
    });
  });
  /** The suggested and closest projects first, under their own heading. */
  readonly best = computed(() => this.shown().filter(choice => choice.suggested || choice.best));
  readonly others = computed(() => this.shown().filter(choice => !choice.suggested && !choice.best));
  readonly selectedTitle = computed(() => this.selected() === 'new' ? this.data.extra?.label ?? ''
    : this.choices().find(choice => choice.cardId === this.selected())?.title ?? '');

  async ngOnInit(): Promise<void> {
    try {
      const result = await this.api.choices(this.data.kind, this.data.id);
      this.choices.set(result.choices);
      this.ranked.set(result.ranked);
    } catch (error: unknown) { this.error.set(describeFailure(error)); }
    finally { this.loading.set(false); }
  }

  select(value: string): void { this.selected.set(value); }
  confirm(): void { if (this.selected()) this.dialog.close(this.selected()!); }
}

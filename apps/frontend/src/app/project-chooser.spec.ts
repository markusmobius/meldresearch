import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ChooserData, ProjectChooser } from './project-chooser';
import { ProjectChoice, ResearchApi } from './research-api';

describe('Project chooser', () => {
  const choice = (cardId: string, title: string, overrides: Partial<ProjectChoice> = {}): ProjectChoice => ({
    cardId, title, summary: `Two sentences about ${title}.`, projects: [], lastChanged: null, best: false, suggested: false, ...overrides });
  const api = { choices: vi.fn() };
  const ref = { close: vi.fn() };
  function render(data: Partial<ChooserData> = {}) {
    TestBed.configureTestingModule({ imports: [ProjectChooser], providers: [provideNoopAnimations(),
      { provide: ResearchApi, useValue: api }, { provide: MatDialogRef, useValue: ref },
      { provide: MAT_DIALOG_DATA, useValue: { heading: 'Add to a research project', subject: '“Teaching” · 4 email threads', kind: 'group',
        id: 'g', confirm: 'Add', ...data } satisfies ChooserData }] });
    const fixture = TestBed.createComponent(ProjectChooser);
    fixture.detectChanges();
    return fixture;
  }
  beforeEach(() => vi.resetAllMocks());

  it('lists every project wide, closest first, with summaries and projects; choosing takes a selection and a confirmation', async () => {
    api.choices.mockResolvedValue({ ranked: true, choices: [
      choice('s', 'Publisher content marketplace', { suggested: true }), choice('a', 'Teaching allocation', { best: true, projects: ['allocation-code', 'survey'],
        lastChanged: '2026-10-20T12:00:00Z' }), choice('b', 'Metalens optics')] });
    const fixture = render();
    await vi.waitFor(() => { fixture.detectChanges(); expect(fixture.nativeElement.querySelectorAll('.choice')).toHaveLength(3); });
    const element: HTMLElement = fixture.nativeElement;
    expect(api.choices).toHaveBeenCalledWith('group', 'g');
    expect(element.querySelector('.subject')?.textContent).toContain('4 email threads');
    expect([...element.querySelectorAll('.section')].map(section => section.textContent)).toEqual(['Best matches', 'Other projects']);
    const rows = [...element.querySelectorAll<HTMLButtonElement>('.choice')];
    expect(rows[0].querySelector('.badge')?.textContent).toBe('Suggested');
    expect(rows[1].querySelector('.badge')?.textContent).toBe('Close match');
    expect(rows[1].querySelector('.summary')?.textContent).toBe('Two sentences about Teaching allocation.');
    expect(rows[1].querySelector('.meta')?.textContent).toContain('allocation-code · survey');
    expect(rows[1].querySelector('.meta')?.textContent).toContain('Changed Oct 20, 2026');

    const confirm = element.querySelector<HTMLButtonElement>('.confirm')!;
    expect(confirm.disabled).toBe(true);
    rows[1].click();
    fixture.detectChanges();
    expect(ref.close).not.toHaveBeenCalled();
    expect(rows[1].getAttribute('aria-checked')).toBe('true');
    expect(element.querySelector('.choice-note')?.textContent).toContain('Add: Teaching allocation');
    expect(confirm.disabled).toBe(false);
    confirm.click();
    expect(ref.close).toHaveBeenCalledWith('a');
  });

  it('searches titles, summaries and project names, and offers the extra choice', async () => {
    api.choices.mockResolvedValue({ ranked: false, choices: [choice('a', 'Teaching allocation', { projects: ['survey-code'] }), choice('b', 'Metalens optics')] });
    const fixture = render({ extra: { label: 'A project of its own', hint: 'Start a separate project.' } });
    await vi.waitFor(() => { fixture.detectChanges(); expect(fixture.nativeElement.querySelectorAll('.choice')).toHaveLength(3); });
    const element: HTMLElement = fixture.nativeElement;
    // Not ranked yet: one list, by latest change.
    expect(element.querySelector('.section')?.textContent).toBe('Projects, most recently changed first');
    const search = element.querySelector<HTMLInputElement>('input[type="search"]')!;
    search.value = 'survey';
    search.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect([...element.querySelectorAll('.choice .title')].map(title => title.textContent?.trim())).toEqual(['Teaching allocation', 'A project of its own']);
    search.value = 'nothing like this';
    search.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(element.querySelector('.empty')?.textContent).toContain('No project matches your search.');
    element.querySelector<HTMLButtonElement>('.choice.extra')!.click();
    fixture.detectChanges();
    element.querySelector<HTMLButtonElement>('.confirm')!.click();
    expect(ref.close).toHaveBeenCalledWith('new');
  });
});

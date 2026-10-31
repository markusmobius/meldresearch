import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ResearcherSetup } from './researcher-setup';
import { PublicationsList } from './publications';
import { ResearchApi, ResearcherStatus } from './research-api';

describe('OpenAlex researcher setup', () => {
  const api = { researcher: vi.fn(), publications: vi.fn() };
  const work = { id: 'W1', title: 'A synthetic paper', authors: ['Synthetic Researcher'], year: 2025, publicationDate: '2025-01-01',
    venue: 'Synthetic Journal', doi: null, abstract: 'Synthetic abstract.', isRetracted: false };
  const candidate = (id: string) => ({ id, name: 'Synthetic Researcher', orcid: null, affiliations: ['Synthetic University'],
    alternativeNames: [], worksCount: 12, samplePublications: [work] });
  const state = (status: ResearcherStatus['status'], extra: Partial<ResearcherStatus> = {}): ResearcherStatus => ({
    version: 4, status, searchName: 'Synthetic Researcher', candidates: [], researcher: null, workCount: 0,
    updatedAt: null, nextRefreshAt: null, lastError: null, ...extra,
  });
  beforeEach(() => {
    vi.resetAllMocks();
    api.researcher.mockResolvedValue(undefined);
    TestBed.configureTestingModule({ imports: [ResearcherSetup, PublicationsList], providers: [
      provideNoopAnimations(), { provide: ResearchApi, useValue: api },
    ] });
  });
  function setup(researcher: ResearcherStatus | null) {
    const fixture = TestBed.createComponent(ResearcherSetup);
    fixture.componentRef.setInput('researcher', researcher);
    fixture.componentRef.setInput('accountName', 'Google Name');
    fixture.detectChanges();
    return fixture;
  }

  it('prefills the account name and searches only when asked', async () => {
    const fixture = setup(null);
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelector<HTMLInputElement>('input')?.value).toBe('Google Name');
    expect(api.researcher).not.toHaveBeenCalled();
    expect(element.textContent).toContain('Azure AI Foundry');
    element.querySelector<HTMLButtonElement>('.search-button')!.click();
    await vi.waitFor(() => expect(api.researcher).toHaveBeenCalledWith('search', { searchName: 'Google Name', baseVersion: undefined }));
    element.querySelector<HTMLButtonElement>('.not-listed')!.click();
    await vi.waitFor(() => expect(api.researcher).toHaveBeenCalledWith('none', { baseVersion: undefined }));
    fixture.destroy();
  });

  it('shows the search in progress while the request runs and reports a failed search', async () => {
    let finish!: (error?: unknown) => void;
    api.researcher.mockReturnValue(new Promise<void>((resolve, reject) => { finish = error => error ? reject(error) : resolve(); }));
    const fixture = setup(null);
    const element: HTMLElement = fixture.nativeElement;
    const searching = fixture.componentInstance.search();
    fixture.detectChanges();
    expect(element.querySelector('[role="status"]')?.textContent).toContain('Searching OpenAlex for Google Name');
    expect(element.querySelector('form.search')).toBeNull();
    finish(new Error('OpenAlex has reached its request limit.'));
    await searching;
    fixture.detectChanges();
    expect(element.querySelector('form.search')).not.toBeNull();
    expect(element.querySelector('[role="alert"]')?.textContent).toBeTruthy();
    fixture.destroy();
  });

  it('lets the user confirm one offered author, or none of them', async () => {
    const fixture = setup(state('choice', { candidates: [candidate('A1'), candidate('A2')] }));
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelectorAll('.researcher-candidate')).toHaveLength(2);
    expect(element.querySelector('.researcher-candidate')?.textContent).toContain('A synthetic paper');
    const confirm = element.querySelector<HTMLButtonElement>('button.confirm')!;
    expect(confirm.disabled).toBe(true);
    fixture.componentInstance.selectedId.set('A2');
    fixture.detectChanges();
    confirm.click();
    await vi.waitFor(() => expect(api.researcher).toHaveBeenCalledWith('confirm', { authorId: 'A2', baseVersion: 4 }));
    await fixture.componentInstance.none();
    expect(api.researcher).toHaveBeenCalledWith('none', { baseVersion: 4 });
    fixture.destroy();
  });

  it('lists imported publications in About me with a search filter', async () => {
    api.publications.mockResolvedValue({ researcher: candidate('A1'), updatedAt: '2026-10-30T00:00:00Z',
      works: [work, { ...work, id: 'W2', title: 'Another study', abstract: null }] });
    const fixture = TestBed.createComponent(PublicationsList);
    fixture.componentRef.setInput('researcher', state('ready', { researcher: candidate('A1'), workCount: 2, updatedAt: '2026-10-30T00:00:00Z' }));
    fixture.detectChanges();
    await vi.waitFor(() => expect(fixture.componentInstance.data()).not.toBeNull());
    fixture.detectChanges();
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelectorAll('.publication-row')).toHaveLength(2);
    const input = element.querySelector<HTMLInputElement>('.filter input')!;
    input.value = 'another';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect([...element.querySelectorAll('.publication-row h3')].map(row => row.textContent?.trim())).toEqual(['Another study']);
    expect(api.publications).toHaveBeenCalledOnce();
    fixture.destroy();
  });

  it('invites linking OpenAlex when no author is confirmed', () => {
    const fixture = TestBed.createComponent(PublicationsList);
    fixture.componentRef.setInput('researcher', null);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('a[href="/#account"]')?.textContent).toContain('Find me on OpenAlex');
    expect(api.publications).not.toHaveBeenCalled();
    fixture.destroy();
  });
});

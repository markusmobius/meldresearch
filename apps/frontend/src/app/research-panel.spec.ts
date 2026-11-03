import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ResearchPanel } from './research-panel';
import { ResearchApi, ResearchStatus } from './research-api';
import { LocalPage } from './meld-toolbar';

describe('Research Updates onboarding', () => {
  const projectId = '0123456789abcdef01234567';
  const api = {
    status: vi.fn(), link: vi.fn(), unlink: vi.fn(), monitor: vi.fn(),
    forget: vi.fn(), sync: vi.fn(), demoChange: vi.fn(), decideGmailThread: vi.fn(), decidePendingFile: vi.fn(),
  };
  const base: ResearchStatus = {
    account: { id: 'account-one', name: 'Synthetic researcher', email: 'researcher@example.invalid' },
    credentialExpiresAt: null, pendingLink: null, accountError: null, busy: false, dummy: false,
    privacyUrl: 'https://www.meldresearch.org/privacy.html', consentVersion: 'overleaf-foundry-2026-10',
    pollSeconds: 300, pendingRevocations: 0, projects: [], updates: [], totalUpdates: 0,
    decisions: [], overleafProjects: null, researcher: null, processing: null,
  };
  beforeEach(() => {
    vi.resetAllMocks();
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] });
    api.status.mockResolvedValue(base);
    TestBed.configureTestingModule({ imports: [ResearchPanel], providers: [
      provideNoopAnimations(),
      { provide: ResearchApi, useValue: api },
    ] });
  });
  afterEach(() => vi.useRealTimers());
  async function screen(page: LocalPage = 'home') {
    const fixture = TestBed.createComponent(ResearchPanel);
    fixture.componentRef.setInput('page', page);
    fixture.detectChanges();
    await vi.waitFor(() => expect(fixture.componentInstance.state()).not.toBeNull());
    fixture.detectChanges();
    return fixture;
  }

  it('keeps the prior feed visible when a status request fails', async () => {
    const fixture = await screen();
    api.status.mockRejectedValue(new Error('Synthetic transport failure'));
    await fixture.componentInstance.refresh();
    expect(fixture.componentInstance.state()).toEqual(base);
    expect(fixture.componentInstance.error()).not.toBe('');
    fixture.destroy();
  });

  it('does not promise an extension or private-data source in Dummy mode', async () => {
    api.status.mockResolvedValue({ ...base, dummy: true });
    const fixture = await screen('home');
    expect(fixture.nativeElement.textContent).toContain('Foundry processing is real');
    expect(fixture.nativeElement.textContent).toContain('Synthetic Overleaf evidence');
    fixture.destroy();
  });

  it('shows which project and file Foundry is working on, and names projects that need attention', async () => {
    const project = (name: string, error: string | null, phase = error ? 'error' : 'ready') =>
      ({ projectId: name, name, enabled: true, consentRequired: false, error, phase }) as unknown as ResearchStatus['projects'][number];
    api.status.mockResolvedValue({ ...base, busy: true,
      projects: [project('Agentic Markets', null), project('NewsSlant', 'Foundry rejected the request.'), project('Favors', 'Too large.'),
        project('Briefly offline', "Meld's server is unavailable.", 'retrying')],
      processing: { projectId: 'Agentic Markets', name: 'Agentic Markets', kind: 'import', filesDone: 3, filesTotal: 12, queued: 2,
        currentFile: 'sections/model.tex', alsoRunning: ['NewsSlant', 'Favors'] } });
    const fixture = await screen('home');
    const strip: HTMLElement = fixture.nativeElement.querySelector('.processing-strip');
    expect(strip.textContent).toContain('Importing Agentic Markets');
    expect(strip.textContent).toContain('3 / 12 files');
    expect(strip.textContent).toContain('Azure AI Foundry is reading model.tex');
    expect(strip.textContent).toContain('2 more projects queued');
    expect(strip.textContent).toContain('Also: NewsSlant, Favors');
    expect(strip.querySelector('[title="sections/model.tex"]')).not.toBeNull();
    expect(fixture.componentInstance.progressPercent()).toBe(25);
    expect(fixture.nativeElement.querySelector('[role="alert"]')?.textContent).toContain('2 projects could not be processed: NewsSlant, Favors.');
    fixture.destroy();
  });

  it('shows what changed research projects as compact Update cards and opens the changes in a dialog', async () => {
    const card = 'a'.repeat(32);
    const changes = [
      { id: 'c1', cardId: card, cardTitle: 'A publisher content marketplace', at: '2026-11-02T10:00:00Z', kind: 'gmail' as const, title: 'Pilot results',
        projectName: null, documents: [], changes: [
          { op: 'revise' as const, detailId: 'd2', before: 'A pilot is planned.', after: 'A pilot with 80 publishers is running.' },
          { op: 'add' as const, detailId: 'd3', before: null, after: 'Publishers are paid per citation.' },
          { op: 'add' as const, detailId: 'd4', before: null, after: 'A third.' },
          { op: 'remove' as const, detailId: 'd5', before: 'An old claim.', after: null }] },
      { id: 'c2', cardId: card, cardTitle: 'A publisher content marketplace', at: '2026-10-01T10:00:00Z', kind: 'overleaf' as const, title: 'main.tex',
        projectName: 'pcm-paper', documents: ['main.tex', 'model.tex'], changes: [{ op: 'add' as const, detailId: 'd1', before: null, after: 'The paper designs a PCM.' }] }];
    api.status.mockResolvedValue({ ...base, changes, totalChanges: 2 });
    const fixture = await screen('home');
    const element: HTMLElement = fixture.nativeElement;
    const cards = element.querySelectorAll('#feed-updates .feed-card');
    expect(cards).toHaveLength(2);
    expect(cards[0].querySelector('.feed-kind')?.textContent).toBe('Email');
    expect(cards[0].querySelector('.feed-source')?.classList).toContain('mail');
    expect(cards[0].querySelector('.feed-card-title')?.textContent).toContain('A publisher content marketplace');
    // The first three changes, then how many more.
    expect([...cards[0].querySelectorAll('.change-bullets li')].map(item => item.textContent?.trim())).toEqual([
      'RevisedA pilot with 80 publishers is running.', 'AddedPublishers are paid per citation.', 'AddedA third.', '1 more']);
    expect(cards[1].querySelector('.feed-kind')?.textContent).toBe('Overleaf');
    expect(cards[1].querySelector('.feed-project')?.getAttribute('title')).toBe('main.tex, model.tex');
    expect(element.querySelector('.feed-end')?.textContent).toContain('Caught up.');

    cards[0].querySelector<HTMLButtonElement>('.feed-card-open')!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    const dialog = document.querySelector('mat-dialog-container')!;
    expect(dialog.textContent).toContain('Pilot results');
    expect(dialog.querySelectorAll('.change-detail li')).toHaveLength(4);
    expect(dialog.querySelector('.change-detail .was')?.textContent).toContain('Was: A pilot is planned.');
    expect(dialog.querySelector('.op-remove')?.textContent).toContain('An old claim.');
    api.status.mockResolvedValue({ ...base, account: null });
    await fixture.componentInstance.refresh();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(fixture.componentInstance.selectedChange()).toBeNull();
    expect(document.querySelector('mat-dialog-container')).toBeNull();
    fixture.destroy();
  });

  it('lets the user decide email threads and removed files in Private updates', async () => {
    const card = 'a'.repeat(32), other = 'b'.repeat(32);
    const bullet = (id: string, text: string) => ({ id, text, sentAt: '2026-11-01T10:00:00Z', newsworthy: false, evidence: [] });
    const profile = (title: string) => ({ title, description: 'd', contributions: [], topics: [] });
    api.status.mockResolvedValue({ ...base,
      themes: [{ id: card, profile: profile('Weekly feedback study'), projectIds: [], updating: false },
        { id: other, profile: profile('Pricing'), projectIds: [], updating: false }],
      pendingFiles: [{ projectId, projectName: 'feedback-paper', path: 'appendix/old-pilot.tex', fromCardId: card,
        fromCardTitle: 'Weekly feedback study', removedAt: '2026-11-03T00:00:00Z' }],
      gmail: { configured: true, windowDays: 30, consentVersion: 'gmail-2026-11', lastError: null,
        accounts: [{ address: 'me@example.invalid', status: 'connected', error: null, threads: 3, checkedAt: null, connectedAt: '2026-11-01T00:00:00Z' }],
        threads: [{ address: 'me@example.invalid', id: 'g1', subject: 'Grant budget', title: 'Grant budget', status: 'pending', cardId: null, cardTitle: null,
          suggested: 'Grant renewal', lastSentAt: '2026-10-20T00:00:00Z', newsAt: null, bullets: [bullet('g', 'The user finalized the budget.')] }] } });
    api.decideGmailThread.mockResolvedValue(undefined);
    api.decidePendingFile.mockResolvedValue(undefined);
    const fixture = await screen('home');
    const element: HTMLElement = fixture.nativeElement;
    const column = element.querySelector('#feed-private')!;
    expect(column.querySelector('h2')?.textContent).toContain('Private updates');
    expect(column.querySelector('.feed-count')?.textContent).toBe('2');
    const [file, waiting] = [...column.querySelectorAll('.pending-card')];
    // Newest first: the removed file, then the thread.
    expect(file.classList).toContain('file-card');
    expect(file.querySelector('.file-path')?.textContent).toBe('appendix/old-pilot.tex');
    expect(file.textContent).toContain('Removed from Weekly feedback study');
    file.querySelector<HTMLButtonElement>('.add-file')!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    // The project it was removed from is not offered.
    const targets = [...document.querySelectorAll<HTMLButtonElement>('.mat-mdc-menu-item')];
    expect(targets.map(item => item.textContent?.trim())).toEqual(['topicPricing']);
    targets[0].click();
    await vi.waitFor(() => expect(api.decidePendingFile).toHaveBeenCalledWith(expect.objectContaining({ path: 'appendix/old-pilot.tex' }), 'project', other));
    await vi.waitFor(() => expect(fixture.componentInstance.deciding()).toBeNull());
    fixture.detectChanges();
    file.querySelector<HTMLButtonElement>('.skip-file')!.click();
    await vi.waitFor(() => expect(api.decidePendingFile).toHaveBeenLastCalledWith(expect.objectContaining({ projectId }), 'dismiss', undefined));
    await vi.waitFor(() => expect(fixture.componentInstance.deciding()).toBeNull());
    fixture.detectChanges();

    expect(waiting.textContent).toContain('Suggested project: Grant renewal');
    waiting.querySelector<HTMLButtonElement>('.new-thread')!.click();
    fixture.detectChanges();
    const input = waiting.querySelector<HTMLInputElement>('.thread-name input')!;
    expect(input.value).toBe('Grant renewal');
    input.value = 'Grant renewal 2027';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    waiting.querySelector<HTMLButtonElement>('.thread-name button[type="submit"]')!.click();
    await vi.waitFor(() => expect(api.decideGmailThread).toHaveBeenCalledWith(
      expect.objectContaining({ id: 'g1' }), 'new', undefined, 'Grant renewal 2027'));
    await vi.waitFor(() => expect(fixture.componentInstance.deciding()).toBeNull());
    fixture.detectChanges();
    waiting.querySelector<HTMLButtonElement>('.skip-thread')!.click();
    await vi.waitFor(() => expect(api.decideGmailThread).toHaveBeenCalledWith(expect.objectContaining({ id: 'g1' }), 'dismiss', undefined, undefined));
    fixture.destroy();
  });

  it('offers Private updates for a removed file even without Gmail', async () => {
    api.status.mockResolvedValue({ ...base, pendingFiles: [{ projectId, projectName: 'p', path: 'a.tex', fromCardId: 'c', fromCardTitle: null,
      removedAt: '2026-11-03T00:00:00Z' }] });
    const fixture = await screen('home');
    expect(fixture.nativeElement.querySelector('#feed-private .file-card')?.textContent).toContain('Removed from a research project');
    fixture.destroy();
  });

  it('keeps a single Updates column without Gmail', async () => {
    const fixture = await screen('home');
    expect(fixture.nativeElement.querySelector('#feed-private')).toBeNull();
    expect(fixture.nativeElement.querySelector('.feed-column-nav')).toBeNull();
    fixture.destroy();
  });
});

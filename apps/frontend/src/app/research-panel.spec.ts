import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ResearchPanel } from './research-panel';
import { ResearchApi, ResearchStatus } from './research-api';
import { LocalPage } from './meld-toolbar';

describe('Research Updates onboarding', () => {
  const projectId = '0123456789abcdef01234567';
  const api = {
    status: vi.fn(), link: vi.fn(), unlink: vi.fn(), monitor: vi.fn(),
    forget: vi.fn(), sync: vi.fn(), demoChange: vi.fn(), decideGmailThread: vi.fn(),
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

  it('renders compact Update cards and opens the complete evidence in a Material dialog', async () => {
    api.status.mockResolvedValue({ ...base, totalUpdates: 1, updates: [{
      id: 'synthetic-update', projectId, projectName: 'Synthetic project', kind: 'initialImport',
      title: 'Synthetic finding', summary: 'A source-grounded synthetic finding.', observedAt: '2026-01-01T00:00:00Z',
      sourceUpdatedAt: null, revision: '1234567890abcdef', previousRevision: null, previousSummary: null,
      evidence: [{ path: 'main.tex', quote: 'Exact synthetic evidence.', version: 'current' }],
    }] });
    const fixture = await screen('home');
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelector('.feed-column')).not.toBeNull();
    expect(element.querySelector('.feed-card.share-source-own')).not.toBeNull();
    expect(element.querySelector('.monitoring')).toBeNull();
    expect(element.querySelector('.account-page')).toBeNull();
    element.querySelector<HTMLButtonElement>('.feed-card-open')!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(document.querySelector('mat-dialog-container')?.textContent).toContain('Exact synthetic evidence.');
    expect(document.querySelector('mat-dialog-container')?.textContent).toContain('main.tex');
    api.status.mockResolvedValue({ ...base, account: null });
    await fixture.componentInstance.refresh();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(fixture.componentInstance.selectedUpdate()).toBeNull();
    expect(document.querySelector('mat-dialog-container')).toBeNull();
    fixture.destroy();
  });

  it('marks Updates from Gmail and shows their email evidence without an Overleaf link', async () => {
    api.status.mockResolvedValue({ ...base, totalUpdates: 1, updates: [{
      id: 'gmail-update', projectId: 'gmail-' + 'a'.repeat(32), projectName: 'Social learning paper', kind: 'change',
      title: 'Robustness confirmed', summary: 'The larger simulations confirm the result.', observedAt: '2026-11-02T10:00:00Z',
      sourceUpdatedAt: '2026-11-02T10:00:00Z', revision: 'gmail:t1:m2', previousRevision: null, previousSummary: null,
      evidence: [{ path: 'Robustness results', quote: 'The 200-agent simulations are done.', version: 'current',
        email: { messageId: 'm2', from: 'Demo <demo@example.invalid>', sentAt: '2026-11-02T10:00:00Z' } }],
    }] });
    const fixture = await screen('home');
    const element: HTMLElement = fixture.nativeElement;
    const source = element.querySelector('.feed-project')!;
    expect(source.querySelector('mat-icon')?.textContent).toContain('mail');
    expect(source.getAttribute('title')).toBe('Gmail workstream: Social learning paper');
    element.querySelector<HTMLButtonElement>('.feed-card-open')!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    const dialog = document.querySelector('mat-dialog-container')!;
    expect(dialog.textContent).toContain('Gmail workstream: Social learning paper');
    expect(dialog.textContent).toContain('from Demo <demo@example.invalid>');
    expect(dialog.querySelector('a[href*="overleaf.com"]')).toBeNull();
    expect(dialog.textContent).not.toContain('Revision');
    fixture.destroy();
  });

  it('shows email threads as growing bullet lists in Updates and lets the user decide threads in Private updates', async () => {
    const card = 'a'.repeat(32);
    const evidence = [{ path: 'Pilot results', quote: 'Task completion is 15 percent higher.', version: 'current' as const,
      email: { messageId: 'm1', from: 'Demo <demo@example.invalid>', sentAt: '2026-11-01T10:00:00Z' } }];
    const bullet = (id: string, text: string, newsworthy = true) => ({ id, text, sentAt: '2026-11-01T10:00:00Z', newsworthy, evidence });
    api.status.mockResolvedValue({ ...base, totalUpdates: 1,
      themes: [{ id: card, profile: { title: 'Weekly feedback study', description: 'd', contributions: [], topics: [] }, projectIds: [], updating: false }],
      updates: [{ id: 'u1', projectId, projectName: 'Synthetic project', kind: 'change', title: 'Overleaf change', summary: 'Changed.',
        observedAt: '2026-10-01T00:00:00Z', sourceUpdatedAt: null, revision: 'r', previousRevision: null, previousSummary: null, evidence: [] }],
      gmail: { configured: true, windowDays: 30, consentVersion: 'gmail-2026-11', lastError: null,
        accounts: [{ address: 'me@example.invalid', status: 'connected', error: null, threads: 3, checkedAt: null, connectedAt: '2026-11-01T00:00:00Z' }],
        threads: [
          { address: 'me@example.invalid', id: 't1', subject: 'Pilot results', title: 'Pilot shows higher completion', status: 'assigned', cardId: card,
            cardTitle: 'Weekly feedback study', suggested: null, lastSentAt: '2026-11-02T00:00:00Z', newsAt: '2026-11-02T00:00:00Z',
            bullets: [1, 2, 3, 4, 5].map(index => bullet('b' + index, 'Bullet ' + index)) },
          { address: 'me@example.invalid', id: 'g1', subject: 'Grant budget', title: 'Grant budget', status: 'pending', cardId: null, cardTitle: null,
            suggested: 'Grant renewal', lastSentAt: '2026-10-20T00:00:00Z', newsAt: null, bullets: [bullet('g', 'The user finalized the budget.', false)] }] } });
    api.decideGmailThread.mockResolvedValue(undefined);
    const fixture = await screen('home');
    const element: HTMLElement = fixture.nativeElement;
    const updates = element.querySelector('#feed-updates')!;
    const cards = updates.querySelectorAll('.feed-card');
    // Newest first: the thread (Nov 2), then the Overleaf Update (Oct 1); one card per thread with its latest bullets.
    expect(cards.length).toBe(2);
    expect(cards[0].classList).toContain('thread-card');
    expect(cards[0].querySelector('.feed-card-title')?.textContent).toContain('Pilot shows higher completion');
    expect(Array.from(cards[0].querySelectorAll('.thread-bullets li:not(.thread-earlier)')).map(item => item.textContent?.trim()))
      .toEqual(['Bullet 2', 'Bullet 3', 'Bullet 4', 'Bullet 5']);
    expect(cards[0].querySelector('.thread-earlier')?.textContent).toContain('1 earlier');
    expect(cards[0].querySelector('.feed-project')?.textContent).toContain('Weekly feedback study');

    const pendingColumn = element.querySelector('#feed-private')!;
    expect(pendingColumn.querySelector('h2')?.textContent).toContain('Private updates');
    const waiting = pendingColumn.querySelector('.pending-card')!;
    expect(waiting.textContent).toContain('Suggested project: Grant renewal');
    expect(element.querySelector('.feed-column-nav')?.textContent).toContain('Private updates');

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

  it('keeps a single Updates column without Gmail', async () => {
    const fixture = await screen('home');
    expect(fixture.nativeElement.querySelector('#feed-private')).toBeNull();
    expect(fixture.nativeElement.querySelector('.feed-column-nav')).toBeNull();
    fixture.destroy();
  });
});

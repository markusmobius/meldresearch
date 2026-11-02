import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { PrivateProfile } from './private-profile';
import { ResearchApi, ResearchProject, ResearchStatus, ResearchUpdate } from './research-api';

describe('Private profile', () => {
  const paperId = '0123456789abcdef01234567', slidesId = '1123456789abcdef01234567', otherId = '2123456789abcdef01234567';
  const cardId = 'a'.repeat(32), otherCard = 'b'.repeat(32);
  const project = (projectId: string, name: string, title: string, overrides: Partial<ResearchProject> = {}): ResearchProject => ({
    projectId, name, enabled: true, consentRequired: false, revision: null, summary: null, factCount: 0, lastAttemptAt: null,
    lastSuccessAt: null, errorCode: null, error: null, explanation: null, unprocessedFiles: 0, phase: 'ready', filesDone: 0, filesTotal: 0,
    lastSweepAt: null, nextSweepAt: null,
    profile: { title, description: `A study of <img src=x> ${name}.`, contributions: ['A field experiment.'], topics: ['pricing', 'auctions', 'demand', 'learning', 'markets'] },
    ...overrides,
  });
  const day = 86400000;
  const update = (id: string, projectId: string, daysAgo: number, title: string): ResearchUpdate => ({
    id, projectId, projectName: projectId === slidesId ? 'news-slant-slides' : 'news-slant', kind: 'change', title, summary: 'The pilot grew to 80.',
    observedAt: new Date(Date.now() - daysAgo * day).toISOString(), sourceUpdatedAt: new Date(Date.now() - daysAgo * day).toISOString(),
    revision: 'r', previousRevision: null, previousSummary: null, evidence: [],
  });
  const api = { moveToTheme: vi.fn(), mergeTheme: vi.fn(), setThemeAnchor: vi.fn(), skipTheme: vi.fn(), decideGmailThread: vi.fn() };
  const status = (projects: ResearchProject[], themes: ResearchStatus['themes'], updates: ResearchUpdate[] = []): ResearchStatus => ({
    account: { id: 'a', name: 'R', email: 'r@example.invalid' }, credentialExpiresAt: null, pendingLink: null, accountError: null,
    busy: false, dummy: false, privacyUrl: '', consentVersion: 'v', pollSeconds: 300, pendingRevocations: 0,
    projects, totalUpdates: updates.length, decisions: [], overleafProjects: null, researcher: null, processing: null, updates, themes,
  });
  const grouped = () => status(
    [project(paperId, 'news-slant', 'Paper'), project(slidesId, 'news-slant-slides', 'Slides'), project(otherId, 'pricing', 'Pricing')],
    [{ id: cardId, projectIds: [paperId, slidesId], updating: false,
       profile: { title: 'Why outlets differ', description: 'One paragraph about news slant.', contributions: ['Selective sampling model.'], topics: ['media'] },
       blurb: 'Four sentences about news slant.' },
     { id: otherCard, projectIds: [otherId], updating: false, profile: null, blurb: null }],
    [update('recent', slidesId, 5, 'Slides updated'), update('old', paperId, 200, 'Old change')]);
  beforeEach(() => {
    vi.resetAllMocks();
    api.moveToTheme.mockResolvedValue(undefined);
    TestBed.configureTestingModule({ providers: [provideNoopAnimations(), { provide: ResearchApi, useValue: api }] });
  });
  afterEach(() => document.querySelectorAll('.cdk-overlay-container').forEach(node => node.remove()));

  it('shows one card per research line with its projects, full description and topics', () => {
    const fixture = TestBed.createComponent(PrivateProfile);
    fixture.componentRef.setInput('status', grouped());
    fixture.detectChanges();
    const element: HTMLElement = fixture.nativeElement;
    const cards = element.querySelectorAll<HTMLButtonElement>('.profile-card');
    expect(cards).toHaveLength(2);
    expect(cards[0].querySelector('.card-title')?.textContent).toBe('Why outlets differ');
    expect(cards[0].querySelector('.card-source')?.textContent).toContain('news-slant · news-slant-slides');
    expect(cards[0].querySelector('.card-description')?.textContent).toBe('Four sentences about news slant.');
    expect(cards[0].querySelector('.contributions')).toBeNull();
    expect(cards[0].querySelector('.recent-badge')?.textContent).toContain('1 update in the last 3 months');
    // A card without a combined summary yet shows its project's own summary, with at most three topics.
    expect(cards[1].querySelector('.card-title')?.textContent).toBe('Pricing');
    expect([...cards[1].querySelectorAll('.topic')].map(topic => topic.textContent?.trim())).toEqual(['pricing', 'auctions', 'demand', '+2']);
    expect(element.querySelector('img')).toBeNull();
  });

  it('orders cards by the most recent Overleaf edit of their projects', () => {
    const fixture = TestBed.createComponent(PrivateProfile);
    const value = grouped();
    fixture.componentRef.setInput('status', { ...value, updates: [], overleafProjects: { fetchedAt: '', projects: [
      { id: paperId, name: 'news-slant', updatedAt: '2026-01-01T12:00:00Z' }, { id: slidesId, name: 'news-slant-slides', updatedAt: '2026-02-01T12:00:00Z' },
      { id: otherId, name: 'pricing', updatedAt: '2026-10-01T12:00:00Z' }] } });
    fixture.detectChanges();
    const cards = [...fixture.nativeElement.querySelectorAll('.profile-card')] as HTMLElement[];
    expect(cards.map(card => card.querySelector('.card-title')?.textContent)).toEqual(['Pricing', 'Why outlets differ']);
    expect(cards[1].querySelector('.card-edited')?.textContent).toContain('Feb 1, 2026');
  });

  it('opens contributions, projects and recent updates, and moves a project to a card of its own', async () => {
    const fixture = TestBed.createComponent(PrivateProfile);
    fixture.componentRef.setInput('status', grouped());
    const changed = vi.fn();
    fixture.componentInstance.changed.subscribe(changed);
    fixture.detectChanges();
    fixture.nativeElement.querySelector('.profile-card').click();
    fixture.detectChanges();
    await fixture.whenStable();
    const dialog = document.querySelector('mat-dialog-container')!;
    expect([...dialog.querySelectorAll('[role="tab"]')].map(tab => tab.textContent?.trim())).toEqual(['Details', 'Recent updates (1)']);
    expect(dialog.querySelector('.contributions')?.textContent).toContain('Selective sampling model.');
    expect([...dialog.querySelectorAll('.member-name')].map(name => name.textContent)).toEqual(['news-slant', 'news-slant-slides']);
    const select = dialog.querySelectorAll<HTMLSelectElement>('.move select')[1];
    expect([...select.options].map(option => option.textContent)).toEqual(['This card', 'A card of its own', 'Pricing']);
    const merge = dialog.querySelector<HTMLSelectElement>('.merge-card select')!;
    expect([...merge.options].map(option => option.textContent)).toEqual(['Choose a card…', 'Pricing']);
    select.value = '';
    select.dispatchEvent(new Event('change'));
    await vi.waitFor(() => expect(api.moveToTheme).toHaveBeenCalledWith(slidesId, null));
    await vi.waitFor(() => expect(changed).toHaveBeenCalled());
    fixture.destroy();
  });

  it('shows a Gmail workstream on a card with its Gmail address, and moves it like a project', async () => {
    const workstreamId = 'gmail-' + 'c'.repeat(32);
    const value = grouped();
    const workstream = project(workstreamId, 'Grant renewal', 'Grant renewal', { source: 'gmail', parent: 'markus@example.invalid' });
    const fixture = TestBed.createComponent(PrivateProfile);
    fixture.componentRef.setInput('status', { ...value, projects: [...value.projects, workstream],
      themes: value.themes!.map(theme => theme.id === cardId ? { ...theme, projectIds: [...theme.projectIds, workstreamId] } : theme) });
    fixture.detectChanges();
    fixture.nativeElement.querySelector('.profile-card').click();
    fixture.detectChanges();
    await fixture.whenStable();
    const dialog = document.querySelector('mat-dialog-container')!;
    expect(dialog.querySelector('.members-title')?.textContent).toBe('On this card');
    const members = [...dialog.querySelectorAll('.members li')];
    expect(members[2].querySelector('.member-source')?.textContent).toContain('Gmail · markus@example.invalid');
    expect(members[0].querySelector('.member-source')).toBeNull();
    const select = members[2].querySelector<HTMLSelectElement>('.move select')!;
    select.value = otherCard;
    select.dispatchEvent(new Event('change'));
    await vi.waitFor(() => expect(api.moveToTheme).toHaveBeenCalledWith(workstreamId, otherCard));
    fixture.destroy();
  });

  it('marks the main paper a card is described from, lets the user choose another, and says why projects are grouped', async () => {
    api.setThemeAnchor.mockResolvedValue(undefined);
    const value = grouped();
    const fixture = TestBed.createComponent(PrivateProfile);
    fixture.componentRef.setInput('status', { ...value, themes: value.themes!.map(theme => theme.id === cardId
      ? { ...theme, anchorId: slidesId, reason: 'The slides present the same paper.' } : theme) });
    fixture.detectChanges();
    // The main paper is named first on the card.
    expect(fixture.nativeElement.querySelector('.card-source')?.textContent).toContain('news-slant-slides · news-slant');
    fixture.nativeElement.querySelector('.profile-card').click();
    fixture.detectChanges();
    await fixture.whenStable();
    const dialog = document.querySelector('mat-dialog-container')!;
    expect(dialog.querySelector('.grouping-reason')?.textContent).toContain('Grouped because: The slides present the same paper.');
    const members = [...dialog.querySelectorAll('.members li')];
    expect(members[1].querySelector('.main-paper')?.textContent).toContain('Main paper');
    expect(members[1].querySelector('.make-main')).toBeNull();
    members[0].querySelector<HTMLButtonElement>('.make-main')!.click();
    await vi.waitFor(() => expect(api.setThemeAnchor).toHaveBeenCalledWith(cardId, paperId));
    fixture.destroy();
  });

  it('keeps skipped work in its own section, skips a card from its dialog and brings work back', async () => {
    api.skipTheme.mockResolvedValue(undefined);
    api.decideGmailThread.mockResolvedValue(undefined);
    const value = grouped();
    const thread = (id: string, cardId: string | null) => ({ address: 'me@example.invalid', id, subject: 'Thesis draft', title: 'Daughter thesis draft ' + id,
      status: 'assigned' as const, cardId, cardTitle: null, suggested: null, bullets: [], lastSentAt: '2026-10-01T00:00:00Z', newsAt: null, skipped: true });
    const fixture = TestBed.createComponent(PrivateProfile);
    fixture.componentRef.setInput('status', { ...value,
      themes: value.themes!.map(theme => theme.id === otherCard ? { ...theme, skipped: true } : theme),
      gmail: { configured: true, windowDays: 30, consentVersion: 'v', accounts: [], lastError: null,
        threads: [thread('t1', otherCard), thread('t2', null)] } });
    fixture.detectChanges();
    const element: HTMLElement = fixture.nativeElement;
    // The skipped card is not among My projects but in Skipped, with its email threads; a thread still being grouped too.
    expect([...element.querySelectorAll('.profile-card .card-title')].map(title => title.textContent)).toEqual(['Why outlets differ']);
    const skipped = element.querySelector('.skipped-work')!;
    expect([...skipped.querySelectorAll('.skipped-title')].map(title => title.textContent?.trim())).toEqual(['Pricing', 'Being grouped…']);
    expect(skipped.textContent).toContain('Daughter thesis draft t1');
    skipped.querySelector<HTMLButtonElement>('.unskip-card')!.click();
    await vi.waitFor(() => expect(api.skipTheme).toHaveBeenCalledWith(otherCard, false));
    await vi.waitFor(() => expect(fixture.componentInstance.working()).toBe(''));
    skipped.querySelectorAll<HTMLButtonElement>('.unskip-thread')[1].click();
    await vi.waitFor(() => expect(api.decideGmailThread).toHaveBeenCalledWith(expect.objectContaining({ id: 't2' }), 'unskip'));
    await vi.waitFor(() => expect(fixture.componentInstance.working()).toBe(''));

    element.querySelector<HTMLButtonElement>('.profile-card')!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    document.querySelector<HTMLButtonElement>('mat-dialog-container .skip-card')!.click();
    await vi.waitFor(() => expect(api.skipTheme).toHaveBeenCalledWith(cardId, true));
    fixture.destroy();
  });

  it('merges a whole card into another card', async () => {
    api.mergeTheme.mockResolvedValue(undefined);
    const fixture = TestBed.createComponent(PrivateProfile);
    fixture.componentRef.setInput('status', grouped());
    fixture.detectChanges();
    fixture.nativeElement.querySelector('.profile-card').click();
    fixture.detectChanges();
    await fixture.whenStable();
    const merge = document.querySelector<HTMLSelectElement>('mat-dialog-container .merge-card select')!;
    merge.value = otherCard;
    merge.dispatchEvent(new Event('change'));
    await vi.waitFor(() => expect(api.mergeTheme).toHaveBeenCalledWith(cardId, otherCard));
    fixture.destroy();
  });

  it('shows import progress for a project that is not on a card yet', () => {
    const fixture = TestBed.createComponent(PrivateProfile);
    fixture.componentRef.setInput('status', status([project(paperId, 'news-slant', 'Paper', { profile: null, phase: 'importing', filesDone: 12, filesTotal: 95 })], []));
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.progress-note')?.textContent).toContain('12 of 95 files');
  });
});

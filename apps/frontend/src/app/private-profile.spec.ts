import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { PrivateProfile, skippedCount } from './private-profile';
import { ResearchApi, ResearchChange, ResearchDetail, ResearchProject, ResearchStatus } from './research-api';

describe('About me: my projects', () => {
  const paperId = '0123456789abcdef01234567', slidesId = '1123456789abcdef01234567', otherId = '2123456789abcdef01234567';
  const cardId = 'a'.repeat(32), otherCard = 'b'.repeat(32);
  const project = (projectId: string, name: string, overrides: Partial<ResearchProject> = {}): ResearchProject => ({
    projectId, name, enabled: true, consentRequired: false, revision: null, summary: null, factCount: 0, lastAttemptAt: null,
    lastSuccessAt: null, errorCode: null, error: null, explanation: null, unprocessedFiles: 0, phase: 'ready', filesDone: 0, filesTotal: 0,
    lastSweepAt: null, nextSweepAt: null, profile: null, ...overrides,
  });
  const mainTex = { key: `overleaf:${paperId}/main.tex`, kind: 'overleaf' as const, label: 'main.tex', version: 'abc', at: '2026-09-01T12:00:00Z',
    quote: 'We build a publisher content marketplace.', origin: 'news-slant' };
  const email = { key: 'gmail:me@example.invalid/t1', kind: 'gmail' as const, label: 'Pilot results', version: 'm2', at: '2026-10-02T12:00:00Z',
    quote: 'The pilot has 80 publishers.', origin: 'me@example.invalid' };
  const details: ResearchDetail[] = [
    { id: 'd1', text: 'The paper designs a publisher content marketplace (PCM).', sources: [mainTex, { ...mainTex, quote: 'A second cited line.' }] },
    { id: 'd2', text: 'A pilot with 80 publishers is running.', sources: [email] },
  ];
  const api = {
    moveToTheme: vi.fn(), mergeTheme: vi.fn(), setThemeAnchor: vi.fn(), skipTheme: vi.fn(), decideGmailThread: vi.fn(),
    removeSource: vi.fn(), changelog: vi.fn(), choices: vi.fn(), renameTheme: vi.fn(),
  };
  const status = (projects: ResearchProject[], themes: ResearchStatus['themes']): ResearchStatus => ({
    account: { id: 'a', name: 'R', email: 'r@example.invalid' }, credentialExpiresAt: null, pendingLink: null, accountError: null,
    busy: false, dummy: false, privacyUrl: '', consentVersion: 'v', pollSeconds: 300, pendingRevocations: 0,
    projects, totalUpdates: 0, decisions: [], overleafProjects: null, researcher: null, processing: null, updates: [], themes,
  });
  const grouped = () => status(
    [project(paperId, 'news-slant'), project(slidesId, 'news-slant-slides'), project(otherId, 'pricing')],
    [{ id: cardId, projectIds: [paperId, slidesId], updating: false, anchorId: paperId,
       profile: { title: 'A publisher content marketplace', description: 'Two sentences about <img src=x> the PCM.', contributions: [], topics: [] }, details },
     { id: otherCard, projectIds: [otherId], updating: false, profile: { title: 'Pricing', description: 'Pricing in two sentences.', contributions: [], topics: [] },
       details: [{ id: 'p1', text: 'Prices fall.', sources: [{ ...mainTex, key: `overleaf:${otherId}/main.tex`, at: '2026-01-01T00:00:00Z', origin: 'pricing' }] }] }]);
  const change = (id: string, at: string, overrides: Partial<ResearchChange> = {}): ResearchChange => ({
    id, cardId, cardTitle: 'A publisher content marketplace', at, kind: 'overleaf', title: 'main.tex', projectName: 'news-slant', documents: ['main.tex'],
    changes: [{ op: 'add', detailId: 'd1', before: null, after: 'The paper designs a PCM.' }], ...overrides,
  });
  beforeEach(() => {
    vi.resetAllMocks();
    for (const call of [api.moveToTheme, api.mergeTheme, api.setThemeAnchor, api.skipTheme, api.decideGmailThread, api.removeSource, api.renameTheme]) call.mockResolvedValue(undefined);
    TestBed.configureTestingModule({ providers: [provideNoopAnimations(), { provide: ResearchApi, useValue: api }] });
  });
  afterEach(() => document.querySelectorAll('.cdk-overlay-container').forEach(node => node.remove()));

  function render(value: ResearchStatus = grouped(), section: 'projects' | 'skipped' = 'projects') {
    const fixture = TestBed.createComponent(PrivateProfile);
    fixture.componentRef.setInput('status', value);
    fixture.componentRef.setInput('section', section);
    fixture.detectChanges();
    return fixture;
  }
  async function expand(fixture: ReturnType<typeof render>, index = 0) {
    fixture.nativeElement.querySelectorAll('.project-toggle')[index].click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement.querySelectorAll('.project-card')[index] as HTMLElement;
  }
  async function selectTab(fixture: ReturnType<typeof render>, card: HTMLElement, index: number) {
    card.querySelectorAll<HTMLElement>('[role="tab"]')[index].click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  it('shows each research project as a compact card: title, two-sentence summary, projects and how much it rests on', () => {
    const element: HTMLElement = render().nativeElement;
    const cards = element.querySelectorAll('.project-card');
    expect(cards).toHaveLength(2);
    expect(cards[0].querySelector('h3')?.textContent?.trim()).toBe('A publisher content marketplace');
    expect(cards[0].querySelector('.project-summary')?.textContent).toBe('Two sentences about <img src=x> the PCM.');
    expect(cards[0].querySelector('.project-with')?.textContent).toContain('news-slant · news-slant-slides');
    expect(cards[0].querySelector('.project-updates')?.textContent).toMatch(/2 details\s+· 2 sources/);
    expect(cards[0].querySelector('.project-date')?.textContent).toBe('Oct 2, 2026');
    // Collapsed: no details, no long description blocks.
    expect(element.querySelector('.project-details')).toBeNull();
    expect(element.querySelector('img')).toBeNull();
  });

  it('heads a card with its short name, shows the full paper title beneath it, and renames it', async () => {
    const value = grouped();
    const fixture = render({ ...value, themes: value.themes!.map(theme => theme.id === cardId
      ? { ...theme, name: 'Publisher Content Marketplace (PCM)', profile: { ...theme.profile!, title: 'Paying for Information, Not for Clicks: A Block-Level Marketplace' } }
      : theme) });
    const element: HTMLElement = fixture.nativeElement;
    const card = element.querySelectorAll<HTMLElement>('.project-card')[0];
    expect(card.querySelector('h3')?.textContent?.trim()).toBe('Publisher Content Marketplace (PCM)');
    expect(card.querySelector('.project-fulltitle')?.textContent).toBe('Paying for Information, Not for Clicks: A Block-Level Marketplace');
    // A card whose short name is its title shows no second line.
    expect(element.querySelectorAll<HTMLElement>('.project-card')[1].querySelector('.project-fulltitle')).toBeNull();

    card.querySelector<HTMLButtonElement>('.rename-project')!.click();
    fixture.detectChanges();
    expect(fixture.componentInstance.expandedId()).toBeNull();
    const input = card.querySelector<HTMLInputElement>('.rename-input')!;
    expect(input.value).toBe('Publisher Content Marketplace (PCM)');
    expect(card.textContent).not.toContain('Reset');
    input.value = 'PCM';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    card.querySelector<HTMLButtonElement>('.rename-save')!.click();
    await vi.waitFor(() => expect(api.renameTheme).toHaveBeenCalledWith(cardId, 'PCM'));
    await vi.waitFor(() => expect(fixture.componentInstance.renaming()).toBeNull());
  });

  it('orders cards by their latest change: a source or an Overleaf edit of a project', () => {
    const value = grouped();
    const element: HTMLElement = render({ ...value, overleafProjects: { fetchedAt: '', projects: [
      { id: otherId, name: 'pricing', updatedAt: '2026-10-20T12:00:00Z' }] } }).nativeElement;
    expect([...element.querySelectorAll('.project-card h3')].map(title => title.textContent?.trim())).toEqual(['Pricing', 'A publisher content marketplace']);
  });

  it('expands a card in place to its details; a detail shows its sources, and a source can be removed', async () => {
    const fixture = render();
    const changed = vi.fn();
    fixture.componentInstance.changed.subscribe(changed);
    const card = await expand(fixture);
    expect(card.querySelector('.project-toggle')?.getAttribute('aria-expanded')).toBe('true');
    expect([...card.querySelectorAll('[role="tab"]')].map(tab => tab.textContent?.trim())).toEqual(['Details (2)', 'Changelog', 'Projects (2)']);
    const items = card.querySelectorAll('.detail-list > li');
    expect([...items].map(item => item.querySelector('.detail-text span')?.textContent)).toEqual(
      ['The paper designs a publisher content marketplace (PCM).', 'A pilot with 80 publishers is running.']);
    expect(card.querySelector('.source-list')).toBeNull();

    items[0].querySelector<HTMLButtonElement>('.detail-text')!.click();
    fixture.detectChanges();
    const sources = card.querySelectorAll('.source-list > li');
    // One source cited at two lines: listed once with both quotes.
    expect(sources).toHaveLength(1);
    expect(sources[0].querySelector('.source-label')?.textContent).toContain('main.tex');
    expect(sources[0].querySelector('.source-label')?.textContent).toContain('news-slant');
    expect([...sources[0].querySelectorAll('blockquote')].map(quote => quote.textContent)).toEqual(
      ['We build a publisher content marketplace.', 'A second cited line.']);
    sources[0].querySelector<HTMLButtonElement>('.remove-source')!.click();
    await vi.waitFor(() => expect(api.removeSource).toHaveBeenCalledWith(cardId, mainTex.key));
    await vi.waitFor(() => expect(changed).toHaveBeenCalled());

    // Another card closes this one.
    await expand(fixture, 1);
    expect(fixture.nativeElement.querySelectorAll('.project-details')).toHaveLength(1);
    fixture.destroy();
  });

  it('shows the changelog newest first and loads earlier entries on request', async () => {
    const first = change('c1', '2026-10-02T12:00:00Z', { kind: 'gmail', title: 'Pilot results', projectName: null,
      changes: [{ op: 'revise', detailId: 'd2', before: 'A pilot is planned.', after: 'A pilot with 80 publishers is running.' }] });
    const second = change('c2', '2026-09-01T12:00:00Z', { changes: [] });
    const review = change('c3', '2026-08-01T12:00:00Z', { kind: 'review', title: 'Review: overlapping details merged, overtaken plans retired', projectName: null,
      changes: [{ op: 'remove', detailId: 'd9', before: 'A pilot is planned.', after: null }] });
    api.changelog.mockResolvedValueOnce({ entries: [first], more: true }).mockResolvedValueOnce({ entries: [second, review], more: false });
    const fixture = render();
    const card = await expand(fixture);
    await selectTab(fixture, card, 1);
    await vi.waitFor(() => { fixture.detectChanges(); expect(card.querySelectorAll('.change')).toHaveLength(1); });
    expect(api.changelog).toHaveBeenCalledWith(cardId, null);
    const entry = card.querySelector('.change')!;
    expect(entry.querySelector('.change-kind')?.textContent).toBe('Email');
    expect(entry.querySelector('.op-revise')?.textContent).toContain('A pilot with 80 publishers is running.');
    expect(entry.querySelector('.was')?.textContent).toContain('Was: A pilot is planned.');

    card.querySelector<HTMLButtonElement>('.changelog-state button')!.click();
    await vi.waitFor(() => { fixture.detectChanges(); expect(card.querySelectorAll('.change')).toHaveLength(3); });
    expect(api.changelog).toHaveBeenLastCalledWith(cardId, first);
    expect(card.querySelectorAll('.change')[1].textContent).toContain('Read; no change to the details.');
    expect(card.querySelectorAll('.change')[2].querySelector('.change-kind')?.textContent).toBe('Review');
    expect(card.querySelectorAll('.change')[2].querySelector('.op-remove')?.textContent).toContain('A pilot is planned.');
    expect(card.querySelector('.changelog-state')?.textContent).toContain("Beginning of this project's history.");
    fixture.destroy();
  });

  it('lists the projects of a card with its main paper, and moves, re-anchors and merges', async () => {
    const value = grouped();
    const fixture = render({ ...value, themes: value.themes!.map(theme => theme.id === cardId ? { ...theme, reason: 'The slides present the same paper.' } : theme) });
    const card = await expand(fixture);
    await selectTab(fixture, card, 2);
    const members = [...card.querySelectorAll('.members li')];
    expect(members.map(member => member.querySelector('.member-name')?.textContent)).toEqual(['news-slant', 'news-slant-slides']);
    expect(members[0].querySelector('.main-paper')?.textContent).toContain('Main paper');
    expect(card.querySelector('.grouping-reason')?.textContent).toContain('Grouped because: The slides present the same paper.');

    members[1].querySelector<HTMLButtonElement>('.make-main')!.click();
    await vi.waitFor(() => expect(api.setThemeAnchor).toHaveBeenCalledWith(cardId, slidesId));
    await vi.waitFor(() => expect(fixture.componentInstance.working()).toBe(''));
    fixture.detectChanges();

    // Move: the chooser ranks projects for this Overleaf project, and offers a project of its own; select, then confirm.
    api.choices.mockResolvedValue({ ranked: true, choices: [{ cardId: otherCard, title: 'Pricing', summary: 'Pricing in two sentences.',
      projects: ['pricing'], lastChanged: null, best: true, suggested: false }] });
    members[1].querySelector<HTMLButtonElement>('.move')!.click();
    await vi.waitFor(() => expect(document.querySelector('app-project-chooser .choice')).not.toBeNull());
    expect(api.choices).toHaveBeenCalledWith('project', slidesId);
    const rows = () => [...document.querySelectorAll<HTMLButtonElement>('app-project-chooser .choice')];
    expect(rows().map(row => row.querySelector('.title')?.textContent?.trim())).toEqual(['Pricing Close match', 'A project of its own']);
    rows()[1].click();
    fixture.detectChanges();
    expect(api.moveToTheme).not.toHaveBeenCalled();
    document.querySelector<HTMLButtonElement>('app-project-chooser .confirm')!.click();
    await vi.waitFor(() => expect(api.moveToTheme).toHaveBeenCalledWith(slidesId, null));
    await vi.waitFor(() => expect(fixture.componentInstance.working()).toBe(''));
    await vi.waitFor(() => expect(document.querySelector('app-project-chooser')).toBeNull());

    // Merge into: the projects closest to this one; the chosen one is confirmed.
    card.querySelector<HTMLButtonElement>('.merge-card')!.click();
    await vi.waitFor(() => expect(document.querySelector('app-project-chooser .choice')).not.toBeNull());
    expect(api.choices).toHaveBeenLastCalledWith('card', cardId);
    expect(rows()).toHaveLength(1);
    rows()[0].click();
    fixture.detectChanges();
    document.querySelector<HTMLButtonElement>('app-project-chooser .confirm')!.click();
    await vi.waitFor(() => expect(api.mergeTheme).toHaveBeenCalledWith(cardId, otherCard));
    fixture.destroy();
  });

  it('keeps skipped work in its own section, collapsed, skips a card and brings work back', async () => {
    const value = grouped();
    const thread = (id: string, cardId: string | null, lastSentAt = '2026-10-01T00:00:00Z') => ({ address: 'me@example.invalid', id, subject: 'Thesis draft',
      title: 'Daughter thesis draft ' + id, status: 'pending' as const, cardId, cardTitle: null, suggested: null, bullets: [], lastSentAt, newsAt: null, skipped: true });
    const skippedStatus: ResearchStatus = { ...value,
      themes: value.themes!.map(theme => theme.id === otherCard ? { ...theme, skipped: true } : theme),
      gmail: { configured: true, windowDays: 30, consentVersion: 'v', accounts: [], lastError: null,
        threads: [thread('t1', otherCard, '2026-01-01T00:00:00Z'), thread('t2', null, '2030-01-01T00:00:00Z')] } };
    expect(skippedCount(skippedStatus)).toBe(2);
    const fixture = render(skippedStatus);
    const element: HTMLElement = fixture.nativeElement;
    expect([...element.querySelectorAll('.project-card h3')].map(title => title.textContent?.trim())).toEqual(['A publisher content marketplace']);
    expect(element.querySelector('.skipped-work')).toBeNull();

    const view = render(skippedStatus, 'skipped');
    const skipped: HTMLElement = view.nativeElement.querySelector('.skipped-work')!;
    expect(view.nativeElement.querySelector('.project-card')).toBeNull();
    // The most recent email first; collapsed cards show what they hold, not their threads.
    expect([...skipped.querySelectorAll('.skipped-title')].map(title => title.textContent?.trim())).toEqual(['Being grouped…', 'Pricing']);
    expect(skipped.querySelector('.skipped-meta')?.textContent).toContain('1 email thread');
    expect(skipped.textContent).not.toContain('Daughter thesis draft t1');
    skipped.querySelectorAll<HTMLButtonElement>('.skipped-toggle')[1].click();
    view.detectChanges();
    expect(skipped.textContent).toContain('Daughter thesis draft t1');
    skipped.querySelector<HTMLButtonElement>('.unskip-card')!.click();
    await vi.waitFor(() => expect(api.skipTheme).toHaveBeenCalledWith(otherCard, false));
    await vi.waitFor(() => expect(view.componentInstance.working()).toBe(''));
    skipped.querySelectorAll<HTMLButtonElement>('.skipped-toggle')[0].click();
    view.detectChanges();
    skipped.querySelector<HTMLButtonElement>('.unskip-thread')!.click();
    await vi.waitFor(() => expect(api.decideGmailThread).toHaveBeenCalledWith(expect.objectContaining({ id: 't2' }), 'unskip'));
    await vi.waitFor(() => expect(view.componentInstance.working()).toBe(''));
    view.destroy();

    const card = await expand(fixture);
    await selectTab(fixture, card, 2);
    card.querySelector<HTMLButtonElement>('.skip-card')!.click();
    await vi.waitFor(() => expect(api.skipTheme).toHaveBeenCalledWith(cardId, true));
    fixture.destroy();
  });

  it('shows projects and skipped threads in pages of 20, loading more on request', () => {
    const projects = Array.from({ length: 45 }, (_, i) => project(`p${i}`, `paper-${i}`));
    const themes = projects.map((item, i) => ({ ...grouped().themes![0], id: `c${i}`, name: `Card ${i}`, projectIds: [item.projectId], anchorId: item.projectId }));
    const fixture = render({ ...status(projects, []), themes });
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelectorAll('.project-card')).toHaveLength(20);
    element.querySelector<HTMLButtonElement>('.more-cards')!.click();
    fixture.detectChanges();
    expect(element.querySelectorAll('.project-card')).toHaveLength(40);
    element.querySelector<HTMLButtonElement>('.more-cards')!.click();
    fixture.detectChanges();
    expect(element.querySelectorAll('.project-card')).toHaveLength(45);
    expect(element.querySelector('.more-cards')).toBeNull();
    fixture.destroy();

    const threads = Array.from({ length: 25 }, (_, i) => ({ address: 'me@example.invalid', id: `t${i}`, subject: 's', title: `Thread ${i}`, status: 'pending' as const,
      cardId: null, cardTitle: null, suggested: null, bullets: [], lastSentAt: new Date(Date.UTC(2026, 0, 1 + i)).toISOString(), newsAt: null, skipped: true }));
    const view = render({ ...grouped(), gmail: { configured: true, windowDays: 30, consentVersion: 'v', accounts: [], lastError: null, threads } }, 'skipped');
    const skipped: HTMLElement = view.nativeElement;
    skipped.querySelector<HTMLButtonElement>('.skipped-toggle')!.click();
    view.detectChanges();
    const titles = () => [...skipped.querySelectorAll('.skipped-thread-title')].map(title => title.textContent?.trim());
    expect(titles()).toHaveLength(20);
    expect(titles()[0]).toBe('Thread 24');
    skipped.querySelector<HTMLButtonElement>('.more-threads')!.click();
    view.detectChanges();
    expect(titles()).toHaveLength(25);
    view.destroy();
  });

  it('shows reading progress for a project that has no card yet', () => {
    const element: HTMLElement = render(status([project(paperId, 'news-slant', { phase: 'importing', filesDone: 12, filesTotal: 95 })], [])).nativeElement;
    expect(element.querySelector('.project-card h3')?.textContent?.trim()).toBe('news-slant');
    expect(element.querySelector('.progress-note')?.textContent).toContain('Reading news-slant: 12 of 95 files');
    expect(element.querySelector('.project-summary')?.textContent).toContain('appears once this project has been read');
  });
});

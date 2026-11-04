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
    decideHousekeepingGroup: vi.fn(), renameHousekeepingGroup: vi.fn(), choices: vi.fn(),
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

  const card = 'a'.repeat(32), other = 'b'.repeat(32), grant = 'c'.repeat(32), teaching = 'd'.repeat(32);
  const bullet = (id: string, text: string) => ({ id, text, sentAt: '2026-11-01T10:00:00Z', newsworthy: false, evidence: [] });
  const profile = (title: string) => ({ title, description: 'd', contributions: [], topics: [] });
  const waiting = (id: string, title: string, at: string, groupId: string | null, suggested: string | null = null) => ({
    address: 'me@example.invalid', id, subject: title, title, status: 'pending' as const, cardId: null, cardTitle: null, suggested,
    lastSentAt: at, newsAt: null, bullets: [bullet(id + 'b', 'Point of ' + title + '.')], groupId });
  const housekeeping = (): ResearchStatus => ({ ...base,
    themes: [{ id: card, profile: profile('Weekly feedback study'), projectIds: [], updating: false },
      { id: other, profile: profile('Pricing'), projectIds: [], updating: false }],
    pendingFiles: [
      { projectId, projectName: 'feedback-paper', path: 'appendix/old-pilot.tex', fromCardId: card, fromCardTitle: 'Weekly feedback study', removedAt: '2026-11-03T00:00:00Z' },
      { projectId, projectName: 'feedback-paper', path: 'appendix/notes.tex', fromCardId: card, fromCardTitle: 'Weekly feedback study', removedAt: '2026-11-02T00:00:00Z' }],
    gmail: { configured: true, windowDays: 30, consentVersion: 'gmail-2026-11', lastError: null,
      accounts: [{ address: 'me@example.invalid', status: 'connected', error: null, threads: 3, checkedAt: null, connectedAt: '2026-11-01T00:00:00Z' }],
      groups: [{ id: grant, name: 'Grant renewal', named: false, held: false, suggestedCardId: other, suggestedCardTitle: 'Pricing', suggestionReason: 'Same grant.' },
        { id: teaching, name: 'Teaching allocation', named: false, held: false }],
      threads: [
        waiting('g1', 'Grant budget', '2026-10-20T00:00:00Z', grant), waiting('g2', 'Reviewer letters', '2026-10-22T00:00:00Z', grant),
        ...[1, 2, 3, 4, 5].map(index => waiting('t' + index, 'Teaching thread ' + index, `2026-10-0${index}T00:00:00Z`, teaching)),
        waiting('n1', 'Shared link', '2026-10-01T00:00:00Z', null, 'Gemini link')] } });

  it('groups Housekeeping: threads by group, removed files by project, newest first', async () => {
    api.status.mockResolvedValue(housekeeping());
    const fixture = await screen('home');
    const column: HTMLElement = fixture.nativeElement.querySelector('#feed-private');
    expect(column.querySelector('h2')?.textContent).toContain('Housekeeping');
    expect(fixture.nativeElement.querySelector('.feed-column-nav')?.textContent).toContain('Housekeeping');
    // 2 removed files of one project, the grant group, the teaching group and a thread still being grouped: 4 cards.
    expect(column.querySelector('.feed-count')?.textContent).toBe('4');
    const cards = [...column.querySelectorAll<HTMLElement>('.pending-card')];
    expect(cards.map(item => item.querySelector('.feed-card-title')?.firstChild?.textContent?.trim())).toEqual(
      ['Removed from Weekly feedback study', 'Grant renewal', 'Teaching allocation', 'Gemini link']);
    const [files, grantCard, teachingCard, single] = cards;
    expect([...files.querySelectorAll('.file-path')].map(item => item.textContent)).toEqual(['appendix/old-pilot.tex', 'appendix/notes.tex']);
    expect(grantCard.querySelector('.feed-kind')?.textContent).toContain('2 threads');
    expect([...grantCard.querySelectorAll('.group-thread-title')].map(item => item.textContent)).toEqual(['Reviewer letters', 'Grant budget']);
    expect(grantCard.querySelector('.group-suggestion')?.textContent).toContain('Looks like Pricing');
    // A big group shows its newest threads and offers the rest.
    expect(teachingCard.querySelectorAll('.group-thread-title')).toHaveLength(4);
    teachingCard.querySelector<HTMLButtonElement>('.group-more button')!.click();
    fixture.detectChanges();
    expect(teachingCard.querySelectorAll('.group-thread-title')).toHaveLength(5);
    // A thread still being grouped is a card of its own, with its points.
    expect(single.querySelector('.grouping-tag')?.textContent).toContain('Grouping');
    expect(single.querySelector('.thread-bullets')?.textContent).toContain('Point of Shared link.');
    expect(single.querySelector('.rename-group')).toBeNull();
    fixture.destroy();
  });

  it('decides on a whole group at once: create project, add to the suggested one, skip all, rename', async () => {
    api.status.mockResolvedValue(housekeeping());
    for (const call of [api.decideHousekeepingGroup, api.renameHousekeepingGroup, api.decideGmailThread, api.decidePendingFile]) call.mockResolvedValue(undefined);
    const fixture = await screen('home');
    const column: HTMLElement = fixture.nativeElement.querySelector('#feed-private');
    const [files, grantCard, teachingCard, single] = [...column.querySelectorAll<HTMLElement>('.pending-card')];
    const settle = async () => { await vi.waitFor(() => expect(fixture.componentInstance.deciding()).toBeNull()); fixture.detectChanges(); await fixture.whenStable(); };

    grantCard.querySelector<HTMLButtonElement>('.accept-suggestion')!.click();
    await vi.waitFor(() => expect(api.decideHousekeepingGroup).toHaveBeenCalledWith(grant, 'project', other));
    await settle();

    teachingCard.querySelector<HTMLButtonElement>('.new-project')!.click();
    fixture.detectChanges();
    const input = teachingCard.querySelector<HTMLInputElement>('.thread-name input')!;
    expect(input.value).toBe('Teaching allocation');
    input.value = 'Teaching algorithm';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    teachingCard.querySelector<HTMLButtonElement>('.thread-name button[type="submit"]')!.click();
    await vi.waitFor(() => expect(api.decideHousekeepingGroup).toHaveBeenCalledWith(teaching, 'new', undefined, 'Teaching algorithm'));
    await settle();

    teachingCard.querySelector<HTMLButtonElement>('.rename-group')!.click();
    fixture.detectChanges();
    const rename = teachingCard.querySelector<HTMLInputElement>('.thread-name input')!;
    rename.value = 'Teaching';
    rename.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    teachingCard.querySelector<HTMLButtonElement>('.thread-name button[type="submit"]')!.click();
    await vi.waitFor(() => expect(api.renameHousekeepingGroup).toHaveBeenCalledWith(teaching, 'Teaching'));
    await settle();

    grantCard.querySelector<HTMLButtonElement>('.skip-group')!.click();
    await vi.waitFor(() => expect(api.decideHousekeepingGroup).toHaveBeenCalledWith(grant, 'dismiss'));
    await settle();

    // A thread still being grouped is decided on its own.
    single.querySelector<HTMLButtonElement>('.skip-group')!.click();
    await vi.waitFor(() => expect(api.decideGmailThread).toHaveBeenCalledWith(expect.objectContaining({ id: 'n1' }), 'dismiss'));
    await settle();

    // Removed files: added together through the chooser (ranked for those files), selecting and then confirming.
    api.choices.mockResolvedValue({ ranked: true, choices: [{ cardId: other, title: 'Pricing', summary: 'Prices.', projects: [], lastChanged: null,
      best: true, suggested: false }] });
    files.querySelector<HTMLButtonElement>('.add-group')!.click();
    await vi.waitFor(() => expect(document.querySelector('app-project-chooser .choice')).not.toBeNull());
    expect(api.choices).toHaveBeenCalledWith('files', card);
    // No "new project" choice for files.
    expect(document.querySelectorAll('app-project-chooser .choice')).toHaveLength(1);
    document.querySelector<HTMLButtonElement>('app-project-chooser .choice')!.click();
    fixture.detectChanges();
    document.querySelector<HTMLButtonElement>('app-project-chooser .confirm')!.click();
    await vi.waitFor(() => expect(api.decidePendingFile).toHaveBeenCalledTimes(2));
    expect(api.decidePendingFile).toHaveBeenCalledWith(expect.objectContaining({ path: 'appendix/notes.tex' }), 'project', other);
    fixture.destroy();
  });

  it('splits a group: a thread moves to another group or a new one, or goes to a project on its own', async () => {
    api.status.mockResolvedValue(housekeeping());
    api.decideGmailThread.mockResolvedValue(undefined);
    const fixture = await screen('home');
    const grantCard = fixture.nativeElement.querySelectorAll('#feed-private .pending-card')[1] as HTMLElement;
    const menuItems = () => [...[...document.querySelectorAll('.mat-mdc-menu-panel')].at(-1)!.querySelectorAll<HTMLButtonElement>('.mat-mdc-menu-item')];
    const open = async (button: HTMLButtonElement) => { button.click(); fixture.detectChanges(); await fixture.whenStable(); };

    await open(grantCard.querySelectorAll<HTMLButtonElement>('.group-thread-menu')[0]);
    expect(menuItems().map(item => item.textContent?.trim())).toEqual(
      ['drive_file_moveMove to group', 'call_splitNew group', 'playlist_addAdd to project…', 'closeSkip this thread']);
    menuItems()[1].click();
    await vi.waitFor(() => expect(api.decideGmailThread).toHaveBeenCalledWith(expect.objectContaining({ id: 'g2' }), 'group', undefined, undefined, null));
    await vi.waitFor(() => expect(fixture.componentInstance.deciding()).toBeNull());
    fixture.detectChanges();

    await open(grantCard.querySelectorAll<HTMLButtonElement>('.group-thread-menu')[1]);
    await open(menuItems()[0]);
    // Other groups only, not its own.
    expect(menuItems().map(item => item.textContent?.trim())).toEqual(['folder_openTeaching allocation']);
    menuItems()[0].click();
    await vi.waitFor(() => expect(api.decideGmailThread).toHaveBeenCalledWith(expect.objectContaining({ id: 'g1' }), 'group', undefined, undefined, teaching));
    fixture.destroy();
  });

  it('offers Housekeeping for a removed file even without Gmail', async () => {
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

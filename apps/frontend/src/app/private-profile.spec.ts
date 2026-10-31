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
  const api = { moveToTheme: vi.fn() };
  const status = (projects: ResearchProject[], themes: ResearchStatus['themes'], updates: ResearchUpdate[] = []): ResearchStatus => ({
    account: { id: 'a', name: 'R', email: 'r@example.invalid' }, credentialExpiresAt: null, pendingLink: null, accountError: null,
    busy: false, dummy: false, privacyUrl: '', consentVersion: 'v', pollSeconds: 300, pendingRevocations: 0,
    projects, totalUpdates: updates.length, decisions: [], overleafProjects: null, researcher: null, processing: null, updates, themes,
  });
  const grouped = () => status(
    [project(paperId, 'news-slant', 'Paper'), project(slidesId, 'news-slant-slides', 'Slides'), project(otherId, 'pricing', 'Pricing')],
    [{ id: cardId, projectIds: [paperId, slidesId], updating: false,
       profile: { title: 'Why outlets differ', description: 'One paragraph about news slant.', contributions: ['Selective sampling model.'], topics: ['media'] } },
     { id: otherCard, projectIds: [otherId], updating: false, profile: null }],
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
    expect(cards[0].querySelector('.card-description')?.textContent).toBe('One paragraph about news slant.');
    expect(cards[0].querySelector('.contributions')).toBeNull();
    expect(cards[0].querySelector('.recent-badge')?.textContent).toContain('1 update in the last 3 months');
    // A card without a combined summary yet shows its project's own summary.
    expect(cards[1].querySelector('.card-title')?.textContent).toBe('Pricing');
    expect(cards[1].querySelectorAll('.topic')).toHaveLength(5);
    expect(element.querySelector('img')).toBeNull();
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
    select.value = '';
    select.dispatchEvent(new Event('change'));
    await vi.waitFor(() => expect(api.moveToTheme).toHaveBeenCalledWith(slidesId, null));
    await vi.waitFor(() => expect(changed).toHaveBeenCalled());
    fixture.destroy();
  });

  it('shows import progress for a project that is not on a card yet', () => {
    const fixture = TestBed.createComponent(PrivateProfile);
    fixture.componentRef.setInput('status', status([project(paperId, 'news-slant', 'Paper', { profile: null, phase: 'importing', filesDone: 12, filesTotal: 95 })], []));
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.progress-note')?.textContent).toContain('12 of 95 files');
  });
});

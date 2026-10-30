import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ResearchPanel } from './research-panel';
import { ResearchApi, ResearchStatus } from './research-api';
import { LocalPage } from './meld-toolbar';

describe('Research Updates onboarding', () => {
  const projectId = '0123456789abcdef01234567';
  const api = {
    status: vi.fn(), link: vi.fn(), unlink: vi.fn(), monitor: vi.fn(),
    forget: vi.fn(), sync: vi.fn(), demoChange: vi.fn(),
  };
  const base: ResearchStatus = {
    account: { id: 'account-one', name: 'Synthetic researcher', email: 'researcher@example.invalid' },
    credentialExpiresAt: null, pendingLink: null, accountError: null, busy: false, dummy: false,
    privacyUrl: 'https://www.meldresearch.org/privacy.html', consentVersion: 'overleaf-foundry-2026-10',
    pollSeconds: 300, pendingRevocations: 0, projects: [], updates: [], totalUpdates: 0,
    decisions: [], overleafProjects: null, researcher: null,
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
});

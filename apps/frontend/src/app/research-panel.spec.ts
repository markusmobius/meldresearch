import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ResearchPanel } from './research-panel';
import { ResearchApi, ResearchStatus } from './research-api';
import { OverleafApi } from './overleaf-api';
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
  };
  beforeEach(() => {
    vi.resetAllMocks();
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] });
    api.status.mockResolvedValue(base);
    api.monitor.mockResolvedValue(undefined);
    TestBed.configureTestingModule({ imports: [ResearchPanel], providers: [
      provideNoopAnimations(),
      { provide: ResearchApi, useValue: api }, { provide: OverleafApi, useValue: { status: vi.fn().mockResolvedValue({}) } },
    ] });
  });
  afterEach(() => vi.useRealTimers());
  async function screen(page: LocalPage = 'about-me') {
    const fixture = TestBed.createComponent(ResearchPanel);
    fixture.componentRef.setInput('page', page);
    fixture.componentRef.setInput('projects', [{ id: projectId, name: '<img src=x> Synthetic project', updatedAt: null }]);
    fixture.componentRef.setInput('sourceReady', true);
    fixture.detectChanges();
    await vi.waitFor(() => expect(fixture.componentInstance.state()).not.toBeNull());
    fixture.detectChanges();
    return fixture;
  }

  it('does not enable monitoring without an explicit consent action', async () => {
    const fixture = await screen();
    await fixture.componentInstance.monitor(projectId, true);
    expect(api.monitor).not.toHaveBeenCalled();
    expect(fixture.componentInstance.error()).toContain('Approve processing');
    fixture.componentInstance.consent.set(true);
    await fixture.componentInstance.monitor(projectId, true);
    expect(api.monitor).toHaveBeenCalledWith(projectId, true, base.consentVersion);
    expect(fixture.nativeElement.querySelector('img')).toBeNull();
    fixture.destroy();
  });

  it('signs in with Google by opening the website sign-in page in this tab', async () => {
    api.status.mockResolvedValue({ ...base, account: null });
    const fixture = await screen('account');
    const signInUrl = 'https://www.meldresearch.org/connect?state=a&challenge=b&redirect_uri=c&device=d';
    api.link.mockResolvedValue({ signInUrl, expiresAt: '2026-10-30T00:00:00Z' });
    const navigate = vi.spyOn(fixture.componentInstance, 'navigate').mockImplementation(() => undefined);
    const button = fixture.nativeElement.querySelector('button.google-sign-in') as HTMLButtonElement;
    expect(button.textContent).toContain('Sign in with Google');
    expect(fixture.nativeElement.textContent).not.toContain('Verification code');
    button.click();
    await vi.waitFor(() => expect(navigate).toHaveBeenCalledWith(signInUrl));
    fixture.destroy();
  });

  it('clears a previous consent checkbox when the linked account changes', async () => {
    const fixture = await screen();
    fixture.componentInstance.consent.set(true);
    api.status.mockResolvedValue({ ...base, account: { ...base.account, id: 'account-two' } });
    await fixture.componentInstance.refresh();
    expect(fixture.componentInstance.consent()).toBe(false);
    fixture.destroy();
  });

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
    const fixture = await screen('account');
    expect(fixture.nativeElement.textContent).toContain('Foundry processing is real');
    expect(fixture.nativeElement.textContent).toContain('synthetic');
    fixture.destroy();
  });

  it('keeps onboarding out of the feed after selection but exposes project failures', async () => {
    api.status.mockResolvedValue({ ...base, projects: [{
      projectId, name: 'Synthetic', enabled: true, consentRequired: false, revision: null, summary: null,
      factCount: 0, lastAttemptAt: null, lastSuccessAt: null, errorCode: 'synthetic_failure',
      error: 'Synthetic model failure; prior state preserved.', explanation: null, unprocessedFiles: 0,
    }] });
    const fixture = await screen();
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelector('.monitoring mat-expansion-panel-header')?.getAttribute('aria-expanded')).toBe('false');
    expect(element.querySelector('.project-statuses mat-expansion-panel-header')?.getAttribute('aria-expanded')).toBe('true');
    expect(element.textContent).toContain('prior state preserved');
    fixture.destroy();
  });

  it('uses an explicit Material checkbox before enabling the monitoring action', async () => {
    const fixture = await screen();
    const element: HTMLElement = fixture.nativeElement;
    const monitor = element.querySelector<HTMLButtonElement>('.selection button')!;
    expect(element.querySelector('mat-checkbox')).not.toBeNull();
    expect(monitor.disabled).toBe(true);
    element.querySelector<HTMLInputElement>('mat-checkbox input')!.click();
    fixture.detectChanges();
    expect(fixture.componentInstance.consent()).toBe(true);
    expect(monitor.disabled).toBe(false);
    monitor.click();
    await fixture.whenStable();
    expect(api.monitor).toHaveBeenCalledWith(projectId, true, base.consentVersion);
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

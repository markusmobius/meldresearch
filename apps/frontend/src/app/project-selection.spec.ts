import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ProjectSelection } from './project-selection';
import { ResearchApi, ResearchStatus } from './research-api';
import { OverleafApi } from './overleaf-api';

describe('Project selection', () => {
  const recentId = '0123456789abcdef01234567';
  const olderId = '1123456789abcdef01234567';
  const api = { decide: vi.fn(), monitor: vi.fn(), forget: vi.fn(), demoChange: vi.fn() };
  const base: ResearchStatus = {
    account: { id: 'account-one', name: 'Synthetic researcher', email: 'researcher@example.invalid' },
    credentialExpiresAt: null, pendingLink: null, accountError: null, busy: false, dummy: false,
    privacyUrl: 'https://www.meldresearch.org/privacy.html', consentVersion: 'overleaf-foundry-2026-10',
    pollSeconds: 300, pendingRevocations: 0, projects: [], updates: [], totalUpdates: 0,
    decisions: [], overleafProjects: null, researcher: null,
  };
  const projects = [
    { id: recentId, name: '<img src=x> Recent project', updatedAt: new Date(Date.now() - 86400000).toISOString() },
    { id: olderId, name: 'Older project', updatedAt: '2019-05-01T00:00:00Z' },
  ];
  beforeEach(() => {
    vi.resetAllMocks();
    api.decide.mockResolvedValue(undefined);
    TestBed.configureTestingModule({ imports: [ProjectSelection], providers: [
      provideNoopAnimations(),
      { provide: ResearchApi, useValue: api }, { provide: OverleafApi, useValue: { status: vi.fn().mockResolvedValue({}) } },
    ] });
  });
  function screen(status: ResearchStatus = base) {
    const fixture = TestBed.createComponent(ProjectSelection);
    fixture.componentRef.setInput('status', status);
    fixture.componentRef.setInput('projects', projects);
    fixture.componentRef.setInput('sourceReady', true);
    fixture.detectChanges();
    return fixture;
  }

  it('asks for an active choice on recent projects and keeps older ones optional', () => {
    const fixture = screen();
    const element: HTMLElement = fixture.nativeElement;
    const rows = [...element.querySelectorAll('.decision-list')[0].querySelectorAll('.decision-row')];
    expect(rows).toHaveLength(1);
    expect(rows[0].classList.contains('undecided')).toBe(true);
    expect(element.querySelector('.to-decide')?.textContent).toContain('1 to decide');
    expect(element.querySelector('.older-projects')?.textContent).toContain('Older project');
    expect(element.querySelector('.older-projects .decision-row.undecided')).toBeNull();
    expect(element.querySelector('img')).toBeNull();
    fixture.destroy();
  });

  it('includes only after explicit consent, while excluding never needs consent', async () => {
    const fixture = screen();
    const element: HTMLElement = fixture.nativeElement;
    const include = element.querySelector<HTMLButtonElement>('.decision-row .include')!;
    expect(include.disabled).toBe(true);
    await fixture.componentInstance.decide(projects[0], true);
    expect(api.decide).not.toHaveBeenCalled();
    expect(fixture.componentInstance.error()).toContain('Approve processing');

    element.querySelector<HTMLButtonElement>('.decision-row .exclude')!.click();
    await vi.waitFor(() => expect(api.decide).toHaveBeenCalledWith(recentId, false, null));

    element.querySelector<HTMLInputElement>('mat-checkbox input')!.click();
    fixture.detectChanges();
    expect(include.disabled).toBe(false);
    include.click();
    await vi.waitFor(() => expect(api.decide).toHaveBeenCalledWith(recentId, true, base.consentVersion));
    fixture.destroy();
  });

  it('shows decisions, treats previously monitored projects as included, and resets consent for another account', () => {
    const monitored = { projectId: recentId, name: 'Recent', enabled: true, consentRequired: false, revision: null, summary: null,
      factCount: 2, lastAttemptAt: null, lastSuccessAt: null, errorCode: null, error: null, explanation: null, unprocessedFiles: 0 };
    const fixture = screen({ ...base, projects: [monitored],
      decisions: [{ projectId: olderId, included: false, decidedAt: '2026-10-01T00:00:00Z' }] });
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelector('.decision-row .include')?.classList.contains('selected')).toBe(true);
    expect(element.querySelector('.all-decided')).not.toBeNull();
    expect(element.querySelector('.older-projects .exclude')?.classList.contains('selected')).toBe(true);
    expect(element.querySelector('.project-card')?.textContent).toContain('2 facts');

    fixture.componentInstance.consent.set(true);
    fixture.componentRef.setInput('status', { ...base, account: { ...base.account!, id: 'account-two' } });
    fixture.detectChanges();
    expect(fixture.componentInstance.consent()).toBe(false);
    fixture.destroy();
  });
});

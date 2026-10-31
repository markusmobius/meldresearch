import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ProjectSelection } from './project-selection';
import { ResearchApi, ResearchStatus } from './research-api';
import { OverleafApi } from './overleaf-api';

describe('Project selection', () => {
  const recentId = '0123456789abcdef01234567';
  const olderId = '1123456789abcdef01234567';
  const api = { decide: vi.fn(), monitor: vi.fn(), forget: vi.fn(), demoChange: vi.fn(), summarize: vi.fn() };
  const base: ResearchStatus = {
    account: { id: 'account-one', name: 'Synthetic researcher', email: 'researcher@example.invalid' },
    credentialExpiresAt: null, pendingLink: null, accountError: null, busy: false, dummy: false,
    privacyUrl: 'https://www.meldresearch.org/privacy.html', consentVersion: 'overleaf-foundry-2026-10',
    pollSeconds: 300, pendingRevocations: 0, projects: [], updates: [], totalUpdates: 0,
    decisions: [], overleafProjects: null, researcher: null, processing: null,
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

  it('summarizes a recent project only when asked and shows the paragraph under its name', async () => {
    api.summarize.mockResolvedValue(undefined);
    const fixture = screen();
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelector('.older-projects .summarize')).toBeNull();
    const button = element.querySelector<HTMLButtonElement>('.decision-row .summarize')!;
    expect(button.textContent).toContain('Summarize');
    expect(button.title).toContain('only this project\'s main .tex file');
    button.click();
    await vi.waitFor(() => expect(api.summarize).toHaveBeenCalledWith(recentId));

    fixture.componentRef.setInput('status', { ...base, summaries: [{ projectId: recentId, status: 'running', summary: null, mainPath: null,
      updatedAt: null, sourceUpdatedAt: null, error: null }] });
    fixture.detectChanges();
    expect(element.querySelector('.blurb-status')?.textContent).toContain('Summarizing the main .tex file');
    expect(element.querySelector('.decision-row .summarize')).toBeNull();

    fixture.componentRef.setInput('status', { ...base, summaries: [{ projectId: recentId, status: 'ready',
      summary: 'A synthetic study of markets.', mainPath: 'paper/main.tex', updatedAt: projects[0].updatedAt,
      sourceUpdatedAt: projects[0].updatedAt, error: null }] });
    fixture.detectChanges();
    expect(element.querySelector('.project-blurb')?.textContent).toContain('A synthetic study of markets.');
    expect(element.querySelector('.blurb-source')?.textContent).toContain('From main.tex');
    expect(element.querySelector('.decision-row .summarize')).toBeNull();

    // A newer Overleaf edit than the summary's source offers an update.
    fixture.componentRef.setInput('status', { ...base, summaries: [{ projectId: recentId, status: 'ready',
      summary: 'A synthetic study of markets.', mainPath: 'main.tex', updatedAt: null, sourceUpdatedAt: '2020-01-01T00:00:00Z', error: null }] });
    fixture.detectChanges();
    expect(element.querySelector('.decision-row .summarize')?.textContent).toContain('Update summary');
    fixture.destroy();
  });

  it('treats Include as the consent under the shown notice, without a separate checkbox', async () => {
    const fixture = screen();
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelector('mat-checkbox')).toBeNull();
    expect(element.querySelector('.fine-print')?.textContent).toContain('Azure AI Foundry');

    element.querySelector<HTMLButtonElement>('.decision-row .exclude')!.click();
    await vi.waitFor(() => expect(api.decide).toHaveBeenCalledWith(recentId, false, null));

    const include = element.querySelector<HTMLButtonElement>('.decision-row .include')!;
    expect(include.disabled).toBe(false);
    include.click();
    await vi.waitFor(() => expect(api.decide).toHaveBeenCalledWith(recentId, true, base.consentVersion));
    fixture.destroy();
  });

  it('shows decisions and treats previously monitored projects as included', () => {
    const monitored = { projectId: recentId, name: 'Recent', enabled: true, consentRequired: false, revision: null, summary: null,
      factCount: 2, lastAttemptAt: null, lastSuccessAt: null, errorCode: null, error: null, explanation: null, unprocessedFiles: 0,
      profile: null, phase: 'importing' as const, filesDone: 2, filesTotal: 5, lastSweepAt: null, nextSweepAt: null };
    const fixture = screen({ ...base, projects: [monitored],
      decisions: [{ projectId: olderId, included: false, decidedAt: '2026-10-01T00:00:00Z' }] });
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelector('.decision-row .include')?.classList.contains('selected')).toBe(true);
    expect(element.querySelector('.all-decided')).not.toBeNull();
    expect(element.querySelector('.older-projects .exclude')?.classList.contains('selected')).toBe(true);
    expect(element.querySelector('.project-card')?.textContent).toContain('Importing 2 of 5 files');
    expect(element.querySelector('.decision-row .phase')?.textContent).toContain('Importing 2 of 5 files');
    fixture.destroy();
  });
});

import { TestBed } from '@angular/core/testing';
import { PrivateProfile } from './private-profile';
import { ResearchProject, ResearchStatus } from './research-api';

describe('Private profile', () => {
  const project: ResearchProject = {
    projectId: '0123456789abcdef01234567', name: 'overleaf_pcm', enabled: true, consentRequired: false, revision: null,
    summary: null, factCount: 0, lastAttemptAt: null, lastSuccessAt: null, errorCode: null, error: null, explanation: null,
    unprocessedFiles: 0, phase: 'ready', filesDone: 0, filesTotal: 0, lastSweepAt: null, nextSweepAt: null,
    profile: { title: 'Pricing under uncertainty', description: 'A study of <img src=x> pricing.', contributions: ['A field experiment.'], topics: ['pricing'] },
  };
  const status = (overrides: Partial<ResearchProject> = {}): ResearchStatus => ({
    account: { id: 'a', name: 'R', email: 'r@example.invalid' }, credentialExpiresAt: null, pendingLink: null, accountError: null,
    busy: false, dummy: false, privacyUrl: '', consentVersion: 'v', pollSeconds: 300, pendingRevocations: 0,
    projects: [{ ...project, ...overrides }], totalUpdates: 1, decisions: [], overleafProjects: null, researcher: null, processing: null,
    updates: [{ id: 'u', projectId: project.projectId, projectName: project.name, kind: 'change', title: 'Pilot doubled',
      summary: 'The pilot grew to 80.', observedAt: '2026-10-30T00:00:00Z', sourceUpdatedAt: '2026-10-29T12:00:00Z', revision: 'r',
      previousRevision: null, previousSummary: null, evidence: [] }],
  });

  it('shows each project summary with its dated updates as text', () => {
    const fixture = TestBed.createComponent(PrivateProfile);
    fixture.componentRef.setInput('status', status());
    fixture.detectChanges();
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelector('h3')?.textContent).toBe('Pricing under uncertainty');
    expect(element.querySelector('.contributions')?.textContent).toContain('A field experiment.');
    expect(element.querySelector('.updates')?.textContent).toContain('Pilot doubled');
    expect(element.querySelector('.updates')?.textContent).toContain('Oct 29, 2026');
    expect(element.querySelector('img')).toBeNull();
  });

  it('shows import progress while the summary is being built', () => {
    const fixture = TestBed.createComponent(PrivateProfile);
    fixture.componentRef.setInput('status', status({ profile: null, phase: 'importing', filesDone: 12, filesTotal: 95 }));
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.progress-note')?.textContent).toContain('12 of 95 files');
  });
});

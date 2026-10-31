import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { PrivateProfile } from './private-profile';
import { ResearchProject, ResearchStatus, ResearchUpdate } from './research-api';

describe('Private profile', () => {
  const project: ResearchProject = {
    projectId: '0123456789abcdef01234567', name: 'overleaf_pcm', enabled: true, consentRequired: false, revision: null,
    summary: null, factCount: 0, lastAttemptAt: null, lastSuccessAt: null, errorCode: null, error: null, explanation: null,
    unprocessedFiles: 0, phase: 'ready', filesDone: 0, filesTotal: 0, lastSweepAt: null, nextSweepAt: null,
    profile: { title: 'Pricing under uncertainty', description: 'A study of <img src=x> pricing.', contributions: ['A field experiment.'],
      topics: ['pricing', 'auctions', 'field experiments', 'demand', 'learning'] },
  };
  const day = 86400000;
  const update = (id: string, daysAgo: number, title: string): ResearchUpdate => ({
    id, projectId: project.projectId, projectName: project.name, kind: 'change', title, summary: 'The pilot grew to 80.',
    observedAt: new Date(Date.now() - daysAgo * day).toISOString(), sourceUpdatedAt: new Date(Date.now() - daysAgo * day).toISOString(),
    revision: 'r', previousRevision: null, previousSummary: null, evidence: [],
  });
  const status = (overrides: Partial<ResearchProject> = {}, updates = [update('recent', 5, 'Pilot doubled'), update('old', 200, 'Old change')]): ResearchStatus => ({
    account: { id: 'a', name: 'R', email: 'r@example.invalid' }, credentialExpiresAt: null, pendingLink: null, accountError: null,
    busy: false, dummy: false, privacyUrl: '', consentVersion: 'v', pollSeconds: 300, pendingRevocations: 0,
    projects: [{ ...project, ...overrides }], totalUpdates: updates.length, decisions: [], overleafProjects: null, researcher: null,
    processing: null, updates,
  });
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideNoopAnimations()] }));
  afterEach(() => document.querySelectorAll('.cdk-overlay-container').forEach(node => node.remove()));

  it('shows a compact card and opens the full summary with recent updates in tabs', async () => {
    const fixture = TestBed.createComponent(PrivateProfile);
    fixture.componentRef.setInput('status', status());
    fixture.detectChanges();
    const element: HTMLElement = fixture.nativeElement;
    const card = element.querySelector<HTMLButtonElement>('.profile-card')!;
    expect(card.querySelector('.card-title')?.textContent).toBe('Pricing under uncertainty');
    expect(card.querySelector('.card-description')?.textContent).toContain('A study of <img src=x> pricing.');
    expect(card.querySelectorAll('.topic')).toHaveLength(4);
    expect(card.querySelector('.contributions')).toBeNull();
    expect(card.querySelector('.recent-badge')?.textContent).toContain('1 update in the last 3 months');
    expect(element.querySelector('img')).toBeNull();

    card.click();
    fixture.detectChanges();
    await fixture.whenStable();
    const dialog = document.querySelector('mat-dialog-container')!;
    const tabs = [...dialog.querySelectorAll('[role="tab"]')].map(tab => tab.textContent?.trim());
    expect(tabs).toEqual(['Details', 'Recent updates (1)']);
    expect(dialog.querySelector('.contributions')?.textContent).toContain('A field experiment.');
    (dialog.querySelectorAll<HTMLElement>('[role="tab"]')[1]).click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(dialog.querySelector('.updates')?.textContent).toContain('Pilot doubled');
    expect(dialog.querySelector('.updates')?.textContent).not.toContain('Old change');
    fixture.destroy();
  });

  it('omits the Recent updates tab and badge when nothing changed in the last three months', async () => {
    const fixture = TestBed.createComponent(PrivateProfile);
    fixture.componentRef.setInput('status', status({}, [update('old', 200, 'Old change')]));
    fixture.detectChanges();
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelector('.recent-badge')).toBeNull();
    element.querySelector<HTMLButtonElement>('.profile-card')!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect([...document.querySelectorAll('mat-dialog-container [role="tab"]')].map(tab => tab.textContent?.trim())).toEqual(['Details']);
    fixture.destroy();
  });

  it('shows import progress while the summary is being built', () => {
    const fixture = TestBed.createComponent(PrivateProfile);
    fixture.componentRef.setInput('status', status({ profile: null, phase: 'importing', filesDone: 12, filesTotal: 95 }));
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.progress-note')?.textContent).toContain('12 of 95 files');
  });
});

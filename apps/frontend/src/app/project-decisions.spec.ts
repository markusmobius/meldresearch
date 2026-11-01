import { includedCount } from './project-decisions';
import { ResearchProject, ResearchStatus } from './research-api';

describe('Project decisions', () => {
  const project = (projectId: string, source: 'gmail' | null = null): ResearchProject => ({
    projectId, name: projectId, enabled: true, consentRequired: false, revision: null, summary: null, factCount: 0, lastAttemptAt: null,
    lastSuccessAt: null, errorCode: null, error: null, explanation: null, unprocessedFiles: 0, phase: 'ready', filesDone: 0, filesTotal: 0,
    lastSweepAt: null, nextSweepAt: null, profile: null, source, parent: source ? 'markus@example.invalid' : null,
  });
  const status = (projects: ResearchProject[], decisions: ResearchStatus['decisions'] = []) =>
    ({ projects, decisions } as unknown as ResearchStatus);
  const overleaf = '0123456789abcdef01234567', gmail = 'gmail-' + 'a'.repeat(32);

  it('counts only Overleaf projects as included (Overleaf stays required; Gmail adds to the profile)', () => {
    expect(includedCount(status([project(gmail, 'gmail')]))).toBe(0);
    expect(includedCount(status([project(gmail, 'gmail')], [{ projectId: gmail, included: true, decidedAt: '' }]))).toBe(0);
    expect(includedCount(status([project(overleaf), project(gmail, 'gmail')]))).toBe(1);
    expect(includedCount(status([project(overleaf)], [{ projectId: overleaf, included: false, decidedAt: '' }]))).toBe(0);
  });
});

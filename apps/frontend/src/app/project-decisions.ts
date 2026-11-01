import { OverleafProject } from './overleaf-api';
import { ResearchStatus, isOverleafProject } from './research-api';

/** Overleaf project ids (Gmail workstreams use "gmail-" ids). */
const OVERLEAF_ID = /^[0-9a-f]{24}$/;

/** Projects updated within about six months need an active include/exclude choice; older ones are optional. */
export const RECENT_DAYS = 183;
const recentMs = RECENT_DAYS * 24 * 60 * 60 * 1000;

export type ProjectChoice = 'included' | 'excluded' | null;

export function projectChoice(status: ResearchStatus | null, projectId: string): ProjectChoice {
  const decision = status?.decisions?.find(item => item.projectId === projectId);
  if (decision) return decision.included ? 'included' : 'excluded';
  // Projects monitored before explicit decisions existed were chosen actively too.
  return status?.projects.some(project => project.projectId === projectId) ? 'included' : null;
}

export function isRecent(project: OverleafProject, now = Date.now()): boolean {
  const updated = project.updatedAt ? Date.parse(project.updatedAt) : NaN;
  return !Number.isNaN(updated) && now - updated < recentMs;
}

export function undecidedRecent(status: ResearchStatus | null, projects: ReadonlyArray<OverleafProject>, now = Date.now()): OverleafProject[] {
  return projects.filter(project => isRecent(project, now) && projectChoice(status, project.id) === null);
}

export function includedCount(status: ResearchStatus | null): number {
  if (!status) return 0;
  // Only Overleaf projects count: other sources (Gmail) add to the profile but do not complete setup.
  const ids = new Set(status.projects.filter(isOverleafProject).map(project => project.projectId));
  for (const decision of status.decisions ?? []) {
    if (!OVERLEAF_ID.test(decision.projectId)) continue;
    if (decision.included) ids.add(decision.projectId); else ids.delete(decision.projectId);
  }
  return ids.size;
}

/** The user has confirmed an OpenAlex author or said none of them is them. */
export function researcherDone(status: ResearchStatus | null): boolean {
  const phase = status?.researcher?.status;
  return phase === 'ready' || phase === 'importing' || phase === 'rejected';
}

import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { OverleafProject } from './overleaf-api';

export interface AccountLink { signInUrl: string; expiresAt: string; }
export interface ProjectProfile { title: string; description: string; contributions: ReadonlyArray<string>; topics: ReadonlyArray<string>; }
export type ProjectPhase = 'waiting' | 'checking' | 'queued' | 'importing' | 'updating' | 'ready' | 'error';
export interface ResearchProject {
  projectId: string; name: string; enabled: boolean; consentRequired: boolean;
  revision: string | null; summary: string | null; factCount: number;
  lastAttemptAt: string | null; lastSuccessAt: string | null; errorCode: string | null;
  error: string | null; explanation: string | null; unprocessedFiles: number;
  profile: ProjectProfile | null; phase: ProjectPhase; filesDone: number; filesTotal: number;
  lastSweepAt: string | null; nextSweepAt: string | null;
}
export interface ProcessingStatus {
  projectId: string; name: string; kind: 'import' | 'update'; filesDone: number; filesTotal: number; queued: number;
  /** The file being folded into the project summary now (or next); null while a project is being read. */
  currentFile?: string | null;
}
export interface ResearchUpdate {
  id: string; projectId: string; projectName: string; kind: 'initialImport' | 'change' | 'correction' | 'removal';
  title: string; summary: string; observedAt: string; sourceUpdatedAt: string | null;
  revision: string; previousRevision: string | null; previousSummary: string | null;
  evidence: ReadonlyArray<{ path: string; quote: string; version: 'current' | 'previous' }>;
}
export interface ProjectDecision { projectId: string; included: boolean; decidedAt: string; }
export interface PublicationWork {
  id: string; title: string; authors: ReadonlyArray<string>; year: number; publicationDate: string | null;
  venue: string | null; doi: string | null; abstract: string | null; isRetracted: boolean;
}
export interface ResearcherCandidate {
  id: string; name: string; orcid: string | null; affiliations: ReadonlyArray<string>;
  alternativeNames: ReadonlyArray<string>; worksCount: number; samplePublications: ReadonlyArray<PublicationWork>;
}
export type ResearcherPhase = 'searching' | 'choice' | 'unmatched' | 'rejected' | 'importing' | 'ready';
export interface ResearcherStatus {
  version: number; status: ResearcherPhase; searchName: string; candidates: ReadonlyArray<ResearcherCandidate>;
  researcher: ResearcherCandidate | null; workCount: number; updatedAt: string | null; nextRefreshAt: string | null;
  lastError: string | null;
}
export interface Publications { researcher: ResearcherCandidate | null; works: ReadonlyArray<PublicationWork>; updatedAt: string | null; }
export interface ResearchStatus {
  account: { id: string; name: string; email: string } | null;
  credentialExpiresAt: string | null; pendingLink: AccountLink | null; accountError: string | null;
  busy: boolean; dummy: boolean; privacyUrl: string; consentVersion: string; pollSeconds: number;
  pendingRevocations: number; projects: ReadonlyArray<ResearchProject>;
  updates: ReadonlyArray<ResearchUpdate>; totalUpdates: number;
  decisions: ReadonlyArray<ProjectDecision>;
  overleafProjects: { projects: ReadonlyArray<OverleafProject>; fetchedAt: string } | null;
  researcher: ResearcherStatus | null;
  processing: ProcessingStatus | null;
  /** One-paragraph summaries from projects' main .tex files, made only when the user chose Summarize. */
  summaries?: ReadonlyArray<ProjectSummary> | null;
  /** Research cards: one line of research made of one or more included Overleaf projects, with a combined summary. */
  themes?: ReadonlyArray<ResearchTheme> | null;
}
export interface ResearchTheme { id: string; profile: ProjectProfile | null; projectIds: ReadonlyArray<string>; updating: boolean; }
export interface ProjectSummary {
  projectId: string; status: 'queued' | 'running' | 'ready' | 'error'; summary: string | null; mainPath: string | null;
  updatedAt: string | null; sourceUpdatedAt: string | null; error: string | null;
}

@Injectable({ providedIn: 'root' })
export class ResearchApi {
  private readonly http = inject(HttpClient);
  status(): Promise<ResearchStatus> {
    return firstValueFrom(this.http.get<ResearchStatus>('/api/research/status', { timeout: 30000 }));
  }
  link(): Promise<AccountLink> { return firstValueFrom(this.http.post<AccountLink>('/api/research/account/link', {})); }
  unlink(): Promise<void> { return firstValueFrom(this.http.delete<void>('/api/research/account')); }
  monitor(id: string, enabled: boolean, consentVersion: string | null): Promise<void> {
    return firstValueFrom(this.http.put<void>(`/api/research/projects/${encodeURIComponent(id)}`, { enabled, consentVersion }));
  }
  /** Includes a project in the private profile (starts monitoring; needs consent) or excludes it. */
  decide(id: string, included: boolean, consentVersion: string | null): Promise<void> {
    return firstValueFrom(this.http.put<void>(`/api/research/projects/${encodeURIComponent(id)}/decision`, { included, consentVersion }));
  }
  researcher(action: 'search' | 'confirm' | 'none' | 'retry', options: { authorId?: string; searchName?: string; baseVersion?: number } = {}): Promise<void> {
    return firstValueFrom(this.http.post<void>('/api/research/researcher', {
      action, authorId: options.authorId ?? null, searchName: options.searchName ?? null, baseVersion: options.baseVersion ?? null,
    }));
  }
  publications(): Promise<Publications> { return firstValueFrom(this.http.get<Publications>('/api/research/publications')); }
  forget(id: string): Promise<void> { return firstValueFrom(this.http.delete<void>(`/api/research/projects/${encodeURIComponent(id)}`)); }
  /** Sends only this project's main .tex file to Foundry for a one-paragraph summary; the user's click is the consent. */
  summarize(id: string): Promise<void> {
    return firstValueFrom(this.http.post<void>(`/api/research/projects/${encodeURIComponent(id)}/summary`, {}));
  }
  /** Moves an imported project to another research card, or to a card of its own (themeId null). */
  moveToTheme(id: string, themeId: string | null): Promise<void> {
    return firstValueFrom(this.http.put<void>(`/api/research/projects/${encodeURIComponent(id)}/theme`, { themeId }));
  }
  sync(): Promise<void> { return firstValueFrom(this.http.post<void>('/api/research/sync', {})); }
  demoChange(): Promise<unknown> { return firstValueFrom(this.http.post('/api/development/overleaf/change', {})); }
}

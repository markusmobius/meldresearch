import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export interface AccountLink { verificationUri: string; userCode: string; expiresAt: string; }
export interface ResearchProject {
  projectId: string; name: string; enabled: boolean; consentRequired: boolean;
  revision: string | null; summary: string | null; factCount: number;
  lastAttemptAt: string | null; lastSuccessAt: string | null; errorCode: string | null;
  error: string | null; explanation: string | null; unprocessedFiles: number;
}
export interface ResearchUpdate {
  id: string; projectId: string; projectName: string; kind: 'initialImport' | 'change' | 'correction' | 'removal';
  title: string; summary: string; observedAt: string; sourceUpdatedAt: string | null;
  revision: string; previousRevision: string | null; previousSummary: string | null;
  evidence: ReadonlyArray<{ path: string; quote: string; version: 'current' | 'previous' }>;
}
export interface ResearchStatus {
  account: { id: string; name: string; email: string } | null;
  credentialExpiresAt: string | null; pendingLink: AccountLink | null; accountError: string | null;
  busy: boolean; dummy: boolean; privacyUrl: string; consentVersion: string; pollSeconds: number;
  pendingRevocations: number; projects: ReadonlyArray<ResearchProject>;
  updates: ReadonlyArray<ResearchUpdate>; totalUpdates: number;
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
  forget(id: string): Promise<void> { return firstValueFrom(this.http.delete<void>(`/api/research/projects/${encodeURIComponent(id)}`)); }
  sync(): Promise<void> { return firstValueFrom(this.http.post<void>('/api/research/sync', {})); }
  demoChange(): Promise<unknown> { return firstValueFrom(this.http.post('/api/development/overleaf/change', {})); }
}

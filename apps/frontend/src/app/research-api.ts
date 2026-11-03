import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { OverleafProject } from './overleaf-api';

export interface AccountLink { signInUrl: string; expiresAt: string; }
export interface ProjectProfile { title: string; description: string; contributions: ReadonlyArray<string>; topics: ReadonlyArray<string>; }
/** retrying: a short problem reaching Meld's server or the browser, retried automatically and not yet shown as an error. */
export type ProjectPhase = 'waiting' | 'checking' | 'queued' | 'importing' | 'updating' | 'ready' | 'retrying' | 'error';
export interface ResearchProject {
  projectId: string; name: string; enabled: boolean; consentRequired: boolean;
  revision: string | null; summary: string | null; factCount: number;
  lastAttemptAt: string | null; lastSuccessAt: string | null; errorCode: string | null;
  error: string | null; explanation: string | null; unprocessedFiles: number;
  profile: ProjectProfile | null; phase: ProjectPhase; filesDone: number; filesTotal: number;
  lastSweepAt: string | null; nextSweepAt: string | null;
  /** null for an Overleaf project; 'gmail' for a Gmail workstream, whose parent is its Gmail address. */
  source?: 'gmail' | null; parent?: string | null;
}
/** An Overleaf project (rather than a workstream from another source such as Gmail). */
export function isOverleafProject(project: Pick<ResearchProject, 'source'>): boolean { return !project.source; }
/** An exact quotation: from a project file (path), or from one email (email set; path is its subject). */
export interface ResearchCitation {
  path: string; quote: string; version: 'current' | 'previous';
  email?: { messageId: string; from: string; sentAt: string } | null;
}
export interface ProcessingStatus {
  projectId: string; name: string; kind: 'import' | 'update'; filesDone: number; filesTotal: number; queued: number;
  /** The file being folded into the project summary now (or next); null while a project is being read. */
  currentFile?: string | null;
  /** Other projects being processed at the same time (projects run in parallel; each keeps its file order). */
  alsoRunning?: ReadonlyArray<string> | null;
}
export interface ResearchUpdate {
  id: string; projectId: string; projectName: string; kind: 'initialImport' | 'change' | 'correction' | 'removal';
  title: string; summary: string; observedAt: string; sourceUpdatedAt: string | null;
  revision: string; previousRevision: string | null; previousSummary: string | null;
  evidence: ReadonlyArray<ResearchCitation>;
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
  /** End-to-end encrypted sync of this account's research between the user's computers. */
  sync?: SyncStatus | null;
  /** Connected Gmail accounts (read-only); configured: Meld's server offers Gmail. */
  gmail?: GmailStatus | null;
  /** The feed's Updates: changelog entries that changed a research project's details, newest first (the latest 100). */
  changes?: ReadonlyArray<ResearchChange> | null; totalChanges?: number;
  /** Files the user removed as a source of a project, waiting in Private updates. */
  pendingFiles?: ReadonlyArray<PendingFile> | null;
  /** Projects edited within this many days need an include/exclude choice and are read; the same window for both. */
  recentDays?: number;
}
/**
 * A source of a detail: an Overleaf document at a version (kind overleaf, origin: its project) or an email thread up to a
 * message (kind gmail, origin: the Gmail address). key identifies the source across versions; quote is the cited text.
 */
export interface DetailSource {
  key: string; kind: 'overleaf' | 'gmail'; label: string; version: string; at: string | null; quote: string; origin: string | null;
}
/** One sentence of a research project's current state, with the sources it rests on. */
export interface ResearchDetail { id: string; text: string; sources: ReadonlyArray<DetailSource>; }
/** What one source did to one detail: added, revised (before and after) or removed it. */
export interface DetailChange { op: 'add' | 'revise' | 'remove'; detailId: string; before: string | null; after: string | null; }
/**
 * A changelog entry of a research project: a source that updated it (kind overleaf: documents of one tree; gmail: new email
 * of a thread), a removed source, the user's change, a merge, or a periodic review (overlaps merged, overtaken plans
 * retired; shown in the changelog only). changes is empty when the source changed no detail.
 */
export interface ResearchChange {
  id: string; cardId: string; cardTitle: string | null; at: string; kind: 'overleaf' | 'gmail' | 'removed' | 'user' | 'merge' | 'review';
  title: string; projectName: string | null; documents: ReadonlyArray<string>; changes: ReadonlyArray<DetailChange>;
}
export interface ChangelogPage { entries: ReadonlyArray<ResearchChange>; more: boolean; }
/** An Overleaf file removed as a source of research project fromCardId; the user adds it to another project or skips it. */
export interface PendingFile {
  projectId: string; projectName: string; path: string; fromCardId: string; fromCardTitle: string | null; removedAt: string;
}
/** connected, reconnect (Google withdrew access, for example weekly while Meld is in Google's testing phase) or error. */
export interface GmailAccount {
  address: string; status: 'connected' | 'reconnect' | 'error'; error: string | null; threads: number | null;
  checkedAt: string | null; connectedAt: string;
  /**
   * Threads analyzed so far, and workstreams: research projects this account's email contributes to. importing: the import
   * running now (waiting: on another computer).
   */
  analyzed?: number; workstreams?: number; lastImportAt?: string | null; nextImportAt?: string | null;
  importing?: { done: number; total: number; waiting: boolean } | null; importError?: string | null;
}
export interface GmailStatus {
  configured: boolean; windowDays: number; consentVersion: string; accounts: ReadonlyArray<GmailAccount>; lastError: string | null;
  /** Recorded threads for the feed: assigned ones with newsworthy progress, and pending ones waiting for the user. */
  threads?: ReadonlyArray<GmailThread> | null;
}
export interface GmailBullet { id: string; text: string; sentAt: string; newsworthy: boolean; evidence: ReadonlyArray<ResearchCitation>; }
/**
 * One email thread as a growing list of bullets. assigned: on research card cardId (newsAt: its latest newsworthy progress);
 * pending: Meld found no fitting project, the user decides (suggested: a name for a new one).
 */
export interface GmailThread {
  address: string; id: string; subject: string; title: string; status: 'assigned' | 'pending'; cardId: string | null;
  cardTitle: string | null; suggested: string | null; bullets: ReadonlyArray<GmailBullet>; lastSentAt: string; newsAt: string | null;
  /** Skipped by the user (or on a skipped project): shown in About me → Skipped, not in the feed. */
  skipped?: boolean;
}
/**
 * dismiss: skip the thread (it joins skipped work); unskip: back to Private updates; project: put it on research card
 * cardId; new: a new research project named name.
 */
export type GmailThreadAction = 'dismiss' | 'unskip' | 'project' | 'new';
/** A data download being prepared, ready (url works until expiresAt, five minutes) or failed. */
export interface ExportStatus {
  id: string; state: 'preparing' | 'ready' | 'failed'; startedAt: string; readyAt: string | null; expiresAt: string | null;
  fileName: string | null; bytes: number | null; projects: number; url: string | null; error: string | null;
}
/** What an uploaded export contained; otherAccount when it was downloaded while signed in as another account. */
export interface ImportResult {
  projects: number; included: number; cards: number; summaries: number; publications: boolean;
  exportedEmail: string | null; otherAccount: boolean; exportedAt: string;
}
/** uninstalling: the installed app is removing itself and stops in a moment; accountDeleted: on Meld's server too. */
export interface LeaveResult { uninstalling: boolean; accountDeleted: boolean; logPath: string | null; }
/**
 * off: the server offers no sync; setup: choose a passphrase; locked: this account has synced research, enter the
 * passphrase (or recovery key) here; checking: not known yet; ready: syncing.
 */
export type SyncState = 'off' | 'signed-out' | 'checking' | 'setup' | 'locked' | 'ready';
export interface SyncStatus {
  state: SyncState; syncedProjects: number; lastPushAt: string | null; lastPullAt: string | null; error: string | null;
}
/**
 * blurb: the card's short description (null until made). anchorId: the project the card is described from, its main paper
 * (anchorChosen: chosen by the user). reason: why the last project was grouped onto the card.
 */
export interface ResearchTheme {
  id: string; profile: ProjectProfile | null; projectIds: ReadonlyArray<string>; updating: boolean; blurb?: string | null;
  anchorId?: string | null; anchorChosen?: boolean; reason?: string | null;
  /** Skipped as not the user's own research (About me → Skipped). */
  skipped?: boolean;
  /** The current state of the research: at most 30 one-sentence details with their sources (profile.description summarizes them). */
  details?: ReadonlyArray<ResearchDetail> | null;
}
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
  /** Combines a research card into another card; its projects stay there. */
  /** Skips a research card as not the user's own research, or brings it back. */
  skipTheme(themeId: string, skipped: boolean): Promise<void> {
    return firstValueFrom(this.http.put<void>(`/api/research/themes/${encodeURIComponent(themeId)}/skip`, { skipped }));
  }
  /** Chooses the project a research card is described from (its main paper). */
  setThemeAnchor(themeId: string, projectId: string): Promise<void> {
    return firstValueFrom(this.http.put<void>(`/api/research/themes/${encodeURIComponent(themeId)}/anchor`, { projectId }));
  }
  /** Removes a source from every detail of a research project; it goes to Private updates. */
  removeSource(themeId: string, key: string): Promise<void> {
    return firstValueFrom(this.http.post<void>(`/api/research/themes/${encodeURIComponent(themeId)}/sources/remove`, { key }));
  }
  /** A page of a research project's changelog, newest first: the entries after the last one shown (last). */
  changelog(themeId: string, last?: Pick<ResearchChange, 'id' | 'at'> | null, limit = 30): Promise<ChangelogPage> {
    const query = new URLSearchParams({ limit: String(limit) });
    if (last) { query.set('after', last.id); query.set('before', last.at); }
    return firstValueFrom(this.http.get<ChangelogPage>(`/api/research/themes/${encodeURIComponent(themeId)}/changelog?${query}`));
  }
  /** A removed file from Private updates: add it to research project cardId, or leave it out (dismiss). */
  decidePendingFile(file: Pick<PendingFile, 'projectId' | 'path'>, action: 'project' | 'dismiss', cardId?: string): Promise<void> {
    return firstValueFrom(this.http.post<void>('/api/research/files/decision',
      { projectId: file.projectId, path: file.path, action, cardId: cardId ?? null }));
  }
  mergeTheme(themeId: string, intoThemeId: string): Promise<void> {
    return firstValueFrom(this.http.put<void>(`/api/research/themes/${encodeURIComponent(themeId)}/merge`, { intoThemeId }));
  }
  sync(): Promise<void> { return firstValueFrom(this.http.post<void>('/api/research/sync', {})); }
  /** Asks Meld's server whether encrypted sync is offered and whether this account already has synced research. */
  encryptedSync(): Promise<SyncStatus> { return firstValueFrom(this.http.get<SyncStatus>('/api/research/encrypted-sync')); }
  /** Chooses the passphrase (it stays on this computer); returns the recovery key, shown once. */
  createSync(passphrase: string): Promise<{ recoveryKey: string }> {
    return firstValueFrom(this.http.post<{ recoveryKey: string }>('/api/research/encrypted-sync/create', { passphrase }));
  }
  unlockSync(secret: { passphrase?: string; recoveryKey?: string }): Promise<void> {
    return firstValueFrom(this.http.post<void>('/api/research/encrypted-sync/unlock',
      { passphrase: secret.passphrase ?? null, recoveryKey: secret.recoveryKey ?? null }));
  }
  /** Deletes the synced copy and starts again with a new passphrase; this computer's research is uploaded again. */
  startSyncOver(passphrase: string): Promise<{ recoveryKey: string }> {
    return firstValueFrom(this.http.post<{ recoveryKey: string }>('/api/research/encrypted-sync/start-over', { passphrase }));
  }
  /** The download being prepared, or the last one if it is less than five minutes old. */
  exportStatus(): Promise<ExportStatus | null> {
    return firstValueFrom(this.http.get<{ current: ExportStatus | null }>('/api/research/export')).then(response => response.current);
  }
  /** Queues a download; while one is being prepared, this returns it instead of starting another. */
  startExport(): Promise<ExportStatus> {
    return firstValueFrom(this.http.post<{ current: ExportStatus }>('/api/research/export', {})).then(response => response.current);
  }
  /** Google's consent page for a Gmail account; it returns to this app (the caller navigates there). */
  connectGmail(consentVersion: string): Promise<{ authorizationUrl: string; expiresAt: string }> {
    return firstValueFrom(this.http.post<{ authorizationUrl: string; expiresAt: string }>('/api/research/gmail/connect', { consentVersion }));
  }
  /** Where a recorded Gmail thread belongs: ignore it, a research card, or a new research project. */
  decideGmailThread(thread: Pick<GmailThread, 'address' | 'id'>, action: GmailThreadAction, cardId?: string, name?: string): Promise<void> {
    return firstValueFrom(this.http.post<void>('/api/research/gmail/threads/decision',
      { address: thread.address, threadId: thread.id, action, cardId: cardId ?? null, name: name ?? null }));
  }
  /** Imports new mail from a connected Gmail account now (also if it was imported recently). */
  importGmail(address: string): Promise<void> {
    return firstValueFrom(this.http.post<void>(`/api/research/gmail/accounts/${encodeURIComponent(address)}/import`, {}));
  }
  /** Disconnects a Gmail account: revokes Meld's access at Google and removes it on every computer. */
  removeGmail(address: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`/api/research/gmail/accounts/${encodeURIComponent(address)}`));
  }
  /** Merges a downloaded export (a ZIP of readable JSON) into this account's research; encrypted sync uploads it. */
  importData(file: Blob): Promise<ImportResult> {
    // The interface is locked meanwhile, so a request that never answers must still end (with an error).
    return firstValueFrom(this.http.post<ImportResult>('/api/research/import', file,
      { headers: { 'Content-Type': 'application/zip' }, timeout: 600_000 }));
  }
  /** Deletes the Meld account, its synced data and this computer's research, and uninstalls the app. */
  leave(confirmation: string): Promise<LeaveResult> {
    return firstValueFrom(this.http.post<LeaveResult>('/api/research/leave', { confirmation }));
  }
  /** Shows the recovery key again (replaced: a new one was made because the key file had no copy of the old one). */
  recoveryKey(): Promise<{ recoveryKey: string; replaced: boolean }> {
    return firstValueFrom(this.http.post<{ recoveryKey: string; replaced: boolean }>('/api/research/encrypted-sync/recovery-key', {}));
  }
  changeSyncPassphrase(passphrase: string): Promise<void> {
    return firstValueFrom(this.http.post<void>('/api/research/encrypted-sync/passphrase', { passphrase }));
  }
  demoChange(): Promise<unknown> { return firstValueFrom(this.http.post('/api/development/overleaf/change', {})); }
}

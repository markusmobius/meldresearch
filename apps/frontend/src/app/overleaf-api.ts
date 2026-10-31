import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export interface BridgeStatus {
  connected: boolean;
  busy: boolean;
  protocolVersion: number;
  connectionVersion: string | null;
  dummy?: boolean;
}

export interface ExtensionSetup {
  extensionId: string;
  extensionVersion: string;
  protocolVersion: number;
  developmentDirectory: string | null;
  storeUrl?: string;
  storePublished?: boolean;
}

export interface OverleafProject {
  id: string;
  name: string;
  updatedAt: string | null;
}

@Injectable({ providedIn: 'root' })
export class OverleafApi {
  private readonly http = inject(HttpClient);

  status(): Promise<BridgeStatus> {
    return firstValueFrom(this.http.get<BridgeStatus>('/api/status', { timeout: 10000 }));
  }

  extension(): Promise<ExtensionSetup> {
    return firstValueFrom(this.http.get<ExtensionSetup>('/api/extension', { timeout: 10000 }));
  }

  async refresh(): Promise<ReadonlyArray<OverleafProject>> {
    const response = await firstValueFrom(
      this.http.post<{ projects: OverleafProject[] }>('/api/overleaf/projects/refresh', {}),
    );
    return response.projects;
  }
}

export function needsOverleafSignIn(error: unknown): boolean {
  if (!(error instanceof HttpErrorResponse)) return false;
  const body: unknown = error.error;
  return typeof body === 'object' && body !== null && 'code' in body &&
    body.code === 'overleaf_sign_in_required';
}

export function describeFailure(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    const body: unknown = error.error;
    if (typeof body === 'object' && body !== null && 'detail' in body &&
        typeof body.detail === 'string' && body.detail.length <= 1000) {
      return body.detail;
    }
    if (error.status === 0) return 'The local backend is unavailable. Start run-dev.ps1, then reload this page.';
    return `The local request failed (HTTP ${error.status}). Reload the page and try again.`;
  }
  return 'The operation could not be completed. Reload the local application and try again.';
}

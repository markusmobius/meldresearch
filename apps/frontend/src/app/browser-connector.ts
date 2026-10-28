import { Injectable, InjectionToken, inject } from '@angular/core';
import { ExtensionSetup } from './overleaf-api';

export type ConnectorCommand = 'status' | 'ensure' | 'connect' | 'disconnect' | 'signIn';

export interface ConnectorState {
  extensionVersion: string;
  protocolVersion: number;
  connected: boolean;
  enabled: boolean;
  connecting: boolean;
  localUrl: string | null;
  signInPending: boolean;
  errorCode: string | null;
  error: string;
}

export type ConnectorResult =
  | { kind: 'available'; state: ConnectorState }
  | { kind: 'missing' | 'unsupported' | 'outdated' | 'error'; message: string };

export interface ExtensionRuntime {
  lastError?: { message?: string };
  sendMessage(id: string, message: { type: ConnectorCommand }, callback: (reply: unknown) => void): void;
}

declare global {
  interface Window {
    chrome?: { runtime?: ExtensionRuntime };
  }
}

export const EXTENSION_RUNTIME = new InjectionToken<ExtensionRuntime | undefined>('extension runtime', {
  providedIn: 'root',
  factory: () => window.chrome?.runtime,
});

const missing: ConnectorResult = {
  kind: 'missing',
  message: 'Connector not detected in this browser profile. Install or reload it once, then reload this Meld page.',
};

@Injectable({ providedIn: 'root' })
export class BrowserConnector {
  private readonly runtime = inject(EXTENSION_RUNTIME);

  send(setup: ExtensionSetup, type: ConnectorCommand): Promise<ConnectorResult> {
    const runtime = this.runtime;
    if (!runtime?.sendMessage) {
      return Promise.resolve(/(?:Chrome|Chromium|Edg)\//.test(navigator.userAgent) ? missing : {
        kind: 'unsupported',
        message: 'Open Meld in your normal Chrome or Edge profile. Safari and Firefox are not supported yet.',
      });
    }
    return new Promise(resolve => {
      let finished = false;
      const finish = (result: ConnectorResult): void => {
        if (finished) return;
        finished = true;
        clearTimeout(timer);
        resolve(result);
      };
      const timer = setTimeout(() => finish({
        kind: 'error', message: 'The connector did not respond. Reload the extension, then reload Meld.',
      }), 5000);
      try {
        runtime.sendMessage(setup.extensionId, { type }, reply => {
          const failed = runtime.lastError;
          if (failed) finish(missing);
          else if (isRecord(reply) &&
              typeof reply['protocolVersion'] === 'number' && reply['protocolVersion'] !== setup.protocolVersion) {
            finish({ kind: 'outdated', message: 'Update or reload the connector and Meld so their connection protocols match.' });
          } else if (isConnectorState(reply)) {
            finish({ kind: 'available', state: reply });
          } else {
            finish({ kind: 'error', message: 'The connector returned an unsupported response. Reload it and Meld together.' });
          }
        });
      } catch {
        finish({ kind: 'error', message: 'Browser messaging is unavailable. Reload the connector and this Meld page.' });
      }
    });
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isConnectorState(value: unknown): value is ConnectorState {
  if (!isRecord(value) || typeof value['extensionVersion'] !== 'string' ||
      !Number.isSafeInteger(value['protocolVersion']) || typeof value['connected'] !== 'boolean' ||
      typeof value['enabled'] !== 'boolean' || typeof value['connecting'] !== 'boolean' ||
      typeof value['signInPending'] !== 'boolean' ||
      !(value['errorCode'] === null || typeof value['errorCode'] === 'string' && value['errorCode'].length <= 64) ||
      typeof value['error'] !== 'string' || value['error'].length > 1000) return false;
  if (value['localUrl'] === null) return !value['connected'];
  if (typeof value['localUrl'] !== 'string') return false;
  try {
    const url = new URL(value['localUrl']);
    return url.protocol === 'http:' && url.hostname === '127.0.0.1' &&
      ['6001', '6010'].includes(url.port) && url.pathname === '/' &&
      !url.username && !url.password && !url.search && !url.hash;
  } catch { return false; }
}

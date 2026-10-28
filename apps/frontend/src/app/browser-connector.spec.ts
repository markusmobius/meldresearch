import { TestBed } from '@angular/core/testing';
import { BrowserConnector, ConnectorState, EXTENSION_RUNTIME, ExtensionRuntime } from './browser-connector';
import { ExtensionSetup } from './overleaf-api';

describe('Browser connector messaging', () => {
  const setup: ExtensionSetup = {
    extensionId: 'pejcljoimoeklelcfbfddnkimlkjgfhe', extensionVersion: '0.3.0', protocolVersion: 2,
    developmentDirectory: null,
  };
  const state: ConnectorState = {
    extensionVersion: '0.3.0', protocolVersion: 2, connected: true, enabled: true, connecting: false,
    localUrl: 'http://127.0.0.1:6010', signInPending: false, errorCode: null, error: '',
  };
  const runtime: ExtensionRuntime = { sendMessage: vi.fn() };

  beforeEach(() => {
    vi.resetAllMocks();
    delete runtime.lastError;
    TestBed.configureTestingModule({ providers: [{ provide: EXTENSION_RUNTIME, useValue: runtime }] });
  });

  it('sends only a control command to the manifest-derived extension identity', async () => {
    vi.mocked(runtime.sendMessage).mockImplementation((_id, _message, reply) => reply(state));
    const result = await TestBed.inject(BrowserConnector).send(setup, 'ensure');
    expect(runtime.sendMessage).toHaveBeenCalledWith(setup.extensionId, { type: 'ensure' }, expect.any(Function));
    expect(result).toEqual({ kind: 'available', state });
  });

  it('reads runtime.lastError inside the callback and does not expose its arbitrary text', async () => {
    vi.mocked(runtime.sendMessage).mockImplementation((_id, _message, reply) => {
      runtime.lastError = { message: 'PRIVATE-SENTINEL' };
      reply(undefined);
      delete runtime.lastError;
    });
    const result = await TestBed.inject(BrowserConnector).send(setup, 'connect');
    expect(result.kind).toBe('missing');
    expect(JSON.stringify(result)).not.toContain('PRIVATE-SENTINEL');
  });

  it.each([
    { ...state, protocolVersion: 1 },
    { ...state, protocolVersion: 3 },
  ])('detects incompatible connector versions', async reply => {
    vi.mocked(runtime.sendMessage).mockImplementation((_id, _message, callback) => callback(reply));
    expect((await TestBed.inject(BrowserConnector).send(setup, 'status')).kind).toBe('outdated');
  });

  it('accepts a store update when its connection protocol is still compatible', async () => {
    const updated = { ...state, extensionVersion: '0.3.1' };
    vi.mocked(runtime.sendMessage).mockImplementation((_id, _message, callback) => callback(updated));
    expect(await TestBed.inject(BrowserConnector).send(setup, 'status')).toEqual({ kind: 'available', state: updated });
  });

  it.each([
    undefined, {}, { ...state, connected: 'yes' }, { ...state, localUrl: null },
    { ...state, localUrl: 'https://untrusted.invalid' }, { ...state, localUrl: 'http://127.0.0.1:6000' },
    { ...state, error: 'x'.repeat(1001) },
  ])('rejects malformed connector responses', async reply => {
    vi.mocked(runtime.sendMessage).mockImplementation((_id, _message, callback) => callback(reply));
    expect((await TestBed.inject(BrowserConnector).send(setup, 'status')).kind).toBe('error');
  });

  it('times out once and ignores a late response', async () => {
    vi.useFakeTimers();
    let respond: ((reply: unknown) => void) | undefined;
    vi.mocked(runtime.sendMessage).mockImplementation((_id, _message, callback) => { respond = callback; });
    try {
      const result = TestBed.inject(BrowserConnector).send(setup, 'ensure');
      await vi.advanceTimersByTimeAsync(5000);
      expect((await result).kind).toBe('error');
      respond?.(state);
      expect((await result).kind).toBe('error');
    } finally { vi.useRealTimers(); }
  });

  it('surfaces synchronous browser messaging failure', async () => {
    vi.mocked(runtime.sendMessage).mockImplementation(() => { throw new Error('Synthetic messaging failure.'); });
    expect((await TestBed.inject(BrowserConnector).send(setup, 'connect')).kind).toBe('error');
  });

  it('explains that non-Chromium browsers need Chrome or Edge', async () => {
    TestBed.overrideProvider(EXTENSION_RUNTIME, { useValue: undefined });
    const agent = vi.spyOn(navigator, 'userAgent', 'get').mockReturnValue('Synthetic Firefox/150');
    try {
      expect((await TestBed.inject(BrowserConnector).send(setup, 'ensure')).kind).toBe('unsupported');
    } finally { agent.mockRestore(); }
  });

  it('offers installation when a normal Chromium profile has no messaging API yet', async () => {
    TestBed.overrideProvider(EXTENSION_RUNTIME, { useValue: undefined });
    const agent = vi.spyOn(navigator, 'userAgent', 'get').mockReturnValue('Synthetic Chrome/150');
    try {
      expect((await TestBed.inject(BrowserConnector).send(setup, 'ensure')).kind).toBe('missing');
    } finally { agent.mockRestore(); }
  });
});

import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { App } from './app';
import { BrowserConnector, ConnectorState } from './browser-connector';
import { BridgeStatus, OverleafApi, describeFailure } from './overleaf-api';
import { ResearchApi } from './research-api';
import { LocalPage } from './meld-toolbar';

describe('Overleaf connection screen', () => {
  const api = {
    status: vi.fn(),
    downloads: vi.fn(),
    refresh: vi.fn(),
    download: vi.fn(),
    extension: vi.fn(),
  };
  const browser = { send: vi.fn() };
  const setup = { extensionId: 'pejcljoimoeklelcfbfddnkimlkjgfhe', extensionVersion: '0.3.0',
    protocolVersion: 2, developmentDirectory: 'C:\\synthetic\\browser\\overleaf' };
  const status = (connected = false, connectionVersion: string | null = connected ? 'first-connection' : null): BridgeStatus =>
    ({ connected, connectionVersion, busy: false, protocolVersion: 2 });
  const connector = (connected = false, enabled = true): { kind: 'available'; state: ConnectorState } => ({
    kind: 'available',
    state: { connected, enabled, connecting: false, extensionVersion: '0.3.0', protocolVersion: 2,
      localUrl: connected ? 'http://127.0.0.1:6010' : null, signInPending: false, errorCode: null, error: '' },
  });
  async function screen(page: LocalPage = 'account') {
    const fixture = TestBed.createComponent(App);
    fixture.componentInstance.page.set(page);
    fixture.detectChanges();
    await vi.waitFor(() => {
      expect(fixture.componentInstance.connector()).not.toBeNull();
      expect(fixture.componentInstance.busy()).toBe('');
      expect(fixture.componentInstance.research()).not.toBeNull();
    });
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }
  const account = { id: 'account-one', name: 'Synthetic researcher', email: 'researcher@example.invalid' };
  const researchStatus = (signedIn = true) => ({
    account: signedIn ? account : null, pendingLink: null, accountError: null, busy: false, dummy: false,
    privacyUrl: 'https://www.meldresearch.org/privacy.html', projects: [], updates: [], totalUpdates: 0, pendingRevocations: 0,
  });
  const researchApi = { status: vi.fn(), link: vi.fn(), unlink: vi.fn() };

  beforeEach(() => {
    vi.resetAllMocks();
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] });
    const stored = new Map<string, string>();
    vi.stubGlobal('localStorage', {
      getItem: (key: string) => stored.get(key) ?? null, setItem: (key: string, value: string) => stored.set(key, value),
      removeItem: (key: string) => stored.delete(key), clear: () => stored.clear(),
    });
    // By default the account is signed in and Overleaf was connected before, so the working pages are available.
    localStorage.setItem('meldresearch.overleafConnectedFor', account.id);
    api.status.mockResolvedValue(status());
    api.extension.mockResolvedValue(setup);
    api.downloads.mockResolvedValue([]);
    api.refresh.mockResolvedValue([]);
    browser.send.mockResolvedValue(connector());
    researchApi.status.mockResolvedValue(researchStatus());
    TestBed.configureTestingModule({ imports: [App], providers: [
      provideNoopAnimations(),
      { provide: OverleafApi, useValue: api }, { provide: BrowserConnector, useValue: browser },
      { provide: ResearchApi, useValue: researchApi },
    ] });
  });

  afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); location.hash = ''; });

  it('shows only the Account view with the Google step until the Meld account is signed in', async () => {
    researchApi.status.mockResolvedValue(researchStatus(false));
    const fixture = await screen('home');
    const element: HTMLElement = fixture.nativeElement;
    expect(fixture.componentInstance.view()).toBe('account');
    expect(element.querySelector('a[aria-label="Feed"]')).toBeNull();
    expect(element.querySelector('a[aria-label="About me"]')).toBeNull();
    expect(element.querySelector('.feed-column')).toBeNull();
    const tabs = [...element.querySelectorAll('.setup-tab')];
    expect(tabs.map(tab => tab.textContent)).toEqual([expect.stringContaining('Meld account'), expect.stringContaining('Overleaf')]);
    expect(tabs[0].getAttribute('aria-selected')).toBe('true');
    const signInUrl = 'https://www.meldresearch.org/connect?state=a&challenge=b&redirect_uri=c&device=d';
    researchApi.link.mockResolvedValue({ signInUrl, expiresAt: '2026-10-30T00:00:00Z' });
    const navigate = vi.spyOn(fixture.componentInstance, 'navigate').mockImplementation(() => undefined);
    const button = element.querySelector<HTMLButtonElement>('button.google-sign-in')!;
    expect(button.textContent).toContain('Sign in with Google');
    button.click();
    await vi.waitFor(() => expect(navigate).toHaveBeenCalledWith(signInUrl));
    fixture.destroy();
  });

  it('presses for Overleaf after signing in and opens the feed once Overleaf is connected', async () => {
    localStorage.clear();
    const fixture = await screen('home');
    const element: HTMLElement = fixture.nativeElement;
    expect(fixture.componentInstance.view()).toBe('account');
    expect(element.querySelector('.setup-tab[aria-selected="true"]')?.textContent).toContain('Overleaf');
    expect(element.querySelector('.connection-settings')).not.toBeNull();
    expect(element.querySelector('a[aria-label="Feed"]')).toBeNull();
    fixture.componentInstance.page.set('account');

    api.status.mockResolvedValue(status(true));
    browser.send.mockResolvedValue(connector(true));
    vi.advanceTimersByTime(3000);
    await vi.waitFor(() => expect(fixture.componentInstance.verified()).toBe(true));
    fixture.detectChanges();
    await vi.waitFor(() => expect(location.hash).toBe('#feed'));
    expect(localStorage.getItem('meldresearch.overleafConnectedFor')).toBe(account.id);
    fixture.componentInstance.locationChanged();
    fixture.detectChanges();
    expect(fixture.componentInstance.view()).toBe('home');
    expect(element.querySelector('a[aria-label="Feed"]')).not.toBeNull();
    expect(element.querySelector('a[aria-label="About me"]')).not.toBeNull();
    fixture.destroy();
  });

  it('uses the Meld Material toolbar, working tabs and More menu instead of a landing dashboard', async () => {
    const fixture = await screen('home');
    const element: HTMLElement = fixture.nativeElement;
    const header = element.querySelector('mat-toolbar');
    expect(header?.classList.contains('mat-toolbar')).toBe(true);
    expect(header?.textContent).toContain('Meld Research');
    expect(header?.textContent).toContain('Local Assistant');
    expect(header?.querySelector('a[aria-label="Feed"]')).not.toBeNull();
    expect(header?.querySelector('a[aria-label="About me"]')).not.toBeNull();
    expect(header?.querySelector('button[aria-label="More"]')).not.toBeNull();
    for (const page of ['docs', 'privacy', 'team'])
      expect(element.querySelector(`footer a[href="/${page}.html"]`)).not.toBeNull();
    expect(element.querySelector('footer')?.textContent).toContain('Independent research software');
    expect(element.querySelector('footer')?.textContent).not.toContain('Microsoft');
    expect(element.querySelector('.intro')).toBeNull();
    expect(element.querySelector('.feed-column')).not.toBeNull();
    expect(element.querySelector('.connection-settings')).toBeNull();
    fixture.destroy();
  });

  it('requires a browser connection before refreshing', async () => {
    const fixture = await screen();
    const element: HTMLElement = fixture.nativeElement;
    expect(element.textContent).toContain('Browser connector disconnected');
    fixture.componentInstance.page.set('about-me');
    fixture.detectChanges();
    expect(element.querySelector<HTMLButtonElement>('.refresh-button')?.disabled).toBe(true);
    expect(api.refresh).not.toHaveBeenCalled();
    fixture.destroy();
  });

  it('renders project names as text and keeps the prior list after a refresh failure', async () => {
    api.status.mockResolvedValue(status(true));
    browser.send.mockResolvedValue(connector(true));
    api.refresh.mockResolvedValue([{ id: '0123456789abcdef01234567', name: '<img src=x> Synthetic', updatedAt: null }]);
    const fixture = await screen('about-me');
    const element: HTMLElement = fixture.nativeElement;
    expect(element.textContent).toContain('<img src=x> Synthetic');
    expect(element.querySelector('.project-list img')).toBeNull();

    api.refresh.mockRejectedValue(new HttpErrorResponse({ status: 502, error: { detail: 'Overleaf page format changed.' } }));
    await fixture.componentInstance.refresh();
    fixture.detectChanges();
    expect(element.querySelector('[role="alert"]')?.textContent).toContain('Overleaf page format changed.');
    expect(fixture.componentInstance.projects()).toHaveLength(1);
    expect(fixture.componentInstance.verified()).toBe(true);
    expect(element.querySelector<HTMLButtonElement>('.project-list button')?.disabled).toBe(false);
    fixture.destroy();
  });

  it('shows honest one-time setup and does not read through a different browser connection', async () => {
    api.status.mockResolvedValue(status(true));
    browser.send.mockResolvedValue({ kind: 'missing', message: 'Connector not detected.' });
    const fixture = await screen();
    const element: HTMLElement = fixture.nativeElement;
    expect(element.textContent).toContain('One-time setup needed');
    expect(element.querySelector('.installation mat-expansion-panel-header')?.getAttribute('aria-expanded')).toBe('true');
    expect(element.querySelector<HTMLInputElement>('[aria-label="Connector folder"]')?.value).toBe(setup.developmentDirectory);
    fixture.componentInstance.page.set('about-me');
    fixture.detectChanges();
    expect(element.querySelector<HTMLButtonElement>('.refresh-button')?.disabled).toBe(true);
    expect(api.refresh).not.toHaveBeenCalled();
    fixture.destroy();
  });

  it('keeps one research component and its consent state while navigating between working pages', async () => {
    const fixture = await screen('home');
    const element: HTMLElement = fixture.nativeElement;
    const research = element.querySelector('app-research-panel');
    for (const page of ['about-me', 'account', 'home'] as const) {
      fixture.componentInstance.page.set(page);
      fixture.detectChanges();
      expect(element.querySelector('app-research-panel')).toBe(research);
    }
    expect(api.refresh).not.toHaveBeenCalled();
    fixture.destroy();
  });

  it('loads projects exactly once per connection version, after initializing request protection', async () => {
    api.status.mockResolvedValue(status(true));
    browser.send.mockResolvedValue(connector(true));
    const fixture = await screen();
    expect(api.refresh).toHaveBeenCalledOnce();
    expect(api.status.mock.invocationCallOrder[0]).toBeLessThan(api.refresh.mock.invocationCallOrder[0]);
    await vi.advanceTimersByTimeAsync(12000);
    expect(api.refresh).toHaveBeenCalledOnce();
    api.status.mockResolvedValue(status(true, 'fast-reconnection'));
    await vi.advanceTimersByTimeAsync(3000);
    expect(api.refresh).toHaveBeenCalledTimes(2);
    fixture.destroy();
  });

  it('waits until the native transfer is idle before auto-loading the next connection', async () => {
    api.status.mockResolvedValue({ ...status(true), busy: true });
    browser.send.mockResolvedValue(connector(true));
    const fixture = await screen();
    expect(api.refresh).not.toHaveBeenCalled();
    api.status.mockResolvedValue(status(true));
    await vi.advanceTimersByTimeAsync(3000);
    expect(api.refresh).toHaveBeenCalledOnce();
    fixture.destroy();
  });

  it('does not repeatedly read Overleaf after a sign-in error and retries after sign-in notification', async () => {
    api.status.mockResolvedValue(status(true));
    browser.send.mockResolvedValue(connector(true));
    api.refresh.mockRejectedValueOnce(new HttpErrorResponse({
      status: 409, error: { code: 'overleaf_sign_in_required', detail: 'Sign in to Overleaf.' },
    }));
    const fixture = await screen();
    expect(fixture.componentInstance.signInRequired()).toBe(true);
    expect(fixture.componentInstance.verified()).toBe(false);
    await vi.advanceTimersByTimeAsync(12000);
    expect(api.refresh).toHaveBeenCalledOnce();
    browser.send.mockImplementation(async (_setup, type) => {
      if (type === 'signIn') api.status.mockResolvedValue(status(true, 'after-sign-in'));
      return connector(true);
    });
    await fixture.componentInstance.connect();
    expect(browser.send).toHaveBeenCalledWith(setup, 'signIn');
    expect(api.refresh).toHaveBeenCalledTimes(2);
    expect(fixture.componentInstance.verified()).toBe(true);
    fixture.destroy();
  });

  it('keeps local snapshots available when connector detection fails', async () => {
    api.downloads.mockResolvedValue([{
      id: 'snapshot', projectId: '0123456789abcdef01234567', projectName: 'Synthetic snapshot',
      downloadedAt: '2026-01-01T00:00:00Z', bytes: 1024, files: [],
    }]);
    browser.send.mockResolvedValue({ kind: 'error', message: 'The connector did not respond.' });
    const fixture = await screen();
    expect(fixture.nativeElement.textContent).toContain('The connector did not respond.');
    expect(fixture.componentInstance.downloads()).toHaveLength(1);
    expect(fixture.componentInstance.busy()).toBe('');
    fixture.destroy();
  });

  it('respects a paused connector while continuing only local status checks', async () => {
    browser.send.mockResolvedValue(connector(false, false));
    const fixture = await screen();
    await vi.advanceTimersByTimeAsync(12000);
    expect(api.refresh).not.toHaveBeenCalled();
    expect(browser.send.mock.calls.every(([, command]) => command === 'ensure')).toBe(true);
    expect(fixture.nativeElement.textContent).toContain('Resume auto-connect');
    fixture.destroy();
  });

  it('prevents overlapping polls when a local status request is slow', async () => {
    let complete: ((value: BridgeStatus) => void) | undefined;
    api.status.mockImplementation(() => new Promise<BridgeStatus>(resolve => { complete = resolve; }));
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(12000);
    expect(api.status).toHaveBeenCalledOnce();
    complete?.(status());
    await fixture.whenStable();
    fixture.destroy();
  });

  it('does not let a stale status poll overwrite a deliberate pause', async () => {
    let complete: ((value: ReturnType<typeof connector>) => void) | undefined;
    browser.send.mockImplementation((_setup, command) => command === 'ensure'
      ? new Promise<ReturnType<typeof connector>>(resolve => { complete = resolve; })
      : Promise.resolve(connector(false, false)));
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    await vi.waitFor(() => expect(complete).toBeDefined());
    await fixture.componentInstance.connectorAction('disconnect');
    complete?.(connector(true));
    await vi.waitFor(() => expect(fixture.componentInstance.browserState()?.enabled).toBe(false));
    fixture.destroy();
  });
});

describe('API failure descriptions', () => {
  it('does not display arbitrary HTML error bodies', () => {
    const error = new HttpErrorResponse({ status: 500, error: '<html>PRIVATE-SENTINEL</html>' });
    expect(describeFailure(error)).not.toContain('PRIVATE-SENTINEL');
    expect(describeFailure(error)).toContain('HTTP 500');
  });
});

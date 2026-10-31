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
  const included = {
    projectId: 'c123456789abcdef01234567', name: 'Included project', enabled: true, consentRequired: false, revision: null,
    summary: null, factCount: 0, lastAttemptAt: null, lastSuccessAt: null, errorCode: null, error: null, explanation: null, unprocessedFiles: 0,
    profile: null, phase: 'ready', filesDone: 0, filesTotal: 0, lastSweepAt: null, nextSweepAt: null,
  };
  const recent = (id: string, name: string, daysAgo = 3) =>
    ({ id, name, updatedAt: new Date(Date.now() - daysAgo * 86400000).toISOString() });
  const researcher = (phase: string) => ({ version: 1, status: phase, searchName: account.name, candidates: [], researcher: null,
    workCount: 0, updatedAt: null, nextRefreshAt: null, lastError: null });
  // By default one project is included and OpenAlex was declined, so nothing nudges away from the requested page.
  const researchStatus = (signedIn = true, extra: Record<string, unknown> = {}) => ({
    account: signedIn ? account : null, pendingLink: null, accountError: null, busy: false, dummy: false,
    privacyUrl: 'https://www.meldresearch.org/privacy.html', consentVersion: 'overleaf-foundry-2026-10', pollSeconds: 300,
    projects: signedIn ? [included] : [], updates: [], totalUpdates: 0, pendingRevocations: 0,
    decisions: [], overleafProjects: null, researcher: researcher('rejected'), processing: null, ...extra,
  });
  const researchApi = { status: vi.fn(), link: vi.fn(), unlink: vi.fn(), decide: vi.fn(), researcher: vi.fn(), publications: vi.fn(), monitor: vi.fn() };

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
    expect(tabs.map(tab => tab.textContent)).toEqual([expect.stringContaining('Meld account'), expect.stringContaining('Overleaf'),
      expect.stringContaining('OpenAlex')]);
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

  it('presses for Overleaf after signing in, then requires an included project before the feed opens', async () => {
    localStorage.clear();
    researchApi.status.mockResolvedValue(researchStatus(true, { projects: [] }));
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
    // Connected, but nothing is included yet: the Projects tab is shown and the working pages stay hidden.
    expect(fixture.componentInstance.view()).toBe('account');
    expect(fixture.componentInstance.overleafTab()).toBe('projects');
    expect(element.querySelector('app-project-selection')).not.toBeNull();
    expect(element.querySelector('a[aria-label="Feed"]')).toBeNull();

    researchApi.status.mockResolvedValue(researchStatus());
    await (fixture.componentInstance as unknown as { panel: () => { refresh(): Promise<void> } }).panel().refresh();
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

  it('asks an older connector to reload once and otherwise shows the update steps instead of the feed', async () => {
    const session = new Map<string, string>();
    vi.stubGlobal('sessionStorage', {
      getItem: (key: string) => session.get(key) ?? null, setItem: (key: string, value: string) => session.set(key, value),
    });
    api.extension.mockResolvedValue({ ...setup, extensionVersion: '0.5.2' });
    browser.send.mockResolvedValue(connector(true));
    api.status.mockResolvedValue(status(true));
    const fixture = await screen('home');
    const element: HTMLElement = fixture.nativeElement;
    await vi.waitFor(() => expect(browser.send).toHaveBeenCalledWith(expect.anything(), 'reload'));
    expect(fixture.componentInstance.connectorStale()).toBe(true);
    expect(fixture.componentInstance.view()).toBe('account');
    fixture.detectChanges();
    expect(element.querySelector('.setup-tab[aria-selected="true"]')?.textContent).toContain('Connector update needed');
    expect(element.querySelector('.update-notice')?.textContent).toContain('0.3.0; this Meld needs 0.5.2');
    expect(element.querySelector('.installation mat-panel-title')?.textContent).toContain('Update the browser connector');
    expect(element.querySelector('.setup-steps')?.textContent).toContain('Remove');
    expect(element.querySelector('a[aria-label="Feed"]')).toBeNull();

    vi.advanceTimersByTime(3000);
    await vi.waitFor(() => expect(browser.send.mock.calls.filter(call => call[1] === 'ensure').length).toBeGreaterThan(1));
    expect(browser.send.mock.calls.filter(call => call[1] === 'reload')).toHaveLength(1);

    const current = connector(true);
    current.state.extensionVersion = '0.5.2';
    browser.send.mockResolvedValue(current);
    vi.advanceTimersByTime(3000);
    await vi.waitFor(() => expect(fixture.componentInstance.connectorStale()).toBe(false));
    expect(fixture.componentInstance.view()).toBe('home');
    fixture.destroy();
  });

  it('nudges to an active choice for recent projects on load and again for a new project, without blocking the feed', async () => {
    const old = { id: 'b123456789abcdef01234567', name: 'Old project', updatedAt: '2020-01-01T00:00:00Z' };
    const projects = [recent('a123456789abcdef01234567', 'Recent project'), old];
    researchApi.status.mockResolvedValue(researchStatus(true, { overleafProjects: { projects, fetchedAt: new Date().toISOString() } }));
    api.status.mockResolvedValue(status(true));
    browser.send.mockResolvedValue(connector(true));
    const fixture = await screen('home');
    const element: HTMLElement = fixture.nativeElement;
    await vi.waitFor(() => expect(fixture.componentInstance.view()).toBe('account'));
    fixture.detectChanges();
    expect(location.hash).toBe('#account');
    expect(fixture.componentInstance.setupTab()).toBe('overleaf');
    expect(fixture.componentInstance.overleafTab()).toBe('projects');
    expect(element.querySelector('.projects-tab .sub-tab-badge')?.textContent).toContain('1');
    expect(element.querySelector('.decision-row.undecided')?.textContent).toContain('Recent project');
    expect(element.querySelector('.older-projects')?.textContent).toContain('Old project');
    // One included project already unlocks the working pages.
    expect(element.querySelector('a[aria-label="Feed"]')).not.toBeNull();

    // Leaving is allowed; a newly appearing project brings the user back.
    fixture.componentInstance.page.set('home');
    location.hash = '#feed';
    researchApi.status.mockResolvedValue(researchStatus(true, {
      overleafProjects: { projects: [...projects, recent('d123456789abcdef01234567', 'Brand new project', 0)], fetchedAt: new Date().toISOString() },
      decisions: [{ projectId: 'a123456789abcdef01234567', included: false, decidedAt: new Date().toISOString() }],
    }));
    vi.advanceTimersByTime(3000);
    await vi.waitFor(() => expect(location.hash).toBe('#account'));
    fixture.detectChanges();
    expect([...element.querySelectorAll('.decision-row.undecided')].map(row => row.textContent)).toEqual([expect.stringContaining('Brand new project')]);
    fixture.destroy();
  });

  it('offers OpenAlex once per load after the projects are settled and opens the feed when it is done', async () => {
    researchApi.status.mockResolvedValue(researchStatus(true, {
      researcher: null, overleafProjects: { projects: [recent(included.projectId, included.name)], fetchedAt: new Date().toISOString() },
    }));
    api.status.mockResolvedValue(status(true));
    browser.send.mockResolvedValue(connector(true));
    const fixture = await screen('home');
    const element: HTMLElement = fixture.nativeElement;
    await vi.waitFor(() => expect(fixture.componentInstance.setupTab()).toBe('openalex'));
    fixture.detectChanges();
    expect(location.hash).toBe('#account');
    expect(element.querySelector('app-researcher-setup')).not.toBeNull();
    expect(element.querySelector<HTMLInputElement>('app-researcher-setup input')?.value).toBe(account.name);
    expect(element.querySelector('a[aria-label="Feed"]')).not.toBeNull();

    researchApi.status.mockResolvedValue(researchStatus(true, {
      researcher: researcher('rejected'), overleafProjects: { projects: [recent(included.projectId, included.name)], fetchedAt: new Date().toISOString() },
    }));
    vi.advanceTimersByTime(3000);
    await vi.waitFor(() => expect(location.hash).toBe('#feed'));
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
    expect(fixture.componentInstance.overleafTab()).toBe('projects');
    expect(api.refresh).not.toHaveBeenCalled();
    fixture.destroy();
  });

  it('renders project names as text and keeps the prior list after a refresh failure', async () => {
    api.status.mockResolvedValue(status(true));
    browser.send.mockResolvedValue(connector(true));
    api.refresh.mockResolvedValue([{ id: '0123456789abcdef01234567', name: '<img src=x> Synthetic', updatedAt: null }]);
    const fixture = await screen('account');
    const element: HTMLElement = fixture.nativeElement;
    expect(element.textContent).toContain('<img src=x> Synthetic');
    expect(element.querySelector('app-project-selection img')).toBeNull();

    api.refresh.mockRejectedValue(new HttpErrorResponse({ status: 502, error: { detail: 'Overleaf page format changed.' } }));
    await fixture.componentInstance.refresh();
    fixture.detectChanges();
    expect(element.querySelector('[role="alert"]')?.textContent).toContain('Overleaf page format changed.');
    expect(fixture.componentInstance.projects()).toHaveLength(1);
    expect(fixture.componentInstance.verified()).toBe(true);
    fixture.destroy();
  });

  it('shows compact one-time setup and does not read through a different browser connection', async () => {
    api.status.mockResolvedValue(status(true));
    browser.send.mockResolvedValue({ kind: 'missing', message: 'Connector not detected.' });
    const fixture = await screen();
    const element: HTMLElement = fixture.nativeElement;
    expect(element.textContent).toContain('One-time setup needed');
    expect(element.querySelector('.installation mat-expansion-panel-header')?.getAttribute('aria-expanded')).toBe('true');
    expect(element.textContent).not.toContain(setup.developmentDirectory);
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    element.querySelector<HTMLButtonElement>('button.copy-folder')!.click();
    await vi.waitFor(() => expect(writeText).toHaveBeenCalledWith(setup.developmentDirectory));
    fixture.detectChanges();
    expect(element.querySelector('button.copy-folder')?.textContent).toContain('Copied');
    element.querySelector<HTMLButtonElement>('button.copy-address')!.click();
    await vi.waitFor(() => expect(writeText).toHaveBeenCalledWith('chrome://extensions'));
    expect(element.querySelector('button.open-extensions')).toBeNull();
    expect(element.querySelector('button.reload-button')).not.toBeNull();
    const actions = [...element.querySelectorAll<HTMLElement>('.setup-steps .step-action')];
    expect(actions).toHaveLength(3);
    fixture.componentInstance.chooseOverleafTab('projects');
    fixture.detectChanges();
    expect(api.refresh).not.toHaveBeenCalled();
    fixture.destroy();
  });

  it('nudges Chrome and Edge users to install the app once, and remembers a dismissal', async () => {
    vi.spyOn(navigator, 'userAgent', 'get').mockReturnValue('Mozilla/5.0 (Windows NT 10.0) Chrome/130.0 Safari/537.36');
    const fixture = await screen('home');
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelector('.install-tip')?.textContent).toContain('Install Desktop App');
    expect(element.querySelector('.install-tip-now')).toBeNull();
    const prompt = vi.fn().mockResolvedValue(undefined);
    const event = Object.assign(new Event('beforeinstallprompt', { cancelable: true }),
      { prompt, userChoice: Promise.resolve({ outcome: 'accepted' as const }) });
    window.dispatchEvent(event);
    fixture.detectChanges();
    expect(event.defaultPrevented).toBe(true);
    element.querySelector<HTMLButtonElement>('.install-tip-now')!.click();
    await vi.waitFor(() => expect(fixture.componentInstance.showInstallTip()).toBe(false));
    expect(prompt).toHaveBeenCalled();
    fixture.destroy();

    const again = await screen('home');
    again.componentInstance.dismissInstallTip();
    again.destroy();
    const third = await screen('home');
    expect(third.componentInstance.showInstallTip()).toBe(false);
    third.destroy();
  });

  it('shows About me as Project Meld tabs: Private profile and Publications, without an empty Public profile', async () => {
    const fixture = await screen('about-me');
    await vi.waitFor(() => expect(fixture.componentInstance.view()).toBe('about-me'));
    fixture.detectChanges();
    await fixture.whenStable();
    const element: HTMLElement = fixture.nativeElement;
    const labels = [...element.querySelectorAll('.profile-tabs [role="tab"]')].map(tab => tab.textContent?.trim());
    expect(labels).toEqual(['Private profile', 'Publications']);
    expect(element.querySelector('app-private-profile')).not.toBeNull();
    expect(element.querySelector('app-publications')).toBeNull();
    fixture.componentInstance.aboutTab.set('publications');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(element.querySelector('app-publications')).not.toBeNull();
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

  it('stays usable when connector detection fails', async () => {
    browser.send.mockResolvedValue({ kind: 'error', message: 'The connector did not respond.' });
    const fixture = await screen();
    expect(fixture.nativeElement.textContent).toContain('The connector did not respond.');
    expect(fixture.componentInstance.busy()).toBe('');
    fixture.destroy();
  });

  it('respects a paused connector while continuing only local status checks', async () => {
    browser.send.mockResolvedValue(connector(false, false));
    const fixture = await screen();
    await vi.advanceTimersByTimeAsync(12000);
    expect(api.refresh).not.toHaveBeenCalled();
    expect(browser.send.mock.calls.every(([, command]) => command === 'ensure')).toBe(true);
    fixture.componentInstance.chooseOverleafTab('connection');
    fixture.detectChanges();
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

import { TestBed } from '@angular/core/testing';
import { MeldToolbar, readRemoteAccount, versionLabel } from './meld-toolbar';

describe('Local version and connection indicator', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('labels releases by their first eight characters and commit time', () => {
    expect(versionLabel(null)).toBe('');
    expect(versionLabel({ version: 'development', versionDate: null, updateStatus: 'disabled' })).toBe('development build');
    const label = versionLabel({ version: 'b916c395' + '0'.repeat(56), versionDate: '2026-10-30T01:28:21-04:00', updateStatus: 'current' });
    expect(label.startsWith('vb916c395 \u00b7 ')).toBe(true);
    expect(label).toContain('2026');
  });

  it.each([
    [true, false, 'Connected to the Meld Research server'],
    [false, true, 'Working offline'],
  ])('shows the remote connection (online: %s)', async (online, offline, label) => {
    vi.stubGlobal('fetch', vi.fn(async (url: string) => new Response(JSON.stringify(url === '/api/version'
      ? { version: 'a'.repeat(64), versionDate: '2026-10-30T01:28:21Z', updateStatus: 'current' }
      : { online, checkedAt: '2026-10-30T01:28:21Z' }), { status: 200 })));
    const fixture = TestBed.createComponent(MeldToolbar);
    await vi.waitFor(() => expect(fixture.componentInstance.remoteOnline()).toBe(online));
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    const dot = element.querySelector('.connection-dot')!;
    expect(dot.classList.contains('offline')).toBe(offline);
    expect(dot.getAttribute('aria-label')).toContain(label);
    expect(element.querySelector('.brand-version')?.textContent).toContain('vaaaaaaaa');
    fixture.destroy();
  });

  it('shows no indicator until the first check and treats a failed check as offline', async () => {
    let fail!: (reason: unknown) => void;
    vi.stubGlobal('fetch', vi.fn(() => new Promise((_, reject) => { fail = reject; })));
    const fixture = TestBed.createComponent(MeldToolbar);
    await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).querySelector('.connection-dot')).toBeNull();
    fail(new TypeError('network down'));
    await vi.waitFor(() => expect(fixture.componentInstance.remoteOnline()).toBe(false));
    fixture.destroy();
  });
});

describe('Remote account menu', () => {
  afterEach(() => document.querySelectorAll('meta[name="meld-account"], form#meld-sign-out').forEach(node => node.remove()));

  it('is absent without a signed-in session', async () => {
    const fixture = TestBed.createComponent(MeldToolbar);
    await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).querySelector('.remote-account-btn')).toBeNull();
  });

  it('shows the signed-in account and signs out through the page form', async () => {
    const meta = document.createElement('meta');
    meta.name = 'meld-account';
    meta.content = 'Ada Lovelace';
    meta.dataset['email'] = 'ada@example.invalid';
    document.head.append(meta);
    const form = document.createElement('form');
    form.id = 'meld-sign-out';
    const submit = vi.spyOn(form, 'submit').mockImplementation(() => undefined);
    document.body.append(form);

    const fixture = TestBed.createComponent(MeldToolbar);
    await fixture.whenStable();
    const button = (fixture.nativeElement as HTMLElement).querySelector('.remote-account-btn')!;
    expect(button.textContent).toContain('A');
    expect(button.textContent).toContain('Ada Lovelace');
    fixture.componentInstance.signOut();
    expect(submit).toHaveBeenCalled();
  });

  it('offers LLM usage only to the superuser', async () => {
    const meta = document.createElement('meta');
    meta.name = 'meld-account';
    meta.content = 'Ada Lovelace';
    meta.dataset['email'] = 'ada@example.invalid';
    document.head.append(meta);
    expect(readRemoteAccount()?.superuser).toBe(false);
    meta.dataset['superuser'] = 'true';
    expect(readRemoteAccount()?.superuser).toBe(true);

    const fixture = TestBed.createComponent(MeldToolbar);
    await fixture.whenStable();
    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('.remote-account-btn')!.click();
    await fixture.whenStable();
    expect(document.querySelector('a[href="/usage"]')?.textContent).toContain('LLM usage');
    fixture.destroy();
  });
});

import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { GmailSetup } from './gmail-setup';
import { GmailStatus, ResearchApi } from './research-api';

describe('Gmail setup', () => {
  const api = { connectGmail: vi.fn(), checkGmail: vi.fn(), removeGmail: vi.fn() };
  const status = (extra: Partial<GmailStatus> = {}): GmailStatus => ({
    configured: true, windowDays: 30, consentVersion: 'gmail-2026-11', accounts: [], lastError: null, ...extra,
  });
  const account = (address: string, extra: Partial<GmailStatus['accounts'][number]> = {}) => ({
    address, status: 'connected' as const, error: null, threads: 12, checkedAt: '2026-11-01T12:00:00Z', connectedAt: '2026-11-01T11:00:00Z', ...extra,
  });
  beforeEach(() => {
    vi.resetAllMocks();
    TestBed.configureTestingModule({ imports: [GmailSetup], providers: [provideNoopAnimations(), { provide: ResearchApi, useValue: api }] });
  });
  function setup(value: GmailStatus) {
    const fixture = TestBed.createComponent(GmailSetup);
    fixture.componentRef.setInput('status', value);
    fixture.detectChanges();
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

  it('connects only after consent and opens Google in this tab', async () => {
    api.connectGmail.mockResolvedValue({ authorizationUrl: 'https://accounts.google.com/o/oauth2/v2/auth?x=1', expiresAt: '' });
    const { fixture, element } = setup(status());
    const navigate = vi.spyOn(fixture.componentInstance, 'navigate').mockImplementation(() => undefined);
    const button = element.querySelector<HTMLButtonElement>('button.connect-gmail')!;
    expect(button.textContent).toContain('Connect Gmail');
    expect(button.disabled).toBe(true);
    expect(element.textContent).toContain('threads you wrote in');
    expect(element.textContent).toContain('reconnect every 7 days');
    fixture.componentInstance.agreed.set(true);
    fixture.detectChanges();
    button.click();
    await vi.waitFor(() => expect(navigate).toHaveBeenCalledWith('https://accounts.google.com/o/oauth2/v2/auth?x=1'));
    expect(api.connectGmail).toHaveBeenCalledWith('gmail-2026-11');
  });

  it('lists accounts with their thread counts, checks one and removes one after confirmation', async () => {
    api.checkGmail.mockResolvedValue(account('a@example.invalid'));
    api.removeGmail.mockResolvedValue(undefined);
    const { fixture, element } = setup(status({ accounts: [account('a@example.invalid'),
      account('b@example.invalid', { status: 'reconnect', threads: null, error: 'Google no longer accepts Meld\'s access.' })] }));
    const changed = vi.fn();
    fixture.componentInstance.changed.subscribe(changed);
    const items = element.querySelectorAll('.account');
    expect(items[0].textContent).toContain('12 threads you wrote in during the last 30 days');
    expect(items[1].querySelector('.account-status')?.textContent).toContain('Reconnect needed');
    expect(items[1].querySelector('.check-gmail')).toBeNull();
    expect(element.querySelector('button.connect-gmail')?.textContent).toContain('Add another Gmail account');

    items[0].querySelector<HTMLButtonElement>('.check-gmail')!.click();
    await vi.waitFor(() => expect(api.checkGmail).toHaveBeenCalledWith('a@example.invalid'));
    await vi.waitFor(() => expect(changed).toHaveBeenCalled());

    const remove = items[1].querySelector<HTMLButtonElement>('.remove-gmail')!;
    remove.click();
    fixture.detectChanges();
    expect(api.removeGmail).not.toHaveBeenCalled();
    expect(remove.textContent).toContain('are you sure');
    remove.click();
    await vi.waitFor(() => expect(api.removeGmail).toHaveBeenCalledWith('b@example.invalid'));
  });

  it('shows why the last connection did not complete', () => {
    const { element } = setup(status({ lastError: 'Gmail was not connected: access was not allowed.' }));
    expect(element.querySelector('[role="alert"]')?.textContent).toContain('access was not allowed');
  });
});

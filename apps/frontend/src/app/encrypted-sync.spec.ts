import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { EncryptedSync } from './encrypted-sync';
import { ResearchApi, SyncStatus } from './research-api';

describe('Encrypted sync', () => {
  const api = { encryptedSync: vi.fn(), createSync: vi.fn(), unlockSync: vi.fn(), startSyncOver: vi.fn(), changeSyncPassphrase: vi.fn() };
  const state = (value: SyncStatus['state'], extra: Partial<SyncStatus> = {}): SyncStatus =>
    ({ state: value, syncedProjects: 0, lastPushAt: null, lastPullAt: null, error: null, ...extra });
  beforeEach(() => {
    vi.resetAllMocks();
    TestBed.configureTestingModule({ imports: [EncryptedSync], providers: [provideNoopAnimations(), { provide: ResearchApi, useValue: api }] });
  });
  function setup(status: SyncStatus) {
    api.encryptedSync.mockResolvedValue(status);
    const fixture = TestBed.createComponent(EncryptedSync);
    fixture.componentRef.setInput('status', status);
    fixture.detectChanges();
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }
  function type(element: HTMLElement, selector: string, value: string) {
    const input = element.querySelector<HTMLInputElement>(selector)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
  }

  it('creates the passphrase only when it is long enough and repeated, then shows the recovery key once', async () => {
    api.createSync.mockResolvedValue({ recoveryKey: 'ABCD-EFGH-JKMN-PQRS-TVWX-YZ01-2345-6789' });
    const { fixture, element } = setup(state('setup'));
    const shown: boolean[] = [];
    fixture.componentInstance.keyShown.subscribe(value => shown.push(value));
    const create = () => element.querySelector<HTMLButtonElement>('button.create-sync')!;
    expect(element.textContent).toContain('only ciphertext');
    type(element, 'input.passphrase', 'short');
    fixture.detectChanges();
    expect(create().disabled).toBe(true);
    type(element, 'input.passphrase', 'four unrelated words here');
    type(element, 'input.confirmation', 'four unrelated words her');
    fixture.detectChanges();
    expect(element.textContent).toContain('do not match');
    expect(create().disabled).toBe(true);
    type(element, 'input.confirmation', 'four unrelated words here');
    fixture.detectChanges();
    create().click();
    await vi.waitFor(() => expect(api.createSync).toHaveBeenCalledWith('four unrelated words here'));
    await fixture.whenStable();
    fixture.detectChanges();
    expect(element.querySelector('.recovery-key')?.textContent).toContain('ABCD-EFGH-JKMN-PQRS-TVWX-YZ01-2345-6789');
    const done = element.querySelector<HTMLButtonElement>('button.key-saved')!;
    expect(done.disabled).toBe(true);
    fixture.componentInstance.saved.set(true);
    fixture.detectChanges();
    done.click();
    fixture.detectChanges();
    expect(element.querySelector('.recovery-key')).toBeNull();
    expect(shown).toEqual([true, false]);
    fixture.destroy();
  });

  it('unlocks with the passphrase or the recovery key', async () => {
    api.unlockSync.mockResolvedValue(undefined);
    const { fixture, element } = setup(state('locked'));
    expect(element.textContent).toContain('encrypted research from another computer');
    type(element, 'input.passphrase', 'my existing passphrase');
    fixture.detectChanges();
    element.querySelector<HTMLButtonElement>('button.unlock-sync')!.click();
    await vi.waitFor(() => expect(api.unlockSync).toHaveBeenCalledWith({ passphrase: 'my existing passphrase' }));
    await fixture.whenStable();
    element.querySelector<HTMLButtonElement>('button.use-recovery')!.click();
    fixture.detectChanges();
    type(element, 'input.recovery-input', 'abcd-efgh-jkmn-pqrs-tvwx-yz01-2345-6789');
    fixture.detectChanges();
    element.querySelector<HTMLButtonElement>('button.unlock-sync')!.click();
    await vi.waitFor(() => expect(api.unlockSync).toHaveBeenCalledWith({ recoveryKey: 'abcd-efgh-jkmn-pqrs-tvwx-yz01-2345-6789' }));
    fixture.destroy();
  });

  it('starts over only after the warning and a new passphrase', async () => {
    api.startSyncOver.mockResolvedValue({ recoveryKey: 'NEWK-EYNE-WKEY-NEWK-EYNE-WKEY-NEWK-EY00' });
    const { fixture, element } = setup(state('locked'));
    element.querySelector<HTMLButtonElement>('button.forgot')!.click();
    fixture.detectChanges();
    expect(element.querySelector('.warning')?.textContent).toContain('deletes the synced copy');
    type(element, 'input.passphrase', 'a brand new passphrase');
    type(element, 'input.confirmation', 'a brand new passphrase');
    fixture.detectChanges();
    element.querySelector<HTMLButtonElement>('button.start-over')!.click();
    await vi.waitFor(() => expect(api.startSyncOver).toHaveBeenCalledWith('a brand new passphrase'));
    await fixture.whenStable();
    fixture.detectChanges();
    expect(element.querySelector('.recovery-key')?.textContent).toContain('NEWK-EYNE');
    fixture.destroy();
  });

  it('shows the sync line when ready and nothing when the server offers no sync', () => {
    const ready = setup(state('ready', { syncedProjects: 3, lastPushAt: '2026-10-30T10:00:00Z' }));
    expect(ready.element.querySelector('.sync-line')?.textContent).toContain('3 projects synced');
    ready.fixture.destroy();
    const off = setup(state('off'));
    expect(off.element.textContent?.trim()).toBe('');
    off.fixture.destroy();
  });

  it('reports a wrong passphrase', async () => {
    api.unlockSync.mockRejectedValue(new Error('wrong'));
    const { fixture, element } = setup(state('locked'));
    type(element, 'input.passphrase', 'not the passphrase');
    fixture.detectChanges();
    await fixture.componentInstance.unlock();
    fixture.detectChanges();
    expect(element.querySelector('[role="alert"]')).not.toBeNull();
    fixture.destroy();
  });
});

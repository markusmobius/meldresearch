import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { BackupData } from './backup-data';
import { InterfaceLock } from './interface-lock';
import { ExportStatus, ResearchApi } from './research-api';

describe('Backup', () => {
  const api = { importData: vi.fn(), exportStatus: vi.fn(), startExport: vi.fn() };
  const job = (state: ExportStatus['state'], extra: Partial<ExportStatus> = {}): ExportStatus => ({
    id: 'job1', state, startedAt: '2026-11-01T12:00:00Z', readyAt: null, expiresAt: null, fileName: null, bytes: null, projects: 0,
    url: null, error: null, ...extra,
  });
  const ready = (id = 'job1') => job('ready', { id, readyAt: new Date().toISOString(), expiresAt: new Date(Date.now() + 300_000).toISOString(),
    fileName: 'meld-research-2026-11-01.zip', bytes: 20480, projects: 2, url: `/api/research/export/${id}` });
  beforeEach(() => {
    vi.resetAllMocks();
    api.exportStatus.mockResolvedValue(null);
    TestBed.configureTestingModule({ imports: [BackupData], providers: [provideNoopAnimations(), { provide: ResearchApi, useValue: api }] });
  });
  afterEach(() => vi.useRealTimers());
  async function setup(synced = true, setupMode = false) {
    const fixture = TestBed.createComponent(BackupData);
    fixture.componentRef.setInput('synced', synced);
    fixture.componentRef.setInput('setup', setupMode);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

  it('queues a download, blocks another until it is ready, then starts it and shows the link', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    api.startExport.mockResolvedValue(job('preparing'));
    const { fixture, element } = await setup();
    const save = vi.spyOn(fixture.componentInstance, 'save').mockImplementation(() => undefined);
    expect(element.textContent).toContain('not encrypted');
    const button = () => element.querySelector<HTMLButtonElement>('button.download-data')!;
    button().click();
    await vi.waitFor(() => expect(api.startExport).toHaveBeenCalledOnce());
    fixture.detectChanges();
    expect(button().disabled).toBe(true);
    expect(element.querySelector('.export-status')?.textContent).toContain('Preparing your download');
    await fixture.componentInstance.startDownload();
    expect(api.startExport).toHaveBeenCalledOnce();

    api.exportStatus.mockResolvedValue(ready());
    await vi.advanceTimersByTimeAsync(1000);
    fixture.detectChanges();
    expect(save).toHaveBeenCalledWith('/api/research/export/job1', 'meld-research-2026-11-01.zip');
    expect(button().disabled).toBe(false);
    const link = element.querySelector<HTMLAnchorElement>('a.export-link')!;
    expect(link.getAttribute('href')).toBe('/api/research/export/job1');
    expect(element.querySelector('.export-ready')?.textContent).toContain('20 KB, 2 projects');
  });

  it('starts a download that was ready before the queue request returned', async () => {
    api.startExport.mockResolvedValue(ready('quick'));
    const { fixture } = await setup();
    const save = vi.spyOn(fixture.componentInstance, 'save').mockImplementation(() => undefined);
    await fixture.componentInstance.startDownload();
    expect(save).toHaveBeenCalledExactlyOnceWith('/api/research/export/quick', 'meld-research-2026-11-01.zip');
  });

  it('after a reload shows a recent download without starting it again', async () => {
    api.exportStatus.mockResolvedValue(ready('earlier'));
    const save = vi.spyOn(BackupData.prototype, 'save').mockImplementation(() => undefined);
    const { element } = await setup();
    expect(element.querySelector('a.export-link')?.getAttribute('href')).toBe('/api/research/export/earlier');
    expect(save).not.toHaveBeenCalled();
    save.mockRestore();
  });

  it('locks the interface during an upload and reports what it restored', async () => {
    let finish!: (value: unknown) => void;
    api.importData.mockReturnValue(new Promise(resolve => { finish = resolve; }));
    const { fixture, element } = await setup();
    const lock = TestBed.inject(InterfaceLock);
    const changed = vi.fn();
    fixture.componentInstance.changed.subscribe(changed);
    const input = element.querySelector<HTMLInputElement>('input.upload-input')!;
    const file = new File([new Uint8Array([80, 75, 3, 4])], 'meld-research-2026-11-01.zip', { type: 'application/zip' });
    Object.defineProperty(input, 'files', { value: [file] });
    const uploading = fixture.componentInstance.upload({ target: input } as unknown as Event);
    expect(lock.running()).toBe(true);
    expect(lock.state()?.detail).toContain('meld-research-2026-11-01.zip');
    lock.close();
    expect(lock.locked()).toBe(true);

    finish({ projects: 3, included: 2, cards: 2, summaries: 1, publications: true,
      exportedEmail: 'old@example.invalid', otherAccount: true, exportedAt: '2026-11-01T12:00:00Z' });
    await uploading;
    fixture.detectChanges();
    expect(api.importData).toHaveBeenCalledWith(file);
    expect(changed).toHaveBeenCalled();
    expect(lock.state()?.outcome).toBe('done');
    expect(lock.state()?.message).toContain('Imported 3 projects (2 included)');
    expect(lock.state()?.message).toContain("old@example.invalid's earlier account");
    expect(element.querySelector('.imported')!.textContent).toContain('Encrypted sync uploads it');
    lock.close();
    expect(lock.locked()).toBe(false);
  });

  it('unlocks with the error when an upload fails', async () => {
    api.importData.mockRejectedValue(new Error('invalid'));
    const { fixture, element } = await setup();
    const lock = TestBed.inject(InterfaceLock);
    const input = element.querySelector<HTMLInputElement>('input.upload-input')!;
    Object.defineProperty(input, 'files', { value: [new File(['x'], 'x.zip')] });
    await fixture.componentInstance.upload({ target: input } as unknown as Event);
    fixture.detectChanges();
    expect(lock.state()?.outcome).toBe('failed');
    expect(element.querySelector('[role="alert"]')).not.toBeNull();
    lock.close();
  });

  it('during setup offers only an optional restore, which can be skipped', async () => {
    const { fixture, element } = await setup(true, true);
    const settled = vi.fn();
    fixture.componentInstance.settled.subscribe(settled);
    expect(element.querySelector('button.download-data')).toBeNull();
    expect(api.exportStatus).not.toHaveBeenCalled();
    expect(element.textContent).toContain('optional');
    element.querySelector<HTMLButtonElement>('button.skip-restore')!.click();
    expect(settled).toHaveBeenCalledOnce();
  });

  it('a successful restore also settles the step', async () => {
    api.importData.mockResolvedValue({ projects: 1, included: 1, cards: 1, summaries: 0, publications: false,
      exportedEmail: 'a@example.invalid', otherAccount: false, exportedAt: '2026-11-01T12:00:00Z' });
    const { fixture, element } = await setup(true, true);
    const settled = vi.fn();
    fixture.componentInstance.settled.subscribe(settled);
    const input = element.querySelector<HTMLInputElement>('input.upload-input')!;
    Object.defineProperty(input, 'files', { value: [new File(['x'], 'backup.zip')] });
    await fixture.componentInstance.upload({ target: input } as unknown as Event);
    expect(settled).toHaveBeenCalledOnce();
    TestBed.inject(InterfaceLock).close();
  });
});

import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { AccountData } from './account-data';
import { LeaveResult, ResearchApi } from './research-api';

describe('Your data', () => {
  const api = { importData: vi.fn(), leave: vi.fn() };
  beforeEach(() => {
    vi.resetAllMocks();
    TestBed.configureTestingModule({ imports: [AccountData], providers: [provideNoopAnimations(), { provide: ResearchApi, useValue: api }] });
  });
  function setup(synced = true) {
    const fixture = TestBed.createComponent(AccountData);
    fixture.componentRef.setInput('synced', synced);
    fixture.detectChanges();
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

  it('downloads through a plain link and explains the file is not encrypted', () => {
    const { element } = setup();
    const link = element.querySelector<HTMLAnchorElement>('a.download-data')!;
    expect(link.getAttribute('href')).toBe('/api/research/export');
    expect(link.hasAttribute('download')).toBe(true);
    expect(element.textContent).toContain('not encrypted');
  });

  it('uploads a chosen ZIP and reports what it restored', async () => {
    api.importData.mockResolvedValue({ projects: 3, included: 2, cards: 2, summaries: 1, publications: true,
      exportedEmail: 'old@example.invalid', otherAccount: true, exportedAt: '2026-11-01T12:00:00Z' });
    const { fixture, element } = setup();
    const changed = vi.fn();
    fixture.componentInstance.changed.subscribe(changed);
    const input = element.querySelector<HTMLInputElement>('input.upload-input')!;
    const file = new File([new Uint8Array([80, 75, 3, 4])], 'meld-research-2026-11-01.zip', { type: 'application/zip' });
    Object.defineProperty(input, 'files', { value: [file] });
    await fixture.componentInstance.upload({ target: input } as unknown as Event);
    fixture.detectChanges();
    expect(api.importData).toHaveBeenCalledWith(file);
    expect(changed).toHaveBeenCalled();
    const status = element.querySelector('.imported')!.textContent!;
    expect(status).toContain('Imported 3 projects (2 included)');
    expect(status).toContain("old@example.invalid's earlier account");
    expect(status).toContain('Encrypted sync uploads it');
  });

  it('reports a file that cannot be imported', async () => {
    api.importData.mockRejectedValue(new Error('invalid'));
    const { fixture, element } = setup();
    const input = element.querySelector<HTMLInputElement>('input.upload-input')!;
    Object.defineProperty(input, 'files', { value: [new File(['x'], 'x.zip')] });
    await fixture.componentInstance.upload({ target: input } as unknown as Event);
    fixture.detectChanges();
    expect(element.querySelector('[role="alert"]')).not.toBeNull();
  });

  it('leaves only after the warning and typing DELETE', async () => {
    const result: LeaveResult = { uninstalling: true, accountDeleted: true, logPath: 'C:\\Temp\\meldresearch-uninstall.log' };
    api.leave.mockResolvedValue(result);
    const { fixture, element } = setup();
    const left = vi.fn();
    fixture.componentInstance.left.subscribe(left);
    element.querySelector<HTMLButtonElement>('button.leave-toggle')!.click();
    fixture.detectChanges();
    expect(element.querySelector('.warning')!.textContent).toContain('your encrypted synced research');
    const button = () => element.querySelector<HTMLButtonElement>('button.leave-now')!;
    expect(button().disabled).toBe(true);
    const confirmation = element.querySelector<HTMLInputElement>('input.leave-confirmation')!;
    confirmation.value = 'delete';
    confirmation.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(button().disabled).toBe(true);
    confirmation.value = 'DELETE';
    confirmation.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    button().click();
    await vi.waitFor(() => expect(left).toHaveBeenCalledWith(result));
    expect(api.leave).toHaveBeenCalledWith('DELETE');
  });
});

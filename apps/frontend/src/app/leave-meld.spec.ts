import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { LeaveMeld } from './leave-meld';
import { LeaveResult, ResearchApi } from './research-api';

describe('Leave Meld Research', () => {
  const api = { leave: vi.fn() };
  beforeEach(() => {
    vi.resetAllMocks();
    TestBed.configureTestingModule({ imports: [LeaveMeld], providers: [provideNoopAnimations(), { provide: ResearchApi, useValue: api }] });
  });

  it('leaves only after the warning and typing DELETE', async () => {
    const result: LeaveResult = { uninstalling: true, accountDeleted: true, logPath: 'C:\\Temp\\meldresearch-uninstall.log' };
    api.leave.mockResolvedValue(result);
    const fixture = TestBed.createComponent(LeaveMeld);
    fixture.componentRef.setInput('synced', true);
    fixture.detectChanges();
    const element: HTMLElement = fixture.nativeElement;
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

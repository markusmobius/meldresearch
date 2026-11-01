import { TestBed } from '@angular/core/testing';
import { MeldLog } from './meld-log';
import { ResearchProject } from './research-api';

describe('Activity log', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('lists entries newest first with project names, repeats and filters', async () => {
    const urls: string[] = [];
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      urls.push(url);
      return new Response(JSON.stringify({ sources: ['research', 'server'], entries: [
        { id: 2, timestamp: new Date().toISOString(), level: 'warn', source: 'server', message: 'Server not answering.', detail: null, projectId: null, repeats: 3 },
        { id: 1, timestamp: new Date().toISOString(), level: 'info', source: 'research', message: 'Import: file 1 of 5 folded into the summary.',
          detail: null, projectId: '0123456789abcdef01234567', repeats: 1 },
      ] }), { status: 200 });
    }));
    const fixture = TestBed.createComponent(MeldLog);
    fixture.componentRef.setInput('projects', [{ projectId: '0123456789abcdef01234567', name: 'news-slant' } as ResearchProject]);
    fixture.detectChanges();
    await vi.waitFor(() => expect(fixture.componentInstance.entries()).toHaveLength(2));
    fixture.detectChanges();
    const rows = [...(fixture.nativeElement as HTMLElement).querySelectorAll('.log-row')];
    expect(rows[0].classList.contains('log-warn')).toBe(true);
    expect(rows[0].textContent).toContain('×3');
    expect(rows[1].querySelector('.log-project')?.textContent).toBe('news-slant');
    const level = (fixture.nativeElement as HTMLElement).querySelector<HTMLSelectElement>('select[aria-label="Log level"]')!;
    level.value = 'warn';
    level.dispatchEvent(new Event('change'));
    await vi.waitFor(() => expect(urls.at(-1)).toContain('level=warn'));
    fixture.destroy();
  });

  it('says when the backend cannot be reached instead of showing an empty log', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => { throw new TypeError('offline'); }));
    const fixture = TestBed.createComponent(MeldLog);
    fixture.detectChanges();
    await vi.waitFor(() => expect(fixture.componentInstance.error()).toContain('Cannot reach'));
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('.log-empty')).toBeNull();
    fixture.destroy();
  });
});

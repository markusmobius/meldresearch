import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { InferenceUsageResponse, MeldUsage } from './meld-usage';

describe('LLM usage dashboard', () => {
  const now = Date.parse('2026-10-31T12:00:00Z');
  const usage = (buckets: InferenceUsageResponse['buckets']): InferenceUsageResponse => ({
    from: new Date(now - 86400000).toISOString(), to: new Date(now).toISOString(), bucketMinutes: 1, buckets,
    deployments: ['gpt-chat', 'text-embedding'],
    capacity: { chatDeployment: 'gpt-chat', embeddingDeployment: 'text-embedding', active: 1, maxConcurrency: 2 },
  });
  const bucket = (purpose: InferenceUsageResponse['buckets'][number]['purpose'], operation: 'chat' | 'embedding', prompt: number, completion: number,
    extra: Partial<InferenceUsageResponse['buckets'][number]> = {}) => ({
    start: new Date(now - 3600000).toISOString(), deployment: operation === 'chat' ? 'gpt-chat' : 'text-embedding', operation, purpose,
    promptTokens: prompt, completionTokens: completion, requests: 1, failedRequests: 0, rateLimitedRequests: 0, unknownUsageRequests: 0, ...extra,
  });

  beforeEach(() => TestBed.configureTestingModule({
    imports: [MeldUsage], providers: [provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations()],
  }));

  it('totals tokens by operation and purpose and draws the stacked chart', async () => {
    const fixture = TestBed.createComponent(MeldUsage);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    const request = http.expectOne(item => item.url === '/api/usage');
    // The default 24 hours ends after the current minute, so requests made just now are included.
    expect(Date.parse(request.request.params.get('to')!)).toBeGreaterThanOrEqual(Date.now());
    expect(Date.parse(request.request.params.get('to')!) - Date.parse(request.request.params.get('from')!)).toBe(86400000);
    expect(request.request.params.has('purpose')).toBe(false);
    request.flush(usage([
      bucket('profileImport', 'chat', 1200, 300),
      bucket('profileUpdate', 'chat', 500, 100, { failedRequests: 1, rateLimitedRequests: 1 }),
      bucket('other', 'embedding', 40, 0),
    ]));
    await fixture.whenStable();
    fixture.detectChanges();
    const element: HTMLElement = fixture.nativeElement;
    const totals = fixture.componentInstance.totals();
    expect(totals).toEqual({ input: 1700, output: 400, embedding: 40, requests: 3, failed: 1, throttled: 1, unknown: 0 });
    expect(element.querySelectorAll('svg path')).toHaveLength(3);
    const rows = [...element.querySelectorAll('.usage-purposes tbody tr')].map(row => row.textContent?.replace(/\s+/g, ' ').trim());
    expect(rows[0]).toContain('Profile import');
    expect(rows[0]).toContain('1,500');
    expect(element.querySelector('.usage-capacity')?.textContent).toContain('1 / 2');
    http.verify();
  });

  it('filters by purpose and explains a refused request', async () => {
    const fixture = TestBed.createComponent(MeldUsage);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne(item => item.url === '/api/usage').flush(usage([]));
    await fixture.whenStable();
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('No model requests in this date range.');

    fixture.componentInstance.purpose.set('researcherMatching');
    const loading = fixture.componentInstance.load();
    const filtered = http.expectOne(item => item.url === '/api/usage');
    expect(filtered.request.params.get('purpose')).toBe('researcherMatching');
    filtered.flush({}, { status: 404, statusText: 'Not Found' });
    await loading;
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('[role="alert"]')?.textContent).toContain('only to the operator');
    http.verify();
  });

  it('rejects a reversed date range without a request', async () => {
    const fixture = TestBed.createComponent(MeldUsage);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne(item => item.url === '/api/usage').flush(usage([]));
    fixture.componentInstance.recentDays.set(null);
    fixture.componentInstance.from.set('2026-10-31T12:00');
    fixture.componentInstance.to.set('2026-10-30T12:00');
    await fixture.componentInstance.load();
    expect(fixture.componentInstance.error()).toContain('increasing date range');
    http.verify();
  });
});

import { DatePipe, DecimalPipe } from '@angular/common';
import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { AfterViewInit, Component, DestroyRef, ElementRef, OnInit, computed, inject, signal, viewChild } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { firstValueFrom } from 'rxjs';

export type InferencePurpose = 'profileImport' | 'profileUpdate' | 'projectSummary' | 'projectGrouping' | 'researcherMatching' | 'other';

export interface InferenceUsageBucket {
  start: string; deployment: string; operation: 'chat' | 'embedding'; purpose: InferencePurpose;
  promptTokens: number; completionTokens: number; requests: number; failedRequests: number;
  rateLimitedRequests: number; unknownUsageRequests: number;
}

export interface InferenceUsageResponse {
  from: string; to: string; bucketMinutes: number; buckets: ReadonlyArray<InferenceUsageBucket>; deployments: ReadonlyArray<string>;
  capacity: { chatDeployment: string; embeddingDeployment: string; active: number; maxConcurrency: number };
}

interface PlotBucket { start: number; input: number; output: number; embedding: number; unknown: number; }

/** Remote, superusers only: Foundry token usage by time and purpose (ported from Project Meld's LLM Usage page). */
@Component({
  selector: 'app-meld-usage',
  imports: [DatePipe, DecimalPipe, MatButtonModule, MatIconModule],
  templateUrl: './meld-usage.html',
  styleUrl: './meld-usage.css',
})
export class MeldUsage implements OnInit, AfterViewInit {
  private readonly http = inject(HttpClient);
  private readonly destroyRef = inject(DestroyRef);
  private readonly plotHost = viewChild.required<ElementRef<HTMLElement>>('plotHost');
  private readonly plot = viewChild<ElementRef<SVGSVGElement>>('plot');
  private generation = 0;
  readonly data = signal<InferenceUsageResponse | null>(null);
  readonly loading = signal(false);
  readonly error = signal('');
  readonly width = signal(800);
  readonly hovered = signal<PlotBucket | null>(null);
  readonly timezone = Intl.DateTimeFormat().resolvedOptions().timeZone;
  /** A Recent range (in days) follows the current time on every load; editing From or To fixes the range. */
  readonly recentDays = signal<number | null>(1);
  readonly from = signal('');
  readonly to = signal('');
  readonly deployment = signal('');
  readonly purpose = signal<InferencePurpose | ''>('');
  readonly appliedPurpose = signal<InferencePurpose | ''>('');
  readonly purposes: ReadonlyArray<{ value: InferencePurpose; label: string }> = [
    { value: 'profileImport', label: 'Profile import' },
    { value: 'profileUpdate', label: 'Profile updates' },
    { value: 'projectSummary', label: 'Project summaries' },
    { value: 'projectGrouping', label: 'Project grouping' },
    { value: 'researcherMatching', label: 'Researcher matching' },
    { value: 'other', label: 'Other' },
  ];
  readonly series = [
    { key: 'input' as const, label: 'Chat input', color: '#1e3c72' },
    { key: 'output' as const, label: 'Chat output', color: '#5b8fd6' },
    { key: 'embedding' as const, label: 'Embedding input', color: '#cf9b23' },
  ];
  readonly purposeTotals = computed(() => this.purposes.map(purpose => {
    const buckets = (this.data()?.buckets ?? []).filter(bucket => bucket.purpose === purpose.value);
    const input = sum(buckets.filter(bucket => bucket.operation === 'chat'), bucket => bucket.promptTokens);
    const output = sum(buckets, bucket => bucket.completionTokens);
    const embedding = sum(buckets.filter(bucket => bucket.operation === 'embedding'), bucket => bucket.promptTokens);
    return { ...purpose, input, output, embedding, total: input + output + embedding, requests: sum(buckets, bucket => bucket.requests),
      failed: sum(buckets, bucket => bucket.failedRequests), unknown: sum(buckets, bucket => bucket.unknownUsageRequests) };
  }));
  readonly totals = computed(() => {
    const buckets = this.data()?.buckets ?? [];
    return {
      input: sum(buckets.filter(bucket => bucket.operation === 'chat'), bucket => bucket.promptTokens),
      output: sum(buckets, bucket => bucket.completionTokens),
      embedding: sum(buckets.filter(bucket => bucket.operation === 'embedding'), bucket => bucket.promptTokens),
      requests: sum(buckets, bucket => bucket.requests),
      failed: sum(buckets, bucket => bucket.failedRequests),
      throttled: sum(buckets, bucket => bucket.rateLimitedRequests),
      unknown: sum(buckets, bucket => bucket.unknownUsageRequests),
    };
  });
  readonly bucketLabel = computed(() => {
    const minutes = this.data()?.bucketMinutes;
    return minutes === 1 ? 'minute' : minutes === 60 ? 'hour' : 'day';
  });
  readonly chart = computed(() => {
    const data = this.data();
    const width = this.width();
    const left = 52, right = width - 12, top = 22, bottom = 236;
    if (!data) return { paths: [] as string[], bins: [] as PlotBucket[], ticks: [] as { position: number; label: string }[],
      levels: [] as { position: number; label: string }[], left, right, top, bottom, binWidth: 0 };
    const step = data.bucketMinutes * 60000;
    const start = Math.floor(Date.parse(data.from) / step) * step;
    const count = Math.max(1, Math.ceil((Date.parse(data.to) - start) / step));
    const bins: PlotBucket[] = Array.from({ length: count }, (_, index) => ({ start: start + index * step, input: 0, output: 0, embedding: 0, unknown: 0 }));
    for (const bucket of data.buckets) {
      const bin = bins[Math.floor((Date.parse(bucket.start) - start) / step)];
      if (!bin) continue;
      if (bucket.operation === 'embedding') bin.embedding += bucket.promptTokens;
      else bin.input += bucket.promptTokens;
      bin.output += bucket.completionTokens;
      bin.unknown += bucket.unknownUsageRequests;
    }
    const maximum = Math.max(1, Math.ceil(Math.max(...bins.map(bin => bin.input + bin.output + bin.embedding)) / 4) * 4);
    const binWidth = (right - left) / count;
    const paths = this.series.map((series, seriesIndex) => {
      const upper: string[] = [], lower: string[] = [];
      bins.forEach((bin, index) => {
        const before = this.series.slice(0, seriesIndex).reduce((total, item) => total + bin[item.key], 0);
        const upperY = bottom - (before + bin[series.key]) / maximum * (bottom - top);
        const lowerY = bottom - before / maximum * (bottom - top);
        const leftX = left + index * binWidth, rightX = leftX + binWidth;
        upper.push(`${leftX.toFixed(2)},${upperY.toFixed(2)}`, `${rightX.toFixed(2)},${upperY.toFixed(2)}`);
        lower.push(`${leftX.toFixed(2)},${lowerY.toFixed(2)}`, `${rightX.toFixed(2)},${lowerY.toFixed(2)}`);
      });
      return `M${upper.join(' L')} L${lower.reverse().join(' L')} Z`;
    });
    const formatter = new Intl.DateTimeFormat(undefined, (Date.parse(data.to) - start) > 2 * 86400000
      ? { month: 'short', day: 'numeric' } : { hour: '2-digit', minute: '2-digit' });
    const tickCount = width < 500 ? 3 : 6;
    const ticks = Array.from({ length: tickCount }, (_, index) => ({
      position: left + (right - left) * index / (tickCount - 1),
      label: formatter.format(new Date(start + count * step * index / (tickCount - 1))),
    }));
    const compact = new Intl.NumberFormat(undefined, { notation: 'compact', maximumFractionDigits: 1 });
    const levels = Array.from({ length: 5 }, (_, index) => ({ position: bottom - (bottom - top) * index / 4, label: compact.format(maximum * index / 4) }));
    return { paths, bins, ticks, levels, left, right, top, bottom, binWidth };
  });

  ngOnInit(): void { void this.load(); }

  ngAfterViewInit(): void {
    if (typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(entries => this.width.set(Math.max(260, Math.round(entries[0].contentRect.width))));
    observer.observe(this.plotHost().nativeElement);
    this.destroyRef.onDestroy(() => observer.disconnect());
  }

  async load(): Promise<void> {
    const days = this.recentDays();
    if (days !== null) {
      // datetime-local has minute precision: round up so requests made this minute are included.
      const end = Math.ceil(Date.now() / 60000) * 60000;
      this.to.set(localDate(new Date(end)));
      this.from.set(localDate(new Date(end - days * 86400000)));
    }
    const from = new Date(this.from()), to = new Date(this.to());
    if (!Number.isFinite(from.getTime()) || !Number.isFinite(to.getTime()) || from >= to || to.getTime() - from.getTime() > 366 * 86400000) {
      this.error.set('Choose an increasing date range of at most 366 days.');
      return;
    }
    const generation = ++this.generation;
    const purpose = this.purpose();
    let params = new HttpParams().set('from', from.toISOString()).set('to', to.toISOString());
    if (this.deployment()) params = params.set('deployment', this.deployment());
    if (purpose) params = params.set('purpose', purpose);
    this.loading.set(true);
    this.error.set('');
    this.hovered.set(null);
    try {
      const data = await firstValueFrom(this.http.get<InferenceUsageResponse>('/api/usage', { params }));
      if (generation !== this.generation) return;
      this.data.set(data);
      this.appliedPurpose.set(purpose);
    } catch (error: unknown) {
      if (generation !== this.generation) return;
      this.data.set(null);
      const status = error instanceof HttpErrorResponse ? error.status : 0;
      this.error.set(status === 404 ? 'LLM usage is available only to the operator account.' : 'Usage data is unavailable. Try again.');
    } finally {
      if (generation === this.generation) this.loading.set(false);
    }
  }

  recent(days: number): void {
    this.recentDays.set(days);
    void this.load();
  }

  setFrom(event: Event): void { this.recentDays.set(null); this.from.set(this.value(event)); }
  setTo(event: Event): void { this.recentDays.set(null); this.to.set(this.value(event)); }

  value(event: Event): string { return (event.target as HTMLInputElement | HTMLSelectElement).value; }
  setPurpose(event: Event): void { this.purpose.set(this.value(event) as InferencePurpose | ''); }

  download(): void {
    const plot = this.plot();
    if (!plot) return;
    const clone = plot.nativeElement.cloneNode(true) as SVGSVGElement;
    clone.setAttribute('xmlns', 'http://www.w3.org/2000/svg');
    clone.setAttribute('width', String(this.width()));
    clone.setAttribute('height', '280');
    const url = URL.createObjectURL(new Blob([new XMLSerializer().serializeToString(clone)], { type: 'image/svg+xml' }));
    const link = document.createElement('a');
    link.href = url;
    link.download = `meldresearch-llm-usage-${this.data()?.from.slice(0, 10)}.svg`;
    link.click();
    URL.revokeObjectURL(url);
  }
}

function sum<T>(items: ReadonlyArray<T>, value: (item: T) => number): number {
  return items.reduce((total, item) => total + value(item), 0);
}

function localDate(date: Date): string {
  return new Date(date.getTime() - date.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
}

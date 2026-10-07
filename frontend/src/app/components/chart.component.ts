import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, afterNextRender, computed, inject, input, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { CHART_W, ChartSpec, categoryTotal, chartSummary, fmt, layoutChart, pct } from '../models/charts.models';

let seq = 0;

/**
 * [AEGIS-ASSESSMENT-VISUALS-01] Gráfico do app em SVG, a partir de um {@link ChartSpec} (colunas, barras, barras empilhadas,
 * rosca, linha mensal ou radar). Título, descrição, legenda em texto, base declarada, valores escritos no desenho e uma tabela
 * com os mesmos valores: cor nunca é o único portador da informação. As categorias com detalhe são áreas focáveis (Enter ou
 * Espaço abrem; toque e clique também) e, na tabela, links comuns.
 */
@Component({
  selector: 'app-chart',
  standalone: true,
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @let c = spec();
    <figure class="chart" [class]="'chart chart-' + c.kind" [attr.id]="'ch-' + c.id">
      <figcaption>
        <span class="t" [id]="uid + '-t'">{{ c.title }}</span>
        @if (!compact()) { <span class="d">{{ c.description }}</span> }
      </figcaption>
      @if (c.emptyMessage) {
        <p class="empty">{{ c.emptyMessage }}</p>
      } @else {
        @let l = layout();
        <svg [attr.viewBox]="'0 0 ' + l.width + ' ' + l.height" [attr.width]="l.width" [attr.height]="l.height" role="img" [attr.aria-labelledby]="uid + '-t ' + uid + '-s'" focusable="false">
          <desc [attr.id]="uid + '-s'">{{ summary() }}</desc>
          @for (s of l.shapes; track $index) {
            @switch (s.k) {
              @case ('rect') {
                <rect [attr.class]="s.cls" [attr.x]="$any(s).x" [attr.y]="$any(s).y" [attr.width]="$any(s).w" [attr.height]="$any(s).h">
                  @if ($any(s).title) { <title>{{ $any(s).title }}</title> }
                </rect>
              }
              @case ('line') {
                <line [attr.class]="s.cls" [attr.x1]="$any(s).x1" [attr.y1]="$any(s).y1" [attr.x2]="$any(s).x2" [attr.y2]="$any(s).y2" />
              }
              @case ('circle') {
                <circle [attr.class]="s.cls" [attr.cx]="$any(s).x" [attr.cy]="$any(s).y" [attr.r]="$any(s).r">
                  @if ($any(s).title) { <title>{{ $any(s).title }}</title> }
                </circle>
              }
              @case ('path') {
                <path [attr.class]="s.cls" [attr.d]="$any(s).d"><title>{{ $any(s).title }}</title></path>
              }
              @case ('polygon') {
                <polygon [attr.class]="s.cls" [attr.points]="$any(s).points" />
              }
              @case ('text') {
                <text [attr.class]="s.cls" [attr.x]="$any(s).x" [attr.y]="$any(s).y" [attr.text-anchor]="$any(s).anchor">{{ $any(s).text }}</text>
              }
            }
          }
          @for (h of l.hits; track h.i) {
            <rect class="hit" [attr.x]="h.x" [attr.y]="h.y" [attr.width]="h.w" [attr.height]="h.h" tabindex="0" role="link"
                  [attr.aria-label]="h.label + ': abrir o detalhe'" (click)="open(h.i)" (keydown.enter)="open(h.i)"
                  (keydown.space)="$event.preventDefault(); open(h.i)" />
          }
        </svg>
        @if (showLegend()) {
          <ul class="legend-list">
            @for (s of c.series; track s.key) {
              <li><i class="sw" [class]="'sw t-' + s.tone + (s.dashed ? ' dash' : '')"></i>{{ s.label }}@if (s.dashed) { (tracejado) }</li>
            }
          </ul>
        }
        @if (c.basis) { <p class="basis">{{ c.basis }}</p> }
        @if (c.notes?.length) {
          <ul class="notes">@for (n of c.notes!; track n) { <li>{{ n }}</li> }</ul>
        }
        @if (!hideTable()) {
          <details class="values">
            <summary>Valores do gráfico ({{ c.unit }})</summary>
            <div class="table-wrap">
              <table class="data-table">
                <thead>
                  <tr>
                    <th scope="col">{{ c.kind === 'line' ? 'Mês' : 'Categoria' }}</th>
                    @for (s of c.series; track s.key) { <th scope="col">{{ s.label }}</th> }
                    @if (c.kind === 'stacked') { <th scope="col">Total</th> }
                    @if (c.kind === 'donut') { <th scope="col">Percentual</th> }
                  </tr>
                </thead>
                <tbody>
                  @for (cat of c.categories; track $index; let i = $index) {
                    <tr>
                      <th scope="row">
                        @if (c.links?.[i]; as link) { <a [routerLink]="link.route" [queryParams]="link.query ?? null">{{ cat }}</a> } @else { {{ cat }} }
                      </th>
                      @for (s of c.series; track s.key) { <td>{{ s.values[i] === null ? c.emptyLabel : fmt(s.values[i], c.decimals) }}</td> }
                      @if (c.kind === 'stacked') { <td>{{ fmt(total(i)) }}</td> }
                      @if (c.kind === 'donut') { <td>{{ pct(c.series[0].values[i] ?? 0, donutTotal()) }}</td> }
                    </tr>
                  }
                  @if (c.kind === 'donut') { <tr><th scope="row">Total (base)</th><td>{{ fmt(donutTotal()) }}</td><td>100%</td></tr> }
                </tbody>
              </table>
            </div>
          </details>
        }
      }
    </figure>
  `,
  styles: [
    `
      :host { display: block; min-width: 0; }
      .chart { margin: 0; min-width: 0; }
      figcaption { display: flex; flex-direction: column; gap: 2px; }
      figcaption .t { font-size: var(--fs-panel); font-weight: 600; color: var(--text); }
      figcaption .d { font-size: var(--fs-meta); color: var(--muted); line-height: 1.4; }
      svg { display: block; max-width: 100%; height: auto; margin: var(--sp-2) auto 0; overflow: visible; }
      .empty { margin: var(--sp-3) 0; color: var(--text-2); font-size: var(--fs-sm); font-style: italic; }
      .txt { fill: var(--text-2); font-size: 12px; font-family: var(--sans); }
      .sm { fill: var(--muted); font-size: 11px; font-family: var(--sans); }
      .val { fill: var(--text); font-size: 11.5px; font-weight: 700; font-family: var(--sans); }
      .big { fill: var(--text); font-size: 22px; font-weight: 700; font-family: var(--sans); }
      .in { fill: #05070f; font-size: 10px; font-weight: 700; font-family: var(--sans); }
      .in-na, .in-bad, .in-err { fill: var(--text); }
      .g { stroke: var(--line); stroke-width: 1; }
      .ring { fill: none; stroke: var(--line); stroke-width: 1; }
      .track { fill: var(--panel-2); }
      .f-acc { fill: var(--cyan-2); } .f-ok { fill: var(--cyan); } .f-bad { fill: var(--red); } .f-warn { fill: var(--amber); }
      .f-ne { fill: var(--muted); } .f-err { fill: var(--violet-text); } .f-na { fill: #3b4458; } .f-cur { fill: var(--cyan); }
      .f-tgt { fill: var(--magenta); } .f-prog { fill: var(--violet-text); }
      .f-sev-Critical { fill: var(--red); } .f-sev-High { fill: #ff7a3d; } .f-sev-Medium { fill: var(--amber); }
      .f-sev-Low { fill: var(--cyan-2); } .f-sev-Informational { fill: var(--muted); }
      .l-acc, .l-cur, .l-tgt { fill: none; stroke-width: 2.5; stroke-linecap: round; }
      .l-acc { stroke: var(--cyan-2); } .l-cur { stroke: var(--cyan); } .l-tgt { stroke: var(--magenta); }
      .r-cur { fill: rgba(38, 224, 255, 0.14); stroke: var(--cyan); stroke-width: 2; } .r-tgt { fill: none; stroke: var(--magenta); stroke-width: 2; }
      .dash { stroke-dasharray: 6 4; }
      .hit { fill: transparent; cursor: pointer; pointer-events: all; }
      .hit:hover { fill: rgba(122, 145, 190, 0.08); }
      .hit:focus-visible { outline: none; stroke: var(--cyan); stroke-width: 2; }
      .legend-list { list-style: none; display: flex; flex-wrap: wrap; gap: 4px 14px; padding: 0; margin: var(--sp-2) 0 0; font-size: var(--fs-meta); color: var(--text-2); }
      .sw { display: inline-block; width: 10px; height: 10px; border-radius: 2px; margin-right: 6px; vertical-align: -1px; }
      .sw.t-acc { background: var(--cyan-2); } .sw.t-ok { background: var(--cyan); } .sw.t-bad { background: var(--red); } .sw.t-warn { background: var(--amber); }
      .sw.t-ne { background: var(--muted); } .sw.t-err { background: var(--violet-text); } .sw.t-na { background: #3b4458; } .sw.t-cur { background: var(--cyan); }
      .sw.t-tgt { background: var(--magenta); } .sw.t-prog { background: var(--violet-text); }
      .sw.dash { background: none; border-top: 3px dashed var(--magenta); height: 0; width: 16px; border-radius: 0; vertical-align: 3px; }
      .basis { margin: var(--sp-2) 0 0; font-size: var(--fs-meta); color: var(--muted); line-height: 1.4; }
      .notes { margin: var(--sp-1) 0 0; padding-left: var(--sp-4); font-size: var(--fs-meta); color: var(--text-2); }
      .values summary { cursor: pointer; font-size: var(--fs-meta); color: var(--text-2); margin-top: var(--sp-2); }
      .values .data-table { font-size: var(--fs-meta); }
    `,
  ],
})
export class ChartComponent {
  private readonly router = inject(Router);
  readonly spec = input.required<ChartSpec>();
  /** Sem descrição sob o título (a seção já explica). */
  readonly compact = input(false);
  /** Sem a tabela de valores (quando a mesma tabela já está ao lado). */
  readonly hideTable = input(false);

  protected readonly uid = `chart-${++seq}`;
  /** Largura disponível (px), medida no host; o desenho usa ela (até 560 px; a linha mensal até 680). */
  private readonly hostWidth = signal(CHART_W);
  protected readonly fmt = fmt;
  protected readonly pct = pct;
  protected readonly layout = computed(() =>
    layoutChart(this.spec(), Math.min(this.hostWidth(), this.spec().kind === 'line' ? 680 : 560)),
  );

  constructor() {
    const host = inject(ElementRef<HTMLElement>);
    const destroy = inject(DestroyRef);
    afterNextRender(() => {
      const el = host.nativeElement as HTMLElement;
      const measure = () => {
        const w = Math.floor(el.clientWidth / 10) * 10;
        if (w > 0 && w !== this.hostWidth()) this.hostWidth.set(w);
      };
      measure();
      if (typeof ResizeObserver === 'undefined') return;
      const ro = new ResizeObserver(measure);
      ro.observe(el);
      destroy.onDestroy(() => ro.disconnect());
    });
  }
  protected readonly summary = computed(() => chartSummary(this.spec()));
  protected readonly donutTotal = computed(() => this.spec().series[0]?.values.reduce<number>((s, v) => s + (v ?? 0), 0) ?? 0);
  protected readonly showLegend = computed(() => {
    const c = this.spec();
    return c.kind !== 'donut' && !(c.series.length === 1 && c.categoryTones);
  });

  protected total(i: number): number {
    return categoryTotal(this.spec(), i);
  }

  protected open(i: number): void {
    const link = this.spec().links?.[i];
    if (link) void this.router.navigate(link.route, { queryParams: link.query ?? {} });
  }
}

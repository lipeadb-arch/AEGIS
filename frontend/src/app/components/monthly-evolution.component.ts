import { DatePipe } from '@angular/common';
import { Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { NistHistoryItem, averageText } from '../models/nist.models';
import {
  MonthlyCell,
  PostureMonthlyHistory,
  PostureMonthlyPoint,
  PostureMonthlySeries,
  incompatibilityLabel,
  monthShort,
  monthlyCells,
} from '../models/posture-history.models';

interface SeriesView {
  series: PostureMonthlySeries;
  cells: MonthlyCell[];
  /** Segmentos a desenhar (só entre meses consecutivos e comparáveis). */
  segments: { x1: number; y1: number; x2: number; y2: number }[];
  dots: { x: number; y: number; label: string }[];
  restarts: string[];
}

const W = 600;
const H = 120;
const PAD = 12;

/**
 * [AEGIS-NIST-JOURNEY-01] Evolução MENSAL: uma série por instrumento e fonte (KNIGHT e NIST nunca somados). Mês sem
 * publicação fica sem ponto e sem linha; a linha só liga meses vizinhos comparáveis (mesma fórmula, catálogo e esquema,
 * decidido no servidor). A maturidade NIST por avaliação vem à parte, porque é outra metodologia.
 */
@Component({
  selector: 'app-monthly-evolution',
  standalone: true,
  imports: [DatePipe, RouterLink],
  template: `
    @let h = history();
    @if (h) {
      <p class="muted crit">{{ h.criterion }}</p>
      @if (views().length === 0) {
        <p class="muted">Nenhuma fotografia publicada nos últimos {{ h.months.length }} meses. Publique um registro no histórico para começar a série.</p>
      }
      @for (v of views(); track v.series.semanticFamily) {
        <figure class="serie">
          <figcaption>
            <strong>{{ v.series.label }}</strong>
            <span class="muted">{{ v.series.type === 'Knight' ? 'AEGIS KNIGHT' : 'AEGIS NIST · postura' }} · última fórmula {{ last(v)?.formulaVersion }} · catálogo {{ last(v)?.catalogVersion }}</span>
          </figcaption>
          <svg [attr.viewBox]="'0 0 ' + W + ' ' + H" preserveAspectRatio="none" role="img" [attr.aria-label]="aria(v)">
            <line [attr.x1]="PAD" [attr.x2]="W - PAD" [attr.y1]="H - PAD" [attr.y2]="H - PAD" class="axis" />
            @for (s of v.segments; track $index) { <line [attr.x1]="s.x1" [attr.y1]="s.y1" [attr.x2]="s.x2" [attr.y2]="s.y2" class="seg" /> }
            @for (d of v.dots; track $index) { <circle [attr.cx]="d.x" [attr.cy]="d.y" r="4" class="dot"><title>{{ d.label }}</title></circle> }
          </svg>
          <ol class="months" [style.--n]="v.cells.length">
            @for (c of v.cells; track c.month) {
              <li [class.empty]="!c.point"><span class="m">{{ monthShort(c.month) }}</span>
                <span class="s">{{ c.point ? scoreText(c.point.score) : '—' }}</span></li>
            }
          </ol>
          @if (v.restarts.length > 0) { <p class="muted">Série recomeça: {{ v.restarts.join(' · ') }}</p> }
          <details>
            <summary>Detalhe mês a mês</summary>
            <ul class="detail">
              @for (c of v.cells; track c.month) {
                @if (c.point; as p) {
                  <li><strong>{{ monthShort(c.month) }}</strong> · nota {{ scoreText(p.score) }} · cobertura {{ p.coverage }}% ·
                    {{ p.evaluatedItems }} de {{ p.eligibleItems }} avaliados · publicada em {{ p.capturedAt | date: 'dd/MM/yyyy' }}
                    @if (p.sourceLabel) { · {{ p.sourceLabel }} } · {{ p.formulaVersion }} / {{ p.catalogVersion }}
                    @if (p.publishedInMonth > 1) { · {{ p.publishedInMonth }} publicações no mês (vale a última) }</li>
                }
              }
            </ul>
          </details>
        </figure>
      }
    }

    @if (nist() !== null) {
      <h3 class="sub">AEGIS NIST · maturidade por avaliação</h3>
      <p class="muted crit">Um ponto por escopo, no mês da revisão humana mais recente. Metodologia de maturidade (1 a 5) — não é somada à postura nem ao KNIGHT.</p>
      @if (nist()!.length === 0) {
        <p class="muted">Nenhuma avaliação NIST com revisão registrada. <a routerLink="/nist">Abrir o AEGIS NIST</a></p>
      } @else {
        <ul class="detail">
          @for (i of nist()!; track i.scopeId) {
            <li><strong>{{ monthShort(i.referenceMonth) }}</strong> · {{ i.assessmentName }} — {{ i.scopeName }} ·
              {{ i.evaluated }} de {{ i.subcategories }} avaliadas · atual {{ averageText(i.current) }} · alvo {{ averageText(i.target) }} ·
              {{ i.methodologyVersion }} · revisão em {{ i.lastReviewedAt | date: 'dd/MM/yyyy' }}</li>
          }
        </ul>
      }
    }
  `,
  styles: [
    `
      .crit { margin: 0 0 var(--sp-3); font-size: var(--fs-sm); }
      .serie { margin: 0 0 var(--sp-5); min-width: 0; }
      figcaption { display: flex; flex-direction: column; gap: 2px; margin-bottom: var(--sp-2); overflow-wrap: anywhere; }
      figcaption .muted { font-size: var(--fs-meta); }
      svg { width: 100%; height: 120px; display: block; }
      .axis { stroke: var(--line-strong); stroke-width: 1; }
      .seg { stroke: var(--cyan); stroke-width: 2; vector-effect: non-scaling-stroke; }
      .dot { fill: var(--violet-text); }
      .months { list-style: none; padding: 0; margin: 4px 0 0; display: grid; grid-template-columns: repeat(var(--n), minmax(0, 1fr)); gap: 2px; }
      .months li { display: flex; flex-direction: column; align-items: center; font-size: 11px; color: var(--text-2); min-width: 0; }
      .months li.empty { color: var(--muted); }
      .months .m { color: var(--muted); }
      .detail { margin: var(--sp-2) 0 0; padding-left: var(--sp-4); font-size: var(--fs-sm); display: flex; flex-direction: column; gap: 4px; overflow-wrap: anywhere; }
      :host { display: block; min-width: 0; }
      details summary { cursor: pointer; font-size: var(--fs-sm); margin-top: var(--sp-2); }
      .sub { margin: var(--sp-5) 0 var(--sp-2); font-size: var(--fs-panel); }
      @media (max-width: 520px) { .months li:nth-child(odd) .m { visibility: hidden; } }
    `,
  ],
})
export class MonthlyEvolutionComponent {
  readonly history = input<PostureMonthlyHistory | null>(null);
  /** Maturidade NIST por avaliação; nulo = não mostrar o bloco. */
  readonly nist = input<NistHistoryItem[] | null>(null);

  protected readonly W = W;
  protected readonly H = H;
  protected readonly PAD = PAD;
  protected readonly monthShort = monthShort;
  protected readonly averageText = averageText;

  protected readonly views = computed<SeriesView[]>(() => {
    const h = this.history();
    if (!h) return [];
    const n = h.months.length;
    const x = (i: number) => (n <= 1 ? W / 2 : PAD + (i * (W - 2 * PAD)) / (n - 1));
    const y = (score: number) => H - PAD - (Math.max(0, Math.min(100, score)) / 100) * (H - 2 * PAD);
    return h.series.map((series) => {
      const cells = monthlyCells(h.months, series.points);
      const segments: SeriesView['segments'] = [];
      const dots: SeriesView['dots'] = [];
      const restarts: string[] = [];
      cells.forEach((c, i) => {
        if (!c.point) return;
        if (c.point.score !== null) dots.push({ x: x(i), y: y(c.point.score), label: `${monthShort(c.month)}: ${this.scoreText(c.point.score)}` });
        if (c.connected) {
          const prev = cells[i - 1].point!;
          segments.push({ x1: x(i - 1), y1: y(prev.score!), x2: x(i), y2: y(c.point.score!) });
        }
        if (c.point.breakReasons.length > 0)
          restarts.push(`${monthShort(c.month)} (${c.point.breakReasons.map(incompatibilityLabel).join(', ')})`);
      });
      return { series, cells, segments, dots, restarts };
    });
  });

  protected last(v: SeriesView): PostureMonthlyPoint | null {
    return v.series.points.length > 0 ? v.series.points[v.series.points.length - 1] : null;
  }

  protected scoreText(score: number | null): string {
    return score === null ? 'sem nota' : score.toLocaleString('pt-BR', { maximumFractionDigits: 1 });
  }

  protected aria(v: SeriesView): string {
    const pts = v.cells.filter((c) => c.point).map((c) => `${monthShort(c.month)} ${this.scoreText(c.point!.score)}`);
    return `${v.series.label}: ${pts.length ? pts.join(', ') : 'sem publicação no período'}.`;
  }
}

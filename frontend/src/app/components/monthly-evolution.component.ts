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
  maturityText,
  monthShort,
  monthlyCells,
  seriesValue,
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
            <span class="muted">{{ instrument(v) }} · última fórmula {{ last(v)?.formulaVersion }} · catálogo {{ last(v)?.catalogVersion }}</span>
          </figcaption>
          <svg [attr.viewBox]="'0 0 ' + W + ' ' + H" preserveAspectRatio="none" role="img" [attr.aria-label]="aria(v)">
            <line [attr.x1]="PAD" [attr.x2]="W - PAD" [attr.y1]="H - PAD" [attr.y2]="H - PAD" class="axis" />
            @for (s of v.segments; track $index) { <line [attr.x1]="s.x1" [attr.y1]="s.y1" [attr.x2]="s.x2" [attr.y2]="s.y2" class="seg" /> }
            @for (d of v.dots; track $index) { <circle [attr.cx]="d.x" [attr.cy]="d.y" r="4" class="dot"><title>{{ d.label }}</title></circle> }
          </svg>
          <ol class="months" [style.--n]="v.cells.length">
            @for (c of v.cells; track c.month) {
              <li [class.empty]="!c.point"><span class="m">{{ monthShort(c.month) }}</span>
                <span class="s">{{ c.point ? valueText(v, c.point) : '—' }}</span></li>
            }
          </ol>
          @if (v.restarts.length > 0) { <p class="muted">Série recomeça: {{ v.restarts.join(' · ') }}</p> }
          <details>
            <summary>Detalhe mês a mês</summary>
            <ul class="detail">
              @for (c of v.cells; track c.month) {
                @if (c.point; as p) {
                  <li><strong>{{ monthShort(c.month) }}</strong> ·
                    @if (v.series.type === 'NistMaturity') {
                      rodada {{ p.cycleName ?? '—' }} · atual {{ maturity(p.maturityCurrent) }} · alvo {{ maturity(p.maturityTarget) }} ·
                      universo aplicável {{ p.applicableItems ?? '—' }} ·
                    } @else { nota {{ scoreText(p.score) }} · }
                    cobertura {{ p.coverage }}% ·
                    {{ p.evaluatedItems }} de {{ p.eligibleItems }} avaliados · publicada em {{ p.capturedAt | date: 'dd/MM/yyyy' }}
                    @if (p.sourceLabel) { · {{ p.sourceLabel }} } · {{ p.formulaVersion }} / {{ p.catalogVersion }}
                    @if (p.publishedInMonth > 1) { · {{ p.publishedInMonth }} publicações no mês (vale a última) }
                    @for (n of p.notes ?? []; track n) { <span class="note">{{ n }}</span> }</li>
                }
              }
            </ul>
          </details>
        </figure>
      }
    }

    @if (nist() !== null) {
      <h3 class="sub">AEGIS NIST · maturidade por rodada (ainda não publicada)</h3>
      <p class="muted crit">Leitura viva: um ponto por rodada e escopo, no mês do fim do período, só com avaliações confirmadas. Metodologia de
        maturidade (1 a 5) — não é somada à postura nem ao KNIGHT. A série mensal acima usa apenas rodadas publicadas.</p>
      @if (nist()!.length === 0) {
        <p class="muted">Nenhuma avaliação NIST com revisão registrada. <a routerLink="/nist">Abrir o AEGIS NIST</a></p>
      } @else {
        <ul class="detail">
          @for (i of nist()!; track i.scopeId + (i.cycleId ?? '')) {
            <li><strong>{{ monthShort(i.referenceMonth) }}</strong> · {{ i.assessmentName }} — {{ i.scopeName }}@if (i.cycleName) { · rodada {{ i.cycleName }} } ·
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
      .note { display: block; color: var(--muted); font-size: var(--fs-meta); }
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
    return h.series.map((series) => {
      // Escala própria de cada instrumento: maturidade 0–5 (níveis 1–5) e score 0–100 — nunca no mesmo eixo.
      const max = series.type === 'NistMaturity' ? 5 : 100;
      const y = (v: number) => H - PAD - (Math.max(0, Math.min(max, v)) / max) * (H - 2 * PAD);
      const val = (p: PostureMonthlyPoint) => seriesValue(series.type, p);
      const cells = monthlyCells(h.months, series.points, val);
      const segments: SeriesView['segments'] = [];
      const dots: SeriesView['dots'] = [];
      const restarts: string[] = [];
      cells.forEach((c, i) => {
        if (!c.point) return;
        const cur = val(c.point);
        if (cur !== null) dots.push({ x: x(i), y: y(cur), label: `${monthShort(c.month)}: ${this.valueText({ series } as SeriesView, c.point)}` });
        if (c.connected) {
          const prev = cells[i - 1].point!;
          segments.push({ x1: x(i - 1), y1: y(val(prev)!), x2: x(i), y2: y(cur!) });
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

  protected maturity(v: number | null | undefined): string {
    return maturityText(v);
  }

  /** Texto do valor do ponto na escala do instrumento ("2,5/5" na maturidade; "62" no score). */
  protected valueText(v: Pick<SeriesView, 'series'>, p: PostureMonthlyPoint): string {
    if (v.series.type !== 'NistMaturity') return this.scoreText(p.score);
    return p.maturityCurrent === null || p.maturityCurrent === undefined ? 'sem nível' : `${maturityText(p.maturityCurrent)}/5`;
  }

  protected instrument(v: SeriesView): string {
    return v.series.type === 'Knight' ? 'AEGIS KNIGHT · 0–100' : v.series.type === 'NistMaturity' ? 'AEGIS NIST · maturidade 1–5 (metodologia AEGIS)' : 'AEGIS NIST · postura 0–100';
  }

  protected aria(v: SeriesView): string {
    const pts = v.cells.filter((c) => c.point).map((c) => `${monthShort(c.month)} ${this.valueText(v, c.point!)}`);
    return `${v.series.label}: ${pts.length ? pts.join(', ') : 'sem publicação no período'}.`;
  }
}

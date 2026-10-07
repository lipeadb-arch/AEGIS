import { DatePipe } from '@angular/common';
import { Component, computed, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ChartSpec, historyLine } from '../models/charts.models';
import { NistHistoryItem, averageText } from '../models/nist.models';
import {
  HistoryWindowRequest,
  PostureMonthlyHistory,
  PostureMonthlyPoint,
  PostureMonthlySeries,
  deltaText,
  incompatibilityLabel,
  maturityText,
  monthOptions,
  monthShort,
} from '../models/posture-history.models';
import { ChartComponent } from './chart.component';

interface SeriesView {
  series: PostureMonthlySeries;
  chart: ChartSpec;
  rows: { month: string; point: PostureMonthlyPoint | null }[];
}

/**
 * [AEGIS-NIST-JOURNEY-01 · AEGIS-ASSESSMENT-VISUALS-01] Evolução MENSAL: um gráfico por instrumento e série (KNIGHT 0–100 e
 * NIST 1–5 nunca no mesmo eixo, nem somados) e a tabela mês a mês com valor, base, período avaliado, publicação, variação
 * (só entre pontos comparáveis) e observações. Mês sem publicação fica sem ponto e sem linha; pontos incompatíveis não são
 * ligados. Com `period`, mostra o seletor de período (fim e quantidade de meses) e avisa a escolha por `periodChange`.
 */
@Component({
  selector: 'app-monthly-evolution',
  standalone: true,
  imports: [DatePipe, RouterLink, ChartComponent],
  template: `
    @if (period(); as p) {
      <div class="period" role="group" aria-label="Período da evolução">
        <label class="field">
          <span class="field-label">Período</span>
          <select [value]="'' + (p.months ?? 12)" (change)="emit(p, { months: +$any($event.target).value })">
            <option value="6">6 meses</option>
            <option value="12">12 meses</option>
            <option value="24">24 meses</option>
          </select>
        </label>
        <label class="field">
          <span class="field-label">Até</span>
          <select [value]="p.until ?? ''" (change)="emit(p, { until: $any($event.target).value || null })">
            <option value="">Mês atual</option>
            @for (m of untilOptions; track m.value) { <option [value]="m.value">{{ m.label }}</option> }
          </select>
        </label>
      </div>
    }
    @let h = history();
    @if (h) {
      <p class="muted crit">{{ h.criterion }}@if (h.months.length) { Período: {{ monthShort(h.months[0]) }} a {{ monthShort(h.months[h.months.length - 1]) }}. }</p>
      @if (views().length === 0) {
        <p class="muted">Nenhuma fotografia publicada no período escolhido. Publique um registro no KNIGHT ou no NIST para começar a série.</p>
      }
      @for (v of views(); track v.series.semanticFamily) {
        <section class="serie" [attr.aria-label]="v.series.label">
          <header class="serie-head">
            <h3>{{ v.series.label }}</h3>
            <p class="muted">{{ v.series.instrument ?? instrument(v.series) }} · última fórmula {{ last(v)?.formulaVersion }} · catálogo {{ last(v)?.catalogVersion }}</p>
          </header>
          <app-chart [spec]="v.chart" [compact]="true" [hideTable]="true" />
          <details class="mm" [open]="openTables()">
            <summary>Mês a mês ({{ v.series.points.length }} mês(es) com publicação em {{ v.rows.length }})</summary>
            <div class="table-wrap">
              <table class="data-table">
                <thead>
                  <tr>
                    <th scope="col">Mês</th>
                    <th scope="col">{{ isMaturity(v) ? 'Atual' : 'Nota' }}</th>
                    @if (isMaturity(v)) { <th scope="col">Alvo</th> }
                    <th scope="col">Cobertura e base</th>
                    <th scope="col">{{ isMaturity(v) ? 'Rodada e período avaliado' : 'Coleta' }}</th>
                    <th scope="col">Publicação</th>
                    <th scope="col">Variação</th>
                    <th scope="col">Observações</th>
                  </tr>
                </thead>
                <tbody>
                  @for (r of v.rows; track r.month) {
                    @if (r.point; as p) {
                      <tr>
                        <th scope="row">{{ monthShort(r.month) }}</th>
                        <td>{{ isMaturity(v) ? maturity(p.maturityCurrent) : scoreText(p.score) }}</td>
                        @if (isMaturity(v)) { <td>{{ maturity(p.maturityTarget) }}</td> }
                        <td>{{ pctText(p.coverage) }} · {{ p.evaluatedItems }} de {{ p.eligibleItems }}
                          <span class="dim">{{ isMaturity(v) ? 'aplicáveis: ' + (p.applicableItems ?? '—') : 'controles aplicáveis' }}</span></td>
                        <td>
                          @if (isMaturity(v)) {
                            {{ p.cycleName ?? '—' }}
                            @if (p.periodStart && p.periodEnd) { <span class="dim">{{ p.periodStart | date: 'dd/MM/yyyy' : 'UTC' }} a {{ p.periodEnd | date: 'dd/MM/yyyy' : 'UTC' }}</span> }
                          } @else {
                            {{ p.dataRecency ? (p.dataRecency | date: 'dd/MM/yyyy') : '—' }}
                          }
                        </td>
                        <td>
                          @if (p.isThisPublication) {
                            <strong>esta publicação</strong>
                          } @else {
                            {{ p.capturedAt | date: 'dd/MM/yyyy' }}
                            @if (p.sourceRunId) {
                              <a class="dim inline" routerLink="/knight" [queryParams]="{ run: p.sourceRunId }">avaliação</a>
                            } @else if (v.series.nistAssessmentId) {
                              <a class="dim inline" routerLink="/nist" [queryParams]="{ avaliacao: v.series.nistAssessmentId, escopo: v.series.nistScopeId }">avaliação</a>
                            }
                          }
                          @if (p.publishedInMonth > 1) { <span class="dim">{{ p.publishedInMonth }} no mês; vale a última</span> }
                        </td>
                        <td>{{ p.breakReasons.length ? 'não comparável' : deltaText(p, isMaturity(v) ? 2 : 1) }}</td>
                        <td class="obs">
                          @if (p.breakReasons.length) { <span class="note">Recomeça: {{ breaks(p) }}.</span> }
                          @for (n of p.notes ?? []; track n) { <span class="note">{{ n }}</span> }
                          @if (!p.breakReasons.length && !(p.notes ?? []).length) { — }
                        </td>
                      </tr>
                    } @else {
                      <tr class="empty-row">
                        <th scope="row">{{ monthShort(r.month) }}</th>
                        <td [attr.colspan]="isMaturity(v) ? 7 : 6" class="dimtxt">Sem publicação neste mês</td>
                      </tr>
                    }
                  }
                </tbody>
              </table>
            </div>
            <p class="muted foot">A variação só aparece entre pontos comparáveis da mesma série e não indica, sozinha, a causa
              (correção, mudança no ambiente ou de cobertura). Para ver o que mudou, compare as fotografias em
              <a routerLink="/history" [queryParams]="{ vista: 'fotografias' }">Fotografias publicadas</a>.</p>
          </details>
        </section>
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
      :host { display: block; min-width: 0; }
      .period { display: flex; flex-wrap: wrap; gap: var(--sp-3); margin-bottom: var(--sp-3); }
      .period .field { min-width: 140px; }
      .crit { margin: 0 0 var(--sp-3); font-size: var(--fs-sm); }
      .serie { margin: 0 0 var(--sp-6); min-width: 0; }
      .serie-head h3 { margin: 0; font-size: var(--fs-panel); overflow-wrap: anywhere; }
      .serie-head .muted { margin: 2px 0 0; font-size: var(--fs-meta); overflow-wrap: anywhere; }
      .mm summary { cursor: pointer; font-size: var(--fs-sm); margin-top: var(--sp-2); color: var(--text-2); }
      .data-table { font-size: var(--fs-meta); }
      .data-table tbody th[scope='row'] { white-space: nowrap; text-transform: none; letter-spacing: 0; vertical-align: top; color: var(--text); }
      .data-table td { vertical-align: top; }
      .dim { display: block; color: var(--muted); font-size: var(--fs-meta); }
      .dim.inline { display: inline; margin-left: 6px; }
      .dimtxt { color: var(--muted); font-style: italic; }
      .obs { min-width: 200px; }
      .note { display: block; color: var(--text-2); }
      .foot { margin-top: var(--sp-2); font-size: var(--fs-meta); }
      .detail { margin: var(--sp-2) 0 0; padding-left: var(--sp-4); font-size: var(--fs-sm); display: flex; flex-direction: column; gap: 4px; overflow-wrap: anywhere; }
      .sub { margin: var(--sp-5) 0 var(--sp-2); font-size: var(--fs-panel); }
    `,
  ],
})
export class MonthlyEvolutionComponent {
  readonly history = input<PostureMonthlyHistory | null>(null);
  /** Maturidade NIST por avaliação (leitura viva, à parte); nulo = não mostrar o bloco. */
  readonly nist = input<NistHistoryItem[] | null>(null);
  /** Período atual (mostra o seletor quando informado). */
  readonly period = input<HistoryWindowRequest | null>(null);
  /** Tabelas mês a mês abertas por padrão (prévia da publicação). */
  readonly openTables = input(false);
  readonly periodChange = output<HistoryWindowRequest>();

  protected readonly monthShort = monthShort;
  protected readonly averageText = averageText;
  protected readonly deltaText = deltaText;
  /** Fins de período possíveis: os 23 meses anteriores ao atual (o atual é "Mês atual"). */
  protected readonly untilOptions = monthOptions(new Date(), 24).slice(1);

  protected readonly views = computed<SeriesView[]>(() => {
    const h = this.history();
    if (!h) return [];
    return h.series.map((series) => {
      const byMonth = new Map(series.points.map((p) => [p.month, p]));
      return { series, chart: historyLine(series, h.months), rows: h.months.map((month) => ({ month, point: byMonth.get(month) ?? null })) };
    });
  });

  protected emit(p: HistoryWindowRequest, change: Partial<HistoryWindowRequest>): void {
    this.periodChange.emit({ months: p.months, until: p.until, ...change });
  }

  protected isMaturity(v: SeriesView): boolean {
    return v.series.type === 'NistMaturity';
  }

  protected last(v: SeriesView): PostureMonthlyPoint | null {
    return v.series.points.length > 0 ? v.series.points[v.series.points.length - 1] : null;
  }

  protected scoreText(score: number | null): string {
    return score === null ? 'sem nota' : score.toLocaleString('pt-BR', { maximumFractionDigits: 1 });
  }

  protected maturity(v: number | null | undefined): string {
    return v === null || v === undefined ? 'sem nível' : `${maturityText(v)}/5`;
  }

  protected pctText(v: number): string {
    return `${v.toLocaleString('pt-BR', { maximumFractionDigits: 1 })}%`;
  }

  protected breaks(p: PostureMonthlyPoint): string {
    return p.breakReasons.map(incompatibilityLabel).join(', ');
  }

  protected instrument(s: PostureMonthlySeries): string {
    return s.type === 'Knight'
      ? 'AEGIS KNIGHT · 0–100'
      : s.type === 'NistMaturity'
        ? 'AEGIS NIST · maturidade 1–5 (metodologia AEGIS)'
        : 'AEGIS Score (postura do ambiente) · 0–100';
  }
}

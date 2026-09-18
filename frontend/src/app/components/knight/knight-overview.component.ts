import { DatePipe } from '@angular/common';
import { Component, computed, input, output } from '@angular/core';
import {
  KnightAffectedSummary,
  KnightAssessment,
  KnightControlFilters,
  KnightIndicatorStatus,
  affectedKindLabel,
  distributionBy,
  findingTitle,
  limitationViews,
  overviewKpis,
  priorityControls,
  severityLabel,
  sourceTypeLabel,
  statusLabel,
} from '../../models/knight.models';

const STATUS_ORDER: KnightIndicatorStatus[] = ['Passed', 'Exposed', 'Mitigated', 'Error', 'NotEvaluated', 'NotApplicable'];

/**
 * [AEGIS-KNIGHT-MULTICLOUD-01] Aba "Visão geral" do assessment. Tudo sai da avaliação exibida — nenhuma série
 * histórica, nenhum número estimado. Score, aprovação e cobertura aparecem SEPARADOS (medem coisas diferentes e
 * não se somam), e controles, ocorrências e objetos únicos nunca se confundem. Todo gráfico é um botão que leva
 * à lista correspondente na aba de controles.
 */
@Component({
  selector: 'app-knight-overview',
  standalone: true,
  imports: [DatePipe],
  template: `
    @let a = assessment();
    @let k = kpis();
    <div class="metric-grid">
      <div class="metric">
        <span class="metric-label">Score de postura KNIGHT</span>
        <span class="metric-value">{{ a.score === null ? '—' : round(a.score) }}</span>
        <span class="metric-foot">
          {{ a.score === null ? 'Sem controle avaliado: não há nota — nunca zero por ausência.' : 'Escala 0–100 · ' + a.scoreFormulaVersion + ' · pondera severidade e resultado.' }}
        </span>
      </div>
      <div class="metric">
        <span class="metric-label">Aprovação</span>
        <span class="metric-value">{{ pct(k.approvalPercent) }}</span>
        <span class="metric-foot">{{ k.passed }} aprovado(s) de {{ k.evaluated }} avaliado(s). Não é a nota.</span>
      </div>
      <div class="metric">
        <span class="metric-label">Cobertura do assessment</span>
        <span class="metric-value">{{ pct(a.coverage) }}</span>
        <span class="metric-foot">Avaliados ÷ aplicáveis. Cobertura não é conformidade.</span>
      </div>
      <div class="metric">
        <span class="metric-label">Findings</span>
        <span class="metric-value">{{ k.findings }}</span>
        <span class="metric-foot">{{ unitsLine() }}</span>
      </div>
    </div>
    <p class="notice ov-note">
      Score, aprovação e cobertura medem coisas diferentes e não se somam. O score KNIGHT não é o AEGIS Score/NIST
      nem índice de fornecedor.
    </p>

    <div class="panel">
      <div class="hd"><h3>Controles por resultado</h3></div>
      <div class="chips">
        @for (st of statuses; track st) {
          <button type="button" class="filter-chip" (click)="filter.emit({ status: st })">
            <b>{{ countOf(st) }}</b> <span class="dot" [class]="st" aria-hidden="true"></span>{{ statusLabel(st) }}
          </button>
        }
      </div>
      <p class="muted small">
        {{ k.total }} controle(s) no escopo. Não avaliado = faltou dado ou permissão (reduz a cobertura, nunca
        aprova). Erro = a regra falhou ao avaliar. Mitigado = exposição com controle compensatório comprovado.
      </p>
    </div>

    <div class="ov-grid">
      <div class="panel">
        <div class="hd"><h3>Findings por severidade</h3></div>
        <p class="muted small">Controles reprovados ou mitigados. Clique para abrir a lista.</p>
        @for (s of k.findingsBySeverity; track s.key) {
          <button type="button" class="bar" (click)="filter.emit({ status: 'findings', severity: s.key })"
                  [attr.aria-label]="s.label + ': ' + s.count + ' finding(s)'">
            <span class="lbl"><span class="sev" [class]="s.key">{{ s.label }}</span></span>
            <span class="track"><span class="seg Exposed" [style.width.%]="share(s.count, maxSeverity())"></span></span>
            <span class="num">{{ s.count }}</span>
          </button>
        }
      </div>
      <div class="panel">
        <div class="hd"><h3>Resultado por domínio de segurança</h3></div>
        <p class="muted small">Só os domínios com controles no escopo desta avaliação.</p>
        @for (r of byDomain(); track r.key) {
          <button type="button" class="bar" (click)="filter.emit({ domain: r.key })" [attr.aria-label]="rowLabel(r.label, r.counts, r.total)">
            <span class="lbl">{{ r.label }}</span>
            <span class="track">
              @for (st of statuses; track st) {
                @if (r.counts[st]) { <span class="seg" [class]="st" [style.width.%]="share(r.counts[st], maxDomain())" [title]="statusLabel(st) + ': ' + r.counts[st]"></span> }
              }
            </span>
            <span class="num">{{ r.total }}</span>
          </button>
        }
        <div class="legend">
          @for (st of statuses; track st) { <span><span class="dot" [class]="st" aria-hidden="true"></span>{{ statusLabel(st) }}</span> }
        </div>
      </div>
    </div>

    <div class="panel">
      <div class="hd"><h3>Resultado por serviço</h3></div>
      @for (r of byService(); track r.key) {
        <button type="button" class="bar" (click)="filter.emit({ service: r.key })" [attr.aria-label]="rowLabel(r.label, r.counts, r.total)">
          <span class="lbl">{{ r.label }}</span>
          <span class="track">
            @for (st of statuses; track st) {
              @if (r.counts[st]) { <span class="seg" [class]="st" [style.width.%]="share(r.counts[st], maxService())" [title]="statusLabel(st) + ': ' + r.counts[st]"></span> }
            }
          </span>
          <span class="num">{{ r.total }}</span>
        </button>
      }
    </div>

    <div class="ov-grid">
      <div class="panel">
        <div class="hd"><h3>Riscos prioritários</h3></div>
        <p class="muted small">Até cinco controles reprovados — severidade, depois objetos afetados. Nenhum critério além do comprovado.</p>
        @if (priorities().length === 0) {
          <p class="muted">Nenhum controle reprovado nesta avaliação.</p>
        } @else {
          <ol class="prio">
            @for (p of priorities(); track p.indicatorId) {
              <li>
                <button type="button" class="linkbtn" (click)="open.emit(p.indicatorId)">{{ findingTitle(p) }}</button>
                <span class="muted small">
                  <span class="sev" [class]="p.severity">{{ severityLabel(p.severity) }}</span>
                  {{ p.indicatorId }}@if (p.affectedObjectCount > 0) { · {{ p.affectedObjectCount }} objeto(s) }
                </span>
              </li>
            }
          </ol>
        }
      </div>
      <div class="panel">
        <div class="hd"><h3>Principais objetos afetados</h3></div>
        @if (summaryState() === 'loading') {
          <p class="muted">Carregando…</p>
        } @else if (summaryState() === 'error' || !summary()) {
          <p class="muted">O resumo de objetos não pôde ser carregado agora. Os controles continuam na outra aba.</p>
        } @else if (summary()!.top.length === 0) {
          <p class="muted">Nenhum objeto afetado preservado nesta avaliação.</p>
        } @else {
          <div class="table-wrap">
            <table class="data-table">
              <thead><tr><th>Objeto</th><th>Tipo</th><th>Controles</th></tr></thead>
              <tbody>
                @for (o of summary()!.top; track o.kind + o.externalId) {
                  <tr>
                    <td>{{ o.displayName || o.userPrincipalName || o.externalId }}<span class="meta">{{ o.indicatorIds.join(', ') }}</span></td>
                    <td>{{ kindLabel(o.kind) }}</td>
                    <td>{{ o.controlCount }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </div>
    </div>

    <div class="panel">
      <div class="hd"><h3>Fontes, datas e limitações de cobertura</h3></div>
      <p class="small">
        <b>{{ sourceTypeLabel(a.sourceType) }}</b> · {{ a.source }} · coleta de
        {{ (a.completedAt || a.startedAt) | date: 'dd/MM/yyyy HH:mm' }} · catálogo {{ a.catalogVersion }}.
        Escopo desta entrega: identidade. Microsoft 365/Teams e Azure ainda não são avaliados e não aparecem como cobertos.
      </p>
      @if (limitations().length === 0) {
        <p class="muted small">Nenhuma limitação de coleta registrada: todas as capacidades desta fonte foram lidas.</p>
      } @else {
        <div class="table-wrap">
          <table class="data-table">
            <thead><tr><th>Capacidade</th><th>Causa</th><th>Controles prejudicados</th><th>O que fazer</th></tr></thead>
            <tbody>
              @for (l of limitations(); track l.capability) {
                <tr>
                  <td>{{ l.label }}@if (l.detail) { <span class="meta">{{ l.detail }}</span> }</td>
                  <td>{{ l.cause }}</td>
                  <td>{{ l.affectedControls.length ? l.affectedControls.join(', ') : 'nenhum controle ficou sem avaliação por isso' }}</td>
                  <td>{{ l.guidance }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </div>
  `,
  styles: [
    `
      :host { display: flex; flex-direction: column; gap: var(--sp-4); }
      .ov-note { margin: 0; }
      .ov-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--sp-4); }
      .chips, .legend { display: flex; flex-wrap: wrap; gap: var(--sp-2); }
      .legend { margin-top: var(--sp-2); font-size: var(--fs-meta); color: var(--text-2); }
      .small { font-size: var(--fs-meta); }
      .bar { display: grid; grid-template-columns: 150px minmax(0, 1fr) 40px; gap: var(--sp-2); align-items: center; width: 100%;
        padding: 4px 0; border: 0; background: none; color: var(--text); text-align: left; cursor: pointer; }
      .bar:hover .track { box-shadow: 0 0 0 1px var(--cyan); }
      .bar:focus-visible { outline: none; box-shadow: var(--focus); border-radius: var(--radius-xs); }
      .lbl { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: var(--fs-sm); }
      .num { text-align: right; font-weight: 700; }
      .track { display: flex; height: 14px; border-radius: 4px; overflow: hidden; background: var(--hover); }
      .seg { height: 100%; }
      .seg.Passed, .dot.Passed { background: var(--cyan); }
      .seg.Exposed, .dot.Exposed { background: var(--red); }
      .seg.Mitigated, .dot.Mitigated { background: var(--amber); }
      .seg.Error, .dot.Error { background: var(--violet); }
      .seg.NotEvaluated, .dot.NotEvaluated { background: var(--muted); }
      .seg.NotApplicable, .dot.NotApplicable { background: var(--line-strong); }
      .dot { display: inline-block; width: 10px; height: 10px; margin-right: 6px; border-radius: 2px; }
      .sev { padding: 1px 8px; border: 1px solid currentColor; border-radius: var(--radius-pill); font-size: var(--fs-caps); font-weight: 600; }
      .sev.Critical { color: var(--red-text); } .sev.High { color: #ff9a3d; } .sev.Medium { color: var(--amber); }
      .sev.Low { color: var(--cyan); } .sev.Informational { color: var(--text-2); }
      .prio { margin: 0; padding-left: 20px; display: flex; flex-direction: column; gap: 8px; }
      .prio li span { display: block; margin-top: 2px; }
      @media (max-width: 900px) {
        .ov-grid { grid-template-columns: minmax(0, 1fr); }
        .bar { grid-template-columns: 110px minmax(0, 1fr) 32px; }
      }
    `,
  ],
})
export class KnightOverviewComponent {
  readonly assessment = input.required<KnightAssessment>();
  readonly summary = input<KnightAffectedSummary | null>(null);
  readonly summaryState = input<'loading' | 'ok' | 'error'>('loading');
  /** Pedido de abrir a aba de controles já com um recorte. */
  readonly filter = output<Partial<KnightControlFilters>>();
  /** Pedido de abrir UM controle. */
  readonly open = output<string>();

  protected readonly statuses = STATUS_ORDER;
  protected readonly statusLabel = statusLabel;
  protected readonly severityLabel = severityLabel;
  protected readonly sourceTypeLabel = sourceTypeLabel;
  protected readonly findingTitle = findingTitle;
  protected readonly kindLabel = affectedKindLabel;

  readonly kpis = computed(() => overviewKpis(this.assessment()));
  readonly byDomain = computed(() => distributionBy(this.assessment(), 'domain'));
  readonly byService = computed(() => distributionBy(this.assessment(), 'service'));
  readonly priorities = computed(() => priorityControls(this.assessment()));
  readonly limitations = computed(() => limitationViews(this.assessment()));
  readonly maxSeverity = computed(() => Math.max(1, ...this.kpis().findingsBySeverity.map((s) => s.count)));
  readonly maxDomain = computed(() => Math.max(1, ...this.byDomain().map((r) => r.total)));
  readonly maxService = computed(() => Math.max(1, ...this.byService().map((r) => r.total)));

  /** Três unidades que não se confundem: controles, ocorrências (objeto × controle) e objetos únicos. */
  readonly unitsLine = computed(() => {
    const s = this.summary();
    if (!s) return 'Controles reprovados ou mitigados.';
    const unique = `${s.complete ? '' : '≥ '}${s.uniqueObjects} objeto(s) único(s)`;
    return `${s.occurrences} ocorrência(s) objeto × controle · ${unique}${s.complete ? '' : ' (detalhe parcial: é um piso)'}`;
  });

  countOf(st: KnightIndicatorStatus): number {
    return this.assessment().indicators.filter((i) => i.status === st).length;
  }

  share(n: number, max: number): number {
    return max > 0 ? (100 * n) / max : 0;
  }

  round(n: number): number {
    return Math.round(n);
  }

  pct(n: number | null): string {
    return n === null ? '—' : `${String(Math.round(n * 10) / 10).replace('.', ',')}%`;
  }

  rowLabel(label: string, counts: Record<KnightIndicatorStatus, number>, total: number): string {
    return `${label}: ${counts.Exposed} reprovado(s), ${counts.Passed} aprovado(s), ${counts.NotEvaluated + counts.Error} não avaliado(s) ou erro, de ${total}`;
  }
}

import { DatePipe } from '@angular/common';
import { Component, computed, input, output } from '@angular/core';
import {
  KnightAffectedSummary,
  KnightAssessment,
  KnightControlFilters,
  KnightIndicatorStatus,
  KnightReferenceCoverage,
  affectedKindLabel,
  distributionBy,
  findingTitle,
  knightUnitsLine,
  limitationViews,
  overviewKpis,
  priorityControls,
  severityLabel,
  sourceTypeLabel,
  statusLabel,
  threeMeasures,
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
        <span class="metric-foot">{{ a.score === null ? 'Sem controle avaliado, não há nota.' : 'Escala própria do KNIGHT, de 0 a 100.' }}</span>
        <details class="help">
          <summary>Como é calculado</summary>
          Pondera a severidade e o resultado de cada controle avaliado (fórmula {{ a.scoreFormulaVersion }}). Controles
          não avaliados ficam fora da nota: nunca contam como zero nem como aprovados.
        </details>
      </div>
      <div class="metric">
        <span class="metric-label">Aprovação</span>
        <span class="metric-value">{{ pct(k.approvalPercent) }}</span>
        <span class="metric-foot">{{ k.passed }} de {{ k.evaluated }} controles avaliados foram aprovados.</span>
        <details class="help">
          <summary>O que significa</summary>
          Proporção de aprovados entre os controles avaliados. Não é a nota: a nota também pesa a severidade.
        </details>
      </div>
      <div class="metric">
        <span class="metric-label">Cobertura do assessment</span>
        <span class="metric-value">{{ pct(a.coverage) }}</span>
        <span class="metric-foot">Parte dos controles que pôde ser verificada.</span>
        <details class="help">
          <summary>O que significa</summary>
          Controles avaliados ÷ controles aplicáveis. Mostra quanto foi possível verificar, não se o ambiente está conforme.
        </details>
      </div>
      <div class="metric">
        <span class="metric-label">Controles com achados</span>
        <span class="metric-value">{{ k.findings }}</span>
        <span class="metric-foot">{{ unitsLine() }}</span>
        <details class="help">
          <summary>Como contar</summary>
          Controles reprovados ou mitigados. Uma mesma conta, aplicação, papel ou política pode aparecer em mais de um
          controle: cada aparição é uma ocorrência; itens distintos contam uma vez.
        </details>
      </div>
    </div>
    <p class="muted small ov-note">
      O score KNIGHT resume os controles de configuração desta avaliação — desta fonte e desta coleta. O AEGIS Score
      (NIST) é outra medida; as duas notas não se somam.
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
        {{ k.total }} controle(s) no escopo. Não avaliado: a evidência faltou, foi insuficiente ou inconclusiva
        (inclui dado ou permissão ausente) — o motivo aparece em cada controle; reduz a cobertura e nunca aprova.
        Erro: a regra falhou ao avaliar. Mitigado: exposição com controle compensatório comprovado.
      </p>
    </div>

    <div class="ov-grid">
      <div class="panel">
        <div class="hd"><h3>Controles com achados por severidade</h3></div>
        <p class="muted small">Controles reprovados ou mitigados. Clique para abrir a lista.</p>
        @for (s of k.findingsBySeverity; track s.key) {
          <button type="button" class="bar" (click)="filter.emit({ status: 'findings', severity: s.key })"
                  [attr.aria-label]="s.label + ': ' + s.count + ' finding(s)'">
            <span class="lbl"><span class="sev" [class]="s.key">{{ s.label }}</span></span>
            <span class="track"><span class="seg" [class]="'sev-' + s.key" [style.width.%]="share(s.count, maxSeverity())"></span></span>
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

    @if (byPlatform().length > 1) {
      <div class="panel">
        <div class="hd"><h3>Resultado por plataforma</h3></div>
        @for (r of byPlatform(); track r.key) {
          <button type="button" class="bar" (click)="filter.emit({ platform: r.key })" [attr.aria-label]="rowLabel(r.label, r.counts, r.total)">
            <span class="lbl">{{ r.label }}</span>
            <span class="track">
              @for (st of statuses; track st) {
                @if (r.counts[st]) { <span class="seg" [class]="st" [style.width.%]="share(r.counts[st], maxPlatform())" [title]="statusLabel(st) + ': ' + r.counts[st]"></span> }
              }
            </span>
            <span class="num">{{ r.total }}</span>
          </button>
        }
      </div>
    }

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
        <p class="muted small">Até cinco controles reprovados — severidade, depois quantidade afetada. Nenhum critério além do comprovado.</p>
        @if (priorities().length === 0) {
          <p class="muted">Nenhum controle reprovado nesta avaliação.</p>
        } @else {
          <ol class="prio">
            @for (p of priorities(); track p.indicatorId) {
              <li>
                <button type="button" class="linkbtn" (click)="open.emit(p.indicatorId)">{{ findingTitle(p) }}</button>
                <span class="muted small">
                  <span class="sev" [class]="p.severity">{{ severityLabel(p.severity) }}</span>
                  {{ p.indicatorId }}@if (p.affectedComposition) { · {{ p.affectedComposition }} } @else if (p.affectedObjectCount > 0) { · {{ p.affectedObjectCount }} afetado(s) }
                </span>
              </li>
            }
          </ol>
        }
      </div>
      <div class="panel">
        <div class="hd"><h3>Contas, aplicações e demais itens mais recorrentes</h3></div>
        @if (summaryState() === 'loading') {
          <p class="muted">Carregando…</p>
        } @else if (summaryState() === 'error' || !summary()) {
          <p class="muted">O resumo de itens afetados não pôde ser carregado agora. Os controles continuam na outra aba.</p>
        } @else if (summary()!.top.length === 0) {
          <p class="muted">Nenhum item afetado preservado nesta avaliação.</p>
        } @else {
          <div class="table-wrap">
            <table class="data-table">
              <thead><tr><th>Item</th><th>Tipo</th><th>Controles</th></tr></thead>
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
        Serviços ainda não coletados não aparecem como avaliados — o que falta está na cobertura do catálogo abaixo.
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

    <div class="panel">
      <div class="hd"><h3>Três medidas que não se somam</h3></div>
      @let m = measures();
      <div class="metric-grid three">
        <div class="metric">
          <span class="metric-label">Cobertura do catálogo de referência</span>
          <span class="metric-value">{{ pct(m.catalogFull) }}</span>
          <span class="metric-foot">
            integral · {{ pct(m.catalogPartial) }} parcial · {{ pct(m.catalogAnyAutomated) }} com alguma avaliação automatizada (não é cobertura completa)
          </span>
        </div>
        <div class="metric">
          <span class="metric-label">Cobertura desta avaliação</span>
          <span class="metric-value">{{ pct(m.assessmentCoverage) }}</span>
          <span class="metric-foot">O que a coleta conseguiu avaliar neste ambiente.</span>
        </div>
        <div class="metric">
          <span class="metric-label">Aprovação</span>
          <span class="metric-value">{{ pct(m.approval) }}</span>
          <span class="metric-foot">Dos controles avaliados, os que estão conformes.</span>
        </div>
      </div>
      @if (coverage(); as c) {
        <p class="muted small">
          Catálogo: o que o AEGIS consegue avaliar (propriedade do produto, {{ c.frameworks.join(' · ') }}). Limitação da API
          oficial, verificação manual e acesso que o conector não tem nunca contam como avaliados.
        </p>
        <div class="table-wrap">
          <table class="data-table">
            <thead><tr><th>Plataforma</th><th>Total</th><th>Integral</th><th>Parcial</th><th>Pendente</th><th>Limitação da API</th><th>Manual</th><th>Outro acesso</th></tr></thead>
            <tbody>
              @for (g of c.byPlatform; track g.key) {
                <tr>
                  <td>{{ g.label }}</td><td>{{ g.total }}</td>
                  <td>{{ g.implemented }} ({{ pct(g.fullPercent) }})</td><td>{{ g.partial }} ({{ pct(g.partialPercent) }})</td>
                  <td>{{ g.pending }}</td><td>{{ g.apiLimitation }}</td><td>{{ g.manualOnly }}</td><td>{{ g.requiresAccess }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      } @else {
        <p class="muted small">A cobertura do catálogo de referência não pôde ser carregada agora; as outras duas medidas continuam valendo.</p>
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
      .seg.sev-Critical { background: var(--red); } .seg.sev-High { background: #ff9a3d; }
      .seg.sev-Medium { background: var(--amber); } .seg.sev-Low { background: var(--cyan); }
      .seg.sev-Informational { background: var(--text-2); }
      .help { font-size: var(--fs-meta); color: var(--text-2); margin-top: 4px; }
      .help summary { cursor: pointer; color: var(--cyan); width: fit-content; }
      .dot { display: inline-block; width: 10px; height: 10px; margin-right: 6px; border-radius: 2px; }
      .sev { padding: 1px 8px; border: 1px solid currentColor; border-radius: var(--radius-pill); font-size: var(--fs-caps); font-weight: 600; }
      .sev.Critical { color: var(--red-text); } .sev.High { color: #ff9a3d; } .sev.Medium { color: var(--amber); }
      .sev.Low { color: var(--cyan); } .sev.Informational { color: var(--text-2); }
      .prio { margin: 0; padding-left: 20px; display: flex; flex-direction: column; gap: 8px; }
      .prio li span { display: block; margin-top: 2px; }
      .metric-grid.three { grid-template-columns: repeat(3, minmax(0, 1fr)); }
      @media (max-width: 700px) { .metric-grid.three { grid-template-columns: minmax(0, 1fr); } }
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
  /** [AEGIS-KNIGHT-COVERAGE-01] Cobertura do catálogo de referência (propriedade do produto) — nula se não carregou. */
  readonly coverage = input<KnightReferenceCoverage | null>(null);
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
  readonly byPlatform = computed(() => distributionBy(this.assessment(), 'platform'));
  readonly maxPlatform = computed(() => Math.max(1, ...this.byPlatform().map((r) => r.total)));
  readonly measures = computed(() => threeMeasures(this.assessment(), this.coverage()));
  readonly priorities = computed(() => priorityControls(this.assessment()));
  readonly limitations = computed(() => limitationViews(this.assessment()));
  readonly maxSeverity = computed(() => Math.max(1, ...this.kpis().findingsBySeverity.map((s) => s.count)));
  readonly maxDomain = computed(() => Math.max(1, ...this.byDomain().map((r) => r.total)));
  readonly maxService = computed(() => Math.max(1, ...this.byService().map((r) => r.total)));

  /** Três unidades que não se confundem: controles, ocorrências (objeto × controle) e objetos únicos. */
  readonly unitsLine = computed(() => knightUnitsLine(this.summary()));

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

import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AgentStateService } from '../services/agent-state.service';
import { PostureExposureService } from '../services/posture-exposure.service';
import {
  PostureExposureItem,
  PostureExposureList,
  PostureExposureStateFilter,
} from '../models/posture-exposure.models';

/**
 * [AEGIS-MVP-POSTURE-02] Exposições de CONFIGURAÇÃO (postura) — o primeiro vertical cloud-first funcional.
 * Consome a superfície somente leitura `GET /api/v1/posture/exposures`: Secure Score real mais recente,
 * exposições abertas, agrupamento por categoria e a tabela priorizada (rank da fonte, depois maior gap).
 *
 * Honestidade do produto: fonte SEMPRE identificada ("Microsoft Secure Score"); ausência de dados vira
 * "Ainda não coletado" (NUNCA 0); estados loading/vazio/erro/retry explícitos; ZERO fallback demonstrativo.
 * NÃO são CVEs/vulnerabilidades de ativo — são "recomendações de postura".
 */
@Component({
  selector: 'app-posture-exposures',
  standalone: true,
  imports: [FormsModule],
  template: `
    <section class="page">
      <header class="page-head">
        <div>
          <h1>Exposições de configuração</h1>
          <p class="sub">
            Lacunas de postura detectadas por coleta real. Fonte:
            <strong>{{ sourceLabel() }}</strong>. São recomendações de postura — não vulnerabilidades ou CVEs
            de ativos.
          </p>
        </div>
        <div class="head-actions">
          <button type="button" class="primary" (click)="analyzeWithAi()" [disabled]="loading() || !!error()">
            Analisar exposições com IA
          </button>
          <button type="button" class="ghost" (click)="reload()" [disabled]="loading()">
            {{ loading() ? 'Carregando…' : 'Atualizar' }}
          </button>
        </div>
      </header>

      <!-- ---------- Resumo ---------- -->
      <div class="cards">
        <div class="card">
          <span class="card-label">Secure Score geral</span>
          @if (summary()?.latestSecureScorePercent != null) {
            <span class="card-value">{{ pct(summary()!.latestSecureScorePercent!) }}</span>
            <span class="card-meta">coletado {{ fmtDate(summary()?.latestSecureScoreAt) }}</span>
          } @else {
            <span class="card-value muted">Ainda não coletado</span>
            <span class="card-meta">configure em Integrações e clique em Coletar</span>
          }
        </div>
        <div class="card">
          <span class="card-label">Exposições abertas</span>
          <span class="card-value">{{ summary()?.totalOpen ?? '—' }}</span>
          <span class="card-meta">{{ summary()?.totalResolved ?? 0 }} resolvida(s)</span>
        </div>
        <div class="card">
          <span class="card-label">Última coleta</span>
          @if (summary()?.lastCollectedAt) {
            <span class="card-value sm">{{ fmtDate(summary()?.lastCollectedAt) }}</span>
          } @else {
            <span class="card-value sm muted">Ainda não coletado</span>
          }
        </div>
        <div class="card wide">
          <span class="card-label">Por categoria (abertas)</span>
          @if ((summary()?.openByCategory?.length ?? 0) > 0) {
            <div class="cats">
              @for (c of summary()!.openByCategory; track c.category) {
                <button
                  type="button"
                  class="cat-chip"
                  [class.active]="categoryFilter() === c.category"
                  (click)="toggleCategory(c.category)"
                >
                  {{ c.category }} <span class="cat-n">{{ c.open }}</span>
                </button>
              }
            </div>
          } @else {
            <span class="card-meta">Sem exposições abertas por categoria.</span>
          }
        </div>
      </div>

      <!-- ---------- Filtros ---------- -->
      <div class="filters">
        <div class="seg" role="tablist">
          @for (s of stateOptions; track s.value) {
            <button
              type="button"
              class="seg-btn"
              [class.active]="stateFilter() === s.value"
              (click)="setState(s.value)"
            >
              {{ s.label }}
            </button>
          }
        </div>
        <input
          type="search"
          class="search"
          placeholder="Buscar título, controle ou serviço…"
          [ngModel]="search()"
          (ngModelChange)="onSearch($event)"
        />
        @if (categoryFilter()) {
          <button type="button" class="ghost sm" (click)="toggleCategory(categoryFilter()!)">
            Categoria: {{ categoryFilter() }} ✕
          </button>
        }
      </div>

      <!-- ---------- Tabela ---------- -->
      <div class="panel">
        @if (loading()) {
          <p class="muted">Carregando exposições…</p>
        } @else if (error()) {
          <div class="state error">
            <p class="err">⚠ {{ error() }}</p>
            <button type="button" class="ghost" (click)="retry()">Tentar novamente</button>
          </div>
        } @else if (!hasEverCollected()) {
          <div class="state empty">
            <p class="muted">
              Ainda não coletado. Configure o <strong>{{ sourceLabel() }}</strong> em
              <strong>Configurações → Integrações</strong> e use <strong>Coletar</strong> para atualizar score e
              exposições.
            </p>
          </div>
        } @else if (items().length === 0) {
          <div class="state empty">
            <p class="muted">Nenhuma exposição para o filtro atual.</p>
          </div>
        } @else {
          <table class="grid-table">
            <thead>
              <tr>
                <th class="c-rank">Rank</th>
                <th>Recomendação</th>
                <th class="c-score">Score</th>
                <th class="c-gap">Gap</th>
                <th class="c-tier">Tier</th>
                <th class="c-exp" aria-label="Detalhes"></th>
              </tr>
            </thead>
            <tbody>
              @for (x of items(); track x.id) {
                <tr class="row" [class.resolved]="x.lifecycleState === 'Resolved'">
                  <td class="c-rank">{{ x.sourceRank ?? '—' }}</td>
                  <td>
                    <strong class="title">{{ x.title }}</strong>
                    <span class="meta">
                      {{ x.service || '—' }} · {{ x.category || '—' }}
                      @if (x.lifecycleState === 'Resolved') {
                        <span class="badge ok">Resolvida</span>
                      }
                      @if (x.sourceState && x.sourceState !== 'Default') {
                        <span class="badge src" title="Estado informado pela fonte (metadado)">{{ x.sourceState }}</span>
                      }
                    </span>
                  </td>
                  <td class="c-score">{{ num(x.currentScore) }}/{{ num(x.maxScore) }}</td>
                  <td class="c-gap"><span class="gap">{{ num(x.gap) }}</span></td>
                  <td class="c-tier">{{ x.tier || '—' }}</td>
                  <td class="c-exp">
                    <button type="button" class="linkbtn" (click)="toggleExpand(x.id)">
                      {{ expanded().has(x.id) ? 'Ocultar' : 'Detalhes' }}
                    </button>
                  </td>
                </tr>
                @if (expanded().has(x.id)) {
                  <tr class="details-row">
                    <td colspan="6">
                      <div class="details">
                        <div class="det">
                          <span class="det-label">Remediação</span>
                          <p>{{ x.remediation || 'Sem detalhe de remediação fornecido pela fonte.' }}</p>
                        </div>
                        @if (x.remediationImpact) {
                          <div class="det">
                            <span class="det-label">Impacto da remediação</span>
                            <p>{{ x.remediationImpact }}</p>
                          </div>
                        }
                        <div class="det-grid">
                          <div><span class="det-label">Custo</span><span>{{ x.implementationCost || '—' }}</span></div>
                          <div><span class="det-label">Impacto ao usuário</span><span>{{ x.userImpact || '—' }}</span></div>
                          <div><span class="det-label">Tipo de ação</span><span>{{ x.actionType || '—' }}</span></div>
                          <div><span class="det-label">Controle (fonte)</span><span class="mono">{{ x.externalId }}</span></div>
                        </div>
                        @if (x.threats.length > 0) {
                          <div class="det">
                            <span class="det-label">Ameaças</span>
                            <div class="threats">
                              @for (t of x.threats; track t) {
                                <span class="threat">{{ t }}</span>
                              }
                            </div>
                          </div>
                        }
                        <p class="seen">
                          Vista de {{ fmtDate(x.firstSeenAt) }} até {{ fmtDate(x.lastSeenAt) }}
                          @if (x.resolvedAt) {
                            · resolvida em {{ fmtDate(x.resolvedAt) }}
                          }
                        </p>
                      </div>
                    </td>
                  </tr>
                }
              }
            </tbody>
          </table>

          <footer class="pager">
            <button type="button" class="ghost sm" (click)="prevPage()" [disabled]="page() <= 1">← Anterior</button>
            <span class="pg-info">Página {{ page() }} de {{ pageCount() }} · {{ total() }} no total</span>
            <button type="button" class="ghost sm" (click)="nextPage()" [disabled]="page() >= pageCount()">
              Próxima →
            </button>
          </footer>
        }
      </div>
    </section>
  `,
  styles: [
    `
      .page {
        padding: 1.25rem 1.5rem 2rem;
        display: flex;
        flex-direction: column;
        gap: 1.1rem;
      }
      .page-head {
        display: flex;
        justify-content: space-between;
        align-items: flex-start;
        gap: 1rem;
        flex-wrap: wrap;
      }
      h1 {
        margin: 0;
        font-size: 1.35rem;
        letter-spacing: 0.02em;
      }
      .sub {
        margin: 0.35rem 0 0;
        max-width: 70ch;
        opacity: 0.72;
        font-size: 0.85rem;
      }
      .head-actions {
        display: flex;
        gap: 0.5rem;
        flex-wrap: wrap;
      }
      .muted {
        opacity: 0.65;
        font-size: 0.85rem;
      }
      .err {
        color: #ff6b8a;
        font-size: 0.85rem;
      }

      /* ---- cards ---- */
      .cards {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(12rem, 1fr));
        gap: 0.75rem;
      }
      .card {
        background: color-mix(in srgb, var(--hud-cyan, #26e0ff) 4%, transparent);
        border: 1px solid color-mix(in srgb, var(--hud-cyan, #26e0ff) 22%, transparent);
        border-radius: 8px;
        padding: 0.8rem 0.95rem;
        display: flex;
        flex-direction: column;
        gap: 0.2rem;
      }
      .card.wide {
        grid-column: span 2;
        min-width: 0;
      }
      .card-label {
        font-size: 0.66rem;
        text-transform: uppercase;
        letter-spacing: 0.1em;
        opacity: 0.6;
      }
      .card-value {
        font-size: 1.6rem;
        font-weight: 600;
        letter-spacing: 0.01em;
      }
      .card-value.sm {
        font-size: 1rem;
      }
      .card-value.muted {
        font-size: 1rem;
        opacity: 0.7;
      }
      .card-meta {
        font-size: 0.72rem;
        opacity: 0.6;
      }
      .cats {
        display: flex;
        flex-wrap: wrap;
        gap: 0.35rem;
        margin-top: 0.25rem;
      }
      .cat-chip {
        background: transparent;
        border: 1px solid color-mix(in srgb, var(--hud-cyan, #26e0ff) 30%, transparent);
        color: inherit;
        border-radius: 999px;
        padding: 0.2rem 0.6rem;
        font: inherit;
        font-size: 0.75rem;
        cursor: pointer;
      }
      .cat-chip.active {
        background: color-mix(in srgb, var(--hud-cyan, #26e0ff) 18%, transparent);
        border-color: var(--hud-cyan, #26e0ff);
      }
      .cat-n {
        opacity: 0.7;
        margin-left: 0.2rem;
      }

      /* ---- filtros ---- */
      .filters {
        display: flex;
        align-items: center;
        gap: 0.6rem;
        flex-wrap: wrap;
      }
      .seg {
        display: inline-flex;
        border: 1px solid color-mix(in srgb, var(--hud-cyan, #26e0ff) 26%, transparent);
        border-radius: 6px;
        overflow: hidden;
      }
      .seg-btn {
        background: transparent;
        border: 0;
        color: inherit;
        font: inherit;
        font-size: 0.8rem;
        padding: 0.35rem 0.8rem;
        cursor: pointer;
      }
      .seg-btn.active {
        background: color-mix(in srgb, var(--hud-cyan, #26e0ff) 20%, transparent);
      }
      .search {
        flex: 1;
        min-width: 12rem;
        background: rgba(4, 8, 18, 0.6);
        border: 1px solid color-mix(in srgb, var(--hud-cyan, #26e0ff) 26%, transparent);
        border-radius: 5px;
        padding: 0.4rem 0.6rem;
        color: inherit;
        font: inherit;
        font-size: 0.85rem;
      }

      /* ---- painel/tabela ---- */
      .panel {
        background: color-mix(in srgb, var(--hud-cyan, #26e0ff) 4%, transparent);
        border: 1px solid color-mix(in srgb, var(--hud-cyan, #26e0ff) 22%, transparent);
        border-radius: 8px;
        padding: 0.6rem;
        overflow-x: auto;
      }
      .state {
        padding: 1.5rem 1rem;
        text-align: center;
        display: flex;
        flex-direction: column;
        gap: 0.75rem;
        align-items: center;
      }
      .grid-table {
        width: 100%;
        border-collapse: collapse;
        font-size: 0.85rem;
      }
      .grid-table th {
        text-align: left;
        font-size: 0.66rem;
        text-transform: uppercase;
        letter-spacing: 0.08em;
        opacity: 0.6;
        padding: 0.4rem 0.6rem;
        border-bottom: 1px solid color-mix(in srgb, var(--hud-cyan, #26e0ff) 18%, transparent);
      }
      .grid-table td {
        padding: 0.5rem 0.6rem;
        vertical-align: top;
        border-bottom: 1px solid color-mix(in srgb, var(--hud-cyan, #26e0ff) 8%, transparent);
      }
      .row.resolved {
        opacity: 0.62;
      }
      .title {
        display: block;
        line-height: 1.3;
      }
      .meta {
        font-size: 0.72rem;
        opacity: 0.62;
        display: inline-flex;
        gap: 0.4rem;
        align-items: center;
        flex-wrap: wrap;
      }
      .c-rank {
        width: 3.5rem;
        opacity: 0.85;
      }
      .c-score,
      .c-gap,
      .c-tier {
        white-space: nowrap;
      }
      .gap {
        color: #f5a524;
        font-weight: 600;
      }
      .c-exp {
        text-align: right;
        white-space: nowrap;
      }
      .badge {
        font-size: 0.62rem;
        padding: 0.05rem 0.4rem;
        border-radius: 3px;
        border: 1px solid currentColor;
        text-transform: uppercase;
        letter-spacing: 0.05em;
      }
      .badge.ok {
        color: var(--hud-cyan, #26e0ff);
      }
      .badge.src {
        color: #f5a524;
      }
      .linkbtn {
        background: transparent;
        border: 0;
        color: var(--hud-cyan, #26e0ff);
        font: inherit;
        font-size: 0.78rem;
        cursor: pointer;
      }
      .details-row td {
        background: rgba(4, 8, 18, 0.35);
      }
      .details {
        display: flex;
        flex-direction: column;
        gap: 0.6rem;
        padding: 0.3rem 0.2rem;
      }
      .det p {
        margin: 0.15rem 0 0;
        font-size: 0.82rem;
        line-height: 1.4;
        opacity: 0.9;
        max-width: 90ch;
      }
      .det-label {
        font-size: 0.64rem;
        text-transform: uppercase;
        letter-spacing: 0.08em;
        opacity: 0.55;
      }
      .det-grid {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(11rem, 1fr));
        gap: 0.5rem;
      }
      .det-grid > div {
        display: flex;
        flex-direction: column;
      }
      .mono {
        font-family: ui-monospace, monospace;
        font-size: 0.76rem;
        opacity: 0.85;
      }
      .threats {
        display: flex;
        flex-wrap: wrap;
        gap: 0.3rem;
        margin-top: 0.2rem;
      }
      .threat {
        font-size: 0.72rem;
        padding: 0.1rem 0.45rem;
        border-radius: 999px;
        background: color-mix(in srgb, #ff6b8a 12%, transparent);
        border: 1px solid color-mix(in srgb, #ff6b8a 30%, transparent);
      }
      .seen {
        font-size: 0.72rem;
        opacity: 0.55;
        margin: 0.1rem 0 0;
      }
      .pager {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 0.75rem;
        padding: 0.6rem 0.4rem 0.2rem;
      }
      .pg-info {
        font-size: 0.75rem;
        opacity: 0.65;
      }

      /* ---- botões ---- */
      button.primary {
        background: color-mix(in srgb, var(--hud-cyan, #26e0ff) 18%, transparent);
        border: 1px solid var(--hud-cyan, #26e0ff);
        color: inherit;
        border-radius: 5px;
        padding: 0.45rem 1rem;
        font: inherit;
        font-size: 0.82rem;
        cursor: pointer;
      }
      button.ghost {
        background: transparent;
        border: 1px solid color-mix(in srgb, var(--hud-cyan, #26e0ff) 30%, transparent);
        color: inherit;
        border-radius: 5px;
        padding: 0.4rem 0.8rem;
        font: inherit;
        font-size: 0.8rem;
        cursor: pointer;
      }
      button.sm {
        padding: 0.25rem 0.6rem;
        font-size: 0.74rem;
      }
      button:disabled {
        opacity: 0.5;
        cursor: not-allowed;
      }
    `,
  ],
})
export class PostureExposuresComponent {
  private readonly api = inject(PostureExposureService);
  private readonly agent = inject(AgentStateService);

  protected readonly stateOptions: { value: PostureExposureStateFilter; label: string }[] = [
    { value: 'open', label: 'Abertas' },
    { value: 'resolved', label: 'Resolvidas' },
    { value: 'all', label: 'Todas' },
  ];

  protected readonly data = signal<PostureExposureList | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly stateFilter = signal<PostureExposureStateFilter>('open');
  protected readonly categoryFilter = signal<string | null>(null);
  protected readonly search = signal('');
  protected readonly page = signal(1);
  protected readonly expanded = signal<Set<string>>(new Set());

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  protected readonly summary = computed(() => this.data()?.summary ?? null);
  protected readonly items = computed<PostureExposureItem[]>(() => this.data()?.items ?? []);
  protected readonly total = computed(() => this.data()?.total ?? 0);
  protected readonly pageCount = computed(() => {
    const d = this.data();
    if (!d || d.pageSize <= 0) return 1;
    return Math.max(1, Math.ceil(d.total / d.pageSize));
  });
  protected readonly sourceLabel = computed(() => this.summary()?.sourceLabel ?? 'Microsoft Secure Score');

  /**
   * "Já coletou alguma vez?" — separa o estado de onboarding (nunca coletado → "Ainda não coletado") do
   * estado "coletado mas sem exposição para o filtro". Deriva do resumo, que reflete o tenant inteiro.
   */
  protected readonly hasEverCollected = computed(() => {
    const s = this.summary();
    return !!s && (s.lastCollectedAt != null || s.totalOpen > 0 || s.totalResolved > 0);
  });

  constructor() {
    this.load();
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api
      .list({
        state: this.stateFilter(),
        category: this.categoryFilter() ?? undefined,
        search: this.search().trim() || undefined,
        page: this.page(),
        pageSize: 25,
      })
      .subscribe({
        next: (list) => {
          this.data.set(list);
          this.loading.set(false);
        },
        error: (err: Error) => {
          this.data.set(null);
          this.error.set(err.message);
          this.loading.set(false);
        },
      });
  }

  protected reload(): void {
    this.load();
  }

  protected retry(): void {
    this.load();
  }

  protected setState(s: PostureExposureStateFilter): void {
    if (this.stateFilter() === s) return;
    this.stateFilter.set(s);
    this.page.set(1);
    this.load();
  }

  protected toggleCategory(cat: string): void {
    this.categoryFilter.update((c) => (c === cat ? null : cat));
    this.page.set(1);
    this.load();
  }

  protected onSearch(value: string): void {
    this.search.set(value);
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => {
      this.page.set(1);
      this.load();
    }, 350);
  }

  protected toggleExpand(id: string): void {
    this.expanded.update((set) => {
      const next = new Set(set);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  protected prevPage(): void {
    if (this.page() <= 1) return;
    this.page.update((p) => p - 1);
    this.load();
  }

  protected nextPage(): void {
    if (this.page() >= this.pageCount()) return;
    this.page.update((p) => p + 1);
    this.load();
  }

  /**
   * Reutiliza o Auditor Virtual GLOBAL, semeando uma pergunta contextual. O backend já inclui as principais
   * exposições abertas no contexto tenant-scoped (máx. 8) e sabe que rank/gap/score/estado são AUTORITATIVOS e
   * a resposta é CONSULTIVA. A IA não abre/fecha/altera finding — só explica, correlaciona e prioriza.
   */
  protected analyzeWithAi(): void {
    this.agent.requestAudit(
      'Analise as exposições de configuração abertas do Microsoft Secure Score: explique o impacto das ' +
        'principais lacunas, correlacione-as com as lacunas do NIST CSF e a postura atual, e sugira uma ' +
        'sequência de remediação priorizada. Deixe claro o que é fato da fonte, inferência e recomendação.',
    );
  }

  protected pct(n: number): string {
    return `${Math.round(n)}%`;
  }

  protected num(n: number): string {
    return Number.isInteger(n) ? String(n) : n.toFixed(1);
  }

  protected fmtDate(iso: string | null | undefined): string {
    if (!iso) return '—';
    const d = new Date(iso);
    return isNaN(d.getTime()) ? '—' : d.toLocaleString('pt-BR');
  }
}

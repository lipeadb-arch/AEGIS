import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AgentStateService } from '../services/agent-state.service';
import { PostureExposureService } from '../services/posture-exposure.service';
import {
  EXPOSURE_REACH_UNKNOWN,
  PostureExposureItem,
  PostureExposureList,
  PostureExposureStateFilter,
  RECOMMENDATION_NO_LONGER_PENDING_HINT,
  actionTypePt,
  categoryPt,
  impactPt,
  recommendationLifecyclePt,
  recommendationReading,
  sourceStatePt,
  tierPt,
} from '../models/posture-exposure.models';

/**
 * [AEGIS-MVP-POSTURE-02] RECOMENDAÇÕES DE POSTURA — fonte: Microsoft Secure Score. A rota (`/exposures`), o
 * componente e os contratos `PostureExposure*` mantêm o nome técnico; a apresentação não fala mais em
 * "exposições de configuração" porque a diferença de pontos da fonte não comprova, sozinha, exposição.
 * Consome `GET /api/v1/posture/exposures`: índice da fonte mais recente, recomendações pendentes,
 * agrupamento por categoria e a tabela na ordem da fonte (rank, depois maior diferença de pontos).
 *
 * [AEGIS-LANGUAGE-STATES-01] Estados: sem integração · sem coleta · primeira tentativa falhou · leitura
 * disponível (com aviso quando a tentativa mais recente falhou) · filtro sem correspondência · zero apurado.
 * Ausência de dados NUNCA vira 0; ZERO fallback demonstrativo.
 */
@Component({
  selector: 'app-posture-exposures',
  standalone: true,
  imports: [FormsModule],
  template: `
    <section class="page">
      <header class="page-head">
        <div>
          <p class="page-eyebrow">Ambiente</p>
          <h1>Recomendações de postura</h1>
          <p class="page-desc">
            Recomendações de configuração coletadas do <strong>{{ sourceLabel() }}</strong>. Cada item mostra a
            diferença entre os pontos obtidos e o máximo da recomendação, segundo a fonte — isso, sozinho, não
            comprova configuração insegura, exposição de ativo ou vulnerabilidade. O índice da fonte não é o
            AEGIS Score.
          </p>
        </div>
        <div class="page-actions">
          <!-- Sem leitura, uma análise pressuporia evidência que não existe: o botão só vale com dados. -->
          <button
            type="button"
            class="primary"
            (click)="analyzeWithAi()"
            [disabled]="loading() || !!error() || !reading().hasData"
            [title]="reading().hasData ? '' : 'Disponível depois da primeira coleta da fonte'"
          >
            Analisar recomendações com IA
          </button>
          <button type="button" class="ghost" (click)="reload()" [disabled]="loading()">
            {{ loading() ? 'Carregando…' : 'Atualizar' }}
          </button>
        </div>
      </header>

      @if (!loading() && !error() && reading().hasData && reading().notice) {
        <p class="notice warn" role="status">{{ reading().notice }}</p>
      }

      <!-- ---------- Resumo ---------- -->
      <!-- [AEGIS-LANGUAGE-STATES-01] Sem leitura as contagens ficam "—" (nunca 0) e o motivo é dito pelo estado. -->
      <div class="cards">
        <div class="card">
          <span class="metric-label">Microsoft Secure Score · índice da fonte</span>
          @if (summary()?.latestSecureScorePercent != null) {
            <span class="metric-value">{{ pct(summary()!.latestSecureScorePercent!) }}</span>
            <span class="metric-unit">coletado {{ fmtDate(summary()?.latestSecureScoreAt) }} · não é o AEGIS Score</span>
          } @else if (reading().hasData) {
            <span class="metric-value is-na">Não informado</span>
            <span class="metric-unit">a fonte não entregou o índice geral nesta leitura</span>
          } @else {
            <span class="metric-value is-na">{{ readingLabel() }}</span>
            <span class="metric-unit">sem leitura da fonte</span>
          }
        </div>
        <div class="card">
          <span class="metric-label">Recomendações pendentes</span>
          @if (reading().hasData) {
            <span class="metric-value">{{ summary()!.totalOpen }}</span>
            <span class="metric-unit" [title]="noLongerPendingHint">
              {{ summary()!.totalResolved }} sem pendência na fonte
            </span>
          } @else {
            <span class="metric-value is-na">—</span>
            <span class="metric-unit">{{ readingLabel() }}</span>
          }
        </div>
        <div class="card">
          <span class="metric-label">Última coleta</span>
          @if (summary()?.lastCollectedAt) {
            <span class="metric-value sm">{{ fmtDate(summary()?.lastCollectedAt) }}</span>
            @if (reading().lastAttemptFailed) {
              <span class="metric-unit warn-text">tentativa mais recente falhou</span>
            } @else if (reading().lastAttemptDegraded) {
              <span class="metric-unit warn-text">coleta mais recente com restrições</span>
            }
          } @else {
            <span class="metric-value sm is-na">{{ readingLabel() }}</span>
          }
        </div>
        <div class="card wide">
          <span class="metric-label">Pendentes por categoria</span>
          @if ((summary()?.openByCategory?.length ?? 0) > 0) {
            <div class="cats">
              @for (c of summary()!.openByCategory; track c.category) {
                <button
                  type="button"
                  class="filter-chip"
                  [class.active]="categoryFilter() === c.category"
                  [attr.aria-pressed]="categoryFilter() === c.category"
                  (click)="toggleCategory(c.category)"
                >
                  {{ cat(c.category) }} <span class="cat-n">{{ c.open }}</span>
                </button>
              }
            </div>
          } @else if (reading().hasData) {
            <span class="metric-unit">Nenhuma recomendação pendente na última leitura.</span>
          } @else {
            <span class="metric-unit">Sem leitura da fonte.</span>
          }
        </div>
      </div>

      <!-- ---------- Filtros ---------- -->
      <div class="filter-bar">
        <div class="filter-row">
        <span class="filter-label">Situação</span>
        <div class="segmented" role="group" aria-label="Situação">
          @for (s of stateOptions; track s.value) {
            <button
              type="button"
              [class.active]="stateFilter() === s.value"
              [attr.aria-pressed]="stateFilter() === s.value"
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
          aria-label="Buscar recomendações por título, controle ou serviço"
          [ngModel]="search()"
          (ngModelChange)="onSearch($event)"
        />
        @if (categoryFilter()) {
          <button type="button" class="ghost sm" (click)="toggleCategory(categoryFilter()!)">
            Categoria: {{ cat(categoryFilter()) }} ✕
          </button>
        }
        </div>
      </div>

      <!-- ---------- Tabela ---------- -->
      <div class="panel flush">
        @if (loading()) {
          <div class="state" role="status"><span class="spinner" aria-hidden="true"></span><p>Carregando recomendações…</p></div>
        } @else if (error()) {
          <!-- Falha ao LER o AEGIS: nada é exibido como se fosse a leitura atual; a tentativa pode ser refeita. -->
          <div class="state error">
            <p class="err">⚠ Não foi possível carregar as recomendações agora. {{ error() }}</p>
            <button type="button" class="ghost" (click)="retry()">Tentar novamente</button>
          </div>
        } @else if (!reading().hasData) {
          <div class="state empty">
            <p class="muted">{{ reading().notice }}</p>
            <p class="muted">
              @if (reading().state === 'NotConfigured') {
                Um administrador do ambiente pode configurar o <strong>{{ sourceLabel() }}</strong> em
                <strong>Configurações → Integrações</strong> e usar <strong>Coletar</strong>.
              } @else {
                Confira a integração em <strong>Configurações → Integrações</strong> e use <strong>Coletar</strong>.
              }
            </p>
          </div>
        } @else if (items().length === 0) {
          <div class="state empty">
            @if (filtersActive()) {
              <p class="muted">Nenhuma recomendação corresponde aos filtros atuais.</p>
            } @else {
              <p class="muted">
                Nenhuma recomendação pendente na última coleta da fonte. Isso reflete a pontuação da fonte, não uma
                validação independente das configurações.
              </p>
            }
          </div>
        } @else {
          <table class="data-table">
            <thead>
              <tr>
                <th class="c-rank" title="Ordem sugerida pela própria fonte">Ordem (fonte)</th>
                <th>Recomendação</th>
                <th class="c-score" title="Pontos obtidos / máximo da recomendação, segundo a fonte">Pontos (fonte)</th>
                <th class="c-gap" title="Pontos que a fonte ainda não credita nesta recomendação">Diferença</th>
                <th class="c-tier">Nível (fonte)</th>
                <th class="c-exp" aria-label="Detalhes"></th>
              </tr>
            </thead>
            <tbody>
              @for (x of items(); track x.id) {
                <tr class="row" [class.resolved]="x.lifecycleState === 'Resolved'">
                  <td class="c-rank">{{ x.sourceRank ?? '—' }}</td>
                  <td>
                    <strong class="title">{{ x.displayTitle }}</strong>
                    <span class="meta">
                      {{ x.service || '—' }} · {{ cat(x.category) || '—' }} · {{ reachUnknown }}
                      @if (x.lifecycleState === 'Resolved') {
                        <span class="badge ok" [title]="noLongerPendingHint">{{ lifecycle(x.lifecycleState) }}</span>
                      }
                      @if (x.languageCoverage === 'SourceOnly') {
                        <span
                          class="badge gen"
                          title="Descrição genérica em português desta recomendação; o texto original da fonte (sanitizado) está nos detalhes."
                          >Descrição da fonte</span
                        >
                      }
                      @if (sourceState(x.sourceState); as st) {
                        <span class="badge src" title="Estado declarado na fonte (metadado do Secure Score, não do AEGIS)">{{ st }}</span>
                      }
                    </span>
                    @if (x.whyItMatters) {
                      <span class="meta why">{{ x.whyItMatters }}</span>
                    }
                    @if (x.firstAction) {
                      <span class="meta act"><em>Ação:</em> {{ x.firstAction }}</span>
                    }
                  </td>
                  <td class="c-score">{{ num(x.currentScore) }}/{{ num(x.maxScore) }}</td>
                  <td class="c-gap"><span class="gap">{{ num(x.gap) }}</span></td>
                  <td class="c-tier">{{ tier(x.tier) || '—' }}</td>
                  <td class="c-exp">
                    <button type="button" class="linkbtn" (click)="toggleExpand(x.id)" [attr.aria-expanded]="expanded().has(x.id)">
                      {{ expanded().has(x.id) ? 'Ocultar' : 'Detalhes' }}
                    </button>
                  </td>
                </tr>
                @if (expanded().has(x.id)) {
                  <tr class="details-row">
                    <td colspan="6">
                      <div class="details">
                        @if (x.plainSummary) {
                          <div class="det"><span class="det-label">O que a recomendação pede</span><p>{{ x.plainSummary }}</p></div>
                        }
                        @if (x.whyItMatters) {
                          <div class="det"><span class="det-label">Por que importa</span><p>{{ x.whyItMatters }}</p></div>
                        }
                        @if (x.firstAction) {
                          <div class="det"><span class="det-label">Primeira ação</span><p>{{ x.firstAction }}</p></div>
                        }
                        <div class="det">
                          <span class="det-label">Remediação (texto da fonte)</span>
                          <p>{{ x.sourceRemediation || 'Sem detalhe de remediação fornecido pela fonte.' }}</p>
                        </div>
                        @if (x.sourceRemediationImpact) {
                          <div class="det">
                            <span class="det-label">Impacto da remediação (fonte)</span>
                            <p>{{ x.sourceRemediationImpact }}</p>
                          </div>
                        }
                        <div class="det-grid">
                          <div><span class="det-label">Custo de implementação (fonte)</span><span>{{ impact(x.implementationCost) || '—' }}</span></div>
                          <div><span class="det-label">Impacto ao usuário (fonte)</span><span>{{ impact(x.userImpact) || '—' }}</span></div>
                          <div><span class="det-label">Tipo de ação</span><span>{{ actionType(x.actionType) || '—' }}</span></div>
                          <div><span class="det-label">Identificador (fonte)</span><span class="mono">{{ x.externalId }}</span></div>
                        </div>
                        @if (x.sourceTitle && x.sourceTitle !== x.displayTitle) {
                          <div class="det"><span class="det-label">Título original (fonte)</span><p class="meta">{{ x.sourceTitle }}</p></div>
                        }
                        @if (x.threats.length > 0) {
                          <!-- Ameaças que a recomendação PRETENDE mitigar, segundo a fonte — não ameaças observadas. -->
                          <div class="det">
                            <span class="det-label">Ameaças que a recomendação visa mitigar (segundo a fonte)</span>
                            <div class="threats">
                              @for (t of x.threats; track t) {
                                <span class="threat">{{ t }}</span>
                              }
                            </div>
                            <p class="meta">Não são ameaças observadas neste ambiente.</p>
                          </div>
                        }
                        <p class="seen">
                          Pendente na fonte desde {{ fmtDate(x.firstSeenAt) }} · última leitura como pendente em
                          {{ fmtDate(x.lastSeenAt) }}
                          @if (x.resolvedAt) {
                            · deixou de constar como pendente em {{ fmtDate(x.resolvedAt) }}
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
            <span class="range">Página {{ page() }} de {{ pageCount() }} · {{ total() }} no total</span>
            <button type="button" class="ghost sm" (click)="prevPage()" [disabled]="page() <= 1">← Anterior</button>
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
      /* Página, cabeçalho, cartões, filtros, tabela, badges, avisos, estados e botões: sistema visual global (styles.css). */
      .card.wide {
        grid-column: span 2;
      }
      .cats {
        display: flex;
        flex-wrap: wrap;
        gap: 6px;
        margin-top: var(--sp-2);
      }
      .cat-n {
        margin-left: 2px;
        font-weight: 600;
        color: var(--muted);
      }
      .search {
        flex: 1 1 18rem;
        min-width: 0;
      }
      .data-table .meta {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 4px var(--sp-2);
      }
      .meta.why,
      .meta.act {
        display: block;
        margin-top: 3px;
        color: var(--text-2);
      }
      .meta.act em {
        font-style: normal;
        color: var(--muted);
      }
      .row.resolved {
        opacity: 0.62;
      }
      .c-rank {
        width: 4rem;
        color: var(--text-2);
      }
      .c-score,
      .c-gap,
      .c-tier {
        white-space: nowrap;
      }
      .gap {
        font-weight: 600;
        color: var(--amber);
      }
      .c-exp {
        text-align: right;
        white-space: nowrap;
      }
      .badge.src {
        color: var(--amber);
      }
      .details-row td {
        background: rgba(5, 7, 15, 0.45);
      }
      .details {
        display: flex;
        flex-direction: column;
        gap: var(--sp-3);
        padding: var(--sp-1) 2px;
      }
      .det p {
        max-width: 90ch;
        margin-top: 2px;
        font-size: var(--fs-sm);
        line-height: var(--lh);
        color: var(--text-2);
      }
      .det-label {
        display: block;
        margin-bottom: 2px;
        font-size: var(--fs-caps);
        font-weight: 600;
        letter-spacing: var(--tracking-caps);
        text-transform: uppercase;
        color: var(--muted);
      }
      .det-grid {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(11rem, 1fr));
        gap: var(--sp-3);
      }
      .det-grid > div {
        display: flex;
        flex-direction: column;
      }
      .threats {
        display: flex;
        flex-wrap: wrap;
        gap: 6px;
        margin-top: var(--sp-1);
      }
      .threat {
        padding: 2px var(--sp-2);
        border: 1px solid rgba(255, 61, 154, 0.35);
        border-radius: var(--radius-pill);
        background: var(--tint-magenta);
        color: #ffb3d4;
        font-size: var(--fs-meta);
      }
      .seen {
        font-size: var(--fs-meta);
        color: var(--muted);
      }
      @media (max-width: 720px) {
        .card.wide {
          grid-column: auto;
        }
      }
    `,
  ],
})
export class PostureExposuresComponent {
  private readonly api = inject(PostureExposureService);
  private readonly agent = inject(AgentStateService);

  // [AEGIS-LANGUAGE-STATES-01] "Resolved" do coletor = a fonte deixou de apontar diferença de pontos numa coleta
  // completa. Não é correção validada — por isso "Sem pendência na fonte", e não "Resolvidas".
  protected readonly stateOptions: { value: PostureExposureStateFilter; label: string }[] = [
    { value: 'open', label: 'Pendentes' },
    { value: 'resolved', label: 'Sem pendência na fonte' },
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

  // [AEGIS-MVP-LANGUAGE-02] Vocabulário visível traduzido (funções puras dos models) — exposto ao template.
  protected readonly cat = categoryPt;
  protected readonly tier = tierPt;
  protected readonly impact = impactPt;
  protected readonly actionType = actionTypePt;
  protected readonly reachUnknown = EXPOSURE_REACH_UNKNOWN;
  protected readonly total = computed(() => this.data()?.total ?? 0);
  protected readonly pageCount = computed(() => {
    const d = this.data();
    if (!d || d.pageSize <= 0) return 1;
    return Math.max(1, Math.ceil(d.total / d.pageSize));
  });
  protected readonly sourceLabel = computed(() => this.summary()?.sourceLabel ?? 'Microsoft Secure Score');

  /**
   * [AEGIS-LANGUAGE-STATES-01] O que a tela pode AFIRMAR sobre a leitura: sem integração × sem coleta × primeira
   * tentativa falhou × leitura disponível (com aviso quando a tentativa mais recente falhou). Deriva do resumo,
   * que reflete o tenant inteiro — nunca da página filtrada.
   */
  protected readonly reading = computed(() => recommendationReading(this.summary()));

  /** Rótulo curto do estado sem leitura, para os cartões. */
  protected readonly readingLabel = computed(() => {
    switch (this.reading().state) {
      case 'NotConfigured':
        return 'Sem integração';
      case 'FailedBeforeFirstCollection':
        return 'Coleta falhou';
      default:
        return 'Ainda não coletado';
    }
  });

  /** Algum filtro além do padrão? Decide entre "nada no filtro" e "nada pendente na fonte". */
  protected readonly filtersActive = computed(
    () => this.stateFilter() !== 'open' || !!this.categoryFilter() || this.search().trim() !== '',
  );

  protected readonly lifecycle = recommendationLifecyclePt;
  protected readonly sourceState = sourceStatePt;
  protected readonly noLongerPendingHint = RECOMMENDATION_NO_LONGER_PENDING_HINT;

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
   * recomendações pendentes no contexto tenant-scoped (máx. 8), com o estado de leitura da fonte, e sabe que rank/gap/score/estado são AUTORITATIVOS e
   * a resposta é CONSULTIVA. A IA não abre/fecha/altera finding — só explica, correlaciona e prioriza.
   */
  protected analyzeWithAi(): void {
    this.agent.requestAudit(
      'Analise as recomendações de postura pendentes do Microsoft Secure Score: explique por que as principais ' +
        'costumam importar, relacione-as com as lacunas dos controles NIST CSF avaliados pelo AEGIS e sugira uma ' +
        'sequência de revisão. Trate a diferença de pontos como informação da fonte — não como configuração ' +
        'insegura, exposição de ativo ou vulnerabilidade confirmada — e as ameaças listadas como as que a ' +
        'recomendação visa mitigar, não como ameaças observadas. Separe fato da fonte, inferência e recomendação.',
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

import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AgentStateService } from '../services/agent-state.service';
import { VulnerabilityService } from '../services/vulnerability.service';
import { SoftwareInventoryTabComponent } from './software-inventory-tab.component';
import {
  VulnerabilityExploitFilter,
  VulnerabilityGroup,
  VulnerabilityItem,
  VulnerabilityLifecycleFilter,
  VulnerabilityOverview,
  VULNERABILITY_NO_LONGER_REPORTED_HINT,
  severityPt,
  vulnerabilityLifecyclePt,
  vulnerabilityReading,
} from '../models/vulnerability.models';

/** [AEGIS-MVP-MICROSOFT-COVERAGE-01] Sub-área desta MESMA tela — sem novo item de primeiro nível no menu lateral. */
type VulnTab = 'vulnerabilities' | 'software';

/**
 * [AEGIS-MVP-LANGUAGE-02 §8/§9] Estado de PAGINAÇÃO por CVE das ocorrências (ativo×CVE) na expansão de um grupo.
 * `error` NUNCA é substituído por lista vazia: uma falha preserva os itens já carregados e oferece "Tentar novamente".
 */
interface OccState {
  items: VulnerabilityItem[];
  total: number;
  loadedPages: number;
  loading: boolean;
  error: string | null;
}

/**
 * [AEGIS-MVP-VULN-01] Vulnerabilidades (exposição ativo×CVE) — MULTICLOUD. Consome a superfície somente
 * leitura `GET /api/v1/vulnerabilities`: exposições consolidadas, fontes observadoras, ordenação determinística
 * (exploit verificado → público → CVSS → EPSS → criticidade). As fontes (Microsoft, Google…) aparecem apenas
 * como FONTE/integrador observadora — sem lógica específica de provedor espalhada na tela.
 *
 * Honestidade do produto: "Ainda não coletado" (nenhum scanner sincronizou) é distinto de "coletado sem
 * achados"; estados loading/vazio/erro/retry explícitos; ZERO fallback demonstrativo. NÃO se mistura com
 * /exposures (exposições de configuração). A IA é consultiva — não cria CVE, observação, lifecycle ou score.
 */
@Component({
  selector: 'app-vulnerabilities',
  standalone: true,
  imports: [FormsModule, SoftwareInventoryTabComponent],
  template: `
    <section class="page">
      <header class="page-head">
        <div>
          <p class="page-eyebrow">Ambiente</p>
          <h1>Vulnerabilidades</h1>
          <p class="page-desc">
            @if (tab() === 'software') {
              Inventário de software observado nos ativos pelas fontes conectadas (hoje, Microsoft Defender). Vulnerabilidades
              conhecidas, exploit público e alerta associado são informações da fonte sobre cada produto — produto
              instalado não é, por si, software exposto. Não altera o AEGIS Score.
            } @else {
              Vulnerabilidades identificadas pelas fontes, agrupadas por PROBLEMA (identificador informado pela
              fonte, normalmente um CVE) — cada linha aparece em um ou mais ativos. Identificação da fonte não é
              exploração confirmada nem comprometimento. Não alteram o AEGIS Score.
            }
          </p>
        </div>
        <div class="page-actions">
          @if (tab() === 'vulnerabilities') {
            <!-- Sem leitura, uma análise pressuporia evidência que não existe: o botão só vale com dados. -->
            <button
              type="button"
              class="primary"
              (click)="analyzeWithAi()"
              [disabled]="loading() || !!error() || !reading().hasData"
              [title]="reading().hasData ? '' : 'Disponível depois da primeira coleta de uma fonte'"
            >
              Analisar com IA
            </button>
            <button type="button" class="ghost" (click)="reload()" [disabled]="loading()">
              {{ loading() ? 'Carregando…' : 'Atualizar' }}
            </button>
          }
        </div>
      </header>

      <!-- ---------- Sub-área: Vulnerabilidades × Inventário de software (MESMA tela, sem novo item de menu) ---------- -->
      <div class="tabbar" role="tablist">
        <button type="button" role="tab" [class.active]="tab() === 'vulnerabilities'"
          [attr.aria-selected]="tab() === 'vulnerabilities'" (click)="setTab('vulnerabilities')">
          Vulnerabilidades
        </button>
        <button type="button" role="tab" [class.active]="tab() === 'software'"
          [attr.aria-selected]="tab() === 'software'" (click)="setTab('software')">
          Inventário de software
        </button>
      </div>

      @if (tab() === 'software') {
        <app-software-inventory-tab />
      } @else {

      @if (!loading() && !error() && reading().hasData && reading().notice) {
        <p class="notice warn" role="status">{{ reading().notice }}</p>
      }

      <!-- ---------- Resumo ---------- -->
      <!-- [AEGIS-LANGUAGE-STATES-01] Sem leitura as contagens ficam "—" (nunca 0) e o motivo é dito pelo estado. -->
      <div class="cards">
        <div class="card">
          <span class="metric-label">Problemas distintos em aberto</span>
          @if (reading().hasData) {
            <span class="metric-value">{{ summary()!.distinctCvesOpen }}</span>
            <span class="metric-unit">{{ summary()!.affectedAssetsOpen }} ativo(s) com vulnerabilidade em aberto</span>
          } @else {
            <span class="metric-value is-na">—</span>
            <span class="metric-unit">{{ readingLabel() }}</span>
          }
        </div>
        <div class="card">
          <span class="metric-label">Ocorrências em aberto</span>
          @if (reading().hasData) {
            <span class="metric-value">{{ summary()!.totalOpen }}</span>
            <span class="metric-unit" [title]="noLongerReportedHint">
              {{ summary()!.totalResolved }} ocorrência(s) não mais reportada(s)
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
          } @else {
            <span class="metric-value sm is-na">{{ readingLabel() }}</span>
          }
          <span class="metric-unit">{{ (summary()?.sources?.length ?? 0) }} fonte(s) configurada(s)</span>
        </div>
        <div class="card wide">
          <span class="metric-label">Em aberto por severidade técnica (fonte)</span>
          @if ((summary()?.openBySeverity?.length ?? 0) > 0) {
            <div class="cats">
              @for (s of summary()!.openBySeverity; track s.severity) {
                <button
                  type="button"
                  class="filter-chip"
                  [class]="'sev-' + s.severity.toLowerCase()"
                  [class.active]="severityFilter() === s.severity"
                  [attr.aria-pressed]="severityFilter() === s.severity"
                  (click)="toggleSeverity(s.severity)"
                >
                  {{ sevPt(s.severity) }} <span class="cat-n">{{ s.open }}</span>
                </button>
              }
            </div>
          } @else if (reading().hasData) {
            <span class="metric-unit">Nenhuma vulnerabilidade em aberto na última leitura.</span>
          } @else {
            <span class="metric-unit">Sem leitura das fontes.</span>
          }
        </div>
      </div>

      <!-- ---------- Filtros: agrupados acima dos resultados ---------- -->
      <div class="filter-bar">
        <div class="filter-row">
          <span class="filter-label">Situação</span>
          <div class="segmented" role="group" aria-label="Situação">
            @for (s of stateOptions; track s.value) {
              <button type="button" [class.active]="stateFilter() === s.value" [attr.aria-pressed]="stateFilter() === s.value"
                (click)="setState(s.value)">
                {{ s.label }}
              </button>
            }
          </div>
          <span class="filter-label">Exploit</span>
          <div class="segmented" role="group" aria-label="Exploit">
            @for (e of exploitOptions; track e.value) {
              <button type="button" [class.active]="exploitFilter() === e.value" [attr.aria-pressed]="exploitFilter() === e.value"
                (click)="setExploit(e.value)">
                {{ e.label }}
              </button>
            }
          </div>
        </div>
        <div class="filter-row">
          <input
            class="search"
            type="search"
            placeholder="Buscar por CVE ou título…"
            [ngModel]="searchTerm()"
            (ngModelChange)="onSearchInput($event)"
            aria-label="Buscar vulnerabilidades por CVE ou título"
          />
          @if ((summary()?.sources?.length ?? 0) > 0) {
            <select class="src-select" [ngModel]="connectorFilter()" (ngModelChange)="setConnector($event)" aria-label="Fonte">
              <option [ngValue]="null">Todas as fontes</option>
              @for (src of summary()!.sources; track src.connectorConfigId) {
                <option [ngValue]="src.connectorConfigId">{{ src.displayName }} ({{ src.provider }})</option>
              }
            </select>
          }
          @if (severityFilter()) {
            <button type="button" class="ghost sm" (click)="toggleSeverity(severityFilter()!)">
              Severidade: {{ sevPt(severityFilter()) }} ✕
            </button>
          }
        </div>
      </div>

      <!-- ---------- Tabela ---------- -->
      <div class="panel flush">
        @if (loading()) {
          <div class="state" role="status"><span class="spinner" aria-hidden="true"></span><p>Carregando vulnerabilidades…</p></div>
        } @else if (error()) {
          <div class="state error" role="alert">
            <p class="err">⚠ {{ error() }}</p>
            <button type="button" class="ghost" (click)="retry()">Tentar novamente</button>
          </div>
        } @else if (!reading().hasData) {
          <div class="state empty">
            <p>{{ reading().notice }}</p>
            <p>
              Fontes suportadas: <strong>Microsoft Defender Vulnerability Management</strong> e
              <strong>Google Cloud VM Manager</strong>, em <strong>Configurações → Integrações</strong>, com
              <strong>Sincronizar agora</strong>. Cada fonte exige seus próprios pré-requisitos (licença/capacidade,
              API habilitada, máquinas/instâncias com inventário e as permissões somente leitura).
            </p>
          </div>
        } @else if (groups().length === 0) {
          <div class="state empty">
            @if (filtersActive()) {
              <p>Nenhuma vulnerabilidade corresponde aos filtros atuais.</p>
            } @else {
              <p>Nenhuma vulnerabilidade em aberto na última leitura das fontes.</p>
            }
          </div>
        } @else {
          <table class="data-table">
            <thead>
              <tr>
                <th>Problema</th>
                <th>Por que importa</th>
                <th class="c-cvss">Ativos afetados</th>
                <th>Exploit (fonte)</th>
                <th>Fontes</th>
                <th>Primeira ação</th>
                <th class="c-exp" aria-label="Detalhes"></th>
              </tr>
            </thead>
            <tbody>
              @for (g of groups(); track g.cveId) {
                <tr class="row" [class.resolved]="g.effectiveLifecycle === 'Resolved'">
                  <td>
                    <strong class="title">{{ g.displayTitle }}</strong>
                    @if (g.effectiveLifecycle === 'Resolved') {
                      <span class="badge ok" [title]="noLongerReportedHint">{{ lifecycle(g.effectiveLifecycle) }}</span>
                    }
                    <span class="meta"><span class="mono">{{ g.cveId }}</span> · severidade {{ g.severityLabel.toLowerCase() }} (fonte)</span>
                  </td>
                  <td><span class="meta why">{{ g.whyItMatters }}</span></td>
                  <td class="c-cvss">
                    <strong>{{ reach(g) }}</strong>
                    <span class="meta" title="Maior criticidade cadastrada entre os ativos afetados">
                      criticidade máx. {{ g.maxAssetCriticality }} (cadastro)
                    </span>
                    @if (g.openAssetCount > 0 && g.resolvedAssetCount > 0) {
                      <span class="meta">{{ g.openAssetCount }} em aberto · {{ g.resolvedAssetCount }} não mais reportado(s)</span>
                    }
                  </td>
                  <td class="c-exploit">
                    @if (g.exploitVerified) {
                      <span class="badge bad">{{ g.exploitLabel }}</span>
                    } @else if (g.publicExploit) {
                      <span class="badge warn">{{ g.exploitLabel }}</span>
                    } @else {
                      <span class="dim">{{ g.exploitLabel }}</span>
                    }
                  </td>
                  <td class="c-src">
                    @for (p of g.providers; track p) {
                      <span class="badge neutral src">{{ p }}</span>
                    }
                  </td>
                  <td><span class="meta">{{ g.firstAction }}</span></td>
                  <td class="c-exp">
                    <button type="button" class="linkbtn" (click)="toggleExpand(g.cveId)" [attr.aria-expanded]="expanded().has(g.cveId)">
                      {{ expanded().has(g.cveId) ? 'Ocultar' : 'Detalhes' }}
                    </button>
                  </td>
                </tr>
                @if (expanded().has(g.cveId)) {
                  <tr class="details-row">
                    <td colspan="7">
                      <div class="details">
                        <div class="det-grid">
                          <div><span class="det-label">Identificador (fonte)</span><span class="mono">{{ g.cveId }}</span></div>
                          <div><span class="det-label">Exploit</span><span>{{ g.exploitLabel }}</span></div>
                          <div><span class="det-label">CVSS</span><span class="mono">{{ g.cvssScore != null ? num(g.cvssScore) : '—' }} {{ g.cvssVector ? '· ' + g.cvssVector : '' }}</span></div>
                          <div><span class="det-label">EPSS</span><span>{{ g.epss != null ? pctEpss(g.epss) : '—' }}</span></div>
                          <div><span class="det-label">Publicado em</span><span>{{ fmtDate(g.publishedOn) }}</span></div>
                          <div><span class="det-label">Primeira observação</span><span>{{ fmtDate(g.firstSeenAt) }}</span></div>
                          <div><span class="det-label">Última observação</span><span>{{ fmtDate(g.lastSeenAt) }}</span></div>
                        </div>
                        @if (g.sourceTitle) {
                          <div class="det">
                            <span class="det-label">Título original da fonte</span>
                            <span class="meta">{{ g.sourceTitle }}</span>
                          </div>
                        }
                        <div class="det">
                          <span class="det-label">Ativos afetados ({{ g.affectedAssetCount }})</span>
                          @let os = occ(g.cveId);
                          @if (os?.loading && (os?.items?.length ?? 0) === 0) {
                            <p class="muted">Carregando ativos…</p>
                          } @else if (os?.error && (os?.items?.length ?? 0) === 0) {
                            <!-- §9: falha na PRIMEIRA carga — NUNCA vira lista vazia silenciosa. -->
                            <div class="occ-err">
                              <span class="err">⚠ {{ os?.error }}</span>
                              <button type="button" class="ghost sm" (click)="loadOccurrences(g.cveId)">Tentar novamente</button>
                            </div>
                          } @else {
                            <div class="obs">
                              @for (o of os?.items ?? []; track o.id) {
                                <div class="obs-row">
                                  <span class="obs-name">{{ o.assetName }}</span>
                                  <span class="obs-life">criticidade {{ o.assetCriticality }} · {{ o.assetSubType || '—' }}</span>
                                  <span class="obs-seen">{{ lifecycle(o.effectiveLifecycle) }}</span>
                                  @for (s of o.sources; track s.connectorConfigId) {
                                    <span class="badge neutral src" [class.res]="s.lifecycleState === 'Resolved'" [title]="s.displayName">{{ s.provider }}</span>
                                  }
                                </div>
                              } @empty {
                                <p class="muted">Nenhum ativo carregado.</p>
                              }
                            </div>
                            @if (os?.error) {
                              <!-- §9: falha ao carregar MAIS — preserva os itens já vistos e oferece nova tentativa. -->
                              <div class="occ-err">
                                <span class="err">⚠ {{ os?.error }}</span>
                                <button type="button" class="linkbtn" (click)="loadOccurrences(g.cveId)">Tentar novamente</button>
                              </div>
                            }
                            @if (occHasMore(g.cveId)) {
                              <button type="button" class="ghost sm load-more" (click)="loadOccurrences(g.cveId)" [disabled]="os?.loading">
                                {{ os?.loading ? 'Carregando…' : 'Carregar mais (' + (os?.items?.length ?? 0) + ' de ' + (os?.total ?? 0) + ')' }}
                              </button>
                            } @else if ((os?.items?.length ?? 0) > 0) {
                              <p class="meta">Todos os {{ os?.total ?? 0 }} ativo(s) carregado(s).</p>
                            }
                          }
                        </div>
                      </div>
                    </td>
                  </tr>
                }
              }
            </tbody>
          </table>

          <footer class="pager">
            <span class="range">Página {{ page() }} de {{ pageCount() }} · {{ total() }} problema(s)</span>
            <button type="button" class="ghost sm" (click)="prevPage()" [disabled]="page() <= 1">← Anterior</button>
            <button type="button" class="ghost sm" (click)="nextPage()" [disabled]="page() >= pageCount()">Próxima →</button>
          </footer>
        }
      </div>
      }
    </section>
  `,
  styles: [
    `
      /* Página, abas, cartões, filtros, tabela, badges, estados e botões: sistema visual global (styles.css). */
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
      .sev-critical {
        color: var(--red-text);
      }
      .sev-high {
        color: #ff9a6b;
      }
      .sev-medium {
        color: var(--amber);
      }
      .sev-low {
        color: var(--cyan);
      }
      .sev-desconhecida {
        color: var(--muted);
      }
      .search {
        flex: 1 1 18rem;
        min-width: 0;
      }
      .src-select {
        max-width: 100%;
      }

      .row.resolved {
        opacity: 0.6;
      }
      .c-cvss {
        white-space: nowrap;
      }
      .c-exp {
        text-align: right;
        white-space: nowrap;
      }
      .badge.ok {
        margin-left: 6px;
      }
      .badge.src {
        margin: 0 4px 4px 0;
      }
      .badge.src.res {
        opacity: 0.5;
      }
      .err {
        color: var(--red-text);
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
      .obs {
        display: flex;
        flex-direction: column;
        gap: 6px;
        margin-top: var(--sp-1);
      }
      .obs-row {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sp-2);
        font-size: var(--fs-sm);
      }
      .obs-name {
        font-weight: 500;
      }
      .obs-life {
        color: var(--text-2);
      }
      .obs-seen {
        font-size: var(--fs-meta);
        color: var(--muted);
      }
      .occ-err {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 10px;
        margin-top: 6px;
      }
      .load-more {
        margin-top: var(--sp-2);
      }
      @media (max-width: 720px) {
        .card.wide {
          grid-column: auto;
        }
      }
    `,
  ],
})
export class VulnerabilitiesComponent {
  private readonly api = inject(VulnerabilityService);
  private readonly agent = inject(AgentStateService);

  // [AEGIS-LANGUAGE-STATES-01] "Resolved" do coletor = a fonte deixou de reportar numa coleta completa — não é
  // correção validada. Exploit é o que a FONTE informa sobre disponibilidade, nunca exploração no ambiente.
  protected readonly stateOptions: { value: VulnerabilityLifecycleFilter; label: string }[] = [
    { value: 'open', label: 'Em aberto' },
    { value: 'resolved', label: 'Não mais reportadas' },
    { value: 'all', label: 'Todas' },
  ];
  protected readonly exploitOptions: { value: VulnerabilityExploitFilter; label: string }[] = [
    { value: 'all', label: 'Com ou sem exploit' },
    { value: 'exploitable', label: 'Exploit público informado' },
    { value: 'verified', label: 'Exploit confirmado pela fonte' },
  ];

  // [AEGIS-MVP-MICROSOFT-COVERAGE-01] Sub-área ativa desta MESMA tela (sem novo item de menu lateral).
  protected readonly tab = signal<VulnTab>('vulnerabilities');

  protected setTab(t: VulnTab): void {
    this.tab.set(t);
  }

  // [AEGIS-MVP-LANGUAGE-02] A leitura PADRÃO é a visão AGRUPADA por CVE/problema (paginação por PROBLEMA).
  protected readonly data = signal<VulnerabilityOverview | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly stateFilter = signal<VulnerabilityLifecycleFilter>('open');
  protected readonly exploitFilter = signal<VulnerabilityExploitFilter>('all');
  protected readonly severityFilter = signal<string | null>(null);
  protected readonly connectorFilter = signal<string | null>(null);
  protected readonly searchTerm = signal('');
  protected readonly page = signal(1);

  // [AEGIS-MVP-LANGUAGE-02 §8] Expansão de um GRUPO carrega as ocorrências ativo×CVE PAGINADAS sob demanda (filtro
  // EXATO por CVE) — sem N+1 inicial. O estado por CVE guarda itens/total/páginas/loading/erro (ver OccState).
  protected readonly expanded = signal<Set<string>>(new Set());
  protected readonly occByCve = signal<Map<string, OccState>>(new Map());

  /** Tamanho de página das ocorrências dentro de um grupo (independente da paginação por PROBLEMA). */
  private readonly OCC_PAGE_SIZE = 25;
  /** Debounce da busca (§7) — sem RxJS Subject; timer simples. */
  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  protected readonly summary = computed(() => this.data()?.summary ?? null);
  protected readonly groups = computed<VulnerabilityGroup[]>(() => this.data()?.groups ?? []);
  protected readonly total = computed(() => this.data()?.total ?? 0);
  protected readonly pageCount = computed(() => {
    const d = this.data();
    if (!d || d.pageSize <= 0) return 1;
    return Math.max(1, Math.ceil(d.total / d.pageSize));
  });

  // [AEGIS-MVP-LANGUAGE-02 §5] A narrativa (título/porquê/exploit/1ª ação) é AUTORIDADE do backend e chega pronta em
  // cada VulnerabilityGroup. O único helper de apresentação restante traduz o ENUM cru dos chips de severidade do RESUMO.
  protected readonly sevPt = severityPt;

  /** [AEGIS-LANGUAGE-STATES-01] Sem fonte × fonte sem coleta × leitura (com ressalva de escopo/falha). */
  protected readonly reading = computed(() => vulnerabilityReading(this.summary()));
  protected readonly readingLabel = computed(() =>
    this.reading().state === 'NoSource' ? 'Sem integração' : 'Ainda não coletado');
  protected readonly filtersActive = computed(
    () =>
      this.stateFilter() !== 'open' ||
      this.exploitFilter() !== 'all' ||
      !!this.severityFilter() ||
      !!this.connectorFilter() ||
      this.searchTerm().trim() !== '',
  );
  protected readonly lifecycle = vulnerabilityLifecyclePt;
  protected readonly noLongerReportedHint = VULNERABILITY_NO_LONGER_REPORTED_HINT;

  /** Alcance relevante ao estado atual (§2): abertas → ativos abertos; resolvidas → resolvidos; todas → total. */
  protected reach(g: VulnerabilityGroup): number {
    switch (this.stateFilter()) {
      case 'resolved':
        return g.resolvedAssetCount;
      case 'all':
        return g.affectedAssetCount;
      default:
        return g.openAssetCount;
    }
  }

  constructor() {
    this.load();
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    // §9: ao mudar filtros/página/busca, a expansão e seu estado de paginação são DESCARTADOS (nada de itens órfãos).
    this.expanded.set(new Set());
    this.occByCve.set(new Map());
    this.api
      .overview({
        state: this.stateFilter(),
        exploit: this.exploitFilter(),
        severity: this.severityFilter() ?? undefined,
        connectorId: this.connectorFilter() ?? undefined,
        search: this.searchTerm().trim() || undefined,
        page: this.page(),
        pageSize: 25,
      })
      .subscribe({
        next: (ov) => {
          this.data.set(ov);
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

  protected setState(s: VulnerabilityLifecycleFilter): void {
    if (this.stateFilter() === s) return;
    this.stateFilter.set(s);
    this.page.set(1);
    this.load();
  }

  protected setExploit(e: VulnerabilityExploitFilter): void {
    if (this.exploitFilter() === e) return;
    this.exploitFilter.set(e);
    this.page.set(1);
    this.load();
  }

  protected toggleSeverity(sev: string): void {
    this.severityFilter.update((s) => (s === sev ? null : sev));
    this.page.set(1);
    this.load();
  }

  protected setConnector(id: string | null): void {
    this.connectorFilter.set(id);
    this.page.set(1);
    this.load();
  }

  /** [AEGIS-MVP-LANGUAGE-02 §7] Busca na visão AGRUPADA com debounce; reseta para a página 1 a cada mudança. */
  protected onSearchInput(value: string): void {
    this.searchTerm.set(value);
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => {
      this.searchTimer = null;
      this.page.set(1);
      this.load();
    }, 300);
  }

  protected toggleExpand(cveId: string): void {
    const isOpen = this.expanded().has(cveId);
    this.expanded.update((set) => {
      const next = new Set(set);
      if (next.has(cveId)) next.delete(cveId);
      else next.add(cveId);
      return next;
    });
    // Carrega a PRIMEIRA página de ocorrências só na primeira expansão (sem N+1 inicial); reexpandir não recarrega.
    if (!isOpen && !this.occByCve().has(cveId)) {
      this.loadOccurrences(cveId);
    }
  }

  /**
   * [AEGIS-MVP-LANGUAGE-02 §8/§9] Carrega a PRÓXIMA página de ocorrências do CVE (também serve de "Carregar mais" e
   * de "Tentar novamente"): respeita o filtro de estado, deduplica por id e NUNCA transforma erro em lista vazia —
   * uma falha preserva os itens já carregados e apenas registra o erro para nova tentativa.
   */
  protected loadOccurrences(cveId: string): void {
    const cur = this.occByCve().get(cveId);
    if (cur?.loading) return;
    const nextPage = (cur?.loadedPages ?? 0) + 1;
    this.patchOcc(cveId, {
      items: cur?.items ?? [],
      total: cur?.total ?? 0,
      loadedPages: cur?.loadedPages ?? 0,
      loading: true,
      error: null,
    });
    this.api.list({ cveId, state: this.stateFilter(), page: nextPage, pageSize: this.OCC_PAGE_SIZE }).subscribe({
      next: (list) => {
        const prev = this.occByCve().get(cveId);
        const base = prev?.items ?? [];
        const seen = new Set(base.map((i) => i.id));
        const merged = base.concat(list.items.filter((i) => !seen.has(i.id)));   // sem duplicar ocorrências
        this.patchOcc(cveId, { items: merged, total: list.total, loadedPages: nextPage, loading: false, error: null });
      },
      error: (err: Error) => {
        const prev = this.occByCve().get(cveId);
        this.patchOcc(cveId, {
          items: prev?.items ?? [],                       // §9: mantém o que já havia
          total: prev?.total ?? 0,
          loadedPages: prev?.loadedPages ?? 0,            // não avança a página falha
          loading: false,
          error: err.message || 'Falha ao carregar os ativos afetados.',
        });
      },
    });
  }

  private patchOcc(cveId: string, state: OccState): void {
    this.occByCve.update((m) => new Map(m).set(cveId, state));
  }

  protected occ(cveId: string): OccState | null {
    return this.occByCve().get(cveId) ?? null;
  }

  /** Há mais ocorrências a carregar? (itens carregados < total conhecido). */
  protected occHasMore(cveId: string): boolean {
    const s = this.occByCve().get(cveId);
    return !!s && !s.loading && s.loadedPages > 0 && s.items.length < s.total;
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
   * Reutiliza o Auditor Virtual, semeando uma pergunta contextual. O backend já inclui as principais
   * vulnerabilidades abertas (máx. 8, com fontes) no contexto tenant-scoped e sabe que os fatos do CVE são
   * AUTORITATIVOS e a resposta é CONSULTIVA. A IA não cria/altera CVE, exploit, observação, lifecycle ou score.
   */
  protected analyzeWithAi(): void {
    this.agent.requestAudit(
      'Analise as vulnerabilidades em aberto identificadas pelas fontes nos ativos: resuma as que merecem atenção ' +
        'primeiro com base nos fatos da fonte (exploit informado, severidade técnica CVSS/EPSS, criticidade ' +
        'cadastrada dos ativos), relacione-as com os controles NIST CSF avaliados e sugira uma sequência de ' +
        'remediação. Não trate CVSS como risco de negócio, exploit disponível como exploração confirmada nem ' +
        'identificação como comprometimento. Deixe claro o que é fato da fonte, inferência e recomendação.',
    );
  }

  protected num(n: number): string {
    return Number.isInteger(n) ? String(n) : n.toFixed(1);
  }

  protected pctEpss(n: number): string {
    return `${Math.round(n * 100)}%`;
  }

  protected fmtDate(iso: string | null | undefined): string {
    if (!iso) return '—';
    const d = new Date(iso);
    return isNaN(d.getTime()) ? '—' : d.toLocaleString('pt-BR');
  }
}

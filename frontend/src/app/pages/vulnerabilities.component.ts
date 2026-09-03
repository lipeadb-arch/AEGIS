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
  severityPt,
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
          <h1>Vulnerabilidades</h1>
          <p class="sub">
            @if (tab() === 'software') {
              Software observado pelo Microsoft Defender nos ativos do ambiente, com produtos, dispositivos
              expostos, fraquezas conhecidas, exploit público e alerta ativo. É evidência operacional/de
              exposição — não altera o AEGIS Score.
            } @else {
              Vulnerabilidades (CVEs) agrupadas por PROBLEMA — cada linha é um CVE observado em um ou mais ativos.
              Expanda para ver os ativos afetados. São fatos operacionais/de exposição — não alteram o AEGIS Score.
            }
          </p>
        </div>
        <div class="head-actions">
          @if (tab() === 'vulnerabilities') {
            <button type="button" class="primary" (click)="analyzeWithAi()" [disabled]="loading() || !!error()">
              Analisar com IA
            </button>
            <button type="button" class="ghost" (click)="reload()" [disabled]="loading()">
              {{ loading() ? 'Carregando…' : 'Atualizar' }}
            </button>
          }
        </div>
      </header>

      <!-- ---------- Sub-área: Vulnerabilidades × Software exposto (MESMA tela, sem novo item de menu) ---------- -->
      <div class="tabs" role="tablist">
        <button type="button" class="tab-btn" [class.active]="tab() === 'vulnerabilities'" (click)="setTab('vulnerabilities')">
          Vulnerabilidades
        </button>
        <button type="button" class="tab-btn" [class.active]="tab() === 'software'" (click)="setTab('software')">
          Software exposto
        </button>
      </div>

      @if (tab() === 'software') {
        <app-software-inventory-tab />
      } @else {

      <!-- ---------- Resumo ---------- -->
      <div class="cards">
        <div class="card">
          <span class="card-label">Problemas distintos</span>
          <span class="card-value">{{ summary()?.distinctCvesOpen ?? '—' }}</span>
          <span class="card-meta">{{ summary()?.affectedAssetsOpen ?? 0 }} ativo(s) ainda afetado(s)</span>
        </div>
        <div class="card">
          <span class="card-label">Ocorrências abertas</span>
          <span class="card-value">{{ summary()?.totalOpen ?? '—' }}</span>
          <span class="card-meta">{{ summary()?.totalResolved ?? 0 }} ocorrência(s) resolvida(s)</span>
        </div>
        <div class="card">
          <span class="card-label">Última coleta</span>
          @if (summary()?.lastCollectedAt) {
            <span class="card-value sm">{{ fmtDate(summary()?.lastCollectedAt) }}</span>
          } @else {
            <span class="card-value sm muted">Ainda não coletado</span>
          }
          <span class="card-meta">{{ (summary()?.sources?.length ?? 0) }} fonte(s) configurada(s)</span>
        </div>
        <div class="card wide">
          <span class="card-label">Por severidade (abertas)</span>
          @if ((summary()?.openBySeverity?.length ?? 0) > 0) {
            <div class="cats">
              @for (s of summary()!.openBySeverity; track s.severity) {
                <button
                  type="button"
                  class="cat-chip"
                  [class]="'sev-' + s.severity.toLowerCase()"
                  [class.active]="severityFilter() === s.severity"
                  (click)="toggleSeverity(s.severity)"
                >
                  {{ sevPt(s.severity) }} <span class="cat-n">{{ s.open }}</span>
                </button>
              }
            </div>
          } @else {
            <span class="card-meta">Sem vulnerabilidades abertas por severidade.</span>
          }
        </div>
      </div>

      <!-- ---------- Filtros ---------- -->
      <div class="filters">
        <div class="seg" role="tablist">
          @for (s of stateOptions; track s.value) {
            <button type="button" class="seg-btn" [class.active]="stateFilter() === s.value" (click)="setState(s.value)">
              {{ s.label }}
            </button>
          }
        </div>
        <div class="seg" role="tablist">
          @for (e of exploitOptions; track e.value) {
            <button type="button" class="seg-btn" [class.active]="exploitFilter() === e.value" (click)="setExploit(e.value)">
              {{ e.label }}
            </button>
          }
        </div>
        @if ((summary()?.sources?.length ?? 0) > 0) {
          <select class="src-select" [ngModel]="connectorFilter()" (ngModelChange)="setConnector($event)">
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
        <input
          class="search"
          type="search"
          placeholder="Buscar por CVE ou título…"
          [ngModel]="searchTerm()"
          (ngModelChange)="onSearchInput($event)"
          aria-label="Buscar vulnerabilidades por CVE ou título"
        />
      </div>

      <!-- ---------- Tabela ---------- -->
      <div class="panel">
        @if (loading()) {
          <p class="muted">Carregando vulnerabilidades…</p>
        } @else if (error()) {
          <div class="state error">
            <p class="err">⚠ {{ error() }}</p>
            <button type="button" class="ghost" (click)="retry()">Tentar novamente</button>
          </div>
        } @else if (summary()?.neverCollected) {
          <div class="state empty">
            <p class="muted">
              Ainda não coletado. Configure um scanner de vulnerabilidades — <strong>Microsoft Defender
              Vulnerability Management</strong> ou <strong>Google Cloud VM Manager</strong> — em
              <strong>Configurações → Integrações</strong> e use <strong>Sincronizar agora</strong> para trazer
              os ativos e CVEs. Cada fonte exige seus próprios pré-requisitos (licença/capacidade, API habilitada,
              máquinas/instâncias com inventário e as permissões somente leitura).
            </p>
          </div>
        } @else if (groups().length === 0) {
          <div class="state empty">
            <p class="muted">Nenhuma vulnerabilidade para o filtro atual.</p>
          </div>
        } @else {
          <table class="grid-table">
            <thead>
              <tr>
                <th>Problema</th>
                <th>Por que importa</th>
                <th class="c-cvss">Ativos afetados</th>
                <th>Exploit</th>
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
                      <span class="badge ok">Resolvida</span>
                    }
                    <span class="meta mono">{{ g.cveId }} · {{ g.severityLabel }}</span>
                  </td>
                  <td><span class="meta why">{{ g.whyItMatters }}</span></td>
                  <td class="c-cvss">
                    <strong>{{ reach(g) }}</strong>
                    <span class="meta">crít. máx. {{ g.maxAssetCriticality }}</span>
                    @if (g.openAssetCount > 0 && g.resolvedAssetCount > 0) {
                      <span class="meta">{{ g.openAssetCount }} aberto(s) · {{ g.resolvedAssetCount }} resolvido(s)</span>
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
                      <span class="badge src">{{ p }}</span>
                    }
                  </td>
                  <td><span class="meta">{{ g.firstAction }}</span></td>
                  <td class="c-exp">
                    <button type="button" class="linkbtn" (click)="toggleExpand(g.cveId)">
                      {{ expanded().has(g.cveId) ? 'Ocultar' : 'Detalhes' }}
                    </button>
                  </td>
                </tr>
                @if (expanded().has(g.cveId)) {
                  <tr class="details-row">
                    <td colspan="7">
                      <div class="details">
                        <div class="det-grid">
                          <div><span class="det-label">CVE</span><span class="mono">{{ g.cveId }}</span></div>
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
                                  <span class="obs-life">crít. {{ o.assetCriticality }} · {{ o.assetSubType || '—' }}</span>
                                  <span class="obs-seen">{{ o.effectiveLifecycle === 'Open' ? 'Aberta' : 'Resolvida' }}</span>
                                  @for (s of o.sources; track s.connectorConfigId) {
                                    <span class="badge src" [class.res]="s.lifecycleState === 'Resolved'" [title]="s.displayName">{{ s.provider }}</span>
                                  }
                                </div>
                              } @empty {
                                <p class="muted">Nenhum ativo carregado.</p>
                              }
                            </div>
                            @if (os?.error) {
                              <!-- §9: falha ao carregar MAIS — preserva os itens já vistos e oferece nova tentativa. -->
                              <div class="occ-err">
                                <span class="err sm">⚠ {{ os?.error }}</span>
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
            <button type="button" class="ghost sm" (click)="prevPage()" [disabled]="page() <= 1">← Anterior</button>
            <span class="pg-info">Página {{ page() }} de {{ pageCount() }} · {{ total() }} problema(s)</span>
            <button type="button" class="ghost sm" (click)="nextPage()" [disabled]="page() >= pageCount()">Próxima →</button>
          </footer>
        }
      </div>
      }
    </section>
  `,
  styles: [
    `
      .page { padding: 1.25rem 1.5rem 2rem; display: flex; flex-direction: column; gap: 1.1rem; }
      .page-head { display: flex; justify-content: space-between; align-items: flex-start; gap: 1rem; flex-wrap: wrap; }
      h1 { margin: 0; font-size: 1.35rem; letter-spacing: 0.02em; }
      .sub { margin: 0.35rem 0 0; max-width: 72ch; opacity: 0.72; font-size: 0.85rem; }
      .head-actions { display: flex; gap: 0.5rem; flex-wrap: wrap; }
      .tabs { display: inline-flex; border: 1px solid color-mix(in srgb, var(--hud-cyan, #26e0ff) 26%, transparent); border-radius: 6px; overflow: hidden; width: fit-content; }
      .tab-btn { background: transparent; border: 0; color: inherit; font: inherit; font-size: 0.82rem; padding: 0.45rem 1rem; cursor: pointer; }
      .tab-btn.active { background: color-mix(in srgb, var(--hud-cyan, #26e0ff) 20%, transparent); }
      .muted { opacity: 0.65; font-size: 0.85rem; }
      .dim { opacity: 0.4; }
      .err { color: #ff6b8a; font-size: 0.85rem; }

      .cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(12rem, 1fr)); gap: 0.75rem; }
      .card {
        background: color-mix(in srgb, var(--hud-cyan, #26e0ff) 4%, transparent);
        border: 1px solid color-mix(in srgb, var(--hud-cyan, #26e0ff) 22%, transparent);
        border-radius: 8px; padding: 0.8rem 0.95rem; display: flex; flex-direction: column; gap: 0.2rem;
      }
      .card.wide { grid-column: span 2; min-width: 0; }
      .card-label { font-size: 0.66rem; text-transform: uppercase; letter-spacing: 0.1em; opacity: 0.6; }
      .card-value { font-size: 1.6rem; font-weight: 600; }
      .card-value.sm { font-size: 1rem; }
      .card-value.muted { font-size: 1rem; opacity: 0.7; }
      .card-meta { font-size: 0.72rem; opacity: 0.6; }
      .cats { display: flex; flex-wrap: wrap; gap: 0.35rem; margin-top: 0.25rem; }
      .cat-chip {
        background: transparent; border: 1px solid color-mix(in srgb, var(--hud-cyan, #26e0ff) 30%, transparent);
        color: inherit; border-radius: 999px; padding: 0.2rem 0.6rem; font: inherit; font-size: 0.75rem; cursor: pointer;
      }
      .cat-chip.active { background: color-mix(in srgb, var(--hud-cyan, #26e0ff) 18%, transparent); border-color: var(--hud-cyan, #26e0ff); }
      .cat-n { opacity: 0.7; margin-left: 0.2rem; }

      .filters { display: flex; align-items: center; gap: 0.6rem; flex-wrap: wrap; }
      .seg { display: inline-flex; border: 1px solid color-mix(in srgb, var(--hud-cyan, #26e0ff) 26%, transparent); border-radius: 6px; overflow: hidden; }
      .seg-btn { background: transparent; border: 0; color: inherit; font: inherit; font-size: 0.8rem; padding: 0.35rem 0.8rem; cursor: pointer; }
      .seg-btn.active { background: color-mix(in srgb, var(--hud-cyan, #26e0ff) 20%, transparent); }
      .src-select, .search {
        background: rgba(4, 8, 18, 0.6); border: 1px solid color-mix(in srgb, var(--hud-cyan, #26e0ff) 26%, transparent);
        border-radius: 5px; padding: 0.4rem 0.6rem; color: inherit; font: inherit; font-size: 0.85rem;
      }
      .search { flex: 1; min-width: 12rem; }

      .panel {
        background: color-mix(in srgb, var(--hud-cyan, #26e0ff) 4%, transparent);
        border: 1px solid color-mix(in srgb, var(--hud-cyan, #26e0ff) 22%, transparent);
        border-radius: 8px; padding: 0.6rem; overflow-x: auto;
      }
      .state { padding: 1.5rem 1rem; text-align: center; display: flex; flex-direction: column; gap: 0.75rem; align-items: center; }
      .grid-table { width: 100%; border-collapse: collapse; font-size: 0.85rem; }
      .grid-table th {
        text-align: left; font-size: 0.66rem; text-transform: uppercase; letter-spacing: 0.08em; opacity: 0.6;
        padding: 0.4rem 0.6rem; border-bottom: 1px solid color-mix(in srgb, var(--hud-cyan, #26e0ff) 18%, transparent);
      }
      .grid-table td { padding: 0.5rem 0.6rem; vertical-align: top; border-bottom: 1px solid color-mix(in srgb, var(--hud-cyan, #26e0ff) 8%, transparent); }
      .row.resolved { opacity: 0.6; }
      .title { display: block; line-height: 1.3; }
      .meta { font-size: 0.72rem; opacity: 0.62; display: block; }
      .c-cvss, .c-epss, .c-sev { white-space: nowrap; }
      .mono { font-family: ui-monospace, monospace; font-size: 0.82rem; }
      .badge { font-size: 0.62rem; padding: 0.05rem 0.4rem; border-radius: 3px; border: 1px solid currentColor; text-transform: uppercase; letter-spacing: 0.05em; }
      .badge.ok { color: var(--hud-cyan, #26e0ff); margin-left: 0.35rem; }
      .badge.warn { color: #f5a524; }
      .badge.bad { color: #ff3d6a; }
      .badge.src { color: #9aa7c7; margin-right: 0.2rem; }
      .badge.src.res { opacity: 0.5; }
      .sev-tag { font-size: 0.72rem; padding: 0.1rem 0.45rem; border-radius: 3px; border: 1px solid currentColor; }
      .sev-critical { color: #ff3d6a; }
      .sev-high { color: #ff8a5c; }
      .sev-medium { color: #f5a524; }
      .sev-low { color: #26e0ff; }
      .sev-desconhecida { color: #9aa7c7; }
      .linkbtn { background: transparent; border: 0; color: var(--hud-cyan, #26e0ff); font: inherit; font-size: 0.78rem; cursor: pointer; }
      .c-exp { text-align: right; white-space: nowrap; }
      .details-row td { background: rgba(4, 8, 18, 0.35); }
      .details { display: flex; flex-direction: column; gap: 0.6rem; padding: 0.3rem 0.2rem; }
      .det-label { font-size: 0.64rem; text-transform: uppercase; letter-spacing: 0.08em; opacity: 0.55; }
      .det-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(11rem, 1fr)); gap: 0.5rem; }
      .det-grid > div { display: flex; flex-direction: column; }
      .obs { display: flex; flex-direction: column; gap: 0.3rem; margin-top: 0.2rem; }
      .obs-row { display: flex; flex-wrap: wrap; gap: 0.5rem; align-items: center; font-size: 0.78rem; }
      .obs-name { opacity: 0.8; }
      .obs-life { opacity: 0.7; }
      .obs-seen, .obs-prod { opacity: 0.55; font-size: 0.72rem; }
      .obs-prod em { opacity: 0.7; }
      .occ-err { display: flex; align-items: center; gap: 0.6rem; flex-wrap: wrap; margin-top: 0.4rem; }
      .occ-err .err.sm { font-size: 0.75rem; }
      .load-more { margin-top: 0.5rem; }
      .pager { display: flex; align-items: center; justify-content: space-between; gap: 0.75rem; padding: 0.6rem 0.4rem 0.2rem; }
      .pg-info { font-size: 0.75rem; opacity: 0.65; }

      button.primary {
        background: color-mix(in srgb, var(--hud-cyan, #26e0ff) 18%, transparent); border: 1px solid var(--hud-cyan, #26e0ff);
        color: inherit; border-radius: 5px; padding: 0.45rem 1rem; font: inherit; font-size: 0.82rem; cursor: pointer;
      }
      button.ghost {
        background: transparent; border: 1px solid color-mix(in srgb, var(--hud-cyan, #26e0ff) 30%, transparent);
        color: inherit; border-radius: 5px; padding: 0.4rem 0.8rem; font: inherit; font-size: 0.8rem; cursor: pointer;
      }
      button.sm { padding: 0.25rem 0.6rem; font-size: 0.74rem; }
      button:disabled { opacity: 0.5; cursor: not-allowed; }
    `,
  ],
})
export class VulnerabilitiesComponent {
  private readonly api = inject(VulnerabilityService);
  private readonly agent = inject(AgentStateService);

  protected readonly stateOptions: { value: VulnerabilityLifecycleFilter; label: string }[] = [
    { value: 'open', label: 'Abertas' },
    { value: 'resolved', label: 'Resolvidas' },
    { value: 'all', label: 'Todas' },
  ];
  protected readonly exploitOptions: { value: VulnerabilityExploitFilter; label: string }[] = [
    { value: 'all', label: 'Qualquer exploit' },
    { value: 'exploitable', label: 'Com exploit' },
    { value: 'verified', label: 'Verificado' },
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
      'Analise as vulnerabilidades (CVEs) abertas dos ativos: explique o impacto das mais críticas ' +
        '(exploit verificado/público, CVSS e EPSS altos, ativos mais críticos), correlacione-as com a postura ' +
        'do NIST CSF e sugira uma sequência de remediação priorizada. Deixe claro o que é fato da fonte, ' +
        'inferência e recomendação.',
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

import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AgentStateService } from '../services/agent-state.service';
import { VulnerabilityService } from '../services/vulnerability.service';
import {
  VulnerabilityExploitFilter,
  VulnerabilityItem,
  VulnerabilityLifecycleFilter,
  VulnerabilityList,
} from '../models/vulnerability.models';

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
  imports: [FormsModule],
  template: `
    <section class="page">
      <header class="page-head">
        <div>
          <h1>Vulnerabilidades</h1>
          <p class="sub">
            Vulnerabilidades (CVEs) associadas a ativos, consolidadas por ativo × CVE e atribuídas às fontes
            que as observam. São fatos operacionais/de exposição — não alteram o AEGIS Score.
          </p>
        </div>
        <div class="head-actions">
          <button type="button" class="primary" (click)="analyzeWithAi()" [disabled]="loading() || !!error()">
            Analisar com IA
          </button>
          <button type="button" class="ghost" (click)="reload()" [disabled]="loading()">
            {{ loading() ? 'Carregando…' : 'Atualizar' }}
          </button>
        </div>
      </header>

      <!-- ---------- Resumo ---------- -->
      <div class="cards">
        <div class="card">
          <span class="card-label">CVEs abertos</span>
          <span class="card-value">{{ summary()?.distinctCvesOpen ?? '—' }}</span>
          <span class="card-meta">{{ summary()?.affectedAssetsOpen ?? 0 }} ativo(s) afetado(s)</span>
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
                  {{ s.severity }} <span class="cat-n">{{ s.open }}</span>
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
        <input
          type="search"
          class="search"
          placeholder="Buscar CVE, título ou ativo…"
          [ngModel]="search()"
          (ngModelChange)="onSearch($event)"
        />
        @if (severityFilter()) {
          <button type="button" class="ghost sm" (click)="toggleSeverity(severityFilter()!)">
            Severidade: {{ severityFilter() }} ✕
          </button>
        }
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
        } @else if (items().length === 0) {
          <div class="state empty">
            <p class="muted">Nenhuma vulnerabilidade para o filtro atual.</p>
          </div>
        } @else {
          <table class="grid-table">
            <thead>
              <tr>
                <th>CVE</th>
                <th class="c-sev">Severidade</th>
                <th class="c-cvss">CVSS</th>
                <th>Exploit</th>
                <th class="c-epss">EPSS</th>
                <th>Ativo</th>
                <th>Fontes</th>
                <th class="c-exp" aria-label="Detalhes"></th>
              </tr>
            </thead>
            <tbody>
              @for (x of items(); track x.id) {
                <tr class="row" [class.resolved]="x.effectiveLifecycle === 'Resolved'">
                  <td>
                    <strong class="mono">{{ x.cveId }}</strong>
                    @if (x.effectiveLifecycle === 'Resolved') {
                      <span class="badge ok">Resolvida</span>
                    }
                    @if (x.cveTitle) {
                      <span class="meta">{{ x.cveTitle }}</span>
                    }
                  </td>
                  <td class="c-sev">
                    <span class="sev-tag" [class]="'sev-' + (x.severity || 'desconhecida').toLowerCase()">
                      {{ x.severity || '—' }}
                    </span>
                  </td>
                  <td class="c-cvss">{{ x.cvssScore != null ? num(x.cvssScore) : '—' }}</td>
                  <td class="c-exploit">
                    @if (x.exploitVerified) {
                      <span class="badge bad">Verificado</span>
                    } @else if (x.publicExploit) {
                      <span class="badge warn">Público</span>
                    } @else {
                      <span class="dim">—</span>
                    }
                  </td>
                  <td class="c-epss">{{ x.epss != null ? pctEpss(x.epss) : '—' }}</td>
                  <td>
                    <span class="title">{{ x.assetName }}</span>
                    <span class="meta">crit. {{ x.assetCriticality }} · {{ x.assetSubType || '—' }}</span>
                  </td>
                  <td class="c-src">
                    @for (s of x.sources; track s.connectorConfigId) {
                      <span class="badge src" [class.res]="s.lifecycleState === 'Resolved'" [title]="s.displayName">
                        {{ s.provider }}
                      </span>
                    }
                  </td>
                  <td class="c-exp">
                    <button type="button" class="linkbtn" (click)="toggleExpand(x.id)">
                      {{ expanded().has(x.id) ? 'Ocultar' : 'Detalhes' }}
                    </button>
                  </td>
                </tr>
                @if (expanded().has(x.id)) {
                  <tr class="details-row">
                    <td colspan="8">
                      <div class="details">
                        <div class="det-grid">
                          <div><span class="det-label">CVSS vetor</span><span class="mono">{{ x.cvssVector || '—' }}</span></div>
                          <div><span class="det-label">Publicado em</span><span>{{ fmtDate(x.publishedOn) }}</span></div>
                          <div><span class="det-label">Detectado em</span><span>{{ fmtDate(x.detectedAt) }}</span></div>
                          <div><span class="det-label">Disposição</span><span>{{ x.status }}</span></div>
                        </div>
                        <div class="det">
                          <span class="det-label">Observações por fonte</span>
                          <div class="obs">
                            @for (s of x.sources; track s.connectorConfigId) {
                              <div class="obs-row">
                                <span class="badge src" [class.res]="s.lifecycleState === 'Resolved'">{{ s.provider }}</span>
                                <span class="obs-name">{{ s.displayName }}</span>
                                <span class="obs-life">{{ s.lifecycleState === 'Open' ? 'Aberta' : 'Resolvida' }}</span>
                                <span class="obs-seen">visto {{ fmtDate(s.lastSeenAt) }}</span>
                                @if (s.products.length > 0) {
                                  <span class="obs-prod">
                                    {{ productLabel(s.products[0]) }}
                                    @if (s.totalProducts > s.products.length || s.productsTruncated) {
                                      <em>(+{{ s.totalProducts - 1 }} produto(s))</em>
                                    } @else if (s.totalProducts > 1) {
                                      <em>(+{{ s.totalProducts - 1 }})</em>
                                    }
                                  </span>
                                }
                              </div>
                            }
                          </div>
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
            <span class="pg-info">Página {{ page() }} de {{ pageCount() }} · {{ total() }} no total</span>
            <button type="button" class="ghost sm" (click)="nextPage()" [disabled]="page() >= pageCount()">Próxima →</button>
          </footer>
        }
      </div>
    </section>
  `,
  styles: [
    `
      .page { padding: 1.25rem 1.5rem 2rem; display: flex; flex-direction: column; gap: 1.1rem; }
      .page-head { display: flex; justify-content: space-between; align-items: flex-start; gap: 1rem; flex-wrap: wrap; }
      h1 { margin: 0; font-size: 1.35rem; letter-spacing: 0.02em; }
      .sub { margin: 0.35rem 0 0; max-width: 72ch; opacity: 0.72; font-size: 0.85rem; }
      .head-actions { display: flex; gap: 0.5rem; flex-wrap: wrap; }
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

  protected readonly data = signal<VulnerabilityList | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly stateFilter = signal<VulnerabilityLifecycleFilter>('open');
  protected readonly exploitFilter = signal<VulnerabilityExploitFilter>('all');
  protected readonly severityFilter = signal<string | null>(null);
  protected readonly connectorFilter = signal<string | null>(null);
  protected readonly search = signal('');
  protected readonly page = signal(1);
  protected readonly expanded = signal<Set<string>>(new Set());

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  protected readonly summary = computed(() => this.data()?.summary ?? null);
  protected readonly items = computed<VulnerabilityItem[]>(() => this.data()?.items ?? []);
  protected readonly total = computed(() => this.data()?.total ?? 0);
  protected readonly pageCount = computed(() => {
    const d = this.data();
    if (!d || d.pageSize <= 0) return 1;
    return Math.max(1, Math.ceil(d.total / d.pageSize));
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
        exploit: this.exploitFilter(),
        severity: this.severityFilter() ?? undefined,
        connectorId: this.connectorFilter() ?? undefined,
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

  protected productLabel(p: { product: string | null; vendor: string | null; version: string | null }): string {
    return [p.vendor, p.product, p.version].filter((s) => !!s).join(' ') || '—';
  }

  protected fmtDate(iso: string | null | undefined): string {
    if (!iso) return '—';
    const d = new Date(iso);
    return isNaN(d.getTime()) ? '—' : d.toLocaleString('pt-BR');
  }
}

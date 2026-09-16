import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { SoftwareInventoryService } from '../services/software-inventory.service';
import {
  SoftwareInstalledAssetPreview,
  SoftwareInventoryList,
  SoftwareObservationStateFilter,
  SoftwareProductListItem,
  softwareCollectionStatePt,
  softwareLifecyclePt,
  softwareReading,
} from '../models/software-inventory.models';

/** Estado de PAGINAÇÃO dos ativos relacionados a UM produto, expandido sob demanda. */
interface AssetState {
  items: SoftwareInstalledAssetPreview[];
  total: number;
  loadedPages: number;
  loading: boolean;
  error: string | null;
}

/**
 * [AEGIS-MVP-MICROSOFT-COVERAGE-01] Aba "Inventário de software" (antes rotulada "Software exposto": produto
 * instalado não é, por si, software exposto) — inventário de software observado pelo Microsoft
 * Defender (produto vendor+nome), correlacionado aos ativos já conhecidos pelo AEGIS. Consome
 * `GET /api/v1/software-inventory`: lista PRIORIZADA por exploit público/alerta ativo/fraquezas, com expansão
 * sob demanda dos ativos relacionados (sem N+1 inicial).
 *
 * Honestidade do produto: "Ainda não coletado" (nenhuma fonte com Software.Read.All sincronizou) é distinto de
 * "coletado sem achados"; estados loading/vazio/erro/parcial explícitos; ZERO fallback demonstrativo/zero
 * sintético. Software Inventory é evidência OPERACIONAL/de exposição — NÃO altera o AEGIS Score. Nada aqui é
 * gerado por IA: filtros/ordenação/contagens/status são 100% determinísticos.
 */
@Component({
  selector: 'app-software-inventory-tab',
  standalone: true,
  imports: [FormsModule],
  template: `
    <section class="sw-page">
      @if (!loading() && !error() && reading().hasData && reading().notice) {
        <p class="notice warn" role="status">{{ reading().notice }}</p>
      }

      <!-- ---------- Resumo ---------- -->
      <!-- [AEGIS-LANGUAGE-STATES-01] Sem leitura as contagens ficam "—" (nunca 0). Fraquezas, exploit e alerta são
           informações DA FONTE sobre o produto — cada uma com o próprio significado, nenhuma é comprometimento. -->
      <div class="cards">
        <div class="card">
          <span class="metric-label">Produtos observados</span>
          <span class="metric-value" [class.is-na]="!reading().hasData">{{ reading().hasData ? summary()!.totalProducts : '—' }}</span>
          <span class="metric-unit">
            @if (reading().hasData) {
              {{ summary()!.exposedInstallations }} instalação(ões) observada(s) na última leitura
            } @else {
              sem leitura da fonte
            }
          </span>
        </div>
        <div class="card">
          <span class="metric-label">Com vulnerabilidades conhecidas (fonte)</span>
          <span class="metric-value" [class.is-na]="!reading().hasData">{{ reading().hasData ? summary()!.productsWithWeaknesses : '—' }}</span>
        </div>
        <div class="card">
          <span class="metric-label">Com exploit público informado</span>
          <span class="metric-value warn" [class.is-na]="!reading().hasData">{{ reading().hasData ? summary()!.productsWithPublicExploit : '—' }}</span>
        </div>
        <div class="card">
          <span class="metric-label">Com alerta associado (fonte)</span>
          <span class="metric-value bad" [class.is-na]="!reading().hasData">{{ reading().hasData ? summary()!.productsWithActiveAlert : '—' }}</span>
        </div>
        <div class="card wide">
          <span class="metric-label">Última coleta</span>
          @if (summary()?.lastCollectedAt) {
            <span class="metric-value sm">{{ fmtDate(summary()?.lastCollectedAt) }}</span>
          } @else {
            <span class="metric-value sm is-na">Ainda não coletado</span>
          }
          @if ((summary()?.sources?.length ?? 0) > 0) {
            <div class="src-states">
              @for (s of summary()!.sources; track s.connectorConfigId) {
                <span class="src-chip" [class.ok]="s.collectionState === 'Available'" [class.warn]="s.collectionState === 'Partial'" [title]="s.lastAttemptDetail || ''">
                  {{ s.displayName }}: {{ statePt(s.collectionState) }}
                </span>
              }
            </div>
          } @else {
            <span class="metric-unit">Nenhuma fonte Microsoft Defender configurada.</span>
          }
        </div>
      </div>

      <!-- ---------- Filtros ---------- -->
      <div class="filter-bar">
        <div class="filter-row">
        <span class="filter-label">Situação</span>
        <div class="segmented" role="group" aria-label="Situação">
          @for (s of stateOptions; track s.value) {
            <button type="button" [class.active]="stateFilter() === s.value" [attr.aria-pressed]="stateFilter() === s.value" (click)="setState(s.value)">
              {{ s.label }}
            </button>
          }
        </div>
        <button type="button" class="filter-chip" [class.active]="exploitOnly()" [attr.aria-pressed]="exploitOnly()" (click)="toggleExploit()">Com exploit público</button>
        <button type="button" class="filter-chip" [class.active]="alertOnly()" [attr.aria-pressed]="alertOnly()" (click)="toggleAlert()">Com alerta associado</button>
        <button type="button" class="filter-chip" [class.active]="weaknessOnly()" [attr.aria-pressed]="weaknessOnly()" (click)="toggleWeakness()">Com vulnerabilidades conhecidas</button>
        </div>
        <div class="filter-row">
        <input
          class="search"
          type="search"
          placeholder="Buscar por produto ou vendor…"
          [ngModel]="searchTerm()"
          (ngModelChange)="onSearchInput($event)"
          aria-label="Buscar software por produto ou vendor"
        />
        </div>
      </div>

      <!-- ---------- Tabela ---------- -->
      <div class="panel flush">
        @if (loading()) {
          <div class="state" role="status"><span class="spinner" aria-hidden="true"></span><p>Carregando inventário de software…</p></div>
        } @else if (error()) {
          <div class="state error">
            <p class="err">⚠ {{ error() }}</p>
            <button type="button" class="ghost" (click)="retry()">Tentar novamente</button>
          </div>
        } @else if (!reading().hasData) {
          <div class="state empty">
            <p class="muted">{{ reading().notice }}</p>
            <p class="muted">
              O conector <strong>Microsoft Defender Vulnerability Management</strong> também coleta o inventário de
              software quando a permissão de aplicativo <strong>Software.Read.All</strong> estiver disponível —
              confira o estado em <strong>Configurações → Integrações</strong> e use <strong>Sincronizar agora</strong>.
            </p>
          </div>
        } @else if (items().length === 0) {
          <div class="state empty">
            @if (filtersActive()) {
              <p class="muted">Nenhum produto de software corresponde aos filtros atuais.</p>
            } @else {
              <p class="muted">Nenhum produto de software observado na última leitura da fonte.</p>
            }
          </div>
        } @else {
          <table class="data-table">
            <thead>
              <tr>
                <th>Produto</th>
                <th class="c-dev">Dispositivos</th>
                <th>Vulnerabilidades conhecidas</th>
                <th>Exploit / Alerta (fonte)</th>
                <th>Primeira ação</th>
                <th>Fonte</th>
                <th class="c-exp" aria-label="Detalhes"></th>
              </tr>
            </thead>
            <tbody>
              @for (p of items(); track p.id) {
                <tr class="row" [class.resolved]="p.effectiveState === 'Resolved'">
                  <td>
                    <strong class="title">{{ p.name }}</strong>
                    @if (p.effectiveState === 'Resolved') {
                      <span class="badge ok" title="A fonte deixou de observar instalações deste produto. Não é validação de remoção.">
                        {{ lifecycle(p.effectiveState) }}
                      </span>
                    }
                    <span class="meta mono">{{ p.vendor }}</span>
                  </td>
                  <td class="c-dev">
                    <strong>{{ p.openInstallationCount }}</strong>
                    <span class="meta">de {{ p.installedDeviceCount }} conhecido(s)</span>
                  </td>
                  <td>
                    @if (p.weaknessesCount > 0) {
                      <span class="badge warn">{{ p.weaknessesCount }} vulnerabilidade(s)</span>
                    } @else {
                      <span class="dim">Nenhuma informada</span>
                    }
                  </td>
                  <td class="c-exploit">
                    @if (p.publicExploit) {
                      <span class="badge bad">Exploit público</span>
                    }
                    @if (p.activeAlert) {
                      <span class="badge bad" title="Alerta ativo associado ao produto pela fonte — não é comprometimento confirmado">Alerta associado</span>
                    }
                    @if (!p.publicExploit && !p.activeAlert) {
                      <span class="dim">—</span>
                    }
                  </td>
                  <td><span class="meta">{{ p.firstAction }}</span></td>
                  <td class="c-src">
                    @for (s of p.sources; track s) {
                      <span class="badge src">{{ s }}</span>
                    }
                  </td>
                  <td class="c-exp">
                    <button type="button" class="linkbtn" (click)="toggleExpand(p.id)" [attr.aria-expanded]="expanded().has(p.id)">
                      {{ expanded().has(p.id) ? 'Ocultar' : 'Detalhes' }}
                    </button>
                  </td>
                </tr>
                @if (expanded().has(p.id)) {
                  <tr class="details-row">
                    <td colspan="7">
                      <div class="details">
                        <div class="det-grid">
                          <div><span class="det-label">Vendor</span><span class="mono">{{ p.vendor }}</span></div>
                          <div><span class="det-label">Índice de impacto (fonte)</span><span>{{ p.impactScore != null ? num(p.impactScore) : '—' }}</span></div>
                          <div><span class="det-label">Primeira observação</span><span>{{ fmtDate(p.firstSeenAt) }}</span></div>
                          <div><span class="det-label">Última observação</span><span>{{ fmtDate(p.lastSeenAt) }}</span></div>
                        </div>
                        <div class="det">
                          <span class="det-label">Ativos relacionados ({{ p.installedDeviceCount }})</span>
                          @let st = assetsByProduct(p.id);
                          @if (st?.loading && (st?.items?.length ?? 0) === 0) {
                            <p class="muted">Carregando ativos…</p>
                          } @else if (st?.error && (st?.items?.length ?? 0) === 0) {
                            <div class="occ-err">
                              <span class="err">⚠ {{ st?.error }}</span>
                              <button type="button" class="ghost sm" (click)="loadAssets(p.id)">Tentar novamente</button>
                            </div>
                          } @else {
                            <div class="obs">
                              @for (a of st?.items ?? []; track a.assetId) {
                                <div class="obs-row">
                                  <span class="obs-name">{{ a.assetName }}</span>
                                  <span class="obs-life">crít. {{ a.criticality }} · {{ a.subType || '—' }}</span>
                                  <span class="obs-prod">{{ a.version ? 'v' + a.version : 'versão não informada' }}</span>
                                  <span class="badge src" [class.res]="a.effectiveState === 'Resolved'">
                                    {{ lifecycle(a.effectiveState) }}
                                  </span>
                                </div>
                              } @empty {
                                <p class="muted">Nenhum ativo carregado.</p>
                              }
                            </div>
                            @if (st?.error) {
                              <div class="occ-err">
                                <span class="err sm">⚠ {{ st?.error }}</span>
                                <button type="button" class="linkbtn" (click)="loadAssets(p.id)">Tentar novamente</button>
                              </div>
                            }
                            @if (assetsHasMore(p.id)) {
                              <button type="button" class="ghost sm load-more" (click)="loadAssets(p.id)" [disabled]="st?.loading">
                                {{ st?.loading ? 'Carregando…' : 'Carregar mais (' + (st?.items?.length ?? 0) + ' de ' + (st?.total ?? 0) + ')' }}
                              </button>
                            } @else if ((st?.items?.length ?? 0) > 0) {
                              <p class="meta">Todos os {{ st?.total ?? 0 }} ativo(s) carregado(s).</p>
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
            <span class="range">Página {{ page() }} de {{ pageCount() }} · {{ total() }} produto(s)</span>
            <button type="button" class="ghost sm" (click)="prevPage()" [disabled]="page() <= 1">← Anterior</button>
            <button type="button" class="ghost sm" (click)="nextPage()" [disabled]="page() >= pageCount()">Próxima →</button>
          </footer>
        }
      </div>
    </section>
  `,
  styles: [
    `
      /* Cartões, filtros, tabela, badges, avisos, estados e botões: sistema visual global (styles.css). */
      .sw-page {
        display: flex;
        flex-direction: column;
        gap: var(--sp-6);
      }
      .card.wide {
        grid-column: span 2;
      }
      .metric-value.warn {
        color: var(--amber);
      }
      .metric-value.bad {
        color: var(--red-text);
      }
      .metric-value.is-na {
        color: var(--muted);
      }
      .src-states {
        display: flex;
        flex-wrap: wrap;
        gap: 6px;
        margin-top: 6px;
      }
      .src-chip {
        padding: 2px var(--sp-2);
        border: 1px solid var(--line-strong);
        border-radius: var(--radius-pill);
        font-size: var(--fs-meta);
        color: var(--text-2);
      }
      .src-chip.ok {
        color: var(--cyan);
        border-color: rgba(38, 224, 255, 0.4);
      }
      .src-chip.warn {
        color: var(--amber);
        border-color: rgba(255, 176, 32, 0.4);
      }
      .search {
        flex: 1 1 18rem;
        min-width: 0;
      }
      .err {
        color: var(--red-text);
      }

      .row.resolved {
        opacity: 0.6;
      }
      .c-dev {
        white-space: nowrap;
      }
      .c-exp {
        text-align: right;
        white-space: nowrap;
      }
      .badge.ok {
        margin-left: 6px;
      }
      .badge.bad,
      .badge.src {
        margin: 0 4px 4px 0;
      }
      .badge.src.res {
        opacity: 0.5;
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
      .obs-prod {
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
export class SoftwareInventoryTabComponent {
  private readonly api = inject(SoftwareInventoryService);

  // [AEGIS-LANGUAGE-STATES-01] "Resolved" do coletor = a fonte deixou de observar a instalação. Não comprova
  // remoção validada — por isso "Não mais observados", e não "Removidos".
  protected readonly stateOptions: { value: SoftwareObservationStateFilter; label: string }[] = [
    { value: 'open', label: 'Observados' },
    { value: 'resolved', label: 'Não mais observados' },
    { value: 'all', label: 'Todos' },
  ];

  protected readonly data = signal<SoftwareInventoryList | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly stateFilter = signal<SoftwareObservationStateFilter>('open');
  protected readonly exploitOnly = signal(false);
  protected readonly alertOnly = signal(false);
  protected readonly weaknessOnly = signal(false);
  protected readonly searchTerm = signal('');
  protected readonly page = signal(1);

  protected readonly expanded = signal<Set<string>>(new Set());
  protected readonly assetsByProductId = signal<Map<string, AssetState>>(new Map());

  private readonly ASSETS_PAGE_SIZE = 25;
  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  protected readonly summary = computed(() => this.data()?.summary ?? null);
  protected readonly items = computed<SoftwareProductListItem[]>(() => this.data()?.items ?? []);
  protected readonly total = computed(() => this.data()?.total ?? 0);
  protected readonly pageCount = computed(() => {
    const d = this.data();
    if (!d || d.pageSize <= 0) return 1;
    return Math.max(1, Math.ceil(d.total / d.pageSize));
  });

  protected readonly statePt = softwareCollectionStatePt;
  protected readonly lifecycle = softwareLifecyclePt;

  /** [AEGIS-LANGUAGE-STATES-01] Sem leitura × leitura (com ressalva de parcial / tentativa recente sem sucesso). */
  protected readonly reading = computed(() => softwareReading(this.summary()));
  protected readonly filtersActive = computed(
    () =>
      this.stateFilter() !== 'open' ||
      this.exploitOnly() ||
      this.alertOnly() ||
      this.weaknessOnly() ||
      this.searchTerm().trim() !== '',
  );

  constructor() {
    this.load();
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.expanded.set(new Set());
    this.assetsByProductId.set(new Map());
    this.api
      .list({
        state: this.stateFilter(),
        publicExploit: this.exploitOnly() || undefined,
        activeAlert: this.alertOnly() || undefined,
        withWeaknesses: this.weaknessOnly() || undefined,
        search: this.searchTerm().trim() || undefined,
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

  protected retry(): void {
    this.load();
  }

  protected setState(s: SoftwareObservationStateFilter): void {
    if (this.stateFilter() === s) return;
    this.stateFilter.set(s);
    this.page.set(1);
    this.load();
  }

  protected toggleExploit(): void {
    this.exploitOnly.update((v) => !v);
    this.page.set(1);
    this.load();
  }

  protected toggleAlert(): void {
    this.alertOnly.update((v) => !v);
    this.page.set(1);
    this.load();
  }

  protected toggleWeakness(): void {
    this.weaknessOnly.update((v) => !v);
    this.page.set(1);
    this.load();
  }

  protected onSearchInput(value: string): void {
    this.searchTerm.set(value);
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => {
      this.searchTimer = null;
      this.page.set(1);
      this.load();
    }, 300);
  }

  protected toggleExpand(productId: string): void {
    const isOpen = this.expanded().has(productId);
    this.expanded.update((set) => {
      const next = new Set(set);
      if (next.has(productId)) next.delete(productId);
      else next.add(productId);
      return next;
    });
    if (!isOpen && !this.assetsByProductId().has(productId)) {
      this.loadAssets(productId);
    }
  }

  protected loadAssets(productId: string): void {
    const cur = this.assetsByProductId().get(productId);
    if (cur?.loading) return;
    const nextPage = (cur?.loadedPages ?? 0) + 1;
    this.patchAssets(productId, {
      items: cur?.items ?? [],
      total: cur?.total ?? 0,
      loadedPages: cur?.loadedPages ?? 0,
      loading: true,
      error: null,
    });
    this.api.assets(productId, nextPage, this.ASSETS_PAGE_SIZE).subscribe({
      next: (page) => {
        const prev = this.assetsByProductId().get(productId);
        const base = prev?.items ?? [];
        const seen = new Set(base.map((i) => i.assetId));
        const merged = base.concat(page.items.filter((i) => !seen.has(i.assetId)));
        this.patchAssets(productId, { items: merged, total: page.total, loadedPages: nextPage, loading: false, error: null });
      },
      error: (err: Error) => {
        const prev = this.assetsByProductId().get(productId);
        this.patchAssets(productId, {
          items: prev?.items ?? [],
          total: prev?.total ?? 0,
          loadedPages: prev?.loadedPages ?? 0,
          loading: false,
          error: err.message || 'Falha ao carregar os ativos relacionados.',
        });
      },
    });
  }

  private patchAssets(productId: string, state: AssetState): void {
    this.assetsByProductId.update((m) => new Map(m).set(productId, state));
  }

  protected assetsByProduct(productId: string): AssetState | null {
    return this.assetsByProductId().get(productId) ?? null;
  }

  protected assetsHasMore(productId: string): boolean {
    const s = this.assetsByProductId().get(productId);
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

  protected num(n: number): string {
    return Number.isInteger(n) ? String(n) : n.toFixed(2);
  }

  protected fmtDate(iso: string | null | undefined): string {
    if (!iso) return '—';
    const d = new Date(iso);
    return isNaN(d.getTime()) ? '—' : d.toLocaleString('pt-BR');
  }
}

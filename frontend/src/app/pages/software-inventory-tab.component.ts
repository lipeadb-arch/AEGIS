import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { SoftwareInventoryService } from '../services/software-inventory.service';
import {
  SoftwareInstalledAssetPreview,
  SoftwareInventoryList,
  SoftwareObservationStateFilter,
  SoftwareProductListItem,
  softwareCollectionStatePt,
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
 * [AEGIS-MVP-MICROSOFT-COVERAGE-01] Aba "Software exposto" — inventário de software observado pelo Microsoft
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
      <!-- ---------- Resumo ---------- -->
      <div class="cards">
        <div class="card">
          <span class="card-label">Produtos observados</span>
          <span class="card-value">{{ summary()?.totalProducts ?? '—' }}</span>
          <span class="card-meta">{{ summary()?.exposedInstallations ?? 0 }} instalação(ões) aberta(s)</span>
        </div>
        <div class="card">
          <span class="card-label">Com fraquezas conhecidas</span>
          <span class="card-value">{{ summary()?.productsWithWeaknesses ?? '—' }}</span>
        </div>
        <div class="card">
          <span class="card-label">Com exploit público</span>
          <span class="card-value warn">{{ summary()?.productsWithPublicExploit ?? '—' }}</span>
        </div>
        <div class="card">
          <span class="card-label">Com alerta ativo</span>
          <span class="card-value bad">{{ summary()?.productsWithActiveAlert ?? '—' }}</span>
        </div>
        <div class="card wide">
          <span class="card-label">Última coleta</span>
          @if (summary()?.lastCollectedAt) {
            <span class="card-value sm">{{ fmtDate(summary()?.lastCollectedAt) }}</span>
          } @else {
            <span class="card-value sm muted">Ainda não coletado</span>
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
            <span class="card-meta">Nenhuma fonte Microsoft Defender configurada.</span>
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
        <button type="button" class="chip-btn" [class.active]="exploitOnly()" (click)="toggleExploit()">Com exploit público</button>
        <button type="button" class="chip-btn" [class.active]="alertOnly()" (click)="toggleAlert()">Com alerta ativo</button>
        <button type="button" class="chip-btn" [class.active]="weaknessOnly()" (click)="toggleWeakness()">Com fraquezas</button>
        <input
          class="search"
          type="search"
          placeholder="Buscar por produto ou vendor…"
          [ngModel]="searchTerm()"
          (ngModelChange)="onSearchInput($event)"
          aria-label="Buscar software por produto ou vendor"
        />
      </div>

      <!-- ---------- Tabela ---------- -->
      <div class="panel">
        @if (loading()) {
          <p class="muted">Carregando inventário de software…</p>
        } @else if (error()) {
          <div class="state error">
            <p class="err">⚠ {{ error() }}</p>
            <button type="button" class="ghost" (click)="retry()">Tentar novamente</button>
          </div>
        } @else if (summary()?.neverCollected) {
          <div class="state empty">
            <p class="muted">
              Ainda não coletado. O conector <strong>Microsoft Defender Vulnerability Management</strong> também
              coleta exposição de software quando a permissão de aplicativo <strong>Software.Read.All</strong>
              estiver disponível — confira o estado em <strong>Configurações → Integrações</strong> e use
              <strong>Sincronizar agora</strong>.
            </p>
          </div>
        } @else if (items().length === 0) {
          <div class="state empty">
            <p class="muted">Nenhum produto de software para o filtro atual.</p>
          </div>
        } @else {
          <table class="grid-table">
            <thead>
              <tr>
                <th>Produto</th>
                <th class="c-dev">Dispositivos</th>
                <th>Fraquezas</th>
                <th>Exploit / Alerta</th>
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
                      <span class="badge ok">Resolvido</span>
                    }
                    <span class="meta mono">{{ p.vendor }}</span>
                  </td>
                  <td class="c-dev">
                    <strong>{{ p.openInstallationCount }}</strong>
                    <span class="meta">de {{ p.installedDeviceCount }} conhecido(s)</span>
                  </td>
                  <td>
                    @if (p.weaknessesCount > 0) {
                      <span class="badge warn">{{ p.weaknessesCount }} fraqueza(s)</span>
                    } @else {
                      <span class="dim">Nenhuma</span>
                    }
                  </td>
                  <td class="c-exploit">
                    @if (p.publicExploit) {
                      <span class="badge bad">Exploit público</span>
                    }
                    @if (p.activeAlert) {
                      <span class="badge bad">Alerta ativo</span>
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
                    <button type="button" class="linkbtn" (click)="toggleExpand(p.id)">
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
                          <div><span class="det-label">Impacto</span><span>{{ p.impactScore != null ? num(p.impactScore) : '—' }}</span></div>
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
                                    {{ a.effectiveState === 'Open' ? 'Instalado' : 'Removido' }}
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
            <button type="button" class="ghost sm" (click)="prevPage()" [disabled]="page() <= 1">← Anterior</button>
            <span class="pg-info">Página {{ page() }} de {{ pageCount() }} · {{ total() }} produto(s)</span>
            <button type="button" class="ghost sm" (click)="nextPage()" [disabled]="page() >= pageCount()">Próxima →</button>
          </footer>
        }
      </div>
    </section>
  `,
  styles: [
    `
      .sw-page { display: flex; flex-direction: column; gap: 1.1rem; }
      .muted { opacity: 0.65; font-size: 0.85rem; }
      .dim { opacity: 0.4; }
      .err { color: #ff6b8a; font-size: 0.85rem; }

      .cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(11rem, 1fr)); gap: 0.75rem; }
      .card {
        background: color-mix(in srgb, var(--hud-cyan, #26e0ff) 4%, transparent);
        border: 1px solid color-mix(in srgb, var(--hud-cyan, #26e0ff) 22%, transparent);
        border-radius: 8px; padding: 0.8rem 0.95rem; display: flex; flex-direction: column; gap: 0.2rem;
      }
      .card.wide { grid-column: span 2; min-width: 0; }
      .card-label { font-size: 0.66rem; text-transform: uppercase; letter-spacing: 0.1em; opacity: 0.6; }
      .card-value { font-size: 1.6rem; font-weight: 600; }
      .card-value.warn { color: #f5a524; }
      .card-value.bad { color: #ff3d6a; }
      .card-value.sm { font-size: 1rem; }
      .card-value.muted { font-size: 1rem; opacity: 0.7; }
      .card-meta { font-size: 0.72rem; opacity: 0.6; }
      .src-states { display: flex; flex-wrap: wrap; gap: 0.3rem; margin-top: 0.3rem; }
      .src-chip {
        font-size: 0.68rem; padding: 0.1rem 0.45rem; border-radius: 4px; opacity: 0.75;
        border: 1px solid color-mix(in srgb, var(--hud-cyan, #26e0ff) 25%, transparent);
      }
      .src-chip.ok { color: var(--hud-cyan, #26e0ff); opacity: 1; }
      .src-chip.warn { color: #f5a524; opacity: 1; }

      .filters { display: flex; align-items: center; gap: 0.6rem; flex-wrap: wrap; }
      .seg { display: inline-flex; border: 1px solid color-mix(in srgb, var(--hud-cyan, #26e0ff) 26%, transparent); border-radius: 6px; overflow: hidden; }
      .seg-btn { background: transparent; border: 0; color: inherit; font: inherit; font-size: 0.8rem; padding: 0.35rem 0.8rem; cursor: pointer; }
      .seg-btn.active { background: color-mix(in srgb, var(--hud-cyan, #26e0ff) 20%, transparent); }
      .chip-btn {
        background: transparent; border: 1px solid color-mix(in srgb, var(--hud-cyan, #26e0ff) 26%, transparent);
        color: inherit; border-radius: 999px; padding: 0.3rem 0.7rem; font: inherit; font-size: 0.78rem; cursor: pointer;
      }
      .chip-btn.active { background: color-mix(in srgb, var(--hud-cyan, #26e0ff) 18%, transparent); border-color: var(--hud-cyan, #26e0ff); }
      .search {
        background: rgba(4, 8, 18, 0.6); border: 1px solid color-mix(in srgb, var(--hud-cyan, #26e0ff) 26%, transparent);
        border-radius: 5px; padding: 0.4rem 0.6rem; color: inherit; font: inherit; font-size: 0.85rem; flex: 1; min-width: 12rem;
      }

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
      .c-dev { white-space: nowrap; }
      .mono { font-family: ui-monospace, monospace; font-size: 0.82rem; }
      .badge { font-size: 0.62rem; padding: 0.05rem 0.4rem; border-radius: 3px; border: 1px solid currentColor; text-transform: uppercase; letter-spacing: 0.05em; }
      .badge.ok { color: var(--hud-cyan, #26e0ff); margin-left: 0.35rem; }
      .badge.warn { color: #f5a524; }
      .badge.bad { color: #ff3d6a; margin-right: 0.25rem; }
      .badge.src { color: #9aa7c7; margin-right: 0.2rem; }
      .badge.src.res { opacity: 0.5; }
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
      .obs-prod { opacity: 0.55; font-size: 0.72rem; }
      .occ-err { display: flex; align-items: center; gap: 0.6rem; flex-wrap: wrap; margin-top: 0.4rem; }
      .occ-err .err.sm { font-size: 0.75rem; }
      .load-more { margin-top: 0.5rem; }
      .pager { display: flex; align-items: center; justify-content: space-between; gap: 0.75rem; padding: 0.6rem 0.4rem 0.2rem; }
      .pg-info { font-size: 0.75rem; opacity: 0.65; }
      button.ghost {
        background: transparent; border: 1px solid color-mix(in srgb, var(--hud-cyan, #26e0ff) 30%, transparent);
        color: inherit; border-radius: 5px; padding: 0.4rem 0.8rem; font: inherit; font-size: 0.8rem; cursor: pointer;
      }
      button.sm { padding: 0.25rem 0.6rem; font-size: 0.74rem; }
      button:disabled { opacity: 0.5; cursor: not-allowed; }
    `,
  ],
})
export class SoftwareInventoryTabComponent {
  private readonly api = inject(SoftwareInventoryService);

  protected readonly stateOptions: { value: SoftwareObservationStateFilter; label: string }[] = [
    { value: 'open', label: 'Instalados' },
    { value: 'resolved', label: 'Removidos' },
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

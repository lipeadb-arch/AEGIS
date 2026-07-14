import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { AssetService } from '../services/asset.service';
import {
  ASSET_CATEGORIES,
  AssetCategory,
  AssetDto,
  RISK_LEVELS,
  RiskLevel,
  categoryLabel,
} from '../models/asset.models';
import { riskColor } from '../lib/scales';
import { environment } from '../../environments/environment';

/**
 * IDENTIFY (ID.AM) — inventário tático de ativos.
 * Smart data table sobre GET /api/v1/assets: filtros NIST combinados (categoria, risco,
 * criticidade, busca), paginação, e a coluna "Risco Associado" (score/nível da IA) em destaque.
 * Sem @angular/forms: usa eventos nativos + bindings [value]/[checked].
 */
@Component({
  selector: 'app-asset-inventory',
  standalone: true,
  imports: [DatePipe],
  template: `
    <div class="app">
      <header class="topbar">
        <div class="brand">
          <span class="mark">Inventário <b>de Ativos</b></span>
          <span class="sub">NIST CSF 2.0 · Identify (ID.AM) · Inventário Contínuo</span>
        </div>
        <div class="client">
          <span class="label">Ativos</span>
          <span class="name">{{ total() }}</span>
        </div>
      </header>

      <!-- Subtítulo tático da Função Identify (mesmo padrão dos painéis de pilar / Govern) -->
      <p class="id-description">{{ idDescription }}</p>

      <!-- ---- Barra de filtros combinados ---- -->
      <section class="panel filters">
        <div class="chips">
          @for (c of categories; track c.value) {
            <button
              type="button"
              class="chip"
              [class.on]="selectedCategories().has(c.value)"
              (click)="toggleCategory(c.value)"
            >
              {{ c.label }}
            </button>
          }
        </div>

        <div class="controls">
          <label class="ctl">
            <span>Risco</span>
            <select [value]="riskLevel() ?? ''" (change)="setRisk($any($event.target).value)">
              <option value="">Todos</option>
              @for (r of riskLevels; track r) {
                <option [value]="r">{{ r }}</option>
              }
            </select>
          </label>

          <label class="ctl">
            <span>Criticidade</span>
            <select [value]="criticality() ?? ''" (change)="setCriticality($any($event.target).value)">
              <option value="">Todas</option>
              @for (n of criticalities; track n) {
                <option [value]="n">{{ n }}</option>
              }
            </select>
          </label>

          <label class="ctl chk">
            <input type="checkbox" [checked]="activeOnly()" (change)="setActiveOnly($any($event.target).checked)" />
            <span>Somente ativos</span>
          </label>

          <input
            class="search"
            type="search"
            placeholder="Buscar nome / tipo / ref…"
            [value]="search()"
            (input)="onSearch($any($event.target).value)"
          />

          @if (hasAnyFilter()) {
            <button type="button" class="clear" (click)="clearFilters()">Limpar</button>
          }
        </div>
      </section>

      <!-- ---- Estados ---- -->
      @if (loadError()) {
        <div class="notice">
          <b>Falha ao carregar o inventário.</b> A API em <code>{{ apiBase }}</code> não respondeu —
          confira o endereço e o <code>tenantId</code>. O console traz o erro completo.
        </div>
      }

      <!-- ---- Tabela ---- -->
      <section class="panel table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Ativo</th>
              <th>Categoria</th>
              <th class="num">Crit.</th>
              <th>Risco Associado</th>
              <th>Responsável</th>
              <th>Origem</th>
              <th>Visto por último</th>
              <th class="num">Status</th>
            </tr>
          </thead>
          <tbody>
            @for (a of rows(); track a.id) {
              <tr>
                <td>
                  <div class="asset-name">{{ a.name }}</div>
                  @if (a.subType) {
                    <div class="asset-sub">{{ a.subType }}</div>
                  }
                </td>
                <td><span class="cat">{{ label(a.category) }}</span></td>
                <td class="num"><span class="crit crit-{{ a.criticality }}">{{ a.criticality }}</span></td>
                <td>
                  @if (a.riskLevel) {
                    <span
                      class="risk-pill"
                      [style.color]="riskColor(a.riskLevel)"
                      [style.borderColor]="riskColor(a.riskLevel)"
                    >
                      <span class="risk-dot" [style.background]="riskColor(a.riskLevel)"></span>
                      <b>{{ a.riskScore?.toFixed(0) }}</b> · {{ a.riskLevel }}
                    </span>
                  } @else {
                    <span class="risk-none">— não avaliado</span>
                  }
                </td>
                <td>{{ a.ownerName || '—' }}</td>
                <td><span class="src">{{ a.discoverySource }}</span></td>
                <td class="dim">{{ a.lastSeenAt ? (a.lastSeenAt | date: 'dd/MM/yy HH:mm') : '—' }}</td>
                <td class="num">
                  <span class="status" [class.off]="!a.isActive">{{ a.isActive ? 'Ativo' : 'Inativo' }}</span>
                </td>
              </tr>
            } @empty {
              <tr class="empty">
                <td colspan="8">
                  @if (loading()) {
                    Carregando inventário…
                  } @else {
                    Nenhum ativo encontrado com os filtros atuais.
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      </section>

      <!-- ---- Paginação ---- -->
      <footer class="pager">
        <span class="info">
          Página {{ page() }} de {{ totalPages() || 1 }} · {{ total() }} ativos
        </span>
        <div class="pg-ctl">
          <label class="ctl">
            <span>Por página</span>
            <select [value]="pageSize()" (change)="setPageSize($any($event.target).value)">
              @for (s of pageSizes; track s) {
                <option [value]="s">{{ s }}</option>
              }
            </select>
          </label>
          <button type="button" (click)="goTo(page() - 1)" [disabled]="page() <= 1">‹ Anterior</button>
          <button type="button" (click)="goTo(page() + 1)" [disabled]="page() >= (totalPages() || 1)">
            Próxima ›
          </button>
        </div>
      </footer>
    </div>
  `,
  styles: [
    `
      /* Subtítulo tático da Função (logo abaixo da topbar) — mutado, mesmo padrão do Govern. */
      .id-description {
        color: var(--muted);
        font-family: var(--sans);
        font-size: 13.5px;
        line-height: 1.6;
        margin: -14px 0 24px;
        max-width: 820px;
      }

      .filters { padding: 16px 18px; margin-bottom: 18px; display: flex; flex-direction: column; gap: 14px; }
      .chips { display: flex; flex-wrap: wrap; gap: 8px; }
      .chip {
        font-family: var(--mono); font-size: 11.5px; letter-spacing: 0.04em;
        color: var(--muted); background: var(--panel-2);
        border: 1px solid var(--line); border-radius: 999px; padding: 6px 14px; cursor: pointer;
        transition: 0.15s;
      }
      .chip:hover { color: var(--text); border-color: rgba(38, 224, 255, 0.4); }
      .chip.on {
        color: #05070f; font-weight: 600; border-color: transparent;
        background: var(--neon-h); box-shadow: 0 0 14px -3px rgba(38, 224, 255, 0.6);
      }
      .controls { display: flex; flex-wrap: wrap; align-items: center; gap: 14px; }
      .ctl { display: inline-flex; align-items: center; gap: 8px; font-family: var(--mono); font-size: 11px; color: var(--muted); text-transform: uppercase; letter-spacing: 0.1em; }
      .ctl.chk { text-transform: none; letter-spacing: 0.02em; cursor: pointer; }
      .ctl select, .search {
        font-family: var(--sans); font-size: 13px; color: var(--text); text-transform: none; letter-spacing: 0;
        background: var(--panel-2); border: 1px solid var(--line); border-radius: 9px; padding: 7px 10px;
      }
      .ctl select:focus, .search:focus { outline: none; border-color: rgba(38, 224, 255, 0.5); }
      .search { min-width: 240px; flex: 1; }
      .clear { font-family: var(--mono); font-size: 11px; color: var(--magenta); background: none; border: 1px solid rgba(255, 61, 154, 0.3); border-radius: 9px; padding: 7px 12px; cursor: pointer; }

      .table-wrap { padding: 6px 8px; overflow-x: auto; }
      table.grid { width: 100%; border-collapse: collapse; font-size: 13px; }
      table.grid thead th {
        font-family: var(--mono); font-size: 10.5px; text-transform: uppercase; letter-spacing: 0.12em;
        color: var(--muted); text-align: left; font-weight: 500;
        padding: 12px 14px; border-bottom: 1px solid var(--line);
      }
      table.grid th.num, table.grid td.num { text-align: center; }
      table.grid tbody td { padding: 13px 14px; border-bottom: 1px solid var(--line-2); vertical-align: middle; }
      table.grid tbody tr:hover td { background: rgba(38, 224, 255, 0.03); }
      .asset-name { font-weight: 600; color: var(--text); }
      .asset-sub { font-family: var(--mono); font-size: 11px; color: var(--muted); margin-top: 2px; }
      .cat { font-family: var(--mono); font-size: 11.5px; color: var(--cyan-2); }
      .dim { color: var(--muted); font-family: var(--mono); font-size: 11.5px; }
      .src { font-family: var(--mono); font-size: 11px; color: var(--muted); }

      .crit { display: inline-flex; align-items: center; justify-content: center; min-width: 22px; height: 22px; border-radius: 6px; font-family: var(--display); font-weight: 700; font-size: 12px; }
      .crit-1 { color: var(--cyan); background: rgba(38, 224, 255, 0.1); }
      .crit-2 { color: var(--amber); background: rgba(255, 176, 32, 0.1); }
      .crit-3 { color: #ff7a3d; background: rgba(255, 122, 61, 0.12); }
      .crit-4 { color: var(--red); background: rgba(255, 45, 111, 0.14); }

      .risk-pill {
        display: inline-flex; align-items: center; gap: 8px;
        font-family: var(--mono); font-size: 12px;
        padding: 5px 11px; border-radius: 999px; border: 1px solid currentColor;
        background: rgba(0, 0, 0, 0.25);
      }
      .risk-pill b { font-family: var(--display); font-weight: 700; }
      .risk-dot { width: 8px; height: 8px; border-radius: 50%; box-shadow: 0 0 10px 1px currentColor; }
      .risk-none { font-family: var(--mono); font-size: 11.5px; color: var(--muted); opacity: 0.7; }

      .status { font-family: var(--mono); font-size: 11px; color: var(--cyan); }
      .status.off { color: var(--muted); }
      tr.empty td { text-align: center; color: var(--muted); font-family: var(--mono); font-size: 12px; padding: 30px; }

      .notice {
        font-family: var(--mono); font-size: 11.5px; color: var(--red);
        border: 1px solid rgba(255, 45, 111, 0.3); background: rgba(255, 45, 111, 0.07);
        padding: 10px 14px; border-radius: 12px; margin-bottom: 18px;
      }
      .notice code { color: var(--text); background: rgba(255, 255, 255, 0.06); padding: 1px 5px; border-radius: 4px; }

      .pager { display: flex; align-items: center; justify-content: space-between; gap: 14px; margin-top: 18px; flex-wrap: wrap; }
      .pager .info { font-family: var(--mono); font-size: 11.5px; color: var(--muted); }
      .pg-ctl { display: flex; align-items: center; gap: 10px; }
      .pg-ctl button {
        font-family: var(--mono); font-size: 12px; color: var(--text);
        background: var(--panel-2); border: 1px solid var(--line); border-radius: 9px; padding: 7px 14px; cursor: pointer; transition: 0.15s;
      }
      .pg-ctl button:hover:not(:disabled) { border-color: rgba(38, 224, 255, 0.5); }
      .pg-ctl button:disabled { opacity: 0.35; cursor: not-allowed; }

      @media (max-width: 720px) { .search { min-width: 140px; } }
    `,
  ],
})
export class AssetInventoryComponent implements OnInit {
  private readonly svc = inject(AssetService);

  // ---- Dados ----
  rows = signal<AssetDto[]>([]);
  total = signal(0);
  totalPages = signal(0);
  page = signal(1);
  pageSize = signal(25);
  loading = signal(false);
  loadError = signal(false);

  // ---- Filtros ----
  selectedCategories = signal<Set<AssetCategory>>(new Set());
  riskLevel = signal<RiskLevel | null>(null);
  criticality = signal<number | null>(null);
  activeOnly = signal(false);
  search = signal('');

  hasAnyFilter = computed(
    () =>
      this.selectedCategories().size > 0 ||
      this.riskLevel() !== null ||
      this.criticality() !== null ||
      this.activeOnly() ||
      this.search().trim().length > 0,
  );

  // Constantes de UI expostas ao template.
  // Subtítulo tático da Função Identify (ID). ID NÃO faz parte de PILLARS (Identify tem tela própria de
  // inventário, não o painel de pilar genérico), então o texto vive aqui — a tela que representa a Função.
  // Mesmo padrão visual (mutado/sans) dos subtítulos dos painéis de pilar e do Govern.
  protected readonly idDescription =
    'A base para entender os riscos aos ativos, dados, pessoas e capacidades do negócio. O Aegis rastreia o ' +
    'inventário contínuo e calcula o Raio de Explosão, medindo o impacto real caso peças fundamentais sejam comprometidas.';
  protected readonly categories = ASSET_CATEGORIES;
  protected readonly riskLevels = RISK_LEVELS;
  protected readonly criticalities = [1, 2, 3, 4];
  protected readonly pageSizes = [10, 25, 50, 100];
  protected readonly riskColor = riskColor;
  protected readonly label = categoryLabel;
  protected readonly apiBase = environment.apiBase;

  private searchTimer?: ReturnType<typeof setTimeout>;

  ngOnInit(): void {
    this.load();
  }

  private load(): void {
    this.loading.set(true);
    this.svc
      .list({
        category: [...this.selectedCategories()],
        riskLevel: this.riskLevel(),
        criticality: this.criticality(),
        isActive: this.activeOnly() ? true : null,
        search: this.search(),
        page: this.page(),
        pageSize: this.pageSize(),
      })
      .subscribe({
        next: (res) => {
          this.rows.set(res.items);
          this.total.set(res.totalCount);
          this.totalPages.set(res.totalPages);
          this.loading.set(false);
          this.loadError.set(false);
        },
        error: (err) => {
          console.error('Falha ao carregar o inventário de ativos:', err);
          this.rows.set([]);
          this.loading.set(false);
          this.loadError.set(true);
        },
      });
  }

  /** Filtros reiniciam a paginação para a página 1 e recarregam. */
  private reload(): void {
    this.page.set(1);
    this.load();
  }

  toggleCategory(c: AssetCategory): void {
    const next = new Set(this.selectedCategories());
    next.has(c) ? next.delete(c) : next.add(c);
    this.selectedCategories.set(next);
    this.reload();
  }

  setRisk(value: string): void {
    this.riskLevel.set((value || null) as RiskLevel | null);
    this.reload();
  }

  setCriticality(value: string): void {
    this.criticality.set(value ? Number(value) : null);
    this.reload();
  }

  setActiveOnly(checked: boolean): void {
    this.activeOnly.set(checked);
    this.reload();
  }

  onSearch(term: string): void {
    this.search.set(term);
    clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => this.reload(), 300);
  }

  clearFilters(): void {
    this.selectedCategories.set(new Set());
    this.riskLevel.set(null);
    this.criticality.set(null);
    this.activeOnly.set(false);
    this.search.set('');
    this.reload();
  }

  setPageSize(value: string): void {
    this.pageSize.set(Number(value));
    this.reload();
  }

  goTo(p: number): void {
    const max = this.totalPages() || 1;
    if (p < 1 || p > max) return;
    this.page.set(p);
    this.load();
  }
}

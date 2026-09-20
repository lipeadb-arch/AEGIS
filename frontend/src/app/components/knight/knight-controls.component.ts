import { Component, computed, input, output } from '@angular/core';
import {
  EMPTY_FILTERS,
  KnightAssessment,
  KnightControlFilters,
  KnightIndicator,
  KnightIndicatorStatus,
  SeverityLevel,
  axesOf,
  describeFilters,
  filterOptions,
  findingSituation,
  findingTitle,
  matchesFilters,
  severityLabel,
  sortIndicatorsByRisk,
  statusLabel,
} from '../../models/knight.models';

const STATUSES: KnightIndicatorStatus[] = ['Exposed', 'Mitigated', 'Error', 'NotEvaluated', 'Passed', 'NotApplicable'];
const SEVERITIES: SeverityLevel[] = ['Critical', 'High', 'Medium', 'Low', 'Informational'];

/**
 * [AEGIS-KNIGHT-MULTICLOUD-01] Aba "Controles e findings": pesquisa e filtros COMBINÁVEIS (resultado,
 * severidade, serviço, domínio, framework), recorte aplicado sempre descrito em palavras e lista de controles
 * que abre o detalhe sem perder o contexto. O estado dos filtros pertence à página (e ao endereço) — este
 * componente só o exibe e pede mudanças.
 */
@Component({
  selector: 'app-knight-controls',
  standalone: true,
  template: `
    @let o = options();
    <div class="filter-bar" role="search">
      <div class="filter-row">
        <label class="fl grow">
          <span class="filter-label">Pesquisa</span>
          <input type="search" [value]="filters().q" (input)="set('q', $any($event.target).value)"
                 placeholder="Controle, código, serviço…" aria-label="Pesquisar controles" />
        </label>
        <label class="fl">
          <span class="filter-label">Resultado</span>
          <select [value]="filters().status" (change)="set('status', $any($event.target).value)">
            <option value="">Todos</option>
            <option value="findings">Findings (reprovado + mitigado)</option>
            @for (s of statuses; track s) { <option [value]="s">{{ statusLabel(s) }}</option> }
          </select>
        </label>
        <label class="fl">
          <span class="filter-label">Severidade</span>
          <select [value]="filters().severity" (change)="set('severity', $any($event.target).value)">
            <option value="">Todas</option>
            @for (s of severities; track s) { <option [value]="s">{{ severityLabel(s) }}</option> }
          </select>
        </label>
        <label class="fl">
          <span class="filter-label">Plataforma</span>
          <select [value]="filters().platform" (change)="set('platform', $any($event.target).value)">
            <option value="">Todas</option>
            @for (p of o.platforms; track p) { <option [value]="p">{{ p }}</option> }
          </select>
        </label>
        <label class="fl">
          <span class="filter-label">Serviço</span>
          <select [value]="filters().service" (change)="set('service', $any($event.target).value)">
            <option value="">Todos</option>
            @for (s of o.services; track s) { <option [value]="s">{{ s }}</option> }
          </select>
        </label>
        <label class="fl">
          <span class="filter-label">Domínio</span>
          <select [value]="filters().domain" (change)="set('domain', $any($event.target).value)">
            <option value="">Todos</option>
            @for (d of o.domains; track d.key) { <option [value]="d.key">{{ d.label }}</option> }
          </select>
        </label>
        <label class="fl">
          <span class="filter-label">Framework</span>
          <select [value]="filters().framework" (change)="set('framework', $any($event.target).value)">
            <option value="">Todos</option>
            @for (f of o.frameworks; track f) { <option [value]="f">{{ f }}</option> }
          </select>
        </label>
        <button type="button" class="ghost sm clear" (click)="filtersChange.emit(empty)">Limpar filtros</button>
      </div>
      <p class="recorte" role="status" aria-live="polite">
        Mostrando <b>{{ visible().length }}</b> de {{ assessment().indicators.length }} controle(s) · Recorte: {{ recorte() }}
        · os filtros afetam só esta visualização; o relatório exportado é sempre a avaliação completa.
      </p>
    </div>

    @if (visible().length === 0) {
      <p class="muted empty">Nenhum controle corresponde aos filtros. Use “Limpar filtros”.</p>
    } @else {
      <ul class="ctl-list">
        @for (i of visible(); track i.indicatorId) {
          <li>
            <button type="button" class="ctl" [class.open]="selected() === i.indicatorId"
                    [attr.aria-expanded]="selected() === i.indicatorId" (click)="select.emit(i.indicatorId)">
              <span class="ctl-main">
                <span class="tt">{{ title(i) }}</span>
                <span class="sit">{{ situation(i) }}</span>
                <span class="code">{{ i.indicatorId }} · {{ axes(i).service }} · {{ axes(i).domainLabel }}</span>
              </span>
              <span class="tags">
                <span class="st" [class]="i.status">{{ statusLabel(i.status) }}</span>
                <span class="sev" [class]="i.severity">{{ severityLabel(i.severity) }}</span>
                @if (i.status === 'Exposed' || i.status === 'Mitigated') {
                  @if (i.affectedComposition) { <span class="aff">{{ i.affectedComposition }}</span> }
                  @else if (i.affectedObjectCount > 0) { <span class="aff"><b>{{ i.affectedObjectCount }}</b> afetado(s)</span> }
                  @else { <span class="aff">configuração do locatário</span> }
                }
              </span>
            </button>
          </li>
        }
      </ul>
    }
  `,
  styles: [
    `
      :host { display: flex; flex-direction: column; gap: var(--sp-3); }
      .fl { display: flex; flex-direction: column; gap: 4px; min-width: 140px; }
      .fl.grow { flex: 1 1 220px; }
      .fl input, .fl select { min-height: var(--control-h); padding: 0 10px; border: 1px solid var(--line-strong);
        border-radius: var(--radius-sm); background: var(--panel-2); color: var(--text); }
      .fl input:focus-visible, .fl select:focus-visible { outline: none; box-shadow: var(--field-focus); border-color: var(--cyan); }
      .clear { align-self: flex-end; }
      .recorte { margin: 0; font-size: var(--fs-sm); color: var(--text-2); }
      .ctl-list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 6px; }
      .ctl { display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: 12px; align-items: center; width: 100%;
        padding: var(--sp-3) 14px; border: 1px solid var(--line); border-radius: var(--radius); background: rgba(122, 145, 190, 0.04);
        color: var(--text); text-align: left; cursor: pointer; }
      .ctl:hover { border-color: rgba(38, 224, 255, 0.35); }
      .ctl:focus-visible { outline: none; box-shadow: var(--focus); }
      .ctl.open { border-color: var(--cyan); background: var(--tint-cyan); }
      .ctl-main { display: flex; flex-direction: column; gap: 3px; min-width: 0; }
      .tt { font-weight: 500; }
      .sit { font-size: var(--fs-meta); color: var(--text-2); line-height: 1.45; }
      .code { font-family: var(--mono); font-size: var(--fs-caps); color: var(--muted); }
      .tags { display: flex; flex-wrap: wrap; gap: 6px; justify-content: flex-end; align-items: center; }
      .st, .sev { padding: 2px 8px; border-radius: var(--radius-pill); font-size: var(--fs-caps); font-weight: 600; white-space: nowrap; }
      .st.Exposed { color: var(--red-text); background: var(--tint-red); }
      .st.Mitigated { color: var(--amber); background: var(--tint-amber); }
      .st.Passed { color: var(--cyan); background: var(--tint-cyan); }
      .st.Error { color: var(--violet-text); background: var(--tint-violet); }
      .st.NotEvaluated, .st.NotApplicable { color: var(--text-2); background: var(--hover); }
      .sev { border: 1px solid currentColor; }
      .sev.Critical { color: var(--red-text); } .sev.High { color: #ff9a3d; } .sev.Medium { color: var(--amber); }
      .sev.Low { color: var(--cyan); } .sev.Informational { color: var(--text-2); }
      .aff { font-size: var(--fs-meta); color: var(--text-2); }
      .empty { margin: 0; }
      @media (max-width: 900px) { .ctl { grid-template-columns: minmax(0, 1fr); } .tags { justify-content: flex-start; } }
    `,
  ],
})
export class KnightControlsComponent {
  readonly assessment = input.required<KnightAssessment>();
  readonly filters = input<KnightControlFilters>(EMPTY_FILTERS);
  readonly selected = input<string | null>(null);
  readonly filtersChange = output<KnightControlFilters>();
  readonly select = output<string>();

  protected readonly empty = EMPTY_FILTERS;
  protected readonly statuses = STATUSES;
  protected readonly severities = SEVERITIES;
  protected readonly statusLabel = statusLabel;
  protected readonly severityLabel = severityLabel;
  protected readonly title = findingTitle;
  protected readonly situation = findingSituation;
  protected readonly axes = axesOf;

  readonly options = computed(() => filterOptions(this.assessment()));
  readonly visible = computed(() =>
    sortIndicatorsByRisk(this.assessment().indicators).filter((i: KnightIndicator) => matchesFilters(i, this.filters())),
  );
  readonly recorte = computed(() => describeFilters(this.filters(), this.assessment()));

  set<K extends keyof KnightControlFilters>(key: K, value: string): void {
    this.filtersChange.emit({ ...this.filters(), [key]: value } as KnightControlFilters);
  }
}

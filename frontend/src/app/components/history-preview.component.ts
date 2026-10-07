import { Component, computed, input, output } from '@angular/core';
import {
  FrozenPostureHistory,
  HistoryWindowRequest,
  PostureMonthlyHistory,
  frozenAsSeries,
  monthShort,
} from '../models/posture-history.models';
import { MonthlyEvolutionComponent } from './monthly-evolution.component';

/**
 * [AEGIS-ASSESSMENT-VISUALS-01] Prévia do histórico mensal que a publicação vai CONGELAR no relatório: a identidade da série
 * (instrumento, fonte ou composição, avaliação e escopo, metodologia, catálogo e base da cobertura), o período escolhido e os
 * pontos — o desta publicação marcado. A publicação confere a impressão digital desta prévia; se a série mudar antes da
 * confirmação, o servidor recusa e a tela mostra a série nova.
 */
@Component({
  selector: 'app-history-preview',
  standalone: true,
  imports: [MonthlyEvolutionComponent],
  template: `
    <section class="hp" aria-labelledby="hp-t">
      <h3 id="hp-t">Histórico que será congelado no relatório</h3>
      @if (notice()) { <p class="notice warn" role="status">{{ notice() }}</p> }
      @if (error()) {
        <p class="notice error" role="alert">{{ error() }}</p>
      } @else if (loading() || !history()) {
        <div class="state" role="status"><span class="spinner" aria-hidden="true"></span><p>Montando a prévia do histórico…</p></div>
      } @else {
        @let h = history()!;
        <dl class="ident">
          <div><dt>Série</dt><dd>{{ h.series.label }}</dd></div>
          <div><dt>Instrumento</dt><dd>{{ h.series.instrument }}</dd></div>
          @if (h.series.composition?.length) { <div><dt>Composição</dt><dd>{{ h.series.composition!.join(', ') }}</dd></div> }
          <div><dt>{{ h.series.type === 'NistMaturity' ? 'Metodologia · catálogo' : 'Fórmula · catálogo' }}</dt><dd>{{ h.series.formulaVersion }} · {{ h.series.catalogVersion }}</dd></div>
          <div><dt>Base da cobertura</dt><dd>{{ h.series.coverageBasis }}</dd></div>
          <div><dt>Esta publicação</dt><dd>{{ h.includesThisPublication ? 'é o ponto de ' + monthShort(h.publicationMonth) : 'fica fora do período escolhido (' + monthShort(h.from) + ' a ' + monthShort(h.until) + ')' }}</dd></div>
        </dl>
        @for (r of h.relatedSeries; track r) { <p class="notice">{{ r }}</p> }
        <app-monthly-evolution [history]="asHistory()" [period]="window()" [openTables]="true" (periodChange)="windowChange.emit($event)" />
      }
      <!-- Ações de quem usa a prévia (confirmar/cancelar), abaixo da série. -->
      <div class="hp-actions"><ng-content /></div>
    </section>
  `,
  styles: [
    `
      .hp { border: 1px solid var(--line); border-radius: var(--radius); padding: var(--sp-4); background: var(--panel-2); min-width: 0; }
      h3 { margin: 0 0 var(--sp-3); font-size: var(--fs-panel); }
      .ident { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 240px), 1fr)); gap: var(--sp-2) var(--sp-4); margin: 0 0 var(--sp-3); }
      .ident dt { font-size: var(--fs-meta); color: var(--muted); }
      .ident dd { margin: 0; font-size: var(--fs-sm); overflow-wrap: anywhere; }
      .notice { margin-bottom: var(--sp-3); }
      .hp-actions { display: flex; flex-wrap: wrap; gap: var(--sp-2); }
      .hp-actions:empty { display: none; }
    `,
  ],
})
export class HistoryPreviewComponent {
  readonly history = input<FrozenPostureHistory | null>(null);
  readonly loading = input(false);
  readonly error = input<string | null>(null);
  /** Aviso acima da série (ex.: a série mudou e a prévia foi relida). */
  readonly notice = input<string | null>(null);
  readonly window = input<HistoryWindowRequest>({ months: 12, until: null });
  readonly windowChange = output<HistoryWindowRequest>();

  protected readonly monthShort = monthShort;
  protected readonly asHistory = computed<PostureMonthlyHistory | null>(() => {
    const h = this.history();
    return h ? { criterion: h.criterion, months: h.months, series: [frozenAsSeries(h)] } : null;
  });
}

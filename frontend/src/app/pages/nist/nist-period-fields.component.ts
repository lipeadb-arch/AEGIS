import { Component, computed, model } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { NistPeriodKind, cyclePeriodProblem, monthPeriod, quarterPeriod } from '../../models/nist.models';

export interface NistPeriodValue {
  kind: NistPeriodKind;
  start: string;
  end: string;
}

/**
 * [AEGIS-NIST-JOURNEY-02] Período de uma rodada: mês civil, trimestre civil ou outro intervalo (até três anos). As regras são
 * as mesmas do servidor; o problema aparece ao lado do campo, e o servidor continua sendo a autoridade.
 */
@Component({
  selector: 'app-nist-period-fields',
  standalone: true,
  imports: [FormsModule],
  template: `
    <fieldset class="period">
      <legend>Período da rodada</legend>
      <div class="kinds" role="radiogroup" aria-label="Tipo de período">
        <label><input type="radio" name="pk-{{ uid }}" [checked]="value().kind === 'Monthly'" (change)="setKind('Monthly')" /> Mensal</label>
        <label><input type="radio" name="pk-{{ uid }}" [checked]="value().kind === 'Quarterly'" (change)="setKind('Quarterly')" /> Trimestral</label>
        <label><input type="radio" name="pk-{{ uid }}" [checked]="value().kind === 'Other'" (change)="setKind('Other')" /> Outro período</label>
      </div>
      <div class="row">
        @switch (value().kind) {
          @case ('Monthly') {
            <label class="field"><span class="field-label">Mês</span>
              <input type="month" [ngModel]="value().start.slice(0, 7)" (ngModelChange)="setMonth($event)" name="pm-{{ uid }}" /></label>
          }
          @case ('Quarterly') {
            <label class="field"><span class="field-label">Ano</span>
              <input type="number" min="2000" max="2100" [ngModel]="year()" (ngModelChange)="setQuarter($event, quarter())" name="py-{{ uid }}" /></label>
            <label class="field"><span class="field-label">Trimestre</span>
              <select [ngModel]="quarter()" (ngModelChange)="setQuarter(year(), +$event)" name="pq-{{ uid }}">
                <option [ngValue]="1">1º (jan–mar)</option><option [ngValue]="2">2º (abr–jun)</option>
                <option [ngValue]="3">3º (jul–set)</option><option [ngValue]="4">4º (out–dez)</option>
              </select></label>
          }
          @default {
            <label class="field"><span class="field-label">Início</span>
              <input type="date" [ngModel]="value().start" (ngModelChange)="patch({ start: $event })" name="ps-{{ uid }}" /></label>
            <label class="field"><span class="field-label">Fim</span>
              <input type="date" [ngModel]="value().end" (ngModelChange)="patch({ end: $event })" name="pe-{{ uid }}" /></label>
          }
        }
      </div>
      @if (problem(); as pb) { <p class="hint warn-text" role="status">{{ pb }}</p> }
    </fieldset>
  `,
  styles: [
    `
      .period { border: 0; padding: 0; margin: 0; min-width: 0; }
      .period legend { font-weight: 600; margin-bottom: 6px; }
      .kinds { display: flex; flex-wrap: wrap; gap: var(--sp-3); font-size: var(--fs-sm); }
      .kinds label { display: flex; align-items: center; gap: 6px; }
      .row { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 180px), 1fr)); gap: var(--sp-3); margin-top: var(--sp-2); }
      .warn-text { color: var(--warn, inherit); }
    `,
  ],
})
export class NistPeriodFieldsComponent {
  private static seq = 0;
  protected readonly uid = ++NistPeriodFieldsComponent.seq;

  readonly value = model.required<NistPeriodValue>();

  protected readonly year = computed(() => Number(this.value().start.slice(0, 4)) || new Date().getFullYear());
  protected readonly quarter = computed(() => Math.floor(((Number(this.value().start.slice(5, 7)) || 1) - 1) / 3) + 1);
  protected readonly problem = computed(() => cyclePeriodProblem(this.value().kind, this.value().start, this.value().end));

  protected setKind(kind: NistPeriodKind): void {
    const v = this.value();
    const y = this.year();
    const m = Number(v.start.slice(5, 7)) || 1;
    if (kind === 'Monthly') this.value.set({ kind, ...monthPeriod(y, m) });
    else if (kind === 'Quarterly') this.value.set({ kind, ...quarterPeriod(y, Math.floor((m - 1) / 3) + 1) });
    else this.value.set({ ...v, kind });
  }

  protected setMonth(ym: string): void {
    const [y, m] = (ym ?? '').split('-').map(Number);
    if (y && m) this.value.set({ kind: 'Monthly', ...monthPeriod(y, m) });
  }

  protected setQuarter(y: number, q: number): void {
    if (y >= 2000 && y <= 2100 && q >= 1 && q <= 4) this.value.set({ kind: 'Quarterly', ...quarterPeriod(y, q) });
  }

  protected patch(p: Partial<NistPeriodValue>): void {
    this.value.set({ ...this.value(), ...p });
  }
}

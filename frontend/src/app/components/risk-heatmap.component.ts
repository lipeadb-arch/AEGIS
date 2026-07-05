import { Component, computed, input } from '@angular/core';
import { HeatCell } from '../models/dashboard.models';
import { heatColor } from '../lib/scales';

const PROBS = [4, 3, 2, 1];
const IMPACTS = [1, 2, 3, 4];

interface Cell {
  impact: number;
  count: number;
  bg: string;
  color: string;
}
interface HeatRow {
  prob: number;
  cells: Cell[];
}

@Component({
  selector: 'app-risk-heatmap',
  standalone: true,
  template: `
    <div>
      <div class="heat">
        @for (row of rows(); track row.prob) {
          <div class="axis">{{ row.prob }}</div>
          @for (cell of row.cells; track cell.impact) {
            <div
              class="cell"
              [style.background]="cell.bg"
              [style.color]="cell.color"
            >
              {{ cell.count || '' }}
            </div>
          }
        }
      </div>
      <div class="heat-x">
        <div class="axis"></div>
        @for (i of impacts; track i) {
          <div class="axis">{{ i }}</div>
        }
      </div>
      <div class="heat-titles">
        <span class="axis-title">↑ Probabilidade</span>
        <span class="axis-title">Impacto →</span>
      </div>
    </div>
  `,
})
export class RiskHeatmapComponent {
  data = input.required<HeatCell[]>();
  readonly impacts = IMPACTS;

  rows = computed<HeatRow[]>(() => {
    const lookup = new Map<string, number>();
    for (const c of this.data()) lookup.set(`${c.probability}:${c.impact}`, c.count);

    return PROBS.map((prob) => ({
      prob,
      cells: IMPACTS.map((impact) => {
        const count = lookup.get(`${prob}:${impact}`) ?? 0;
        return {
          impact,
          count,
          bg: count > 0 ? heatColor(prob, impact) : '#0e1422',
          color: count > 0 ? '#05070f' : '#3a4560',
        };
      }),
    }));
  });
}

import { Component, computed, input } from '@angular/core';
import { GapPoint } from '../models/dashboard.models';

interface GapRow {
  code: string;
  name: string;
  curPct: number;
  gapPct: number;
  gap: number;
}

/**
 * Barras horizontais empilhadas (atual + gap até o alvo) numa escala 0–5.
 * Reimplementa o BarChart vertical do Recharts em HTML/CSS, sem dependências.
 */
@Component({
  selector: 'app-gap-chart',
  standalone: true,
  template: `
    <div class="gap">
      @for (r of rows(); track r.code) {
        <div class="gap-row">
          <span class="gap-lab">{{ r.name }}</span>
          <span class="gap-track" [title]="'Atual ' + r.curPct / 20 + ' · gap ' + r.gap.toFixed(1)">
            <i class="cur" [style.width.%]="r.curPct"></i>
            <i class="seg" [style.width.%]="r.gapPct"></i>
          </span>
          <span class="gap-val">{{ r.gap.toFixed(1) }}</span>
        </div>
      }
    </div>
  `,
})
export class GapChartComponent {
  data = input.required<GapPoint[]>();

  rows = computed<GapRow[]>(() =>
    this.data().map((d) => ({
      code: d.code,
      name: d.name,
      curPct: (d.current / 5) * 100,
      gapPct: (Math.max(0, d.gap) / 5) * 100,
      gap: d.gap,
    })),
  );
}

import { Component, computed, input } from '@angular/core';
import { RiskLevelCount } from '../models/dashboard.models';
import { riskColor } from '../lib/scales';

const ORDER = ['Critico', 'Alto', 'Medio', 'Baixo'];
const LABEL: Record<string, string> = {
  Critico: 'Crítico',
  Alto: 'Alto',
  Medio: 'Médio',
  Baixo: 'Baixo',
};

interface LevelRow {
  level: string;
  label: string;
  count: number;
  pct: number;
  color: string;
}

@Component({
  selector: 'app-risk-levels',
  standalone: true,
  template: `
    <div class="levels">
      @for (r of rows(); track r.level) {
        <div class="level-row">
          <span class="lab">{{ r.label }}</span>
          <span class="bar">
            <i
              [style.width.%]="r.pct"
              [style.background]="r.color"
              [style.boxShadow]="'0 0 10px -2px ' + r.color"
            ></i>
          </span>
          <span class="cnt">{{ r.count }}</span>
        </div>
      }
    </div>
  `,
})
export class RiskLevelsComponent {
  data = input.required<RiskLevelCount[]>();

  rows = computed<LevelRow[]>(() => {
    const byLevel = new Map(this.data().map((d) => [d.level, d.count]));
    const max = Math.max(1, ...this.data().map((d) => d.count));
    return ORDER.map((level) => {
      const count = byLevel.get(level) ?? 0;
      return {
        level,
        label: LABEL[level] ?? level,
        count,
        pct: (count / max) * 100,
        color: riskColor(level),
      };
    });
  });
}

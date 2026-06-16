import { Component, computed, input } from '@angular/core';
import { Icr } from '../models/dashboard.models';
import { icrColor } from '../lib/scales';

const CX = 100;
const CY = 100;
const R = 80;

/**
 * Gauge semicircular do ICR (0–100). Reimplementa o RadialBarChart do Recharts
 * desenhando a trilha e o arco de valor como polilinhas SVG amostradas.
 */
@Component({
  selector: 'app-icr-gauge',
  standalone: true,
  template: `
    <div class="gauge-wrap">
      <svg viewBox="0 0 200 124" width="100%" height="200" preserveAspectRatio="xMidYMid meet">
        <path
          [attr.d]="trackPath"
          fill="none"
          stroke="#1a1a28"
          stroke-width="20"
          stroke-linecap="round"
        />
        <path
          [attr.d]="valuePath()"
          fill="none"
          [attr.stroke]="color()"
          stroke-width="20"
          stroke-linecap="round"
        />
        <text
          x="100"
          y="86"
          text-anchor="middle"
          [attr.fill]="color()"
          font-family="JetBrains Mono, monospace"
          font-weight="700"
          font-size="36"
        >
          {{ icr().score.toFixed(0) }}
        </text>
        <text
          x="100"
          y="108"
          text-anchor="middle"
          fill="#9a9ab4"
          font-family="Inter, sans-serif"
          font-size="12"
        >
          ICR · {{ icr().band }}
        </text>
      </svg>
    </div>
  `,
})
export class IcrGaugeComponent {
  icr = input.required<Icr>();

  color = computed(() => icrColor(this.icr().band));
  readonly trackPath = this.arc(1);
  valuePath = computed(() => this.arc(clamp01(this.icr().score / 100)));

  /** Arco da semicircunferência superior (180°→0°) até a fração `frac`, como polilinha. */
  private arc(frac: number): string {
    const steps = 64;
    const pts: string[] = [];
    for (let i = 0; i <= steps; i++) {
      const t = (i / steps) * frac;
      const ang = Math.PI - t * Math.PI; // π (esquerda) → 0 (direita)
      const x = CX + R * Math.cos(ang);
      const y = CY - R * Math.sin(ang);
      pts.push(`${x.toFixed(2)} ${y.toFixed(2)}`);
    }
    return 'M ' + pts.join(' L ');
  }
}

function clamp01(x: number): number {
  return x < 0 ? 0 : x > 1 ? 1 : x;
}

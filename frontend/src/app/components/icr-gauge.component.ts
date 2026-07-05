import { Component, computed, input } from '@angular/core';
import { Icr } from '../models/dashboard.models';
import { icrColor } from '../lib/scales';

const CX = 100;
const CY = 100;
const R = 78;
const START_DEG = 225; // canto inferior-esquerdo
const SWEEP_DEG = 270; // varredura no sentido horário, deixando um vão de 90° embaixo

/**
 * Anel neon do ICR (0–100). Arco de 270° com trilha esmaecida e valor em
 * gradiente cyan→magenta, com brilho tingido pela banda. SVG puro, sem libs.
 */
@Component({
  selector: 'app-icr-gauge',
  standalone: true,
  template: `
    <div class="gauge-wrap">
      <svg viewBox="0 0 200 200" width="100%" height="216" preserveAspectRatio="xMidYMid meet">
        <defs>
          <linearGradient id="icrGrad" x1="0" y1="1" x2="1" y2="0">
            <stop offset="0%" stop-color="#26e0ff" />
            <stop offset="55%" stop-color="#8b5cff" />
            <stop offset="100%" stop-color="#ff3d9a" />
          </linearGradient>
        </defs>
        <path
          [attr.d]="trackPath"
          fill="none"
          stroke="#141b2c"
          stroke-width="14"
          stroke-linecap="round"
        />
        <path
          [attr.d]="valuePath()"
          fill="none"
          stroke="url(#icrGrad)"
          stroke-width="14"
          stroke-linecap="round"
          [style.filter]="'drop-shadow(0 0 7px ' + glow() + ')'"
        />
        <text
          x="100"
          y="102"
          text-anchor="middle"
          fill="#eaf1ff"
          font-family="Orbitron, JetBrains Mono, monospace"
          font-weight="700"
          font-size="42"
          [style.filter]="'drop-shadow(0 0 12px ' + glow() + ')'"
        >
          {{ icr().score.toFixed(0) }}
        </text>
        <text
          x="100"
          y="126"
          text-anchor="middle"
          fill="#8791a8"
          font-family="JetBrains Mono, monospace"
          font-size="12"
          letter-spacing="1"
        >
          ICR · {{ icr().band }}
        </text>
      </svg>
    </div>
  `,
})
export class IcrGaugeComponent {
  icr = input.required<Icr>();

  glow = computed(() => hexAlpha(icrColor(this.icr().band), 0.55));
  readonly trackPath = this.arc(1);
  valuePath = computed(() => this.arc(clamp01(this.icr().score / 100)));

  /** Arco de 270° (225°→-45°, sentido horário) até a fração `frac`, como polilinha. */
  private arc(frac: number): string {
    const steps = 96;
    const pts: string[] = [];
    for (let i = 0; i <= steps; i++) {
      const deg = START_DEG - (i / steps) * frac * SWEEP_DEG;
      const ang = (deg * Math.PI) / 180;
      const x = CX + R * Math.cos(ang);
      const y = CY - R * Math.sin(ang); // y para baixo: 90° aponta para cima
      pts.push(`${x.toFixed(2)} ${y.toFixed(2)}`);
    }
    return 'M ' + pts.join(' L ');
  }
}

function clamp01(x: number): number {
  return x < 0 ? 0 : x > 1 ? 1 : x;
}

/** Converte um hex #rrggbb em rgba() com a opacidade dada, para o brilho do arco. */
function hexAlpha(hex: string, a: number): string {
  const m = /^#?([0-9a-f]{6})$/i.exec(hex.trim());
  if (!m) return hex;
  const n = parseInt(m[1], 16);
  return `rgba(${(n >> 16) & 255}, ${(n >> 8) & 255}, ${n & 255}, ${a})`;
}

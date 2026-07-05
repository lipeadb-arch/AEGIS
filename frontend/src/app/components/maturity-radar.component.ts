import { Component, computed, input } from '@angular/core';
import { RadarPoint } from '../models/dashboard.models';

const SIZE = 300;
const CX = SIZE / 2;
const CY = SIZE / 2;
const R = 104;
const RINGS = [1, 2, 3, 4, 5];

interface Axis {
  x: number;
  y: number;
  lx: number;
  ly: number;
  anchor: string;
  label: string;
}

/**
 * Radar de maturidade por função NIST (escala 0–5), com polígono "Atual" e "Alvo".
 * Reimplementa o RadarChart do Recharts em SVG puro.
 */
@Component({
  selector: 'app-maturity-radar',
  standalone: true,
  template: `
    <svg
      viewBox="0 0 300 300"
      width="100%"
      height="320"
      preserveAspectRatio="xMidYMid meet"
    >
      <defs>
        <linearGradient id="radarGrad" x1="0" y1="0" x2="1" y2="1">
          <stop offset="0%" stop-color="#26e0ff" />
          <stop offset="100%" stop-color="#8b5cff" />
        </linearGradient>
      </defs>
      @for (ring of ringPolys(); track $index) {
        <polygon [attr.points]="ring" fill="none" stroke="rgba(122,145,190,0.12)" />
      }
      @for (a of axes(); track a.label) {
        <line
          [attr.x1]="cx"
          [attr.y1]="cy"
          [attr.x2]="a.x"
          [attr.y2]="a.y"
          stroke="rgba(122,145,190,0.12)"
        />
        <text
          [attr.x]="a.lx"
          [attr.y]="a.ly"
          [attr.text-anchor]="a.anchor"
          fill="#8791a8"
          font-size="12"
          font-family="JetBrains Mono, monospace"
          dominant-baseline="middle"
        >
          {{ a.label }}
        </text>
      }
      <polygon
        [attr.points]="targetPoly()"
        fill="#ff3d9a"
        fill-opacity="0.05"
        stroke="#ff3d9a"
        stroke-opacity="0.7"
        stroke-dasharray="4 4"
      />
      <polygon
        [attr.points]="currentPoly()"
        fill="url(#radarGrad)"
        fill-opacity="0.3"
        stroke="#26e0ff"
        stroke-width="1.6"
        style="filter: drop-shadow(0 0 6px rgba(38,224,255,0.5))"
      />
    </svg>
  `,
})
export class MaturityRadarComponent {
  data = input.required<RadarPoint[]>();
  readonly cx = CX;
  readonly cy = CY;

  private geom = computed(() => {
    const n = this.data().length || 1;
    return this.data().map((d, i) => {
      const ang = ((-90 + (i * 360) / n) * Math.PI) / 180;
      return { cos: Math.cos(ang), sin: Math.sin(ang), d };
    });
  });

  axes = computed<Axis[]>(() =>
    this.geom().map((g) => {
      const lr = R + 16;
      const anchor = Math.abs(g.cos) < 0.3 ? 'middle' : g.cos > 0 ? 'start' : 'end';
      return {
        x: round(CX + R * g.cos),
        y: round(CY + R * g.sin),
        lx: round(CX + lr * g.cos),
        ly: round(CY + lr * g.sin),
        anchor,
        label: g.d.function,
      };
    }),
  );

  ringPolys = computed(() =>
    RINGS.map((ring) =>
      this.geom()
        .map((g) => {
          const rr = (ring / 5) * R;
          return `${round(CX + rr * g.cos)},${round(CY + rr * g.sin)}`;
        })
        .join(' '),
    ),
  );

  currentPoly = computed(() => this.poly((g) => (g.d.current / 5) * R));
  targetPoly = computed(() => this.poly((g) => (g.d.target / 5) * R));

  private poly(radiusOf: (g: { cos: number; sin: number; d: RadarPoint }) => number): string {
    return this.geom()
      .map((g) => {
        const rr = radiusOf(g);
        return `${round(CX + rr * g.cos)},${round(CY + rr * g.sin)}`;
      })
      .join(' ');
  }
}

function round(x: number): number {
  return Math.round(x * 10) / 10;
}

import { Component, computed, input } from '@angular/core';

/**
 * Gauge de maturidade em meia-lua (180°). O arco desenha a escala completa com
 * degradê Vermelho → Âmbar → Verde (0 → alvo) e um ponteiro aponta o valor atual.
 * SVG puro + Signals, sem libs — mesma técnica de polilinha do IcrGauge da casa.
 */
@Component({
  selector: 'app-maturity-gauge',
  standalone: true,
  template: `
    <div class="mg">
      <svg
        viewBox="0 0 240 138"
        width="100%"
        height="176"
        preserveAspectRatio="xMidYMid meet"
        role="img"
        [attr.aria-label]="'Maturidade geral ' + value().toFixed(1) + ' de ' + max().toFixed(1)"
      >
        <defs>
          <!-- Vermelho (esquerda/0.0) → Âmbar → Verde (direita/alvo) -->
          <linearGradient id="mgArc" x1="0" y1="0" x2="1" y2="0">
            <stop offset="0%" stop-color="#ff2d6f" />
            <stop offset="50%" stop-color="#ffb020" />
            <stop offset="100%" stop-color="#2fe08a" />
          </linearGradient>
        </defs>

        <!-- trilha + arco-escala -->
        <path [attr.d]="track" fill="none" stroke="#111726" stroke-width="20" stroke-linecap="round" />
        <path
          [attr.d]="track"
          fill="none"
          stroke="url(#mgArc)"
          stroke-width="14"
          stroke-linecap="round"
          style="filter: drop-shadow(0 0 6px rgba(38, 224, 255, 0.3))"
        />

        <!-- extremos da escala -->
        <text x="14" y="132" fill="#8791a8" font-family="JetBrains Mono, monospace" font-size="11">0.0</text>
        <text
          x="226"
          y="132"
          fill="#8791a8"
          font-family="JetBrains Mono, monospace"
          font-size="11"
          text-anchor="end"
        >
          {{ max().toFixed(1) }}
        </text>

        <!-- ponteiro -->
        <g [attr.transform]="'rotate(' + angle() + ' 120 116)'">
          <polygon
            points="120,34 124.5,114 115.5,114"
            [attr.fill]="valueColor()"
            [style.filter]="'drop-shadow(0 0 6px ' + glow() + ')'"
          />
        </g>
        <circle cx="120" cy="116" r="9" fill="#0b0f1a" stroke="#26424f" stroke-width="2" />
        <circle cx="120" cy="116" r="3.5" [attr.fill]="valueColor()" />
      </svg>

      <div class="mg-readout">
        <span class="mg-val" [style.color]="valueColor()" [style.textShadow]="'0 0 24px ' + glow()">
          {{ value().toFixed(1) }}
        </span>
        <span class="mg-of">/ {{ max().toFixed(1) }}</span>
      </div>
      <p class="mg-cap">Maturidade Geral <span>· média CMMI</span></p>
    </div>
  `,
  styles: [
    `
      .mg {
        display: flex;
        flex-direction: column;
        align-items: center;
      }
      .mg-readout {
        display: flex;
        align-items: baseline;
        gap: 8px;
        margin-top: 2px;
      }
      .mg-val {
        font-family: var(--display);
        font-weight: 700;
        font-size: 44px;
        line-height: 1;
      }
      .mg-of {
        font-family: var(--mono);
        font-size: 14px;
        color: var(--muted);
      }
      .mg-cap {
        margin: 8px 0 0;
        font-family: var(--mono);
        font-size: 11px;
        letter-spacing: 0.14em;
        text-transform: uppercase;
        color: var(--text);
      }
      .mg-cap span {
        color: var(--muted);
      }
    `,
  ],
})
export class MaturityGaugeComponent {
  /** Valor atual (mesma escala CMMI do alvo). */
  value = input.required<number>();
  /** Fim da escala / alvo — extremo direito do arco. */
  max = input(4);

  /** Semicírculo superior (180° → 0°) como polilinha. */
  readonly track = semiArc(120, 116, 98);

  private readonly frac = computed(() => clamp01(this.value() / this.max()));
  /** −90° (valor 0, aponta à esquerda) → +90° (alvo, aponta à direita). */
  readonly angle = computed(() => -90 + this.frac() * 180);
  /** Cor do ponteiro/leitura interpolada na escala vermelho→verde. */
  readonly valueColor = computed(() => scoreColor(this.frac()));
  readonly glow = computed(() => rgbAlpha(scoreColor(this.frac()), 0.5));
}

/** Meia-lua superior de raio `r` centrada em (cx,cy), traçada da esquerda para a direita. */
function semiArc(cx: number, cy: number, r: number): string {
  const steps = 72;
  const pts: string[] = [];
  for (let i = 0; i <= steps; i++) {
    const a = ((180 - (i / steps) * 180) * Math.PI) / 180;
    pts.push(`${(cx + r * Math.cos(a)).toFixed(2)} ${(cy - r * Math.sin(a)).toFixed(2)}`);
  }
  return 'M ' + pts.join(' L ');
}

function clamp01(x: number): number {
  return x < 0 ? 0 : x > 1 ? 1 : x;
}

/** Interpola a escala Vermelho → Âmbar → Verde para uma fração 0..1. */
function scoreColor(f: number): string {
  const red = [255, 45, 111];
  const amber = [255, 176, 32];
  const green = [47, 224, 138];
  const [a, b, t] = f < 0.5 ? [red, amber, f / 0.5] : [amber, green, (f - 0.5) / 0.5];
  const c = a.map((ch, i) => Math.round(ch + (b[i] - ch) * t));
  return `rgb(${c[0]}, ${c[1]}, ${c[2]})`;
}

/** Converte um `rgb(r, g, b)` em `rgba(...)` com a opacidade dada, para o brilho. */
function rgbAlpha(rgb: string, alpha: number): string {
  const m = /rgb\((\d+), (\d+), (\d+)\)/.exec(rgb);
  return m ? `rgba(${m[1]}, ${m[2]}, ${m[3]}, ${alpha})` : rgb;
}

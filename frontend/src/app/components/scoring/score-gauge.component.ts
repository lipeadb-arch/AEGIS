import { Component, computed, input } from '@angular/core';

const CX = 100;
const CY = 100;
const R = 78;
const START_DEG = 225; // canto inferior-esquerdo
const SWEEP_DEG = 270; // varredura horária, deixando um vão de 90° embaixo

/**
 * ScoreGaugeComponent — DUMB. Anel de conformidade (%) de um pilar, em SVG puro (sem libs), no mesmo
 * idioma do <app-icr-gauge>. Sem estado e sem serviço: recebe o número e desenha.
 *
 * A COR comunica risco à la HUD, para leitura rápida: alta conformidade = cyan (contido, "seguro");
 * atenção = âmbar; baixa = vermelho (salta aos olhos). É o gauge que traduz a postura do pilar num relance.
 */
@Component({
  selector: 'app-score-gauge',
  standalone: true,
  template: `
    <div class="gauge-wrap">
      <svg
        viewBox="0 0 200 200"
        width="100%"
        [attr.height]="height()"
        preserveAspectRatio="xMidYMid meet"
        role="img"
        [attr.aria-label]="caption() + ': ' + rounded() + '%'"
      >
        <!-- trilha esmaecida -->
        <path [attr.d]="trackPath" fill="none" stroke="#141b2c" stroke-width="14" stroke-linecap="round" />
        <!-- arco do valor, tingido pela faixa de risco -->
        <path
          [attr.d]="valuePath()"
          fill="none"
          [attr.stroke]="color()"
          stroke-width="14"
          stroke-linecap="round"
          [style.filter]="'drop-shadow(0 0 8px ' + glow() + ')'"
          [style.transition]="'stroke .3s ease'"
        />
        <text
          x="100"
          y="100"
          text-anchor="middle"
          fill="#eaf1ff"
          font-family="Orbitron, JetBrains Mono, monospace"
          font-weight="700"
          font-size="40"
          [style.filter]="'drop-shadow(0 0 12px ' + glow() + ')'"
        >
          {{ rounded() }}
        </text>
        <text
          x="100"
          y="124"
          text-anchor="middle"
          fill="#8791a8"
          font-family="JetBrains Mono, monospace"
          font-size="11"
          letter-spacing="1.5"
        >
          {{ caption() }}
        </text>
      </svg>
    </div>
  `,
})
export class ScoreGaugeComponent {
  /** Percentual de conformidade do pilar (0–100). */
  readonly percent = input.required<number>();
  /** Legenda curta sob o número. */
  readonly caption = input('% CONFORME');
  /** Altura do SVG em px (o width é 100% do contêiner). */
  readonly height = input(216);

  readonly rounded = computed(() => Math.round(clamp(this.percent(), 0, 100)));
  readonly color = computed(() => colorFor(this.rounded()));
  readonly glow = computed(() => hexAlpha(this.color(), 0.55));

  readonly trackPath = arc(1);
  readonly valuePath = computed(() => arc(clamp(this.percent(), 0, 100) / 100));
}

/** Faixa de risco → cor HUD. ≥80 seguro (cyan); ≥50 atenção (âmbar); <50 risco (vermelho, salta aos olhos). */
function colorFor(pct: number): string {
  if (pct >= 80) return '#26e0ff';
  if (pct >= 50) return '#ffb020';
  return '#ff2d6f';
}

/** Arco de 270° (225°→-45°, sentido horário) até a fração `frac`, como polilinha SVG. */
function arc(frac: number): string {
  const steps = 96;
  const pts: string[] = [];
  for (let i = 0; i <= steps; i++) {
    const deg = START_DEG - (i / steps) * frac * SWEEP_DEG;
    const ang = (deg * Math.PI) / 180;
    const x = CX + R * Math.cos(ang);
    const y = CY - R * Math.sin(ang); // y cresce para baixo: 90° aponta para cima
    pts.push(`${x.toFixed(2)} ${y.toFixed(2)}`);
  }
  return 'M ' + pts.join(' L ');
}

function clamp(x: number, lo: number, hi: number): number {
  return x < lo ? lo : x > hi ? hi : x;
}

/** #rrggbb → rgba() com a opacidade dada, para o brilho do arco. */
function hexAlpha(hex: string, a: number): string {
  const m = /^#?([0-9a-f]{6})$/i.exec(hex.trim());
  if (!m) return hex;
  const n = parseInt(m[1], 16);
  return `rgba(${(n >> 16) & 255}, ${(n >> 8) & 255}, ${n & 255}, ${a})`;
}

import { Component, computed, input } from '@angular/core';
import { ComplianceHistoryPoint } from '../../models/scoring.models';

const W = 72;
const H = 20;
const PAD = 2.5;

/**
 * SparklineComponent — DUMB. Série de conformidade de UM controle (30 dias) em SVG puro, sem libs, no
 * mesmo idioma do <app-score-gauge>. Sem estado e sem serviço: recebe os pontos e desenha.
 *
 * É um gráfico de RELANCE: sem eixos, sem grade, sem tooltip. A FORMA responde "está melhorando ou
 * apodrecendo?" e a COR (mesma faixa de risco do gauge) responde "onde parou". O último ponto ganha um
 * marcador porque é o estado de hoje — o resto é contexto.
 *
 * Com menos de 2 pontos não existe tendência para mostrar, então o componente se OMITE em vez de desenhar
 * uma linha reta que insinuaria estabilidade sem dado que a sustente.
 */
@Component({
  selector: 'app-sparkline',
  standalone: true,
  template: `
    @if (series().length > 1) {
      <svg viewBox="0 0 72 20" width="72" height="20" role="img" [attr.aria-label]="ariaLabel()">
        <!-- área sob a curva: dá massa ao traço numa faixa de 20px -->
        <path [attr.d]="areaPath()" [attr.fill]="color()" opacity="0.16" />
        <path
          [attr.d]="linePath()"
          fill="none"
          [attr.stroke]="color()"
          stroke-width="1.5"
          stroke-linecap="round"
          stroke-linejoin="round"
        />
        <circle [attr.cx]="last().x" [attr.cy]="last().y" r="2" [attr.fill]="color()" />
      </svg>
    }
  `,
  styles: [
    `
      :host {
        display: inline-flex;
        align-items: center;
      }
    `,
  ],
})
export class SparklineComponent {
  /** Série histórica do controle, em ordem cronológica (a mais antiga primeiro). */
  readonly points = input.required<ComplianceHistoryPoint[]>();

  /** Coordenadas já projetadas na caixa do SVG (o template as usa para decidir se há tendência). */
  protected readonly series = computed(() => project(this.points()));

  readonly last = computed(() => this.series()[this.series().length - 1]);

  /** Cor pela faixa de risco do valor ATUAL — mesma régua do gauge (≥80 cyan · ≥50 âmbar · <50 vermelho). */
  readonly color = computed(() => colorFor(this.points()[this.points().length - 1].compliancePercent));

  readonly linePath = computed(() => 'M ' + this.series().map((p) => `${p.x} ${p.y}`).join(' L '));

  /** Fecha a curva contra a base para pintar a área. */
  readonly areaPath = computed(() => {
    const s = this.series();
    return `${this.linePath()} L ${s[s.length - 1].x} ${H} L ${s[0].x} ${H} Z`;
  });

  readonly ariaLabel = computed(() => {
    const pts = this.points();
    const first = pts[0].compliancePercent;
    const lastPct = pts[pts.length - 1].compliancePercent;
    return `Conformidade nos últimos ${pts.length} dias: de ${first}% para ${lastPct}%.`;
  });
}

/** Projeta os pontos na caixa: x distribuído no tempo, y invertido (0% embaixo, 100% em cima). */
function project(points: ComplianceHistoryPoint[]): { x: number; y: number }[] {
  const n = points.length;
  return points.map((p, i) => ({
    x: round(PAD + (n === 1 ? 0 : (i / (n - 1)) * (W - 2 * PAD))),
    y: round(H - PAD - (clamp(p.compliancePercent, 0, 100) / 100) * (H - 2 * PAD)),
  }));
}

/** Faixa de risco → cor HUD. Idêntica ao ScoreGauge: a régua de cor do produto é uma só. */
function colorFor(pct: number): string {
  if (pct >= 80) return '#26e0ff';
  if (pct >= 50) return '#ffb020';
  return '#ff2d6f';
}

function clamp(x: number, lo: number, hi: number): number {
  return x < lo ? lo : x > hi ? hi : x;
}

function round(x: number): number {
  return Math.round(x * 100) / 100;
}

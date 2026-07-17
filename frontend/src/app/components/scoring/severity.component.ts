import { Component, computed, input } from '@angular/core';
import { SeverityLevel } from '../../models/scoring.models';

/**
 * SeverityComponent — DUMB. Renderiza um nível de severidade (escala Purple Knight) como um chip HUD:
 * uma escada de 5 pips acesos conforme a gravidade + rótulo PT-BR, tingido pela cor de risco. Sem estado,
 * sem serviço: recebe o nível e desenha. Reutilizável em qualquer tabela tática de achados.
 */
@Component({
  selector: 'app-severity',
  standalone: true,
  template: `
    <span class="sev" [style.--sev]="color()" [attr.title]="labelPt() + ' (' + level() + ')'">
      <span class="pips" aria-hidden="true">
        @for (on of pips(); track $index) {
          <i [class.on]="on"></i>
        }
      </span>
      <span class="lbl">{{ labelPt() }}</span>
    </span>
  `,
  styles: [
    `
      .sev {
        display: inline-flex;
        align-items: center;
        gap: 8px;
      }
      .pips {
        display: inline-flex;
        gap: 2px;
        align-items: flex-end;
        height: 13px;
      }
      /* Escada: cada pip mais alto que o anterior — a silhueta comunica gravidade num relance. */
      .pips i {
        width: 3px;
        border-radius: 1px;
        background: var(--line);
        opacity: 0.5;
      }
      .pips i:nth-child(1) { height: 5px; }
      .pips i:nth-child(2) { height: 7px; }
      .pips i:nth-child(3) { height: 9px; }
      .pips i:nth-child(4) { height: 11px; }
      .pips i:nth-child(5) { height: 13px; }
      .pips i.on {
        background: var(--sev);
        opacity: 1;
        box-shadow: 0 0 6px -1px var(--sev);
      }
      .lbl {
        font-family: var(--mono);
        font-size: 10.5px;
        text-transform: uppercase;
        letter-spacing: 0.1em;
        color: var(--sev);
      }
    `,
  ],
})
export class SeverityComponent {
  /** Nível de severidade a exibir. */
  readonly level = input.required<SeverityLevel>();

  readonly color = computed(() => COLOR[this.level()]);
  readonly labelPt = computed(() => LABEL_PT[this.level()]);

  /** 5 pips: os primeiros N acesos conforme a gravidade (Critical=5 … Informational=1). */
  readonly pips = computed(() => {
    const n = RANK[this.level()];
    return [1, 2, 3, 4, 5].map((i) => i <= n);
  });
}

/** Gravidade → nº de pips acesos (5 = Critical … 1 = Informational). */
const RANK: Record<SeverityLevel, number> = {
  Critical: 5,
  High: 4,
  Medium: 3,
  Low: 2,
  Informational: 1,
};

/** Cor HUD por nível: vermelho → laranja → âmbar → cyan → cinza. */
const COLOR: Record<SeverityLevel, string> = {
  Critical: '#ff2d6f', // --red
  High: '#ff6a3d', // laranja neon
  Medium: '#ffb020', // --amber
  Low: '#26e0ff', // --cyan
  Informational: '#8791a8', // --muted
};

const LABEL_PT: Record<SeverityLevel, string> = {
  Critical: 'Crítico',
  High: 'Alto',
  Medium: 'Médio',
  Low: 'Baixo',
  Informational: 'Informativo',
};

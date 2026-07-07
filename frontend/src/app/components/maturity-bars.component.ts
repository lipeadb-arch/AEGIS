import { Component, computed, input } from '@angular/core';

/** Uma coluna do histograma: código NIST, rótulo curto e o valor (0..max). */
export interface FunctionScore {
  code: string; // GV, ID, PR, DE, RS, RC
  label: string; // rótulo curto (Detect, Govern, …)
  value: number; // 0..max
}

/**
 * Histograma de maturidade por Função NIST. Barras em <div> com altura
 * proporcional, gradiente azul-neon e linhas de grade. Signals + CSS, sem libs.
 */
@Component({
  selector: 'app-maturity-bars',
  standalone: true,
  template: `
    <div class="mb">
      <div class="mb-plot">
        <!-- grade horizontal nos níveis inteiros -->
        <div class="mb-lines" aria-hidden="true">
          @for (l of levels(); track l) {
            <span [style.bottom.%]="(l / max()) * 100"></span>
          }
        </div>

        <!-- colunas -->
        <div class="mb-cols">
          @for (b of bars(); track b.code) {
            <div class="mb-col">
              <span class="mb-v">{{ b.value.toFixed(1) }}</span>
              <div
                class="mb-bar"
                [style.height.%]="b.pct"
                [style.animation-delay.ms]="b.i * 70"
                [attr.title]="b.label + ' · ' + b.value.toFixed(1)"
              ></div>
            </div>
          }
        </div>
      </div>

      <!-- eixo X -->
      <div class="mb-axis">
        @for (b of bars(); track b.code) {
          <span class="mb-x"><b>{{ b.code }}</b><small>{{ b.label }}</small></span>
        }
      </div>
    </div>
  `,
  styles: [
    `
      .mb {
        display: flex;
        flex-direction: column;
      }
      .mb-plot {
        position: relative;
        height: 196px;
      }
      .mb-lines {
        position: absolute;
        inset: 0;
        pointer-events: none;
      }
      .mb-lines span {
        position: absolute;
        left: 0;
        right: 0;
        height: 0;
        border-top: 1px dashed rgba(122, 145, 190, 0.13);
      }
      .mb-cols {
        position: absolute;
        inset: 0;
        display: flex;
        align-items: flex-end;
        gap: 16px;
        padding: 0 4px;
      }
      .mb-col {
        flex: 1;
        height: 100%;
        display: flex;
        flex-direction: column;
        justify-content: flex-end;
        align-items: center;
        min-width: 0;
      }
      .mb-v {
        font-family: var(--display);
        font-weight: 600;
        font-size: 13px;
        color: var(--text);
        margin-bottom: 7px;
        text-shadow: 0 0 12px rgba(38, 224, 255, 0.45);
      }
      .mb-bar {
        width: 100%;
        max-width: 44px;
        min-height: 3px;
        border-radius: 7px 7px 2px 2px;
        background: linear-gradient(180deg, #7ff2ff 0%, var(--cyan) 28%, var(--cyan-2) 78%, #2b6fff 100%);
        box-shadow: 0 0 16px -3px rgba(38, 224, 255, 0.6), inset 0 1px 0 rgba(255, 255, 255, 0.4);
        transform-origin: bottom;
        animation: mb-rise 0.6s cubic-bezier(0.2, 0.7, 0.2, 1) both;
      }
      .mb-axis {
        display: flex;
        gap: 16px;
        padding: 11px 4px 0;
        border-top: 1px solid rgba(122, 145, 190, 0.16);
      }
      .mb-x {
        flex: 1;
        display: flex;
        flex-direction: column;
        align-items: center;
        gap: 2px;
        min-width: 0;
      }
      .mb-x b {
        font-family: var(--mono);
        font-weight: 700;
        font-size: 12px;
        letter-spacing: 0.1em;
        color: var(--cyan);
      }
      .mb-x small {
        font-family: var(--mono);
        font-size: 9px;
        letter-spacing: 0.05em;
        color: var(--muted);
        text-transform: uppercase;
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
        max-width: 100%;
      }
      @keyframes mb-rise {
        from {
          transform: scaleY(0);
          opacity: 0.35;
        }
        to {
          transform: scaleY(1);
          opacity: 1;
        }
      }
      @media (prefers-reduced-motion: reduce) {
        .mb-bar {
          animation: none;
        }
      }
    `,
  ],
})
export class MaturityBarsComponent {
  data = input.required<FunctionScore[]>();
  /** Topo da escala (0..max). */
  max = input(4);

  readonly bars = computed(() =>
    this.data().map((d, i) => ({ ...d, i, pct: clamp01(d.value / this.max()) * 100 })),
  );

  /** Níveis inteiros 1..max para as linhas de grade. */
  readonly levels = computed(() => {
    const out: number[] = [];
    for (let l = 1; l <= this.max(); l++) out.push(l);
    return out;
  });
}

function clamp01(x: number): number {
  return x < 0 ? 0 : x > 1 ? 1 : x;
}

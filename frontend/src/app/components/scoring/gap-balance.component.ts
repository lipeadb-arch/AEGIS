import { Component, input } from '@angular/core';
import { GapBalance } from '../../models/dashboard.models';

/**
 * GapBalanceComponent — DUMB. O balanço orçamentário das lacunas de evidência:
 * **ferramenta (CAPEX) × processo (OPEX)**, com os pontos cegos mais caros logo abaixo.
 *
 * É a tradução direta da pergunta que a diretoria faz depois de ver o score cair: *"isso se resolve
 * comprando alguma coisa ou botando gente para escrever?"*. Uma barra dupla responde de relance; a
 * lista nomeia por onde começar.
 *
 * Cores no idioma já estabelecido pelas lacunas (ver missing-requirements): telemetria em vermelho
 * (sensor cego — o Aegis não consegue avaliar) e documentação em âmbar (dívida de processo).
 */
@Component({
  selector: 'app-gap-balance',
  standalone: true,
  template: `
    @if (balance(); as b) {
      @if (b.total === 0) {
        <p class="gb-empty">Nenhum ponto cego — todo controle avaliado tem evidência por trás.</p>
      } @else {
        <div class="gb-legend">
          <span class="gb-key tool">
            <i></i> Ferramenta <b>{{ b.telemetryPct }}%</b>
            <em>capex · {{ b.telemetryCount }} lacuna(s)</em>
          </span>
          <span class="gb-key proc">
            <i></i> Processo <b>{{ b.documentationPct }}%</b>
            <em>opex · {{ b.documentationCount }} lacuna(s)</em>
          </span>
        </div>

        <div
          class="gb-bar"
          role="img"
          [attr.aria-label]="
            'Balanço de lacunas: ' + b.telemetryPct + '% de ferramenta e ' +
            b.documentationPct + '% de processo.'
          "
        >
          <span class="gb-seg tool" [style.width.%]="b.telemetryPct"></span>
          <span class="gb-seg proc" [style.width.%]="b.documentationPct"></span>
        </div>

        <span class="gb-k">Pontos cegos prioritários</span>
        <ul class="gb-top">
          @for (s of b.topBlindSpots; track s.code) {
            <li class="gb-row" [class.tool]="s.nature !== 'Documentation'" [class.proc]="s.nature === 'Documentation'">
              <span class="gb-dot" aria-hidden="true"></span>
              <span class="gb-names">
                <span class="gb-name">{{ s.label }}</span>
                <span class="gb-src">{{ s.code }} · {{ s.sourceIdentifier }}</span>
              </span>
              <span class="gb-pts" [title]="'Pontos NIST que este controle deixa de somar'">
                +{{ s.pointsAtStake }}<i>pts</i>
              </span>
            </li>
          }
        </ul>
      }
    }
  `,
  styles: [
    `
      :host {
        display: block;
      }
      .gb-empty,
      .gb-k {
        font-family: var(--mono);
        color: var(--muted);
      }
      .gb-empty {
        font-size: 12px;
        margin: 6px 0 0;
      }
      .gb-legend {
        display: flex;
        flex-wrap: wrap;
        gap: 16px;
        margin-bottom: 9px;
      }
      .gb-key {
        display: flex;
        align-items: center;
        gap: 6px;
        font-family: var(--mono);
        font-size: 11px;
        color: var(--muted);
      }
      .gb-key i {
        width: 9px;
        height: 9px;
        border-radius: 2px;
      }
      .gb-key b {
        font-family: var(--display);
        font-size: 13px;
      }
      .gb-key em {
        font-style: normal;
        font-size: 10px;
        opacity: 0.7;
      }
      .gb-key.tool i {
        background: var(--red);
      }
      .gb-key.tool b {
        color: var(--red);
      }
      .gb-key.proc i {
        background: var(--amber);
      }
      .gb-key.proc b {
        color: var(--amber);
      }

      /* Barra dupla: as duas fatias somam 100% do ESFORÇO, não do nº de controles. */
      .gb-bar {
        display: flex;
        height: 10px;
        border-radius: 5px;
        overflow: hidden;
        background: rgba(122, 145, 190, 0.14);
        margin-bottom: 16px;
      }
      .gb-seg {
        height: 100%;
        transition: width 0.3s ease;
      }
      .gb-seg.tool {
        background: linear-gradient(90deg, rgba(255, 45, 111, 0.55), var(--red));
      }
      .gb-seg.proc {
        background: linear-gradient(90deg, rgba(255, 176, 32, 0.55), var(--amber));
      }

      .gb-k {
        display: block;
        font-size: 10px;
        text-transform: uppercase;
        letter-spacing: 0.12em;
        margin-bottom: 7px;
      }
      .gb-top {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .gb-row {
        display: grid;
        grid-template-columns: 8px minmax(0, 1fr) auto;
        align-items: center;
        gap: 10px;
        border: 1px solid var(--line);
        border-left: 3px solid var(--line);
        border-radius: 8px;
        padding: 8px 11px;
        background: rgba(122, 145, 190, 0.02);
      }
      .gb-dot {
        width: 7px;
        height: 7px;
        border-radius: 50%;
      }
      .gb-row.tool {
        border-left-color: var(--red);
      }
      .gb-row.tool .gb-dot {
        background: var(--red);
      }
      .gb-row.proc {
        border-left-color: var(--amber);
      }
      .gb-row.proc .gb-dot {
        background: var(--amber);
      }
      .gb-names {
        display: flex;
        flex-direction: column;
        gap: 1px;
        min-width: 0;
      }
      .gb-name {
        font-family: var(--sans);
        font-size: 12.5px;
        color: var(--text);
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
      }
      .gb-src {
        font-family: var(--mono);
        font-size: 10px;
        color: var(--muted);
      }
      .gb-pts {
        font-family: var(--display);
        font-weight: 700;
        font-size: 13px;
        color: var(--cyan);
      }
      .gb-pts i {
        font-style: normal;
        font-size: 9.5px;
        opacity: 0.7;
        margin-left: 2px;
      }

      @media (prefers-reduced-motion: reduce) {
        .gb-seg {
          transition: none;
        }
      }
    `,
  ],
})
export class GapBalanceComponent {
  /** Balanço já agregado por `buildGapBalance` — o componente não calcula nada. */
  readonly balance = input.required<GapBalance | null>();
}

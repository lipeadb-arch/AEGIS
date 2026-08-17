import { Component, computed, input } from '@angular/core';

/**
 * Campos comuns a `WorkspaceOverall` e `FunctionPosture` (projeção única do workspace). O componente aceita
 * qualquer um dos dois por tipagem estrutural — é o CONTRATO de postura compartilhado entre Dashboard e as
 * seis Funções NIST.
 */
export interface PostureView {
  evaluationState: 'Evaluated' | 'NotEvaluated';
  percentage: number | null;
  coveragePercentage: number;
  eligibleControls: number;
  evaluatedControls: number;
  compliantControls: number;
  nonCompliantControls: number;
  mitigatedControls: number;
  notEvaluatedControls: number;
}

/**
 * [AEGIS-AUD-021/027/032] Cabeçalho de postura COMPARTILHADO — uma só apresentação do AEGIS Score
 * (aegis-score-v1) para o Dashboard e para GV/ID/PR/DE/RS/RC. Regras invioláveis da entrega:
 *  • `NotEvaluated` NUNCA é exibido como 0% — mostra "Não avaliado";
 *  • score e cobertura são eixos DISTINTOS, exibidos separadamente;
 *  • as contagens vêm prontas do backend (o frontend não recalcula a fórmula).
 * Dumb component: recebe a `posture` e um `label`/`code`, sem tocar em serviços.
 */
@Component({
  selector: 'app-posture-summary',
  standalone: true,
  template: `
    <div class="posture" [class.na]="notEvaluated()">
      <div class="head">
        <span class="eyebrow">{{ label() }}@if (code()) { <b>· {{ code() }}</b> }</span>
      </div>

      <div class="score">
        <span class="v" [style.color]="scoreColor()">{{ scoreText() }}</span>
        @if (!notEvaluated()) { <span class="pct">%</span> }
      </div>

      @if (notEvaluated()) {
        <p class="hint">Sem evidência avaliada — ligue um conector ou suba políticas para medir a postura.</p>
      } @else {
        <p class="hint">
          Cobertura <b>{{ posture().coveragePercentage.toFixed(1) }}%</b> ·
          {{ posture().evaluatedControls }}/{{ posture().eligibleControls }} controles avaliados
        </p>
      }

      <div class="chips">
        <span class="chip ok">{{ posture().compliantControls }} conformes</span>
        @if (posture().mitigatedControls > 0) {
          <span class="chip mid">{{ posture().mitigatedControls }} parciais/compensados</span>
        }
        <span class="chip bad" [class.hot]="posture().nonCompliantControls > 0">
          {{ posture().nonCompliantControls }} não conformes
        </span>
        <span class="chip na">{{ posture().notEvaluatedControls }} não avaliados</span>
      </div>
    </div>
  `,
  styles: [
    `
      .posture {
        display: flex;
        flex-direction: column;
        gap: 8px;
        padding: 16px 18px;
        border: 1px solid var(--line);
        border-left: 3px solid var(--cyan);
        border-radius: 12px;
        background: rgba(122, 145, 190, 0.03);
      }
      .posture.na {
        border-left-color: var(--line-2, var(--line));
      }
      .eyebrow {
        font-family: var(--mono);
        font-size: 10px;
        letter-spacing: 0.14em;
        text-transform: uppercase;
        color: var(--cyan);
      }
      .eyebrow b {
        color: var(--muted);
        font-weight: 600;
      }
      .score {
        display: flex;
        align-items: baseline;
        gap: 3px;
      }
      .score .v {
        font-family: var(--display);
        font-weight: 700;
        font-size: 34px;
        line-height: 1;
      }
      .posture.na .score .v {
        font-size: 20px;
        letter-spacing: 0.01em;
      }
      .score .pct {
        font-family: var(--display);
        font-weight: 700;
        font-size: 16px;
        color: var(--muted);
      }
      .hint {
        margin: 0;
        font-family: var(--mono);
        font-size: 11px;
        color: var(--muted);
      }
      .hint b {
        color: var(--text);
      }
      .chips {
        display: flex;
        flex-wrap: wrap;
        gap: 6px;
        margin-top: 2px;
      }
      .chip {
        font-family: var(--mono);
        font-size: 10.5px;
        letter-spacing: 0.02em;
        padding: 3px 8px;
        border: 1px solid var(--line);
        border-radius: 999px;
        color: var(--muted);
      }
      .chip.ok {
        color: var(--cyan);
        border-color: rgba(38, 224, 255, 0.35);
      }
      .chip.mid {
        color: var(--amber);
        border-color: rgba(255, 176, 32, 0.35);
      }
      .chip.bad.hot {
        color: var(--red);
        border-color: rgba(255, 45, 111, 0.45);
        background: rgba(255, 45, 111, 0.06);
      }
    `,
  ],
})
export class PostureSummaryComponent {
  readonly posture = input.required<PostureView>();
  /** Rótulo do bloco: "AEGIS Score" na visão geral, ou o nome da Função. */
  readonly label = input<string>('AEGIS Score');
  /** Código NIST da Função (ex.: "PR"), quando aplicável. */
  readonly code = input<string | null>(null);

  readonly notEvaluated = computed(() => this.posture().evaluationState === 'NotEvaluated');

  readonly scoreText = computed(() => {
    const p = this.posture().percentage;
    return p === null ? 'Não avaliado' : p.toFixed(1);
  });

  /** Cor por faixa (≥80 ciano · ≥50 âmbar · <50 vermelho · nulo mutado) — mesma régua do resto do HUD. */
  readonly scoreColor = computed(() => {
    const p = this.posture().percentage;
    if (p === null) return 'var(--muted)';
    if (p >= 80) return 'var(--cyan)';
    if (p >= 50) return 'var(--amber)';
    return 'var(--red)';
  });
}

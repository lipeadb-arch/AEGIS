import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, computed, input } from '@angular/core';
import { BlastRadiusResponse } from '../services/auditor.service';

/**
 * BlastRadiusGraphComponent — a Generative UI do RAIO DE EXPLOSÃO (ID.RA), injetada inline no chat. DUMB:
 * recebe o DTO já calculado pelo backend (`input.required`) e o renderiza — o Ativo Épico (root) em
 * destaque + a lista estruturada dos ativos colaterais (distância, força do elo, impacto propagado).
 * Signal-first: os dados derivados (nível, nós formatados) são `computed`; zero libs de grafo, tema HUD.
 */
@Component({
  selector: 'app-blast-radius-graph',
  standalone: true,
  imports: [DatePipe, DecimalPipe],
  template: `
    <div class="blast" [attr.data-risk]="riskClass()">
      <header class="blast-head">
        <span class="mark" aria-hidden="true">◎</span>
        <div class="titles">
          <h5>Raio de Explosão</h5>
          <span class="root">Epicentro <b class="mono">{{ rootShort() }}</b></span>
        </div>
        <span class="risk-pill">{{ data().riskLevel }}</span>
      </header>

      <!-- Ativo Épico (Root) em destaque -->
      <div class="epicenter">
        <div class="score">
          <span class="val">{{ data().blastRadiusScore | number: '1.0-0' }}</span><i>/100</i>
        </div>
        <div class="metrics">
          <div><span class="v">{{ data().impactedAssetCount }}</span><span class="k">ativos atingidos</span></div>
          <div><span class="v">{{ data().impactedProcessCount }}</span><span class="k">processos</span></div>
          <div><span class="v">{{ data().maxDepth }}</span><span class="k">saltos (profundidade)</span></div>
        </div>
      </div>

      <!-- Nós impactados, ordenados por impacto (o backend já ordena) -->
      @if (nodes().length > 0) {
        <div class="table-wrap">
          <table class="nodes">
            <thead>
              <tr><th>Ativo</th><th class="c">Salto</th><th class="c">Elo</th><th>Impacto propagado</th></tr>
            </thead>
            <tbody>
              @for (n of nodes(); track n.impactedAssetId) {
                <tr>
                  <td class="mono">{{ n.shortId }}</td>
                  <td class="c dist">{{ n.distance }}</td>
                  <td class="c"><span class="edge" [attr.data-strength]="n.strength">{{ n.pathStrength }}</span></td>
                  <td>
                    <div class="impact">
                      <span class="track"><span class="bar" [style.width.%]="n.impactPct"></span></span>
                      <span class="num">{{ n.propagatedImpact | number: '1.0-1' }}</span>
                    </div>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      } @else {
        <p class="empty">Nenhum ativo colateral no raio — o epicentro está isolado na topologia.</p>
      }

      <div class="foot"><span>Avaliado em {{ data().computedAt | date: 'dd/MM HH:mm' }}</span></div>
    </div>
  `,
  styles: [
    `
      :host { display: block; width: 100%; }

      .blast {
        border: 1px solid var(--line); border-radius: 4px 14px 14px 14px; background: var(--panel-2);
        padding: 13px 14px; display: flex; flex-direction: column; gap: 12px;
      }
      /* A moldura acende conforme a severidade do raio. */
      .blast[data-risk='critico'] { border-color: rgba(255, 45, 111, 0.55); box-shadow: inset 0 0 34px -20px rgba(255, 45, 111, 0.85); }
      .blast[data-risk='alto'] { border-color: rgba(255, 61, 154, 0.5); box-shadow: inset 0 0 34px -22px rgba(255, 61, 154, 0.7); }
      .blast[data-risk='medio'] { border-color: rgba(255, 176, 32, 0.45); }
      .blast[data-risk='baixo'] { border-color: rgba(38, 224, 255, 0.4); }

      /* ---- Cabeçalho ---- */
      .blast-head { display: grid; grid-template-columns: auto 1fr auto; align-items: center; gap: 11px; }
      .mark { font-size: 19px; color: var(--red); filter: drop-shadow(0 0 10px rgba(255, 45, 111, 0.6)); line-height: 1; }
      .blast[data-risk='baixo'] .mark, .blast[data-risk='medio'] .mark { color: var(--amber); filter: none; }
      .titles { display: flex; flex-direction: column; gap: 1px; min-width: 0; }
      .titles h5 { margin: 0; font-family: var(--display); font-weight: 700; font-size: 12.5px; letter-spacing: 0.05em; color: var(--text); }
      .root { font-family: var(--sans); font-size: 10.5px; color: var(--muted); }
      .mono { font-family: var(--mono); letter-spacing: 0.02em; }
      .root b { color: var(--text); }
      .risk-pill {
        font-family: var(--mono); font-size: 9.5px; font-weight: 600; letter-spacing: 0.12em; text-transform: uppercase;
        border-radius: 999px; padding: 2px 9px; white-space: nowrap; color: #05070f; background: var(--muted);
      }
      .blast[data-risk='critico'] .risk-pill { background: var(--red); }
      .blast[data-risk='alto'] .risk-pill { background: var(--magenta); }
      .blast[data-risk='medio'] .risk-pill { background: var(--amber); }
      .blast[data-risk='baixo'] .risk-pill { background: var(--cyan); }

      /* ---- Epicentro (score + métricas) ---- */
      .epicenter {
        display: grid; grid-template-columns: auto 1fr; gap: 14px; align-items: center;
        padding: 10px 12px; border: 1px solid var(--line-2); border-radius: 10px; background: rgba(122, 145, 190, 0.04);
      }
      .score { display: flex; align-items: baseline; }
      .score .val { font-family: var(--display); font-weight: 800; font-size: 30px; line-height: 1; color: var(--text); }
      .score i { font-style: normal; font-family: var(--mono); font-size: 12px; color: var(--muted); margin-left: 2px; }
      .metrics { display: flex; gap: 16px; flex-wrap: wrap; }
      .metrics > div { display: flex; flex-direction: column; }
      .metrics .v { font-family: var(--display); font-weight: 700; font-size: 15px; color: var(--cyan); }
      .metrics .k { font-family: var(--mono); font-size: 9.5px; letter-spacing: 0.04em; color: var(--muted); text-transform: uppercase; }

      /* ---- Tabela de nós ---- */
      .table-wrap { overflow-x: auto; }
      .nodes { width: 100%; border-collapse: collapse; font-size: 12px; }
      .nodes th {
        text-align: left; font-family: var(--mono); font-size: 9.5px; letter-spacing: 0.1em; text-transform: uppercase;
        color: var(--muted); padding: 4px 8px; border-bottom: 1px solid var(--line);
      }
      .nodes th.c, .nodes td.c { text-align: center; }
      .nodes td { padding: 7px 8px; border-bottom: 1px solid var(--line-2); color: var(--text); }
      .nodes tbody tr:last-child td { border-bottom: 0; }
      .dist { font-family: var(--display); font-weight: 600; color: var(--muted); }

      /* Força do elo: Hard vermelho (propaga integral) → Redundant cyan (amortece). */
      .edge {
        font-family: var(--mono); font-size: 9.5px; font-weight: 600; letter-spacing: 0.06em;
        border: 1px solid var(--line); border-radius: 5px; padding: 1px 6px;
      }
      .edge[data-strength='hard'] { color: var(--red); border-color: rgba(255, 45, 111, 0.45); }
      .edge[data-strength='soft'] { color: var(--amber); border-color: rgba(255, 176, 32, 0.45); }
      .edge[data-strength='redundant'] { color: var(--cyan); border-color: rgba(38, 224, 255, 0.4); }

      /* Impacto propagado: trilha (flex:1) com a barra dentro + o valor fixo à direita (sempre visível). */
      .impact { display: flex; align-items: center; gap: 8px; min-width: 120px; }
      .impact .track {
        flex: 1; min-width: 40px; height: 6px; border-radius: 3px; overflow: hidden;
        background: rgba(122, 145, 190, 0.12);
      }
      .impact .bar {
        display: block; height: 100%; min-width: 2px; border-radius: 3px;
        background: linear-gradient(90deg, var(--magenta), var(--red)); box-shadow: 0 0 8px -2px rgba(255, 61, 154, 0.7);
      }
      .impact .num { flex: none; min-width: 34px; text-align: right; font-family: var(--mono); font-size: 11px; color: var(--text); }

      .empty { margin: 0; font-family: var(--mono); font-size: 11.5px; color: var(--muted); }
      .foot { font-family: var(--mono); font-size: 9.5px; color: var(--muted); text-align: right; }
    `,
  ],
})
export class BlastRadiusGraphComponent {
  /** DTO já calculado pelo backend — o componente não faz I/O, só apresenta. */
  readonly data = input.required<BlastRadiusResponse>();

  /** Faixa de risco em minúsculas — alimenta o seletor de tema [data-risk]. */
  readonly riskClass = computed(() => this.data().riskLevel.toLowerCase());

  /** Nº do maior impacto propagado — normaliza a largura das barras (0–100 na escala do maior). */
  private readonly peakImpact = computed(() =>
    Math.max(1, ...this.data().impactedNodes.map((n) => n.propagatedImpact)));

  /** Nós já formatados para a view (id curto, força em minúsculas, largura % da barra). Signal-first. */
  readonly nodes = computed(() =>
    this.data().impactedNodes.map((n) => ({
      ...n,
      shortId: n.impactedAssetId.slice(0, 8),
      strength: n.pathStrength.toLowerCase(),
      impactPct: Math.round((n.propagatedImpact / this.peakImpact()) * 100),
    })));

  readonly rootShort = computed(() => this.data().rootAssetId.slice(0, 8));
}

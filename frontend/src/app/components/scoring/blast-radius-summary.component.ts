import { Component, input } from '@angular/core';
import { BlastRadiusSummary } from '../../models/dashboard.models';

/**
 * BlastRadiusSummaryComponent — DUMB. O "custo do fracasso" na vitrine executiva: se o ativo mais
 * conectado cair, **quantos outros caem junto**.
 *
 * O motor de ID.RA já calculava isso, mas o resultado só existia dentro do chat do Auditor. Aqui a
 * métrica é traduzida para a pergunta que a diretoria realmente faz — não "qual o score do raio", e sim
 * "quantos processos de negócio param". O número de PROCESSOS vem primeiro por isso: ativo é
 * vocabulário de TI, processo é vocabulário de negócio.
 */
@Component({
  selector: 'app-blast-radius-summary',
  standalone: true,
  template: `
    @if (summary(); as s) {
      <div class="br-hero" [class]="'lvl-' + s.riskLevel.toLowerCase()">
        <span class="br-score">{{ s.score.toFixed(0) }}</span>
        <span class="br-meta">
          <span class="br-level">{{ levelLabel(s.riskLevel) }}</span>
          <span class="br-cap">magnitude do raio</span>
        </span>
      </div>

      <p class="br-lede">
        Se <b>{{ s.rootAssetName }}</b> for comprometido, o impacto se propaga por
        <b>{{ s.impactedAssetCount }}</b> ativo(s) em até <b>{{ s.maxDepth }}</b> salto(s).
      </p>

      <div class="br-facts">
        <span class="br-fact">
          <b>{{ s.impactedProcessCount }}</b>
          <em>processos de negócio atingidos</em>
        </span>
        <span class="br-fact">
          <b>{{ s.impactedAssetCount }}</b>
          <em>ativos no raio</em>
        </span>
      </div>
    } @else {
      <p class="br-empty">
        Nenhum raio de explosão calculado ainda. Rode uma análise de impacto em Identify para
        estimar o efeito cascata de um ativo comprometido.
      </p>
    }
  `,
  styles: [
    `
      :host {
        display: block;
      }
      .br-hero {
        display: flex;
        align-items: center;
        gap: 12px;
        margin-bottom: 12px;
      }
      .br-score {
        font-family: var(--display);
        font-weight: 700;
        font-size: 38px;
        line-height: 1;
        color: var(--muted);
      }
      .br-meta {
        display: flex;
        flex-direction: column;
        gap: 2px;
      }
      .br-level {
        font-family: var(--mono);
        font-size: 11px;
        letter-spacing: 0.14em;
        text-transform: uppercase;
        color: var(--muted);
      }
      .br-cap {
        font-family: var(--mono);
        font-size: 10px;
        color: var(--muted);
        opacity: 0.7;
      }

      /* Banda de risco — a MESMA régua de cor do ICR e do gauge. */
      .lvl-critico .br-score,
      .lvl-critico .br-level {
        color: var(--red);
      }
      .lvl-critico .br-score {
        text-shadow: 0 0 22px rgba(255, 45, 111, 0.45);
      }
      .lvl-alto .br-score,
      .lvl-alto .br-level {
        color: #ff7a3d;
      }
      .lvl-medio .br-score,
      .lvl-medio .br-level {
        color: var(--amber);
      }
      .lvl-baixo .br-score,
      .lvl-baixo .br-level {
        color: var(--cyan);
      }

      .br-lede {
        margin: 0 0 12px;
        font-family: var(--sans);
        font-size: 12.5px;
        line-height: 1.6;
        color: var(--text);
        opacity: 0.9;
      }
      .br-lede b {
        color: var(--text);
        font-weight: 600;
      }

      .br-facts {
        display: flex;
        flex-wrap: wrap;
        gap: 10px;
      }
      .br-fact {
        flex: 1 1 130px;
        display: flex;
        flex-direction: column;
        gap: 2px;
        border: 1px solid var(--line);
        border-radius: 9px;
        padding: 9px 11px;
        background: rgba(122, 145, 190, 0.03);
      }
      .br-fact b {
        font-family: var(--display);
        font-weight: 700;
        font-size: 19px;
        color: var(--cyan);
      }
      .br-fact em {
        font-style: normal;
        font-family: var(--mono);
        font-size: 10px;
        line-height: 1.4;
        color: var(--muted);
      }

      .br-empty {
        margin: 6px 0 0;
        font-family: var(--mono);
        font-size: 11.5px;
        line-height: 1.55;
        color: var(--muted);
      }
    `,
  ],
})
export class BlastRadiusSummaryComponent {
  /** `null` = nenhum raio calculado (204 do backend) — estado vazio, não zero. */
  readonly summary = input.required<BlastRadiusSummary | null>();

  /**
   * Acentua a banda de risco. O valor chega do enum C# `RiskLevel` (`Critico`, `Medio`) — identificador
   * de código, sem acento por construção. Exibi-lo cru numa tela de diretoria é vocabulário de máquina
   * escapando para a vitrine; a classe CSS continua usando o valor original em minúsculas.
   */
  levelLabel(level: string): string {
    const map: Record<string, string> = { Critico: 'Crítico', Medio: 'Médio' };
    return map[level] ?? level;
  }
}

import { Component, input } from '@angular/core';
import { MissingRequirement, MissingRequirementGroup } from '../../models/scoring.models';

/**
 * Token do catálogo NIST SP 800-53 que marca "telemetria não prova este controle sozinha" (espelha
 * RuleEvaluator.ManualAuditToken no backend). Chega como `sourceIdentifier` e NUNCA é exibido cru.
 */
const MANUAL_AUDIT_TOKEN = 'MANUAL_AUDIT_REQUIRED';

/**
 * MissingRequirementsComponent — as LACUNAS DE EVIDÊNCIA de um controle, separadas por natureza:
 * pendência de SENSOR (ícone de rede, vermelho pulsante — o Aegis está cego e não consegue avaliar) e
 * pendência de GOVERNANÇA (ícone de pasta, âmbar — o controle pode até existir, falta a prova formal).
 * A distinção é o produto: cada tom aponta para um destino diferente (ligar conector × Document Hub).
 *
 * Dumb e puro: recebe os grupos JÁ montados por `groupMissingRequirements` (scoring.models) e não
 * decide nada além do texto de chamada de ação. Vive à parte do card por dois motivos — o card passava
 * do budget de CSS do Angular, e o dossiê do controle já era grande demais para um componente só.
 */
@Component({
  selector: 'app-missing-requirements',
  standalone: true,
  template: `
    @for (g of groups(); track g.type) {
      <section class="gap-sec" [class.tone-critical]="g.tone === 'critical'" [class.tone-warn]="g.tone === 'warn'">
        <span class="gap-k">
          <span class="gap-ic" aria-hidden="true">
            @switch (g.icon) {
              @case ('network') {
                <svg viewBox="0 0 16 16" width="13" height="13" fill="none" stroke="currentColor" stroke-width="1.4">
                  <circle cx="8" cy="3" r="1.9" /><circle cx="3" cy="12.5" r="1.9" /><circle cx="13" cy="12.5" r="1.9" />
                  <path d="M8 4.9v3.3M8 8.2 4.2 11M8 8.2 11.8 11" stroke-linecap="round" />
                </svg>
              }
              @case ('folder') {
                <svg viewBox="0 0 16 16" width="13" height="13" fill="none" stroke="currentColor" stroke-width="1.4">
                  <path d="M1.8 4.2h4l1.3 1.7h7.1v6.4a1 1 0 0 1-1 1H2.8a1 1 0 0 1-1-1V4.2Z" stroke-linejoin="round" />
                </svg>
              }
              @case ('both') {
                <svg viewBox="0 0 16 16" width="13" height="13" fill="none" stroke="currentColor" stroke-width="1.4">
                  <path d="M1.5 4h3.4l1.1 1.5h4.2v4.7a1 1 0 0 1-1 1H2.5a1 1 0 0 1-1-1V4Z" stroke-linejoin="round" />
                  <circle cx="13" cy="4" r="1.5" /><circle cx="13" cy="12" r="1.5" /><path d="M13 5.5v5" stroke-linecap="round" />
                </svg>
              }
            }
          </span>
          {{ g.label }}
        </span>

        <ul class="gaps">
          @for (m of g.items; track m.sourceIdentifier + m.description) {
            <li>
              <span class="gap-src">{{ sourceLabel(m) }}</span>
              <p class="gap-cta">{{ callToAction(m) }}</p>
              <p class="gap-detail">{{ m.description }}</p>
            </li>
          }
        </ul>
      </section>
    }
  `,
  styles: [
    `
      :host {
        display: block;
      }
      .gap-sec {
        margin: 14px 0 0;
        border-left: 2px solid var(--line);
        padding-left: 11px;
      }
      /* Rótulo no MESMO idioma dos demais cabeçalhos do dossiê (mono, caixa alta, tracking largo). */
      .gap-k {
        display: flex;
        align-items: center;
        gap: 7px;
        font-family: var(--mono);
        font-size: 10px;
        text-transform: uppercase;
        letter-spacing: 0.12em;
        margin-bottom: 7px;
      }
      .gap-ic {
        display: inline-flex;
        line-height: 0;
      }
      .gaps {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 9px;
      }
      .gap-src {
        display: inline-block;
        margin-bottom: 5px;
        padding: 2px 7px;
        border-radius: 6px;
        font-family: var(--mono);
        font-size: 9.5px;
        letter-spacing: 0.1em;
        text-transform: uppercase;
      }
      .gap-cta {
        margin: 0;
        font-family: var(--sans);
        font-size: 12.5px;
        line-height: 1.5;
        color: var(--text);
      }
      /* A frase do motor (fontes alternativas aceitas etc.) — contexto, não a chamada de ação. */
      .gap-detail {
        margin: 3px 0 0;
        font-family: var(--mono);
        font-size: 10.5px;
        line-height: 1.5;
        color: var(--muted);
      }

      /* Crítico — o Aegis está CEGO neste controle: o pulso é o alarme de sensor caído. */
      .tone-critical {
        border-left-color: var(--red);
      }
      .tone-critical .gap-k,
      .tone-critical .gap-ic {
        color: var(--red);
      }
      .tone-critical .gap-ic {
        animation: gap-blink 1.6s ease-in-out infinite;
      }
      .tone-critical .gap-src {
        color: #ffe3ee;
        background: rgba(255, 45, 111, 0.12);
        border: 1px solid rgba(255, 45, 111, 0.4);
      }

      /* Alerta — dívida de processo: o controle pode estar implementado, falta a prova formal. */
      .tone-warn {
        border-left-color: var(--amber);
      }
      .tone-warn .gap-k,
      .tone-warn .gap-ic {
        color: var(--amber);
      }
      .tone-warn .gap-src {
        color: #ffe9c2;
        background: rgba(255, 176, 32, 0.1);
        border: 1px solid rgba(255, 176, 32, 0.38);
      }

      @keyframes gap-blink {
        0%,
        100% {
          opacity: 1;
          filter: drop-shadow(0 0 4px rgba(255, 45, 111, 0.7));
        }
        50% {
          opacity: 0.42;
          filter: none;
        }
      }

      @media (prefers-reduced-motion: reduce) {
        .tone-critical .gap-ic {
          animation: none;
        }
      }
    `,
  ],
})
export class MissingRequirementsComponent {
  /** Grupos já montados pelo modelo (ordem de urgência, sem grupos vazios). */
  readonly groups = input.required<MissingRequirementGroup[]>();

  /**
   * Rótulo da fonte para exibição. `MANUAL_AUDIT_REQUIRED` é um token do catálogo 800-53, não um nome de
   * produto: jogá-lo na tela em caixa alta com underscores vazaria vocabulário de máquina para o
   * analista. As fontes reais ("Entra ID", "Microsoft Sentinel") passam intactas.
   */
  sourceLabel(m: MissingRequirement): string {
    return m.sourceIdentifier === MANUAL_AUDIT_TOKEN ? 'Auditoria Manual' : m.sourceIdentifier;
  }

  /**
   * A CHAMADA DE AÇÃO da lacuna — o que o operador faz a seguir, não a repetição do diagnóstico. Cada
   * natureza aponta para um destino diferente do produto (conector × Document Hub), que é exatamente a
   * distinção que este bloco existe para tornar óbvia.
   */
  callToAction(m: MissingRequirement): string {
    switch (m.type) {
      case 'Telemetry':
        return `Falta conexão com o provedor ${this.sourceLabel(m)}. Ative o conector para validar este controle automaticamente.`;
      case 'Documentation':
        return m.sourceIdentifier === MANUAL_AUDIT_TOKEN
          ? 'A política exigida por este controle não foi encontrada no Document Hub. Faça o upload do documento de governança.'
          : `A política de ${m.sourceIdentifier} não foi encontrada no Document Hub. Faça o upload do documento de governança.`;
      case 'Both':
        return `Este controle exige as duas provas: ative o conector ${this.sourceLabel(m)} e faça o upload do documento de governança no Document Hub.`;
    }
  }
}

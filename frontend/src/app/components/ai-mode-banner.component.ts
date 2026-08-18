import { Component, OnInit, inject, signal } from '@angular/core';
import { AiStatus, AiStatusService } from '../services/ai-status.service';

/**
 * Componente REUTILIZÁVEL do estado da IA: um chip com o modo efetivo para o tenant (demonstrativo real /
 * simulado / indisponível / externo bloqueado) e, no Free Tier, o aviso de que só dados sintéticos podem
 * trafegar. Auto-suficiente: busca o status no init e some silenciosamente se a API não responder. NUNCA
 * exibe chave — o backend não a envia.
 */
@Component({
  selector: 'app-ai-mode-banner',
  standalone: true,
  template: `
    @if (status(); as s) {
      <div class="ai-mode" [class]="'st-' + s.effectiveState.toLowerCase()">
        <span class="chip">
          <span class="dot"></span>
          {{ label(s) }}
        </span>
        @if (s.freeTier && s.limitationNotice) {
          <p class="notice">{{ s.limitationNotice }}</p>
        }
      </div>
    }
  `,
  styles: [
    `
      .ai-mode {
        display: flex;
        flex-direction: column;
        gap: 6px;
        margin: 0 0 10px;
      }
      .chip {
        display: inline-flex;
        align-items: center;
        gap: 7px;
        align-self: flex-start;
        font-family: var(--mono);
        font-size: 11px;
        letter-spacing: 0.03em;
        padding: 4px 11px;
        border-radius: 999px;
        border: 1px solid currentColor;
      }
      .dot {
        width: 8px;
        height: 8px;
        border-radius: 50%;
        background: currentColor;
        box-shadow: 0 0 8px 0 currentColor;
      }
      /* Demonstrativo CONFIGURADO (não é health check em tempo real) — cyan/brand. */
      .st-democonfigured .chip { color: var(--cyan); }
      /* Simulado — mutado. */
      .st-simulated .chip { color: var(--muted); }
      .st-simulated .dot { box-shadow: none; }
      /* Externo bloqueado para o tenant — âmbar (funciona, mas só determinístico). */
      .st-externalblockedfortenant .chip { color: var(--amber); }
      /* Indisponível — vermelho. */
      .st-unavailable .chip { color: var(--red); }

      .notice {
        margin: 0;
        font-family: var(--sans);
        font-size: 11.5px;
        line-height: 1.5;
        color: var(--amber);
        background: rgba(255, 176, 32, 0.08);
        border: 1px solid rgba(255, 176, 32, 0.28);
        border-radius: 9px;
        padding: 7px 11px;
      }
    `,
  ],
})
export class AiModeBannerComponent implements OnInit {
  private readonly svc = inject(AiStatusService);
  readonly status = signal<AiStatus | null>(null);

  ngOnInit(): void {
    this.svc.status().subscribe((s) => this.status.set(s));
  }

  protected label(s: AiStatus): string {
    switch (s.effectiveState) {
      case 'DemoConfigured':
        return 'IA demonstrativa configurada · Gemini Free';
      case 'ExternalBlockedForTenant':
        return 'IA externa bloqueada para este tenant';
      case 'Unavailable':
        return 'IA indisponível';
      case 'Simulated':
      default:
        return 'IA simulada';
    }
  }
}

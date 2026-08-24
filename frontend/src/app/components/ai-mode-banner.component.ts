import { Component, OnInit, inject, signal } from '@angular/core';
import { AiStatus, AiStatusService } from '../services/ai-status.service';

/**
 * Componente reutilizável do estado tenant-scoped da IA. Mostra o modo efetivo e, quando aplicável,
 * um aviso operacional. Nunca exibe segredo.
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
        @if (s.limitationNotice) {
          <p class="notice" [class.enterprise]="s.effectiveState === 'EnterpriseConfigured'">
            {{ s.limitationNotice }}
          </p>
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
      .st-enterpriseconfigured .chip,
      .st-democonfigured .chip { color: var(--cyan); }
      .st-simulated .chip { color: var(--muted); }
      .st-simulated .dot { box-shadow: none; }
      .st-externalblockedfortenant .chip { color: var(--amber); }
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
      .notice.enterprise {
        color: var(--muted);
        background: rgba(255, 255, 255, 0.03);
        border-color: rgba(255, 255, 255, 0.12);
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
      case 'EnterpriseConfigured':
        return 'IA corporativa configurada · Claude';
      case 'DemoConfigured':
        return 'IA demonstrativa configurada · Claude';
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

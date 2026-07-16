import { DatePipe } from '@angular/common';
import { Component, inject, input, signal } from '@angular/core';
import { AdvisoryDto, ControlStatus, ControlView } from '../../models/scoring.models';
import { categoryName } from '../../models/nist-glossary';
import { AdvisoryService } from '../../services/advisory.service';

/**
 * Estado de UI (por controle) da geração de advisory: ocioso (sem entrada no mapa) → carregando →
 * carregado (com o advisory) ou erro. União discriminada por `status` — o template roteia com @if/@let.
 */
type AdvisoryUiState =
  | { status: 'loading' }
  | { status: 'loaded'; advisory: AdvisoryDto }
  | { status: 'error'; message: string };

/**
 * ControlComplianceCardComponent — recebe uma LISTA de controles e a renderiza como linhas de status
 * expansíveis (clique revela a evidência técnica da IA). O estado é majoritariamente de UI local (Signals:
 * linhas abertas + advisories por controle); a ÚNICA dependência é o AdvisoryService, para gerar sob
 * demanda a recomendação consultiva de um controle NÃO CONFORME (motor consultivo do Aegis Score).
 *
 * Tolerância Zero visual: NonCompliant salta aos olhos (borda + brilho vermelho, chip aceso); Compliant é
 * contido e silencioso (cinza, sem brilho); Parcial (documental, 50%) usa âmbar. NÃO é uma tabela de logs
 * de SOC — é o status do controle e sua evidência, nada de linha temporal de eventos.
 */
@Component({
  selector: 'app-control-compliance-card',
  standalone: true,
  imports: [DatePipe],
  template: `
    <ul class="controls">
      @for (c of controls(); track c.code) {
        <li
          class="ctl"
          [class.is-fail]="c.status === 'NonCompliant'"
          [class.is-partial]="c.status === 'MitigatedByThirdParty'"
          [class.is-ok]="c.status === 'Compliant'"
        >
          <button type="button" class="ctl-head" (click)="toggle(c.code)" [attr.aria-expanded]="isOpen(c.code)">
            <span class="dot" aria-hidden="true"></span>
            <span class="names">
              <span class="name">{{ categoryName(c.code) }}</span>
              <span class="code">{{ c.code }}</span>
            </span>
            <span class="status">{{ statusLabel(c.status) }}</span>
            <span class="pts">{{ c.scorePoints }}<i>/{{ c.maxScorePoints }}</i></span>
            <span class="chev" [class.open]="isOpen(c.code)" aria-hidden="true">›</span>
          </button>

          @if (isOpen(c.code)) {
            <div class="ctl-body">
              @if (c.checks.length > 0) {
                <ul class="checks">
                  @for (chk of c.checks; track chk.name) {
                    <li class="chk" [class.pass]="chk.passed" [class.fail]="!chk.passed">
                      <span class="ic" aria-hidden="true">{{ chk.passed ? '✓' : '✕' }}</span>
                      <span class="nm">{{ chk.name }}</span>
                      <span class="dt">{{ chk.details }}</span>
                    </li>
                  }
                </ul>
              }
              <p class="evidence">{{ c.evidence || 'Sem evidência registrada para este controle.' }}</p>
              <div class="meta">
                <span>Fonte: <b>{{ c.source === 'Telemetry' ? 'Telemetria' : 'Documental' }}</b></span>
                <span>Avaliado em {{ c.evaluatedAt | date: 'dd/MM/yyyy HH:mm' }}</span>
              </div>

              <!-- Motor consultivo: só em controles NÃO CONFORMES o analista gera a recomendação de
                   remediação (ação = tema HUD magenta). O advisory é redigido pela IA no servidor. -->
              @if (c.status === 'NonCompliant') {
                @let adv = advisoryState(c.code);
                <div class="advisory">
                  @if (!adv) {
                    <button type="button" class="advise-btn" (click)="generateAdvisory(c.code)">
                      <span class="ai" aria-hidden="true">✦</span> Gerar Recomendação
                    </button>
                  } @else if (adv.status === 'loading') {
                    <div class="advise-loading">
                      <span class="spin" aria-hidden="true"></span>
                      <span class="pulse">Gerando recomendação técnica…</span>
                    </div>
                  } @else if (adv.status === 'loaded') {
                    <article class="advise-card">
                      <header class="advise-hd">
                        <span class="badge" aria-hidden="true">✦ RECOMENDAÇÃO</span>
                        <h4>{{ adv.advisory.title }}</h4>
                      </header>
                      <section class="advise-sec">
                        <span class="k">Risco Documentado</span>
                        <p>{{ adv.advisory.documentedRisk }}</p>
                      </section>
                      <section class="advise-sec">
                        <span class="k">Passo a Passo Técnico</span>
                        <pre class="steps">{{ adv.advisory.technicalSteps }}</pre>
                      </section>
                      <footer class="advise-ft">
                        <span>Gerado em {{ adv.advisory.createdAt | date: 'dd/MM/yyyy HH:mm' }}</span>
                        <button type="button" class="advise-regen" (click)="generateAdvisory(c.code)">
                          Regenerar
                        </button>
                      </footer>
                    </article>
                  } @else {
                    <div class="advise-error">
                      <span>{{ adv.message }}</span>
                      <button type="button" class="advise-retry" (click)="generateAdvisory(c.code)">
                        Tentar novamente
                      </button>
                    </div>
                  }
                </div>
              }
            </div>
          }
        </li>
      } @empty {
        <li class="empty">Nenhum controle avaliado neste pilar ainda.</li>
      }
    </ul>
  `,
  styles: [
    `
      :host {
        display: block;
      }
      .controls {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 8px;
      }

      /* Linha de controle: por padrão silenciosa (Compliant). */
      .ctl {
        border: 1px solid var(--line);
        border-left: 3px solid var(--line);
        border-radius: 10px;
        background: rgba(122, 145, 190, 0.03);
        overflow: hidden;
        transition: border-color 0.2s ease, background 0.2s ease;
      }
      .ctl-head {
        width: 100%;
        display: grid;
        grid-template-columns: 14px minmax(0, 1fr) auto auto 16px;
        align-items: center;
        gap: 12px;
        padding: 11px 14px;
        background: none;
        border: 0;
        cursor: pointer;
        text-align: left;
        color: var(--text);
        font-family: var(--sans);
      }
      .dot {
        width: 9px;
        height: 9px;
        border-radius: 50%;
        background: var(--muted);
      }
      .names {
        display: flex;
        flex-direction: column;
        gap: 1px;
        min-width: 0;
      }
      .name {
        font-family: var(--sans);
        font-size: 13px;
        color: var(--text);
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
      }
      .code {
        font-family: var(--mono);
        font-size: 10.5px;
        letter-spacing: 0.03em;
        color: var(--muted);
      }
      .status {
        font-family: var(--mono);
        font-size: 10.5px;
        text-transform: uppercase;
        letter-spacing: 0.12em;
        color: var(--muted);
      }
      .pts {
        font-family: var(--display);
        font-weight: 600;
        font-size: 13px;
        color: var(--muted);
      }
      .pts i {
        font-style: normal;
        color: var(--muted);
        opacity: 0.6;
        font-size: 11px;
      }
      .chev {
        font-size: 18px;
        color: var(--muted);
        transform: rotate(90deg);
        transition: transform 0.2s ease;
      }
      .chev.open {
        transform: rotate(-90deg);
      }

      /* Compliant — contido: dot cyan apagado, nada de brilho. */
      .ctl.is-ok {
        opacity: 0.82;
      }
      .ctl.is-ok .dot {
        background: var(--cyan);
        opacity: 0.45;
      }

      /* Parcial (documental, 50%) — âmbar discreto. */
      .ctl.is-partial {
        border-left-color: var(--amber);
        background: rgba(255, 176, 32, 0.05);
      }
      .ctl.is-partial .dot {
        background: var(--amber);
        box-shadow: 0 0 8px -1px var(--amber);
      }
      .ctl.is-partial .status {
        color: var(--amber);
      }

      /* NonCompliant — SALTA AOS OLHOS: borda + brilho vermelho, chip aceso. */
      .ctl.is-fail {
        border-color: rgba(255, 45, 111, 0.5);
        border-left-color: var(--red);
        background: linear-gradient(90deg, rgba(255, 45, 111, 0.1), rgba(255, 45, 111, 0.02));
        box-shadow: inset 0 0 22px -14px rgba(255, 45, 111, 0.8), 0 0 18px -12px rgba(255, 45, 111, 0.6);
      }
      .ctl.is-fail .dot {
        background: var(--red);
        box-shadow: 0 0 10px 1px var(--red);
      }
      .ctl.is-fail .name {
        color: #ffe3ee;
      }
      .ctl.is-fail .status {
        color: var(--red);
        font-weight: 600;
      }

      /* Corpo expandido: a evidência técnica da IA. */
      .ctl-body {
        padding: 0 14px 13px 40px;
        border-top: 1px solid var(--line-2);
        margin-top: -1px;
      }
      /* Checklist técnico — decomposição do veredito em itens ✓/✕ (padrão HUD dual-neon). */
      .checks {
        list-style: none;
        margin: 12px 0 10px;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .chk {
        display: grid;
        grid-template-columns: 16px minmax(0, auto) 1fr;
        align-items: baseline;
        gap: 9px;
        font-family: var(--mono);
        font-size: 11.5px;
      }
      .chk .ic { font-size: 12px; line-height: 1; text-align: center; }
      .chk .nm { color: var(--text); white-space: nowrap; }
      .chk .dt {
        color: var(--muted);
        font-size: 10.5px;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
      /* Passou → cyan (contido); falhou → vermelho aceso, salta aos olhos. */
      .chk.pass .ic { color: var(--cyan); }
      .chk.pass .nm { opacity: 0.85; }
      .chk.fail .ic { color: var(--red); text-shadow: 0 0 8px rgba(255, 45, 111, 0.5); }
      .chk.fail .nm { color: #ffe3ee; }

      .evidence {
        font-family: var(--mono);
        font-size: 12px;
        line-height: 1.55;
        color: var(--text);
        opacity: 0.9;
        margin: 12px 0 10px;
      }
      .meta {
        display: flex;
        flex-wrap: wrap;
        gap: 18px;
        font-family: var(--mono);
        font-size: 11px;
        color: var(--muted);
      }
      .meta b {
        color: var(--text);
        font-weight: 600;
      }

      .empty {
        font-family: var(--mono);
        font-size: 12px;
        color: var(--muted);
        padding: 18px 4px;
      }

      /* ---- Motor consultivo: botão + card do advisory (tema HUD magenta = AÇÃO) ---- */
      .advisory { margin-top: 14px; }
      .advise-btn {
        display: inline-flex;
        align-items: center;
        gap: 8px;
        font-family: var(--mono);
        font-size: 11.5px;
        letter-spacing: 0.06em;
        color: var(--magenta);
        background: rgba(255, 61, 154, 0.08);
        border: 1px solid rgba(255, 61, 154, 0.4);
        border-radius: 8px;
        padding: 8px 14px;
        cursor: pointer;
        transition: 0.15s;
      }
      .advise-btn:hover {
        border-color: var(--magenta);
        box-shadow: 0 0 16px -4px rgba(255, 61, 154, 0.6);
      }
      .advise-btn .ai { font-size: 12px; }

      .advise-loading { display: inline-flex; align-items: center; gap: 10px; padding: 6px 2px; }
      .advise-loading .pulse {
        font-family: var(--mono);
        font-size: 11.5px;
        color: var(--magenta);
        letter-spacing: 0.06em;
        animation: advise-pulse 1.4s ease-in-out infinite;
      }
      .advise-loading .spin {
        width: 12px;
        height: 12px;
        border-radius: 50%;
        border: 2px solid rgba(255, 61, 154, 0.25);
        border-top-color: var(--magenta);
        animation: advise-spin 0.7s linear infinite;
      }
      @keyframes advise-spin { to { transform: rotate(360deg); } }
      @keyframes advise-pulse { 0%, 100% { opacity: 0.4; } 50% { opacity: 0.9; } }

      .advise-card {
        border: 1px solid rgba(255, 61, 154, 0.28);
        border-left: 3px solid var(--magenta);
        border-radius: 10px;
        background: linear-gradient(90deg, rgba(255, 61, 154, 0.07), rgba(255, 61, 154, 0.01));
        padding: 13px 15px;
      }
      .advise-hd { display: flex; flex-direction: column; gap: 6px; margin-bottom: 12px; }
      .advise-hd .badge {
        font-family: var(--mono);
        font-size: 9.5px;
        letter-spacing: 0.16em;
        text-transform: uppercase;
        color: var(--magenta);
      }
      .advise-hd h4 { margin: 0; font-family: var(--sans); font-size: 14px; font-weight: 600; color: #ffe3ee; }
      .advise-sec { margin-bottom: 12px; }
      .advise-sec .k {
        display: block;
        font-family: var(--mono);
        font-size: 10px;
        text-transform: uppercase;
        letter-spacing: 0.12em;
        color: var(--muted);
        margin-bottom: 5px;
      }
      .advise-sec p { margin: 0; font-family: var(--sans); font-size: 12.5px; line-height: 1.55; color: var(--text); }
      .advise-sec .steps {
        margin: 0;
        font-family: var(--mono);
        font-size: 11.5px;
        line-height: 1.6;
        color: var(--text);
        white-space: pre-wrap;
        word-break: break-word;
        background: rgba(0, 0, 0, 0.22);
        border: 1px solid var(--line-2);
        border-radius: 8px;
        padding: 10px 12px;
      }
      .advise-ft {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 12px;
        font-family: var(--mono);
        font-size: 10.5px;
        color: var(--muted);
      }
      .advise-regen,
      .advise-retry {
        font-family: var(--mono);
        font-size: 10.5px;
        color: var(--magenta);
        background: none;
        border: 1px solid rgba(255, 61, 154, 0.3);
        border-radius: 7px;
        padding: 5px 10px;
        cursor: pointer;
        transition: 0.15s;
      }
      .advise-regen:hover,
      .advise-retry:hover { border-color: var(--magenta); }
      .advise-error {
        display: flex;
        align-items: center;
        gap: 12px;
        flex-wrap: wrap;
        font-family: var(--mono);
        font-size: 11.5px;
        color: var(--red);
      }

      @media (prefers-reduced-motion: reduce) {
        .ctl,
        .chev {
          transition: none;
        }
        .advise-loading .spin,
        .advise-loading .pulse {
          animation: none;
        }
      }
    `,
  ],
})
export class ControlComplianceCardComponent {
  /** Lista de controles do pilar (o Smart Component já a entrega ordenada por risco). */
  readonly controls = input.required<ControlView[]>();

  /** Reexpõe ao template a tradução amigável de categoria (função pura do glossário NIST). */
  protected readonly categoryName = categoryName;

  /** Motor consultivo: gera o advisory sob demanda para um controle não-conforme. */
  private readonly advisorySvc = inject(AdvisoryService);

  /** Estado de UI puro: códigos das linhas expandidas. */
  private readonly open = signal<ReadonlySet<string>>(new Set());

  /** Estado da geração de advisory por controle (código NIST → estado). Ocioso = ausência de chave. */
  private readonly advisories = signal<ReadonlyMap<string, AdvisoryUiState>>(new Map());

  isOpen(code: string): boolean {
    return this.open().has(code);
  }

  toggle(code: string): void {
    const next = new Set(this.open());
    next.has(code) ? next.delete(code) : next.add(code);
    this.open.set(next);
  }

  /** Estado atual da geração de advisory do controle (undefined = ocioso, ainda não solicitado). */
  advisoryState(code: string): AdvisoryUiState | undefined {
    return this.advisories().get(code);
  }

  /**
   * Gera (ou regenera) a recomendação de remediação do controle. Fluxo: carregando → carregado/erro,
   * com o estado isolado por código para não vazar entre linhas. Reentrância barrada enquanto carrega.
   */
  generateAdvisory(code: string): void {
    if (this.advisoryState(code)?.status === 'loading') return;
    this.setAdvisory(code, { status: 'loading' });
    this.advisorySvc.generate(code).subscribe({
      next: (advisory) => this.setAdvisory(code, { status: 'loaded', advisory }),
      error: (err: Error) => this.setAdvisory(code, { status: 'error', message: err.message }),
    });
  }

  private setAdvisory(code: string, state: AdvisoryUiState): void {
    const next = new Map(this.advisories());
    next.set(code, state);
    this.advisories.set(next);
  }

  statusLabel(status: ControlStatus): string {
    switch (status) {
      case 'Compliant':
        return 'Conforme';
      case 'NonCompliant':
        return 'Não conforme';
      case 'MitigatedByThirdParty':
        return 'Parcial · 50%';
    }
  }
}

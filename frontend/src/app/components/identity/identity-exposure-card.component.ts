import { Component, input, signal } from '@angular/core';
import { ControlStatus } from '../../models/scoring.models';
import { IdentityFinding, IdentityPlatform } from '../../models/identity.models';
import { SeverityComponent } from '../scoring/severity.component';

/**
 * IdentityExposureCardComponent — DUMB. Renderiza os achados de identidade (Entra ID / Purple Knight) como
 * uma TABELA TÁTICA HUD: colunas Name · Platform · Severity · Status. Cada linha expande para a evidência
 * técnica da IA (os números do indicador e o motivo do veredito). Sem serviço; único estado é o de UI
 * (linhas abertas), num Signal local — mesmo idioma do ControlComplianceCardComponent.
 *
 * Tolerância Zero visual: NonCompliant salta aos olhos (vermelho, brilho); Compliant é contido; o controle
 * COMPENSATÓRIO (MitigatedByThirdParty) ganha um badge âmbar "Compensated Control" — a falha de MFA foi
 * justificada pelo isolamento de rede (OT/legado), e a UI deixa isso explícito.
 */
@Component({
  selector: 'app-identity-exposure-card',
  standalone: true,
  imports: [SeverityComponent],
  template: `
    <div class="tac-table" role="table" aria-label="Exposição de Identidade">
      <div class="thead" role="row">
        <span role="columnheader">Achado</span>
        <span role="columnheader">Plataforma</span>
        <span role="columnheader">Severidade</span>
        <span role="columnheader" class="col-status">Status</span>
        <span aria-hidden="true"></span>
      </div>

      @for (f of findings(); track f.code) {
        <div
          class="trow"
          role="row"
          [class.is-fail]="f.status === 'NonCompliant'"
          [class.is-comp]="f.status === 'MitigatedByThirdParty'"
          [class.is-ok]="f.status === 'Compliant'"
        >
          <button type="button" class="tcell head" (click)="toggle(f.code)" [attr.aria-expanded]="isOpen(f.code)">
            <span class="dot" aria-hidden="true"></span>
            <span class="names">
              <span class="name">{{ f.name }}</span>
              <span class="code">{{ f.code }}</span>
            </span>
          </button>

          <span class="tcell plat" role="cell">
            <span class="plat-badge" [attr.data-plat]="f.platform">
              <span class="glyph" aria-hidden="true">{{ platformGlyph(f.platform) }}</span>{{ f.platform }}
            </span>
          </span>

          <span class="tcell" role="cell"><app-severity [level]="f.severity" /></span>

          <span class="tcell status-cell" role="cell">
            <span class="status" [attr.data-status]="f.status">
              <span class="ico" aria-hidden="true">{{ statusIcon(f.status) }}</span>
              {{ statusLabel(f.status) }}
            </span>
            @if (f.compensated) {
              <span class="badge-comp" title="Falha de MFA justificada por isolamento de rede (OT/legado)">
                Compensated Control
              </span>
            }
          </span>

          <button type="button" class="tcell chev-cell" (click)="toggle(f.code)" [attr.aria-label]="'Detalhes de ' + f.code">
            <span class="chev" [class.open]="isOpen(f.code)" aria-hidden="true">›</span>
          </button>

          @if (isOpen(f.code)) {
            <div class="trow-body" role="cell">
              <p class="evidence">{{ f.evidence || 'Sem evidência registrada para este achado.' }}</p>
            </div>
          }
        </div>
      } @empty {
        <div class="empty">Nenhum achado de identidade. Execute uma análise do Entra ID.</div>
      }
    </div>
  `,
  styles: [
    `
      :host { display: block; }
      .tac-table { display: flex; flex-direction: column; gap: 8px; }

      /* Cabeçalho de colunas — mono, discreto. */
      .thead {
        display: grid;
        grid-template-columns: minmax(0, 1fr) 116px 132px 200px 22px;
        align-items: center;
        gap: 12px;
        padding: 2px 14px 6px;
        font-family: var(--mono);
        font-size: 9.5px;
        text-transform: uppercase;
        letter-spacing: 0.14em;
        color: var(--muted);
      }

      /* Linha: contêiner grid + a linha extra de corpo (span total) quando expandida. */
      .trow {
        display: grid;
        grid-template-columns: minmax(0, 1fr) 116px 132px 200px 22px;
        align-items: center;
        gap: 12px;
        padding: 4px 14px;
        border: 1px solid var(--line);
        border-left: 3px solid var(--line);
        border-radius: 10px;
        background: rgba(122, 145, 190, 0.03);
        transition: border-color 0.2s ease, background 0.2s ease;
      }
      .tcell { min-width: 0; display: flex; align-items: center; }
      .tcell.head {
        gap: 11px;
        background: none;
        border: 0;
        cursor: pointer;
        text-align: left;
        padding: 8px 0;
        color: var(--text);
        font-family: var(--sans);
      }
      .dot { width: 9px; height: 9px; border-radius: 50%; background: var(--muted); flex: none; }
      .names { display: flex; flex-direction: column; gap: 2px; min-width: 0; }
      .name { font-size: 13px; color: var(--text); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
      .code { font-family: var(--mono); font-size: 10.5px; letter-spacing: 0.06em; color: var(--muted); }

      /* Plataforma — badge com glyph tingido por origem. */
      .plat-badge {
        display: inline-flex; align-items: center; gap: 6px;
        font-family: var(--mono); font-size: 11px; color: var(--text);
        border: 1px solid var(--line); border-radius: 999px; padding: 3px 10px;
      }
      .plat-badge .glyph { font-size: 11px; line-height: 1; }
      .plat-badge[data-plat='Entra'] { border-color: rgba(38, 224, 255, 0.4); }
      .plat-badge[data-plat='Entra'] .glyph { color: var(--cyan); }
      .plat-badge[data-plat='AD'] { border-color: rgba(139, 92, 255, 0.4); }
      .plat-badge[data-plat='AD'] .glyph { color: var(--violet); }
      .plat-badge[data-plat='Okta'] { border-color: rgba(70, 176, 255, 0.4); }
      .plat-badge[data-plat='Okta'] .glyph { color: var(--cyan-2); }

      /* Status — ícone HUD + rótulo, tingido pelo veredito. */
      .status-cell { flex-wrap: wrap; gap: 6px 10px; }
      .status {
        display: inline-flex; align-items: center; gap: 6px;
        font-family: var(--mono); font-size: 11px; letter-spacing: 0.04em;
      }
      .status .ico { font-size: 12px; line-height: 1; }
      .status[data-status='Compliant'] { color: var(--cyan); }
      .status[data-status='MitigatedByThirdParty'] { color: var(--amber); }
      .status[data-status='NonCompliant'] { color: var(--red); }

      /* Badge do controle COMPENSATÓRIO — âmbar neon, o destaque pedido. */
      .badge-comp {
        font-family: var(--mono); font-size: 9.5px; font-weight: 500;
        text-transform: uppercase; letter-spacing: 0.1em;
        color: var(--amber);
        border: 1px solid rgba(255, 176, 32, 0.45);
        background: rgba(255, 176, 32, 0.09);
        border-radius: 999px; padding: 2px 9px;
        box-shadow: inset 0 0 14px -8px var(--amber), 0 0 10px -6px rgba(255, 176, 32, 0.7);
      }

      .chev-cell { justify-content: center; background: none; border: 0; cursor: pointer; padding: 0; }
      .chev { font-size: 18px; color: var(--muted); transform: rotate(90deg); transition: transform 0.2s ease; }
      .chev.open { transform: rotate(-90deg); }

      /* Corpo expandido: ocupa a linha inteira (todas as colunas). */
      .trow-body {
        grid-column: 1 / -1;
        border-top: 1px solid var(--line-2);
        padding: 10px 2px 6px 20px;
      }
      .evidence {
        margin: 0; font-family: var(--mono); font-size: 12px; line-height: 1.55;
        color: var(--text); opacity: 0.9;
      }

      /* Compliant — contido. */
      .trow.is-ok { opacity: 0.85; }
      .trow.is-ok .dot { background: var(--cyan); opacity: 0.45; }

      /* Compensatório — âmbar discreto na borda. */
      .trow.is-comp { border-left-color: var(--amber); background: rgba(255, 176, 32, 0.05); }
      .trow.is-comp .dot { background: var(--amber); box-shadow: 0 0 8px -1px var(--amber); }

      /* NonCompliant — SALTA AOS OLHOS. */
      .trow.is-fail {
        border-color: rgba(255, 45, 111, 0.5);
        border-left-color: var(--red);
        background: linear-gradient(90deg, rgba(255, 45, 111, 0.1), rgba(255, 45, 111, 0.02));
        box-shadow: inset 0 0 22px -14px rgba(255, 45, 111, 0.8), 0 0 18px -12px rgba(255, 45, 111, 0.6);
      }
      .trow.is-fail .dot { background: var(--red); box-shadow: 0 0 10px 1px var(--red); }
      .trow.is-fail .name { color: #ffe3ee; }

      .empty {
        font-family: var(--mono); font-size: 12px; color: var(--muted); padding: 20px 6px;
        border: 1px dashed var(--line); border-radius: 10px; text-align: center;
      }

      /* Responsivo: abaixo de 720px a linha vira cartão empilhado (o grid colapsa). */
      @media (max-width: 720px) {
        .thead { display: none; }
        .trow { grid-template-columns: 1fr auto; gap: 8px 12px; padding: 12px 14px; }
        .tcell.head { grid-column: 1 / -1; }
        .chev-cell { display: none; }
        .status-cell { justify-content: flex-start; }
      }
      @media (prefers-reduced-motion: reduce) {
        .trow, .chev { transition: none; }
      }
    `,
  ],
})
export class IdentityExposureCardComponent {
  /** Achados já projetados e ordenados por risco (o Smart Component entrega prontos). */
  readonly findings = input.required<IdentityFinding[]>();

  /** Estado de UI puro: códigos das linhas expandidas. */
  private readonly open = signal<ReadonlySet<string>>(new Set());

  isOpen(code: string): boolean {
    return this.open().has(code);
  }

  toggle(code: string): void {
    const next = new Set(this.open());
    next.has(code) ? next.delete(code) : next.add(code);
    this.open.set(next);
  }

  statusLabel(status: ControlStatus): string {
    switch (status) {
      case 'Compliant':
        return 'Conforme';
      case 'NonCompliant':
        return 'Não conforme';
      case 'MitigatedByThirdParty':
        return 'Mitigado · 50%';
    }
  }

  statusIcon(status: ControlStatus): string {
    switch (status) {
      case 'Compliant':
        return '✓';
      case 'NonCompliant':
        return '✕';
      case 'MitigatedByThirdParty':
        return '◐';
    }
  }

  platformGlyph(platform: IdentityPlatform): string {
    switch (platform) {
      case 'Entra':
        return '◆';
      case 'AD':
        return '⬢';
      case 'Okta':
        return '◉';
    }
  }
}

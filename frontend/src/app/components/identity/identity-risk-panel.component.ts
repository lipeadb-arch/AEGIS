import { DatePipe } from '@angular/common';
import { Component, computed, input } from '@angular/core';
import {
  CONSULTATIVE_CAVEAT,
  IdentityEvidenceProjection,
  IdentityRiskCapability,
  NO_DETECTION_CAVEAT,
  countDisplay,
  detectionTypeLabel,
  isLimited,
  levelSlices,
  outcomeGuidance,
  outcomeLabel,
  sectionMessage,
  sectionState,
  stateSlices,
  topDetectionTypes,
} from '../../models/identity-risk.models';

/**
 * [AEGIS-MVP-MICROSOFT-COVERAGE-03] Painel DUMB de risco de identidade, embutido na página do AEGIS KNIGHT
 * (sem item de menu novo). Não faz E/S: recebe a projeção JÁ lida da Evidence Fabric compartilhada — a MESMA
 * fotografia que o KNIGHT avalia — e apenas apresenta.
 *
 * Regras que o painel garante:
 *  • fica SEPARADO dos indicadores pontuáveis e declara, no rodapé, que não altera nenhum dos dois scores;
 *  • distingue coletado × permissão ausente × licença insuficiente × parcial × indisponível × nunca coletado
 *    × última fotografia preservada após falha;
 *  • nunca transforma "não coletado" em zero (mostra "—") e marca leitura incompleta com "≥ n";
 *  • não exibe nome, e-mail, ID, IP nem localização — o contrato do backend sequer os transporta;
 *  • nomes crus de enum e detalhes técnicos ficam na área expandida, não na visão inicial.
 */
@Component({
  selector: 'app-identity-risk-panel',
  standalone: true,
  imports: [DatePipe],
  template: `
  <!-- [AEGIS-MVP-MICROSOFT-COVERAGE-03] Risco de identidade — SEPARADO dos indicadores pontuáveis.
       Nada aqui altera o AEGIS Score nem o AEGIS KNIGHT Score, e nada aqui identifica uma pessoa. -->
  <section class="panel risk" aria-labelledby="identity-risk-title">
    <div class="hd">
      <h3 id="identity-risk-title">Risco de identidade</h3>
      <span class="hint">Microsoft Entra ID Protection · consultivo</span>
    </div>

    <p class="risk-lead">{{ message() }}</p>

    @if (state() === 'NoConnector' || state() === 'NeverCollected') {
      <p class="risk-empty">
        Use <b>Coletar do Entra ID</b> acima para produzir a primeira fotografia. Enquanto não houver
        coleta, o AEGIS não afirma nem que existe nem que não existe risco.
      </p>
    } @else {
      <div class="counts kpis">
        <div class="count fail" [class.hot]="(usersCap()?.hasData ?? false) && (risk()?.riskyUsers?.active ?? 0) > 0">
          <span class="n">{{ countDisplay(usersCap(), risk()?.riskyUsers?.active) }}</span>
          <span class="l">Usuários que exigem investigação</span>
        </div>
        <div class="count">
          <span class="n">{{ countDisplay(usersCap(), risk()?.riskyUsers?.highRiskActive) }}</span>
          <span class="l">Desses, de risco alto</span>
        </div>
        <div class="count">
          <span class="n">{{ countDisplay(usersCap(), risk()?.riskyUsers?.states?.confirmedCompromised) }}</span>
          <span class="l">A Microsoft marcou como potencialmente comprometidas</span>
        </div>
        <div class="count">
          <span class="n">{{ countDisplay(detectionsCap(), risk()?.detections?.active) }}</span>
          <span class="l">Detecções em aberto na janela</span>
        </div>
        <div class="count">
          <span class="n">{{ countDisplay(detectionsCap(), risk()?.detections?.highRiskActive) }}</span>
          <span class="l">Detecções de risco alto em aberto</span>
        </div>
        <div class="count">
          <span class="n small">{{ (risk()?.detections?.mostRecentDetectionAt | date: 'dd/MM/yyyy HH:mm') || '—' }}</span>
          <span class="l">Detecção mais recente</span>
        </div>
        <div class="count">
          <span class="n small">{{ (projection()?.collectedAt | date: 'dd/MM/yyyy HH:mm') || '—' }}</span>
          <span class="l">Última coleta bem-sucedida</span>
        </div>
      </div>

      <div class="dists">
        <div class="dist">
          <h4>Usuários por nível de risco</h4>
          @if (userLevels().length) {
            <ul>
              @for (sl of userLevels(); track sl.key) {
                <li><span class="dl">{{ sl.label }}</span><span class="dv">{{ sl.count }}</span></li>
              }
            </ul>
          } @else {
            <p class="none">Sem distribuição disponível nesta dimensão.</p>
          }
        </div>

        <div class="dist">
          <h4>Usuários por situação</h4>
          @if (userStates().length) {
            <ul>
              @for (sl of userStates(); track sl.key) {
                <li><span class="dl">{{ sl.label }}</span><span class="dv">{{ sl.count }}</span></li>
              }
            </ul>
          } @else {
            <p class="none">Sem distribuição disponível nesta dimensão.</p>
          }
        </div>

        <div class="dist">
          <h4>Principais tipos de detecção</h4>
          @if (topTypes().length) {
            <ul>
              @for (t of topTypes(); track t.category) {
                <li><span class="dl">{{ detectionTypeLabel(t.category) }}</span><span class="dv">{{ t.count }}</span></li>
              }
            </ul>
          } @else {
            <p class="none">Nenhuma detecção classificada na janela.</p>
          }
        </div>
      </div>

      <div class="caps">
        <div class="cap" [class.limited]="isLimited(usersCap())">
          <span class="cap-name">Usuários em risco</span>
          <span class="cap-state">{{ outcomeLabel(usersCap()!.outcome) }}</span>
          <span class="cap-help">{{ outcomeGuidance(usersCap()!.outcome, 'IdentityRiskyUser.Read.All') }}</span>
        </div>
        <div class="cap" [class.limited]="isLimited(detectionsCap())">
          <span class="cap-name">Detecções de risco</span>
          <span class="cap-state">{{ outcomeLabel(detectionsCap()!.outcome) }}</span>
          <span class="cap-help">{{ outcomeGuidance(detectionsCap()!.outcome, 'IdentityRiskEvent.Read.All') }}</span>
        </div>
      </div>

      <details class="risk-tech">
        <summary>Detalhes técnicos, permissões e valores originais</summary>
        <ul>
          <li>Origem: <b>Microsoft Entra ID Protection</b> · <code>GET /v1.0/identityProtection/riskyUsers</code> e <code>GET /v1.0/identityProtection/riskDetections</code>.</li>
          <li>Permissões de aplicativo: <code>IdentityRiskyUser.Read.All</code> e <code>IdentityRiskEvent.Read.All</code>.</li>
          <li>Janela determinística: <b>{{ risk()?.detections?.windowDays || 30 }} dias</b>; detecções nos últimos 7 dias: <b>{{ countDisplay(detectionsCap(), risk()?.detections?.inRecentWindow) }}</b>.</li>
          <li>Fora da janela: <b>{{ countDisplay(detectionsCap(), risk()?.detections?.outsideWindow) }}</b> · sem data utilizável: <b>{{ countDisplay(detectionsCap(), risk()?.detections?.undated) }}</b>.</li>
          <li>Em tempo real: <b>{{ countDisplay(detectionsCap(), risk()?.detections?.realtime) }}</b> · processadas depois: <b>{{ countDisplay(detectionsCap(), risk()?.detections?.offline) }}</b>.</li>
          <li>Detecções sem categoria por limitação de plano (<code>generic</code>): <b>{{ countDisplay(detectionsCap(), risk()?.detections?.premiumDetailWithheld) }}</b>.</li>
          <li>Contas já excluídas do diretório, fora dos indicadores acima: <b>{{ countDisplay(usersCap(), risk()?.riskyUsers?.deleted) }}</b>.</li>
          @if (usersCap()?.detail) { <li>Usuários em risco — estado detalhado: {{ usersCap()!.detail }}</li> }
          @if (detectionsCap()?.detail) { <li>Detecções — estado detalhado: {{ detectionsCap()!.detail }}</li> }
          <li>Schema do snapshot: <code>{{ projection()?.schemaVersion || '—' }}</code>.</li>
        </ul>
      </details>
    }

    <p class="risk-caveat">{{ noDetectionCaveat }}</p>
    <p class="risk-caveat">{{ consultativeCaveat }}</p>
  </section>
  `,
  styles: [
    `
/* Painel autossuficiente: como o encapsulamento do Angular isola os estilos, o que a página do KNIGHT
   define (.panel/.hd/.counts) é redeclarado aqui, no idioma visual do produto. */
:host { display: block; margin-top: 16px; }
.panel { border: 1px solid var(--line); border-radius: 14px; background: rgba(122, 145, 190, 0.03); padding: 18px; }
.hd { display: flex; align-items: baseline; justify-content: space-between; gap: 12px; flex-wrap: wrap; margin-bottom: 12px; }
.hd h3 { margin: 0; font-family: var(--sans); font-size: 14px; font-weight: 600; color: var(--text); }
.hint { font-family: var(--mono); font-size: 11px; color: var(--muted); }
p { margin: 0 0 4px; font-family: var(--mono); font-size: 11px; line-height: 1.55; color: var(--muted); }
b { color: var(--text); }
code { color: var(--text); background: rgba(255, 255, 255, 0.06); padding: 1px 5px; border-radius: 4px; }
.risk-lead, .risk-empty { margin-bottom: 14px; font-size: 11.5px; }
.risk-empty { border-left: 2px solid var(--line); padding-left: 12px; }

.counts { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 8px; margin-bottom: 16px; }
.count { display: flex; flex-direction: column; gap: 3px; padding: 10px; border: 1px solid var(--line); border-radius: 9px; background: rgba(122, 145, 190, 0.03); }
.count .n { font-family: var(--display); font-weight: 700; font-size: 19px; color: var(--text); }
.count .n.small { font-size: 12px; font-family: var(--mono); }
.count .l { font-family: var(--mono); font-size: 9px; text-transform: uppercase; letter-spacing: 0.08em; line-height: 1.4; color: var(--muted); }
.count.fail.hot { border-color: rgba(255, 45, 111, 0.45); background: rgba(255, 45, 111, 0.06); }
.count.fail.hot .n { color: var(--red); }

.dists { display: grid; grid-template-columns: repeat(auto-fit, minmax(210px, 1fr)); gap: 14px; margin-bottom: 16px; }
.dist h4 { margin: 0 0 8px; font-family: var(--mono); font-size: 11px; text-transform: uppercase; letter-spacing: 0.08em; color: var(--muted); }
.dist ul { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 5px; }
.dist li { display: flex; justify-content: space-between; gap: 10px; font-family: var(--mono); font-size: 11.5px; line-height: 1.5; color: var(--muted); }
.dist .dv { color: var(--text); font-weight: 700; }
.dist .none { margin: 0; }

.caps { display: grid; grid-template-columns: repeat(auto-fit, minmax(250px, 1fr)); gap: 10px; margin-bottom: 14px; }
.cap { border: 1px solid var(--line); border-radius: 11px; padding: 12px; font-family: var(--mono); font-size: 11px; line-height: 1.5; color: var(--muted); }
.cap span { display: block; }
.cap-name { font-size: 12px; font-weight: 700; color: var(--text); }
.cap-state { font-size: 11.5px; color: var(--cyan); margin: 3px 0; }
.cap.limited { border-color: rgba(255, 176, 32, 0.45); background: rgba(255, 176, 32, 0.05); }
.cap.limited .cap-state { color: var(--amber); }

.risk-tech { margin-bottom: 12px; }
.risk-tech summary { cursor: pointer; font-family: var(--mono); font-size: 11.5px; color: var(--cyan); }
.risk-tech ul { margin: 10px 0 0; padding-left: 18px; display: flex; flex-direction: column; gap: 5px; }
.risk-tech li { font-family: var(--mono); font-size: 11px; line-height: 1.5; color: var(--muted); }

@media (max-width: 720px) {
  .counts, .dists, .caps { grid-template-columns: 1fr; }
}

    `,
  ],
})
export class IdentityRiskPanelComponent {
  /** Projeção consultiva da Evidence Fabric — `null` enquanto não houver leitura. */
  readonly projection = input<IdentityEvidenceProjection | null>(null);

  protected readonly countDisplay = countDisplay;
  protected readonly detectionTypeLabel = detectionTypeLabel;
  protected readonly isLimited = isLimited;
  protected readonly outcomeLabel = outcomeLabel;
  protected readonly outcomeGuidance = outcomeGuidance;
  protected readonly noDetectionCaveat = NO_DETECTION_CAVEAT;
  protected readonly consultativeCaveat = CONSULTATIVE_CAVEAT;

  readonly risk = computed(() => this.projection()?.identityRisk ?? null);
  readonly state = computed(() => sectionState(this.projection()));
  readonly message = computed(() => sectionMessage(this.state()));
  readonly usersCap = computed<IdentityRiskCapability | null>(() => this.risk()?.riskyUsersCapability ?? null);
  readonly detectionsCap = computed<IdentityRiskCapability | null>(() => this.risk()?.riskDetectionsCapability ?? null);
  readonly userLevels = computed(() => levelSlices(this.risk()?.riskyUsers?.levels ?? null));
  readonly userStates = computed(() => stateSlices(this.risk()?.riskyUsers?.states ?? null));
  readonly topTypes = computed(() => topDetectionTypes(this.risk()?.detections ?? null));
}

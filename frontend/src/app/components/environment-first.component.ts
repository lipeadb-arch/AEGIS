import { DatePipe } from '@angular/common';
import { Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  EvidenceNatureView,
  WorkspacePosture,
  connectorBreakdown,
  environmentStage,
  evidenceNatures,
} from '../models/workspace.models';

/**
 * [AEGIS-MVP-ENV-01] Bloco "Comece pelo ambiente" — a jornada ENVIRONMENT-FIRST do produto. Reusa a projeção
 * ÚNICA do workspace (/scoring/workspace): NÃO recalcula score nem cobertura e NÃO faz chamada própria. A etapa
 * (A→D) é derivada por FUNÇÃO PURA (`environmentStage`) da própria projeção, então o bloco serve tanto de
 * onboarding (tenant começando) quanto de resumo de progresso (ambiente já medido).
 *
 * Invariantes da entrega:
 *  • cobertura NÃO é conformidade — visual NEUTRO, jamais cores de aprovação (100% coberto pode ter não conformes;
 *    0% é "não avaliado", não reprovação);
 *  • `notAutomated` nunca é apresentado como falha do usuário;
 *  • documentos são REPOSICIONADOS (complemento posterior), nunca removidos;
 *  • CTA administrativo só para TenantAdmin — ao não administrador, orientação, sem botão que redirecionaria em
 *    silêncio (a rota /settings/integrations é guardada por tenantAdminGuard);
 *  • a IA é CONSULTIVA: explica/correlaciona/contextualiza/apoia remediação, sem alterar score, cobertura ou lifecycle.
 */
@Component({
  selector: 'app-environment-first',
  standalone: true,
  imports: [DatePipe, RouterLink],
  template: `
    <section class="env" [attr.data-stage]="stage()">
      <div class="head">
        <span class="eyebrow">Comece pelo ambiente</span>
        <span class="step">Etapa {{ stepLabel() }}</span>
      </div>

      <!-- Aviso operacional: falha, degradação OU sincronização parcial (nunca-sincronizado fora da etapa B). -->
      @if (hasConnectorAlert()) {
        <div class="alert">
          <b>Atenção aos conectores.</b>
          {{ connectorBreakdown(connectors()) }} — a coleta pode estar incompleta. A última coleta válida e os
          resultados já reconciliados seguem preservados.
          @if (isTenantAdmin()) {
            <a routerLink="/settings/integrations" class="link">Revisar integrações</a>
          } @else {
            <span class="muted">Peça a um administrador para revisar as integrações.</span>
          }
        </div>
      }

      @switch (stage()) {
        @case ('no-enabled-connector') {
          <!-- Etapa A, duas apresentações derivadas de configured: enabled==0 não implica nada configurado. -->
          @if (connectors().configured === 0) {
            <h4>Nenhum ambiente conectado</h4>
            <p>
              O primeiro valor do Aegis vem da <b>leitura segura e somente leitura</b> do seu ambiente — cloud,
              identidade, ativos e controles. <b>Documentos não são necessários para começar</b>: a governança
              entra depois, onde houver lacuna genuinamente organizacional.
            </p>
            @if (isTenantAdmin()) {
              <a routerLink="/settings/integrations" class="cta">Conectar um ambiente</a>
            } @else {
              <p class="muted">
                Peça a um administrador para conectar um ambiente em <b>Configurações › Integrações</b>.
              </p>
            }
          } @else {
            <h4>Nenhuma coleta está ativa</h4>
            <p>
              Há {{ connectors().configured }} integração(ões) configurada(s), porém <b>nenhuma habilitada</b>.
              Resultados já coletados podem seguir visíveis, mas <b>nenhuma nova sincronização ocorrerá</b>
              enquanto as integrações permanecerem desabilitadas.
            </p>
            @if (isTenantAdmin()) {
              <a routerLink="/settings/integrations" class="cta">Revisar integrações</a>
            } @else {
              <p class="muted">
                Peça a um administrador para revisar as integrações em <b>Configurações › Integrações</b>.
              </p>
            }
          }
        }

        @case ('never-synced') {
          <h4>Ambiente configurado, ainda sem coleta</h4>
          <p>
            {{ connectors().enabled }} conector(es) habilitado(s), mas nenhum sincronizou ainda. Sem coleta não há
            leitura técnica — e leitura ausente <b>não é zero risco</b>.
          </p>
          @if (isTenantAdmin()) {
            <a routerLink="/settings/integrations" class="cta">Testar e sincronizar</a>
          } @else {
            <p class="muted">
              Peça a um administrador para testar e sincronizar em <b>Configurações › Integrações</b>.
            </p>
          }
        }

        @case ('synced-no-tech-coverage') {
          <h4>Ambiente sincronizado</h4>
          <p>
            A coleta aconteceu. Conforme a capacidade do conector, já podem existir <b>ativos, exposições ou
            vulnerabilidades</b> — mesmo sem alterar o AEGIS Score. O Score só recebe crédito quando sinais
            mapeados comprovam controles deterministicamente.
          </p>
          <div class="links">
            <a routerLink="/assets" class="link">Ativos</a>
            <a routerLink="/exposures" class="link">Exposições</a>
            <a routerLink="/vulnerabilities" class="link">Vulnerabilidades</a>
          </div>
        }

        @case ('measured') {
          <!-- Existe coleta técnica. O TÍTULO reflete a saúde ATUAL sem apagar a evidência histórica. -->
          @if (hasConnectorAlert()) {
            <h4>Medição disponível, integrações requerem atenção</h4>
          } @else {
            <h4>Ambiente medido</h4>
          }
          <p>
            Cobertura técnica de <b>{{ telemetry().coveragePercentage.toFixed(1) }}%</b>
            ({{ telemetry().evaluatedControls }}/{{ telemetry().eligibleControls }} controles de ambiente),
            apurada da última evidência válida@if (connectors().lastSyncAt) {
              (sincronização {{ connectors().lastSyncAt | date: 'dd/MM HH:mm' }})}. A cobertura e os resultados
            vêm dessa última evidência; a <b>saúde operacional atual</b> dos conectores
            ({{ connectors().healthy }}/{{ connectors().enabled }} operacionais) é um eixo à parte.
          </p>
          <div class="links">
            <a routerLink="/exposures" class="link">Exposições</a>
            <a routerLink="/vulnerabilities" class="link">Vulnerabilidades</a>
            <a routerLink="/assets" class="link">Ativos</a>
          </div>
        }
      }

      <!-- Cobertura por NATUREZA — visível assim que há coleta (C e D). Visual NEUTRO: cobertura não é aprovação. -->
      @if (showCoverage()) {
        <div class="cov-wrap">
          <p class="cov-cap">Cobertura por natureza da medição</p>
          <div class="cov-grid">
            @for (n of natures(); track n.key) {
              <div class="cov" [class.zero]="n.slice.eligibleControls === 0">
                <div class="cov-top">
                  <span class="cov-label">{{ n.label }}</span>
                  <span class="cov-pct">{{ n.slice.coveragePercentage.toFixed(1) }}%</span>
                </div>
                <div class="cov-bar" role="presentation">
                  <span class="cov-fill" [style.width.%]="clampPct(n.slice.coveragePercentage)"></span>
                </div>
                <span class="cov-sub">
                  {{ n.slice.evaluatedControls }}/{{ n.slice.eligibleControls }} controles avaliados
                </span>
                <span class="cov-help">{{ n.help }}</span>
              </div>
            }
          </div>
          @if (showScoreCaption()) {
            <p class="cov-note">
              Score calculado somente sobre a parcela avaliada — cobertura geral
              {{ overall().coveragePercentage.toFixed(1) }}%.
            </p>
          }
        </div>
      }

      <!-- IA consultiva: só apresentação. Não dispara chamada ao abrir o Dashboard nem modifica score/lifecycle. -->
      @if (showAiNote()) {
        <p class="ai">
          <b>IA consultiva:</b> após a coleta, a IA explica achados, correlaciona a postura com ativos e
          vulnerabilidades, contextualiza impacto e apoia a remediação — sem inventar evidência nem alterar
          score, cobertura, status ou lifecycle.
        </p>
      }

      <!-- Documentos REPOSICIONADOS (não removidos): complemento posterior, não etapa obrigatória paralela. -->
      <p class="docs">
        Depois de ler o que foi coletado, complemente a <a routerLink="/governance" class="link">governança</a>
        onde houver lacuna organizacional — com entrevista, evidência dirigida ou documento, só quando necessário.
      </p>
    </section>
  `,
  styles: [
    `
      .env {
        border: 1px solid var(--line);
        border-left: 3px solid var(--cyan);
        border-radius: 12px;
        background: rgba(122, 145, 190, 0.03);
        padding: 16px 18px;
        margin: 0 0 20px;
      }
      .head {
        display: flex;
        align-items: baseline;
        justify-content: space-between;
        gap: 12px;
      }
      .eyebrow {
        font-family: var(--mono);
        font-size: 10px;
        letter-spacing: 0.14em;
        text-transform: uppercase;
        color: var(--cyan);
      }
      .step {
        font-family: var(--mono);
        font-size: 10px;
        letter-spacing: 0.08em;
        color: var(--muted);
      }
      h4 {
        margin: 10px 0 6px;
        font-family: var(--display);
        font-size: 16px;
        font-weight: 600;
        color: var(--text);
      }
      p {
        margin: 0 0 10px;
        font-family: var(--sans);
        font-size: 13px;
        line-height: 1.6;
        color: var(--text);
        opacity: 0.9;
      }
      p b {
        color: var(--text);
        font-weight: 600;
      }
      .muted {
        color: var(--muted);
      }
      /* CTA administrativo — só o TenantAdmin o vê; ao não admin, orientação em texto. */
      .cta {
        display: inline-block;
        font-family: var(--mono);
        font-size: 12px;
        letter-spacing: 0.02em;
        color: var(--text);
        background: rgba(38, 224, 255, 0.1);
        border: 1px solid var(--cyan);
        border-radius: 8px;
        padding: 8px 16px;
        text-decoration: none;
        transition: background 0.15s ease;
      }
      .cta:hover {
        background: rgba(38, 224, 255, 0.18);
      }
      .links {
        display: flex;
        flex-wrap: wrap;
        gap: 14px;
        margin: 2px 0 4px;
      }
      .link {
        font-family: var(--mono);
        font-size: 12px;
        color: var(--cyan);
        text-decoration: none;
        border-bottom: 1px solid transparent;
      }
      .link:hover {
        border-bottom-color: var(--cyan);
      }
      /* Aviso de conectores — tom de alerta, mas sem apagar a coleta válida anterior. */
      .alert {
        border: 1px solid rgba(255, 176, 32, 0.4);
        background: rgba(255, 176, 32, 0.06);
        border-radius: 8px;
        padding: 10px 12px;
        margin: 10px 0;
        font-family: var(--sans);
        font-size: 12.5px;
        line-height: 1.55;
        color: var(--text);
      }
      .alert b {
        color: var(--amber);
      }
      .alert .link {
        margin-left: 6px;
      }
      /* Cobertura por natureza — NEUTRO de propósito: cobertura não é conformidade. */
      .cov-wrap {
        margin-top: 12px;
        padding-top: 12px;
        border-top: 1px solid var(--line-2, var(--line));
      }
      .cov-cap {
        margin: 0 0 8px;
        font-family: var(--mono);
        font-size: 10px;
        letter-spacing: 0.1em;
        text-transform: uppercase;
        color: var(--muted);
      }
      .cov-grid {
        display: grid;
        grid-template-columns: repeat(4, 1fr);
        gap: 10px;
      }
      .cov {
        display: flex;
        flex-direction: column;
        gap: 5px;
        padding: 10px 12px;
        border: 1px solid var(--line);
        border-radius: 10px;
        background: rgba(122, 145, 190, 0.04);
      }
      .cov.zero {
        opacity: 0.72;
      }
      .cov-top {
        display: flex;
        align-items: baseline;
        justify-content: space-between;
        gap: 8px;
      }
      .cov-label {
        font-family: var(--sans);
        font-size: 12px;
        font-weight: 600;
        color: var(--text);
      }
      .cov-pct {
        font-family: var(--display);
        font-weight: 700;
        font-size: 15px;
        /* Neutro: NUNCA a régua de cor do score (ciano/âmbar/vermelho). */
        color: var(--text);
      }
      .cov-bar {
        position: relative;
        height: 5px;
        border-radius: 999px;
        background: rgba(122, 145, 190, 0.16);
        overflow: hidden;
      }
      .cov-fill {
        position: absolute;
        left: 0;
        top: 0;
        bottom: 0;
        border-radius: 999px;
        /* Cinza-azulado neutro — leitura de "quanto foi medido", não de aprovação. */
        background: rgba(122, 145, 190, 0.6);
      }
      .cov-sub {
        font-family: var(--mono);
        font-size: 10.5px;
        color: var(--muted);
      }
      .cov-help {
        font-family: var(--sans);
        font-size: 11px;
        line-height: 1.45;
        color: var(--muted);
      }
      .cov-note {
        margin: 10px 0 0;
        font-family: var(--mono);
        font-size: 11px;
        color: var(--muted);
      }
      .ai {
        margin: 12px 0 0;
        font-family: var(--sans);
        font-size: 12px;
        line-height: 1.55;
        color: var(--muted);
      }
      .ai b {
        color: var(--cyan);
        font-weight: 600;
      }
      .docs {
        margin: 12px 0 0;
        padding-top: 10px;
        border-top: 1px solid var(--line-2, var(--line));
        font-family: var(--sans);
        font-size: 12px;
        line-height: 1.55;
        color: var(--muted);
      }
      @media (max-width: 860px) {
        .cov-grid {
          grid-template-columns: repeat(2, 1fr);
        }
      }
      @media (max-width: 520px) {
        .cov-grid {
          grid-template-columns: 1fr;
        }
      }
    `,
  ],
})
export class EnvironmentFirstComponent {
  /** Projeção única do workspace — a MESMA fonte do AEGIS Score; o bloco não recalcula nada. */
  readonly posture = input.required<WorkspacePosture>();
  /** Papel no tenant ativo (AuthService.isTenantAdmin) — gate de visibilidade do CTA administrativo. */
  readonly isTenantAdmin = input<boolean>(false);

  protected readonly connectorBreakdown = connectorBreakdown;

  readonly stage = computed(() => environmentStage(this.posture()));
  readonly connectors = computed(() => this.posture().connectors);
  readonly overall = computed(() => this.posture().overall);
  readonly telemetry = computed(() => this.posture().evidenceCoverage.telemetry);
  readonly natures = computed<EvidenceNatureView[]>(() => evidenceNatures(this.posture().evidenceCoverage));

  /**
   * Aviso operacional visível: falha, degradação OU sincronização parcial (algum habilitado nunca sincronizou).
   * O nunca-sincronizado NÃO duplica o aviso na etapa B, onde TODOS os habilitados nunca sincronizaram e o
   * conteúdo central já explica isso. `disabled` NÃO é falha (pode ser intencional) e não dispara o aviso.
   */
  readonly hasConnectorAlert = computed(() => {
    const c = this.connectors();
    return c.failed > 0 || c.degraded > 0 || (c.neverSynced > 0 && this.stage() !== 'never-synced');
  });

  /**
   * Cobertura por natureza aparece sempre que há ALGO real (histórico inclusive): alguma sincronização em
   * qualquer conector (mesmo depois desabilitado) OU algum controle avaliado. Some apenas quando não há
   * nenhuma avaliação e nenhuma sincronização. `overall.evaluatedControls > 0` já equivale a "algum bucket
   * avaliado" (os buckets particionam o total). Não usa `lastSyncAt` (que só olha habilitados).
   */
  readonly showCoverage = computed(() => {
    const w = this.posture();
    const anySync = w.connectors.items.some((i) => i.everSynced);
    return anySync || w.overall.evaluatedControls > 0;
  });

  /** Nota da IA consultiva — quando há algo real a explicar (alguma sincronização OU algum controle avaliado). */
  readonly showAiNote = computed(() => this.showCoverage());

  /** Leitura honesta do escopo do score, quando há score e a cobertura geral ainda não é total. */
  readonly showScoreCaption = computed(() => {
    const o = this.overall();
    return o.evaluationState === 'Evaluated' && o.coveragePercentage < 100;
  });

  readonly stepLabel = computed(
    () =>
      ({ 'no-enabled-connector': 'A', 'never-synced': 'B', 'synced-no-tech-coverage': 'C', measured: 'D' })[
        this.stage()
      ],
  );

  /** Barra de cobertura: largura clampada a [0,100] — nunca estoura o trilho por dado inesperado. */
  clampPct(v: number): number {
    return Math.max(0, Math.min(100, v));
  }
}

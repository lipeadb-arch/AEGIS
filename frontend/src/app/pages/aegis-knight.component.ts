import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { environment } from '../../environments/environment';
import { ScoreGaugeComponent } from '../components/scoring/score-gauge.component';
import {
  KnightAssessment,
  KnightIndicator,
  KnightSourceType,
  KnightSources,
  capabilityLabel,
  capabilityOutcomeLabel,
  categoryLabel,
  connectionBadgeLabel,
  connectionStateOf,
  findingSituation,
  findingTitle,
  isProblemState,
  problemCapabilities,
  severityLabel,
  sortIndicatorsByRisk,
  sourceStateLabel,
  sourceTypeLabel,
  statusLabel,
} from '../models/knight.models';
import { IdentityRiskPanelComponent } from '../components/identity/identity-risk-panel.component';
import { KnightFindingDetailComponent } from '../components/knight/finding-detail.component';
import { IdentityEvidenceProjection } from '../models/identity-risk.models';
import { IdentityRiskService } from '../services/identity-risk.service';
import { KnightService } from '../services/knight.service';
import {
  ActionPlan,
  KnightOriginMode,
  KnightOriginSource,
  PinnedPlanState,
  activePlanFor,
  pinnedPlanRejection,
  planForPanel,
} from '../models/remediation.models';
import { RemediationService } from '../services/remediation.service';
import { PostureHistoryService } from '../services/posture-history.service';

/**
 * AegisKnightComponent — SMART. Tela MULTICOLETOR do AEGIS KNIGHT.
 *
 * Ao abrir, lê as FONTES disponíveis e o ÚLTIMO assessment (somente leitura — NÃO executa análise). O botão
 * "Executar assessment demo" roda a fonte Demo (sintética); quando uma fonte real (Entra ID) está configurada,
 * um botão dedicado dispara a COLETA REAL. A tela identifica a fonte usada e o estado da coleta (concluída,
 * parcial, permissão insuficiente…), lista as limitações de cobertura e nunca confunde Demo com coleta real —
 * uma coleta real que falha mostra o estado real, jamais o resultado Demo.
 */
@Component({
  selector: 'app-aegis-knight',
  standalone: true,
  imports: [ScoreGaugeComponent, DatePipe, RouterLink, IdentityRiskPanelComponent, KnightFindingDetailComponent],
  template: `
    <section class="knight">
      <p class="eyebrow">AEGIS KNIGHT · Postura de Identidade e Exposição · Multicoletor</p>

      <header class="head">
        <div class="titles">
          <h1>
            AEGIS KNIGHT
            <span class="badge" [class]="badgeState()">{{ badgeLabel() }}</span>
          </h1>
          <p class="blurb">
            Avaliação determinística de exposição de identidade. Vereditos por regras; interpretação
            assistida por IA.
          </p>
        </div>
        <div class="actions">
          <a class="btn ghost" routerLink="/history">Histórico auditável</a>
          <!-- [AEGIS-MVP-PRODUCT-03] Publica EXATAMENTE a avaliação aberta. Sem o runId, o servidor
               congelaria a mais recente — e o relatório sairia de uma coleta diferente da que está na tela. -->
          @if (assessment(); as pub) {
            <button type="button" class="btn real" (click)="publishReport(pub.id)" [disabled]="publishing()">
              {{ publishing() ? 'Publicando…' : 'Publicar relatório desta avaliação' }}
            </button>
          }
          <button type="button" class="btn run" (click)="runDemo()" [disabled]="busy()">
            {{ running() ? 'Executando…' : 'Executar avaliação demo' }}
          </button>
          @if (entraConfigured()) {
            <button type="button" class="btn real" (click)="runSource('MicrosoftEntraId')" [disabled]="busy()">
              {{ running() ? 'Coletando…' : 'Coletar do Entra ID' }}
            </button>
          }
          @if (googleConfigured()) {
            <button type="button" class="btn real" (click)="runSource('GoogleWorkspace')" [disabled]="busy()">
              {{ running() ? 'Coletando…' : 'Coletar do Google Workspace' }}
            </button>
          }
        </div>
      </header>

      @if (loading()) {
        <div class="panel state"><span class="pulse">Carregando fontes e última avaliação…</span></div>
      } @else if (error() && !assessment()) {
        <div class="panel state err">
          <b>{{ error() }}</b>
          <span>O serviço não respondeu agora. Tente novamente em alguns instantes.</span>
          <button type="button" class="btn ghost" (click)="reload()">Tentar novamente</button>
        </div>
      } @else {
        @if (error()) {
          <div class="banner err">
            <span>{{ error() }}</span>
            <button type="button" class="btn ghost" (click)="clearError()">Fechar</button>
          </div>
        }

        @if (publishNotice(); as pmsg) {
          <div class="banner pinned">
            <span>{{ pmsg }}</span>
            <a class="btn ghost" routerLink="/history">Abrir histórico</a>
          </div>
        }

        @if (assessment(); as a) {
          @if (pinnedRun()) {
            <div class="banner pinned">
              <span>
                Avaliação <b>aberta por link</b> e fixada no endereço — pode não ser a mais recente. Os
                achados e os afetados abaixo pertencem a <b>esta</b> coleta.
              </span>
              <button type="button" class="btn ghost" (click)="openLatest()">Ver a mais recente</button>
            </div>
          }
          @if (findingNotice(); as fmsg) {
            <div class="banner err">
              <span>{{ fmsg }}</span>
              <button type="button" class="btn ghost" (click)="clearFindingNotice()">Fechar</button>
            </div>
          }
          <!-- [AEGIS-MVP-PRODUCT-03] Um link que identifica a AÇÃO e não pôde ser honrado vira estado
               explícito. Abrir o plano ativo no lugar dela seria mostrar outro trabalho a quem veio conferir
               um encerramento específico — e um aviso ao lado do substituto não desfaz a substituição. Por
               isso a saída é oferecida como ESCOLHA: enquanto ninguém a toma, o endereço continua mandando. -->
          @if (planUnavailable(); as plmsg) {
            <div class="banner err">
              <span>{{ plmsg }}</span>
              <button type="button" class="btn ghost" (click)="retryPinnedPlan()">Tentar de novo</button>
              <button type="button" class="btn ghost" (click)="dropPinnedPlan()">
                Ver a ação ativa deste achado
              </button>
            </div>
          }

          <!-- Linha de fonte + estado da coleta: distingue Demo de coleta real, sempre. -->
          <div class="source-line" [class.problem]="isProblemState(a.sourceState)">
            <span class="src-tag" [class.demo]="a.isDemo">{{ sourceTypeLabel(a.sourceType) }}</span>
            <span class="sep">·</span>
            <span>Fonte: <b>{{ a.source }}</b></span>
            <span class="sep">·</span>
            <span>Estado da coleta: <b>{{ sourceStateLabel(a.sourceState) }}</b></span>
            <span class="sep">·</span>
            <span>Última coleta: <b>{{ (a.completedAt || a.startedAt) | date: 'dd/MM/yyyy HH:mm' }}</b></span>
          </div>

          @if (a.isDemo) {
            <p class="demo-note">
              <b>Modo demonstração.</b> Dados 100% sintéticos (domínio <code>demo.example.com</code>). O Aegis
              <b>não</b> está conectado ao Entra ID, AD local ou Okta.
            </p>
          } @else if (isProblemState(a.sourceState)) {
            <p class="demo-note real-problem">
              <b>Coleta real com limitação.</b> Esta é uma coleta da fonte <b>{{ a.source }}</b> (não é Demo) —
              o estado <b>{{ sourceStateLabel(a.sourceState) }}</b> reduz a cobertura; itens sem dado ficam
              "Não avaliado", nunca "Conforme".
            </p>
          }

          @if (limitations().length) {
            <div class="panel limits">
              <h4>Limitações de coleta</h4>
              <ul>
                @for (c of limitations(); track c.capability) {
                  <li><b>{{ capabilityLabel(c.capability) }}</b> — {{ capabilityOutcomeLabel(c.outcome) }}@if (c.detail) { · {{ c.detail }} }</li>
                }
              </ul>
            </div>
          }

          <div class="grid">
            <div class="panel summary">
              @if (a.score !== null) {
                <app-score-gauge [percent]="a.score" caption="AEGIS KNIGHT" />
              } @else {
                <div class="no-score"><span class="dash">—</span><span class="l">sem avaliação</span></div>
              }
              <!-- [AEGIS-MVP-PRODUCT-01] O número sozinho não se lê: a ESCALA e a COBERTURA vêm junto dele, e
                   a frase abaixo resume, em linguagem clara, o que a avaliação de fato encontrou — sem
                   inventar lista de afetados e sem misturar este score com o AEGIS Score geral. -->
              <p class="score-note">
                Escala 0–100 · cobertura {{ a.coverage }}% = indicadores aplicáveis que puderam ser avaliados
                (os não aplicáveis ficam fora). Cobertura não é conformidade. Score do AEGIS KNIGHT, distinto do
                AEGIS Score geral.
              </p>
              <p class="score-lead">{{ summaryLine(a) }}</p>

              <div class="meta">
                <div class="mrow"><span class="k">Cobertura (avaliados / aplicáveis)</span><span class="v">{{ a.coverage }}%</span></div>
                <div class="mrow"><span class="k">Catálogo</span><span class="v mono">{{ a.catalogVersion }}</span></div>
                <div class="mrow"><span class="k">Fórmula</span><span class="v mono">{{ a.scoreFormulaVersion }}</span></div>
              </div>

              <div class="counts">
                <div class="count ok"><span class="n">{{ a.counts.passed }}</span><span class="l">Conformes</span></div>
                <div class="count fail" [class.hot]="a.counts.exposed > 0"><span class="n">{{ a.counts.exposed }}</span><span class="l">Expostos</span></div>
                <div class="count comp"><span class="n">{{ a.counts.mitigated }}</span><span class="l">Mitigados</span></div>
                <div class="count mute"><span class="n">{{ a.counts.notEvaluated }}</span><span class="l">Não avaliados</span></div>
                <div class="count mute" [class.hot]="a.counts.error > 0"><span class="n">{{ a.counts.error }}</span><span class="l">Erros</span></div>
              </div>
            </div>

            <div class="panel list">
              <div class="hd">
                <h3>Achados</h3>
                <span class="hint">Veredito por regras determinísticas · expostos no topo · abra um achado para ver quem sustenta o resultado</span>
              </div>
              <ul class="findings">
                @for (ind of sortedIndicators(); track ind.indicatorId) {
                  <li>
                    <button
                      type="button"
                      class="finding"
                      [class.open]="selected() === ind.indicatorId"
                      (click)="selectFinding(ind.indicatorId)"
                      [attr.aria-expanded]="selected() === ind.indicatorId">
                      <span class="f-title">
                        <span class="tt">{{ findingTitle(ind) }}</span>
                        <span class="sit">{{ findingSituation(ind) }}</span>
                        <span class="code">{{ ind.indicatorId }}</span>
                      </span>
                      <span class="f-tags">
                        <span class="sev" [class]="ind.severity">{{ severityLabel(ind.severity) }}</span>
                        <span class="st" [class]="ind.status">{{ statusLabel(ind.status) }}</span>
                      </span>
                      <span class="f-affected">
                        @if (ind.status === 'Exposed' || ind.status === 'Mitigated') {
                          <b>{{ ind.affectedObjectCount }}</b><span class="l">afetado(s)</span>
                        } @else {
                          <span class="l">—</span>
                        }
                      </span>
                    </button>
                  </li>
                }
              </ul>
            </div>
          </div>

          <!-- Detalhe de UM achado (componente dedicado): resumo · afetados · evidência. -->
          @if (selectedIndicator(); as ind) {
            <app-knight-finding-detail
              [assessment]="a"
              [indicator]="ind"
              [activePlan]="activePlan()"
              [plan]="focusedPlan()"
              [planState]="pinned()"
              [focusPlan]="pinned().kind !== 'livre'"
              (closed)="closeFinding()"
              (planChanged)="onPlanChanged()"
              (planRetry)="retryPinnedPlan()"
              (planRelease)="dropPinnedPlan()" />
          }

          <!-- [AEGIS-MVP-PRODUCT-02] O painel abaixo lê o snapshot ATUAL da Evidence Fabric e diz isso por
               conta própria — ele NÃO pertence à avaliação aberta acima. -->
          <app-identity-risk-panel [projection]="riskProjection()" />

          <div class="panel ai">
            <div class="hd">
              <h3>Interpretação e priorização assistidas por IA</h3>
              <span class="src" [class.fallback]="!a.advisoryFromAi">
                {{ a.advisoryFromAi ? 'Gerado por IA' : 'Fallback determinístico (IA indisponível)' }}
              </span>
            </div>
            @if (a.advisory; as ai) {
              <p class="summary-txt">{{ ai.executiveSummary }}</p>
              @if (ai.priorityRisks.length) {
                <div class="ai-block"><h4>Riscos prioritários</h4><ol>
                  @for (r of ai.priorityRisks; track $index) {
                    <li><b>{{ r.title }}</b> — {{ r.rationale }} <span class="ids">[{{ r.indicatorIds.join(', ') }}]</span></li>
                  }
                </ol></div>
              }
              @if (ai.recommendedActions.length) {
                <div class="ai-block"><h4>Ações recomendadas</h4><ol>
                  @for (act of ai.recommendedActions; track act.order) {
                    <li>{{ act.action }} <span class="ids">[{{ act.indicatorIds.join(', ') }}]</span></li>
                  }
                </ol></div>
              }
              @if (ai.correlations.length) {
                <div class="ai-block"><h4>Correlações</h4><ul>
                  @for (c of ai.correlations; track $index) {
                    <li>{{ c.description }} <span class="ids">[{{ c.indicatorIds.join(', ') }}]</span></li>
                  }
                </ul></div>
              }
              @if (ai.collectionGaps.length) {
                <div class="ai-block"><h4>Lacunas de coleta</h4><ul>
                  @for (g of ai.collectionGaps; track $index) { <li>{{ g }}</li> }
                </ul></div>
              }
            } @else {
              <p class="summary-txt muted">Sem interpretação disponível para esta avaliação.</p>
            }
          </div>
        } @else {
          @if (linkNotice(); as msg) {
            <!-- [AEGIS-MVP-PRODUCT-02] O endereço indicava uma avaliação específica que NÃO pôde ser aberta.
                 Cair para a última seria o pior desfecho: o link diria uma coisa e a tela mostraria outra. -->
            <div class="panel state err">
              <b>{{ msg }}</b>
              <span>
                O AEGIS não substitui a avaliação pedida pela mais recente — os resultados são de coletas
                diferentes e não se substituem.
              </span>
              <button type="button" class="btn ghost" (click)="openLatest()">
                Abrir a avaliação mais recente
              </button>
            </div>
          } @else {
          <div class="panel state empty">
            <b>Nenhuma avaliação executada ainda.</b>
            <span>
              Este módulo é MULTICOLETOR. Em <code>example.com</code> use a DEMONSTRAÇÃO; conecte o
              Microsoft Entra ID (somente leitura) para executar uma coleta real.
            </span>
            @if (entraConfigured()) {
              <span>Fonte real configurada: <b>Microsoft Entra ID</b>.</span>
            }
            @if (googleConfigured()) {
              <span>Fonte real configurada: <b>Google Workspace</b>.</span>
            }
            @if (!entraConfigured() && !googleConfigured()) {
              <span>Nenhuma fonte real configurada — apenas a demonstração está disponível.</span>
            }
            <div class="empty-actions">
              <button type="button" class="btn run" (click)="runDemo()" [disabled]="busy()">
                {{ running() ? 'Executando…' : 'Executar avaliação demo' }}
              </button>
              @if (entraConfigured()) {
                <button type="button" class="btn real" (click)="runSource('MicrosoftEntraId')" [disabled]="busy()">
                  {{ running() ? 'Coletando…' : 'Coletar do Entra ID' }}
                </button>
              }
              @if (googleConfigured()) {
                <button type="button" class="btn real" (click)="runSource('GoogleWorkspace')" [disabled]="busy()">
                  {{ running() ? 'Coletando…' : 'Coletar do Google Workspace' }}
                </button>
              }
            </div>
          </div>
          }
        }
      }
    </section>
  `,
  styles: [
    `
      :host { display: block; padding: 28px 32px 60px; }
      .eyebrow { font-family: var(--mono); font-size: 11px; letter-spacing: 0.14em; color: var(--muted); text-transform: uppercase; margin: 0 0 8px; }
      .head { display: flex; align-items: flex-end; justify-content: space-between; gap: 18px; flex-wrap: wrap; margin: 0 0 20px; }
      .head h1 { font-family: var(--sans); font-size: 24px; color: var(--text); margin: 0 0 4px; display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
      .head .blurb { color: var(--muted); font-size: 13px; margin: 0; font-family: var(--mono); letter-spacing: 0.02em; }
      .badge { font-family: var(--mono); font-size: 10px; font-weight: 700; letter-spacing: 0.1em; padding: 4px 10px; border-radius: 999px; border: 1px solid var(--line); }
      .badge.Demo { color: var(--amber); border-color: rgba(255, 176, 32, 0.5); background: rgba(255, 176, 32, 0.08); }
      .badge.Connected { color: var(--cyan); border-color: rgba(38, 224, 255, 0.5); background: rgba(38, 224, 255, 0.08); }
      .badge.NotConfigured { color: var(--muted); background: rgba(122, 145, 190, 0.08); }
      .actions { display: flex; gap: 10px; flex-wrap: wrap; }
      .btn { cursor: pointer; font-family: var(--mono); font-size: 12px; font-weight: 600; border-radius: 11px; padding: 9px 16px; transition: 0.15s; border: 1px solid transparent; }
      .btn:disabled { opacity: 0.5; cursor: not-allowed; }
      .btn.run { color: #05070f; background: var(--neon-h); box-shadow: 0 0 14px -3px rgba(38, 224, 255, 0.6); }
      .btn.real { color: var(--cyan); background: rgba(38, 224, 255, 0.08); border-color: rgba(38, 224, 255, 0.45); }
      .btn.ghost { color: var(--text); background: rgba(122, 145, 190, 0.08); border-color: var(--line); }
      a.btn { text-decoration: none; display: inline-flex; align-items: center; }
      .panel { border: 1px solid var(--line); border-radius: 14px; background: rgba(122, 145, 190, 0.03); padding: 18px; }

      .source-line { font-family: var(--mono); font-size: 11.5px; color: var(--muted); display: flex; gap: 8px; align-items: center; flex-wrap: wrap; margin-bottom: 12px; }
      .source-line b { color: var(--text); }
      .banner.pinned b { color: var(--cyan); }
      .source-line.problem b { color: var(--amber); }
      .src-tag { font-weight: 700; letter-spacing: 0.06em; color: var(--cyan); border: 1px solid rgba(38, 224, 255, 0.4); border-radius: 6px; padding: 2px 8px; }
      .src-tag.demo { color: var(--amber); border-color: rgba(255, 176, 32, 0.4); }
      .source-line .sep { opacity: 0.4; }

      .demo-note { margin: 0 0 16px; font-family: var(--mono); font-size: 12px; line-height: 1.5; color: var(--muted); border-left: 2px solid var(--amber); padding: 8px 12px; background: rgba(255, 176, 32, 0.05); border-radius: 0 8px 8px 0; }
      .demo-note b { color: var(--amber); }
      .demo-note.real-problem { border-left-color: var(--cyan); background: rgba(38, 224, 255, 0.05); }
      .demo-note.real-problem b { color: var(--cyan); }
      code { color: var(--text); background: rgba(255, 255, 255, 0.06); padding: 1px 5px; border-radius: 4px; }

      .limits { margin-bottom: 16px; }
      .limits h4 { margin: 0 0 8px; font-family: var(--mono); font-size: 11px; text-transform: uppercase; letter-spacing: 0.08em; color: var(--amber); }
      .limits ul { margin: 0; padding-left: 18px; }
      .limits li { font-family: var(--mono); font-size: 11.5px; color: var(--muted); line-height: 1.5; }
      .limits li b { color: var(--text); }

      .banner { display: flex; align-items: center; justify-content: space-between; gap: 12px; margin-bottom: 16px; padding: 12px 16px; border-radius: 10px; }
      .banner.err { border: 1px solid rgba(255, 45, 111, 0.4); background: rgba(255, 45, 111, 0.06); color: #ffe3ee; font-family: var(--mono); font-size: 12px; }

      .grid { display: grid; grid-template-columns: 300px 1fr; gap: 18px; align-items: start; }
      .summary { display: flex; flex-direction: column; gap: 14px; }
      .no-score { display: flex; flex-direction: column; align-items: center; gap: 4px; padding: 24px 0; }
      .no-score .dash { font-family: var(--display); font-size: 44px; color: var(--muted); }
      .no-score .l { font-family: var(--mono); font-size: 11px; letter-spacing: 0.1em; color: var(--muted); text-transform: uppercase; }
      .score-note { margin: 0; font-family: var(--mono); font-size: 10.5px; color: var(--muted); text-align: center; line-height: 1.5; }
      /* Leitura em uma frase — o que a avaliação encontrou, antes das contagens. */
      .score-lead { margin: 10px 0 0; font-family: var(--sans); font-size: 12.5px; line-height: 1.55; color: var(--text); text-align: center; }
      .meta { display: flex; flex-direction: column; gap: 6px; border-top: 1px solid var(--line); padding-top: 12px; }
      .mrow { display: flex; justify-content: space-between; gap: 10px; font-family: var(--mono); font-size: 11.5px; }
      .mrow .k { color: var(--muted); } .mrow .v { color: var(--text); } .mrow .v.mono { color: var(--cyan); }
      .counts { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
      .count { display: flex; flex-direction: column; gap: 2px; padding: 8px 10px; border: 1px solid var(--line); border-radius: 9px; background: rgba(122, 145, 190, 0.03); }
      .count .n { font-family: var(--display); font-weight: 700; font-size: 19px; color: var(--text); }
      .count .l { font-family: var(--mono); font-size: 9px; text-transform: uppercase; letter-spacing: 0.1em; color: var(--muted); }
      .count.ok .n { color: var(--cyan); } .count.comp .n { color: var(--amber); }
      .count.fail.hot { border-color: rgba(255, 45, 111, 0.45); background: rgba(255, 45, 111, 0.06); }
      .count.fail.hot .n { color: var(--red); } .count.mute.hot .n { color: var(--red); }

      .list .hd, .ai .hd { display: flex; align-items: baseline; justify-content: space-between; gap: 12px; margin-bottom: 14px; flex-wrap: wrap; }
      .list h3, .ai h3 { margin: 0; font-size: 14px; font-weight: 600; color: var(--text); }
      .list .hint { font-family: var(--mono); font-size: 11px; color: var(--muted); }
      .sev { font-family: var(--mono); font-size: 10px; font-weight: 700; padding: 2px 7px; border-radius: 6px; white-space: nowrap; }
      .sev.Critical { color: var(--red); background: rgba(255, 45, 111, 0.12); }
      .sev.High { color: #ff9a3d; background: rgba(255, 154, 61, 0.12); }
      .sev.Medium { color: var(--amber); background: rgba(255, 176, 32, 0.12); }
      .sev.Low { color: var(--cyan); background: rgba(38, 224, 255, 0.1); }
      .sev.Informational { color: var(--muted); background: rgba(122, 145, 190, 0.1); }
      .st { font-family: var(--mono); font-size: 10.5px; font-weight: 700; padding: 2px 8px; border-radius: 6px; white-space: nowrap; }
      .st.Passed { color: var(--cyan); background: rgba(38, 224, 255, 0.1); }
      .st.Exposed { color: var(--red); background: rgba(255, 45, 111, 0.12); }
      .st.Mitigated { color: var(--amber); background: rgba(255, 176, 32, 0.12); }
      .st.NotEvaluated, .st.NotApplicable { color: var(--muted); background: rgba(122, 145, 190, 0.1); }
      .st.Error { color: #ff9a3d; background: rgba(255, 154, 61, 0.12); }

      .ai { margin-top: 18px; }
      .ai .src { font-family: var(--mono); font-size: 10px; font-weight: 700; letter-spacing: 0.06em; padding: 3px 9px; border-radius: 999px; color: var(--cyan); border: 1px solid rgba(38, 224, 255, 0.4); }
      .ai .src.fallback { color: var(--amber); border-color: rgba(255, 176, 32, 0.4); }
      .summary-txt { font-size: 13px; line-height: 1.6; color: var(--text); margin: 0 0 14px; }
      .summary-txt.muted { color: var(--muted); }
      .ai-block { margin-bottom: 14px; }
      .ai-block h4 { margin: 0 0 6px; font-family: var(--mono); font-size: 11px; text-transform: uppercase; letter-spacing: 0.08em; color: var(--cyan); }
      .ai-block ol, .ai-block ul { margin: 0; padding-left: 20px; }
      .ai-block li { font-size: 12.5px; line-height: 1.55; color: var(--text); margin-bottom: 5px; }
      .ai-block .ids { font-family: var(--mono); font-size: 10.5px; color: var(--muted); }

      .state { display: flex; flex-direction: column; gap: 10px; align-items: flex-start; }
      .state b { color: var(--text); font-size: 14px; }
      .state span, .pulse { font-family: var(--mono); font-size: 12px; color: var(--muted); line-height: 1.5; }
      .empty-actions { display: flex; gap: 10px; flex-wrap: wrap; margin-top: 4px; }
      .pulse { letter-spacing: 0.08em; animation: pulse 1.4s ease-in-out infinite; }
      .state.err { border-color: rgba(255, 45, 111, 0.4); } .state.err b { color: #ffe3ee; }
      @keyframes pulse { 0%, 100% { opacity: 0.35; } 50% { opacity: 0.75; } }
      /* [AEGIS-MVP-PRODUCT-03] Em 1024 px a coluna de 300 px espremia a lista de achados: título, situação,
         severidade, veredito e contagem disputavam o que sobrava. Ausência de rolagem horizontal não bastava —
         a leitura ficava comprimida. A partir de 1100 px o resumo/score e a lista passam a EMPILHAR, e cada um
         usa a largura inteira. */
      @media (max-width: 1100px) {
        .grid { grid-template-columns: 1fr; }
        .summary { max-width: 100%; }
      }
      /* [AEGIS-MVP-PRODUCT-02] Lista compacta de achados (o detalhe tem componente e estilo próprios). */
      .findings { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 6px; }
      .finding { width: 100%; display: grid; grid-template-columns: 1fr auto auto; gap: 14px; align-items: center; text-align: left; cursor: pointer; background: rgba(122, 145, 190, 0.04); border: 1px solid var(--line); border-radius: 11px; padding: 11px 14px; color: var(--text); font-family: var(--sans); }
      .finding:hover { border-color: rgba(38, 224, 255, 0.35); }
      .finding.open { border-color: var(--cyan); background: rgba(38, 224, 255, 0.06); }
      .finding .f-title { display: flex; flex-direction: column; gap: 3px; min-width: 0; }
      .finding .tt { font-size: 13.5px; }
      .finding .sit { font-size: 11.5px; color: var(--muted); line-height: 1.45; }
      .banner.pinned { border: 1px solid rgba(38, 224, 255, 0.4); color: var(--text); }
      .finding .f-tags { display: flex; gap: 8px; align-items: center; }
      .finding .f-affected { display: flex; flex-direction: column; align-items: flex-end; min-width: 76px; }
      .finding .f-affected b { font-family: var(--mono); font-size: 16px; }
      .finding .code, .finding .f-affected .l { font-family: var(--mono); font-size: 10.5px; color: var(--muted); }
      @media (max-width: 900px) {
        .finding { grid-template-columns: 1fr; gap: 8px; }
        .finding .f-affected { align-items: flex-start; }
      }

      @media (prefers-reduced-motion: reduce) { .pulse { animation: none; } .btn { transition: none; } }
    `,
  ],
})
export class AegisKnightComponent implements OnInit {
  private readonly knight = inject(KnightService);
  private readonly remediation = inject(RemediationService);
  private readonly history = inject(PostureHistoryService);
  private readonly identityRisk = inject(IdentityRiskService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly assessment = signal<KnightAssessment | null>(null);
  readonly sources = signal<KnightSources | null>(null);
  readonly loading = signal(true); // 1ª carga (fontes + último)
  readonly running = signal(false); // execução (demo ou real)
  readonly error = signal<string | null>(null);

  protected readonly apiBase = environment.apiBase;

  // Helpers de apresentação (funções puras do modelo) expostos ao template.
  protected readonly categoryLabel = categoryLabel;
  protected readonly severityLabel = severityLabel;
  protected readonly statusLabel = statusLabel;
  protected readonly sourceTypeLabel = sourceTypeLabel;
  protected readonly sourceStateLabel = sourceStateLabel;
  protected readonly capabilityOutcomeLabel = capabilityOutcomeLabel;
  protected readonly capabilityLabel = capabilityLabel;
  protected readonly isProblemState = isProblemState;
  protected readonly findingTitle = findingTitle;
  protected readonly findingSituation = findingSituation;

  readonly badgeState = computed(() => connectionStateOf(this.assessment()));
  readonly badgeLabel = computed(() => connectionBadgeLabel(this.badgeState()));
  readonly sortedIndicators = computed(() => sortIndicatorsByRisk(this.assessment()?.indicators ?? []));
  readonly limitations = computed(() => problemCapabilities(this.assessment()?.capabilities ?? []));

  /**
   * [AEGIS-MVP-PRODUCT-01] Uma frase que resume o que a avaliação ENCONTROU, derivada só das contagens que o
   * backend já apurou. Não inventa lista de afetados, não estima nada e nunca transforma "não avaliado" em
   * "conforme" — quando não há indicador avaliado, a frase diz exatamente isso.
   */
  summaryLine(a: KnightAssessment): string {
    const c = a.counts;
    const avaliados = c.passed + c.exposed + c.mitigated;
    if (avaliados === 0) {
      return 'Nenhum indicador de identidade pôde ser avaliado nesta coleta.';
    }
    const partes = [`${c.exposed} exposto(s)`, `${c.passed} conforme(s)`];
    if (c.mitigated > 0) partes.push(`${c.mitigated} mitigado(s)`);
    const pendentes = c.notEvaluated > 0 ? ` ${c.notEvaluated} seguem não avaliados.` : '';
    return `${avaliados} indicador(es) avaliados: ${partes.join(', ')}.${pendentes}`;
  }
  readonly busy = computed(() => this.running() || this.loading());
  readonly entraConfigured = computed(
    () => this.sources()?.realSources.some((s) => s.source === 'MicrosoftEntraId' && s.configured) ?? false,
  );
  readonly googleConfigured = computed(
    () => this.sources()?.realSources.some((s) => s.source === 'GoogleWorkspace' && s.configured) ?? false,
  );

  // ---- [AEGIS-MVP-MICROSOFT-COVERAGE-03] Risco de identidade -------------------------------------
  // Lê o snapshot JÁ persistido pela Evidence Fabric: a MESMA fotografia que o KNIGHT avalia, sem uma
  // segunda consulta ao Microsoft Graph.
  readonly riskProjection = signal<IdentityEvidenceProjection | null>(null);

  // ---- [AEGIS-MVP-PRODUCT-03] Ações de remediação -----------------------------------------------
  // UMA leitura da lista de ações ATIVAS serve à página inteira: o detalhe do achado aberto pergunta a ela
  // se já existe ação, em vez de cada achado disparar a própria consulta. É também a MESMA autoridade que a
  // Central de Prioridades usa, de modo que os dois lugares não podem discordar sobre "existe ação ativa?".

  readonly activePlans = signal<ActionPlan[]>([]);
  readonly publishing = signal(false);
  readonly publishNotice = signal<string | null>(null);

  /**
   * A PROCEDÊNCIA da avaliação exibida. Ela entra na leitura das ações porque o indicador sozinho não
   * identifica o problema: "AK-ENTRA-001 na demonstração" e "AK-ENTRA-001 na coleta real do diretório" são
   * dois problemas distintos, e misturá-los faria uma ação de treinamento aparecer como trabalho real em
   * curso — além de bloquear a criação da ação real.
   */
  readonly originSource = computed<KnightOriginSource | null>(
    () => (this.assessment()?.sourceType as KnightOriginSource | undefined) ?? null,
  );
  readonly originMode = computed<KnightOriginMode | null>(() => {
    const a = this.assessment();
    return a ? (a.isDemo ? 'Demo' : 'Live') : null;
  });

  /** Ação ATIVA do achado aberto, NESTA procedência, se houver. */
  readonly activePlan = computed<ActionPlan | null>(() => {
    const id = this.selected();
    return id ? activePlanFor(this.activePlans(), id, this.originSource(), this.originMode()) : null;
  });

  /**
   * O estado da ação identificada pelo endereço (`?plan=`). É UM estado, e não um par "id + plano lido",
   * porque a diferença entre "ainda lendo", "não pôde ser aberta" e "não há ação indicada" muda o que a tela
   * pode mostrar — e um par de signals não consegue distingui-las: nos dois primeiros casos o plano é nulo,
   * e nulo, aqui, também significa "crie uma ação".
   */
  readonly pinned = signal<PinnedPlanState>({ kind: 'livre' });

  /** O identificador que o endereço carrega — vazio quando a navegação não nomeia ação alguma. */
  readonly pinnedPlanId = computed<string | null>(() => {
    const e = this.pinned();
    return e.kind === 'livre' ? null : e.id;
  });

  /** Motivo, em palavras, de a ação indicada não poder ser aberta — nulo quando não há impedimento. */
  readonly planUnavailable = computed<string | null>(() => {
    const e = this.pinned();
    return e.kind === 'indisponivel' ? e.reason : null;
  });

  /**
   * A ação que o painel deve mostrar.
   *
   * Com o endereço nomeando uma ação, é AQUELA — e somente ela. Enquanto está sendo lida, ou quando não pôde
   * ser aberta, NADA ocupa o painel: cair para a ação ativa mostraria outro trabalho, com a mesma aparência
   * de resposta, a quem veio conferir um encerramento específico. Uma ação ENCERRADA legitimamente indicada
   * continua sendo a certa mesmo havendo outro ciclo ativo.
   */
  readonly focusedPlan = computed<ActionPlan | null>(() => planForPanel(this.pinned(), this.activePlan()));

  /**
   * Relê as ações ativas DESTA procedência. Falha aqui NÃO bloqueia a tela: o detalhe apenas deixa de
   * oferecer o atalho. Sem avaliação carregada não há procedência — e sem procedência a leitura seria a
   * mistura que esta correção existe para impedir.
   */
  private reloadPlans(): void {
    const fonte = this.originSource();
    const modo = this.originMode();
    if (!fonte || !modo) return;
    this.remediation.list({ activeOnly: true, sourceType: fonte, mode: modo }).subscribe({
      next: (plans) => this.activePlans.set(plans),
      error: () => {
        /* seção secundária: preserva a lista anterior em vez de fingir que não há ação alguma */
      },
    });
  }

  /**
   * O CONTEXTO a que uma leitura de ação pertence: a ação pedida, o achado aberto, a avaliação exibida e a
   * procedência dela. A chave é COMPLETA de propósito — comparar só o identificador da ação deixaria uma
   * resposta atrasada preencher o painel depois de a tela já ter trocado de avaliação (demonstração para
   * coleta real, por exemplo), que é exatamente o contexto em que o mesmo indicador significa outro problema.
   */
  private planContext(id: string): string {
    return [
      id,
      this.selected() ?? '',
      this.assessment()?.id ?? '',
      this.originSource() ?? '',
      this.originMode() ?? '',
    ].join('|');
  }

  /**
   * Relê a ação identificada pelo endereço. Enquanto a leitura corre, o estado é `carregando` — e o painel
   * fica vazio, não preenchido pela ação ativa. Uma ação inexistente, inacessível, de OUTRO achado ou de
   * OUTRA procedência produz `indisponivel`: estado explícito, com saída oferecida à pessoa. Nunca a
   * substituição silenciosa.
   */
  private reloadPinnedPlan(): void {
    const id = this.pinnedPlanId();
    if (!id) return;

    const contexto = this.planContext(id);
    this.pinned.set({ kind: 'carregando', id });
    this.remediation.get(id).subscribe({
      next: (p) => {
        // O endereço, o achado ou a procedência mudaram enquanto a leitura estava em voo: esta resposta
        // pertence a outra tela e não escreve nesta.
        if (contexto !== this.planContext(id)) return;
        const recusa = pinnedPlanRejection(p, this.selected(), this.originSource(), this.originMode());
        this.pinned.set(
          recusa ? { kind: 'indisponivel', id, reason: recusa } : { kind: 'carregada', id, plan: p },
        );
      },
      error: (e: Error) => {
        if (contexto !== this.planContext(id)) return;
        this.pinned.set({
          kind: 'indisponivel',
          id,
          reason: `A ação indicada no endereço não pôde ser aberta. ${e.message}`,
        });
      },
    });
  }

  /** Tenta de novo a leitura da ação indicada — decisão da pessoa, jamais automática. */
  retryPinnedPlan(): void {
    if (this.pinnedPlanId()) this.reloadPinnedPlan();
  }

  /**
   * ABANDONA explicitamente a ação indicada pelo endereço e volta à navegação comum do achado, na qual a
   * ação ATIVA pode ocupar o painel. É a única porta pela qual a substituição acontece — e ela é aberta por
   * quem está olhando, que assim sabe que passou a ver outra coisa.
   */
  dropPinnedPlan(): void {
    this.pinned.set({ kind: 'livre' });
    this.syncQueryParam(this.selected());
  }

  /**
   * Uma escrita já enviada não é desfeita porque o painel fechou: o servidor a recebeu. O que a tela faz é
   * RELER — a lista de ações e, quando há uma ação em foco, ela própria.
   */
  onPlanChanged(): void {
    this.reloadPlans();
    if (this.pinnedPlanId()) this.reloadPinnedPlan();
  }

  /**
   * Publica o relatório da avaliação ABERTA — nunca "a mais recente". O identificador viaja explicitamente
   * para o servidor, que recusa (409) se a avaliação não existir, em vez de silenciosamente congelar outra.
   */
  publishReport(runId: string): void {
    this.publishing.set(true);
    this.publishNotice.set(null);
    this.error.set(null);
    this.history.publish({ type: 'Knight', runId }).subscribe({
      next: (d) => {
        this.publishing.set(false);
        this.publishNotice.set(
          `Relatório publicado a partir desta avaliação (${d.summary.id}). O conteúdo foi congelado: ` +
            'reexportá-lo depois traz exatamente o que foi publicado agora.',
        );
      },
      error: (e: Error) => {
        this.publishing.set(false);
        this.error.set(e.message);
      },
    });
  }



  // ---- [AEGIS-MVP-PRODUCT-02] Seleção do achado -------------------------------------------------
  // O estado do DETALHE (aba, página, busca, erro) vive no componente dedicado. A página só decide QUAL
  // achado está aberto — e mantém isso no endereço, que é como a Central de Prioridades aponta para cá.

  readonly selected = signal<string | null>(null);

  /**
   * Avaliação FIXADA pelo endereço (`?run=`). Quando presente, a tela carrega exatamente essa avaliação — a
   * Central de Prioridades aponta para um resultado específico, e abrir outro com o mesmo link seria
   * apresentar a coleta de hoje como prova do resultado de ontem.
   */
  readonly pinnedRun = signal<string | null>(null);
  /** Estado explícito de um link que não pôde ser honrado (avaliação inexistente ou inacessível). */
  readonly linkNotice = signal<string | null>(null);
  /** Estado explícito de um achado pedido pelo link que não existe na avaliação carregada. */
  readonly findingNotice = signal<string | null>(null);

  readonly selectedIndicator = computed<KnightIndicator | null>(() => {
    const id = this.selected();
    if (!id) return null;
    return this.assessment()?.indicators.find((i) => i.indicatorId === id) ?? null;
  });

  /** Abre um achado (ou fecha, se já estava aberto). */
  selectFinding(indicatorId: string): void {
    if (this.selected() === indicatorId) {
      this.closeFinding();
      return;
    }
    // Escolher outro achado ABANDONA a ação fixada pelo endereço: ela pertencia ao achado anterior, e
    // arrastá-la para cá exibiria a ação de um problema ao lado do veredito de outro.
    this.pinned.set({ kind: 'livre' });
    this.selected.set(indicatorId);
    this.syncQueryParam(indicatorId);
    // Relê ao abrir: uma escrita enviada de um painel que foi fechado já está gravada, e a fila precisa
    // mostrar o estado do servidor, não o que estava em memória antes.
    this.reloadPlans();
  }

  closeFinding(): void {
    this.selected.set(null);
    this.pinned.set({ kind: 'livre' });
    this.syncQueryParam(null);
  }

  /**
   * Mantém achado E avaliação no endereço — é assim que a Central de Prioridades aponta para cá. O `run`
   * viaja junto para que recarregar, compartilhar ou voltar traga exatamente o mesmo resultado.
   */
  private syncQueryParam(indicatorId: string | null): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { finding: indicatorId, run: this.pinnedRun(), plan: this.pinnedPlanId() },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  /** Solta a fixação e volta à navegação sem avaliação indicada (aí sim, a mais recente). */
  openLatest(): void {
    this.pinnedRun.set(null);
    this.linkNotice.set(null);
    this.findingNotice.set(null);
    this.selected.set(null);
    this.pinned.set({ kind: 'livre' });
    void this.router
      .navigate([], {
        relativeTo: this.route,
        queryParams: { finding: null, run: null, plan: null },
        replaceUrl: true,
      })
      .then(() => this.reload());
  }

  clearFindingNotice(): void {
    this.findingNotice.set(null);
  }

  ngOnInit(): void {
    // Somente LEITURA ao abrir — fontes + último assessment. NÃO executa análise automaticamente.
    this.reload();
  }

  /**
   * Recarrega fontes + a avaliação a exibir (read-only). Com `?run=` no endereço, carrega EXATAMENTE aquela
   * avaliação; sem ele, a mais recente. Uma avaliação indicada e indisponível produz estado explícito — cair
   * para a mais recente deixaria a URL apontando para uma coleta e a tela mostrando outra.
   */
  reload(): void {
    const requested = this.route.snapshot.queryParamMap.get('run');
    this.pinnedRun.set(requested);
    const pedida = this.route.snapshot.queryParamMap.get('plan');
    // O endereço nomeia uma ação: o painel já nasce COMPROMETIDO com ela. Começar em `livre` deixaria a ação
    // ativa aparecer no intervalo entre abrir a tela e a leitura responder — uma substituição de milissegundos
    // é uma substituição.
    this.pinned.set(pedida ? { kind: 'carregando', id: pedida } : { kind: 'livre' });
    this.linkNotice.set(null);
    this.findingNotice.set(null);

    this.loading.set(true);
    this.error.set(null);
    this.knight.getSources().subscribe({
      next: (s) => this.sources.set(s),
      error: () => this.sources.set(null), // fontes é secundário; não bloqueia a tela
    });

    const wanted$ = requested ? this.knight.getById(requested) : this.knight.getLatest();
    wanted$.subscribe({
      next: (a) => {
        this.assessment.set(a);
        this.loading.set(false);
        this.applyDeepLink(a);
        // Só agora a PROCEDÊNCIA é conhecida — ler a fila antes traria ações de outra fonte/modo.
        this.reloadPlans();
        this.reloadPinnedPlan();
      },
      error: (e: Error) => {
        this.loading.set(false);
        if (requested) {
          this.assessment.set(null);
          this.selected.set(null);
          this.linkNotice.set('A avaliação indicada no endereço não está disponível para este tenant.');
          return;
        }
        this.error.set(e.message);
      },
    });
    this.reloadRisk();
  }

  /**
   * Recarrega a fotografia de risco de identidade (somente leitura do snapshot compartilhado). Uma falha
   * aqui NÃO bloqueia a tela nem zera a seção: mantemos a última projeção carregada, se houver.
   */
  private reloadRisk(): void {
    this.identityRisk.get().subscribe({
      next: (p) => this.riskProjection.set(p),
      error: () => {
        /* seção secundária: preserva a projeção anterior em vez de exibir zeros */
      },
    });
  }

  /**
   * A Central de Prioridades aponta para um achado específico (?finding=AK-ENTRA-002). O achado só é aberto
   * se EXISTIR na avaliação carregada — um identificador desconhecido é ignorado em silêncio, jamais vira
   * uma tela de detalhe vazia.
   */
  private applyDeepLink(a: KnightAssessment | null): void {
    const wanted = this.route.snapshot.queryParamMap.get('finding');
    if (!wanted) return;
    if (!a?.indicators.some((i) => i.indicatorId === wanted)) {
      // Silenciar aqui seria abrir a tela como se o link não existisse. O achado pedido pode não ter sido
      // avaliado nesta coleta — a tela diz isso, em vez de abrir outro achado ou nenhum.
      this.selected.set(null);
      this.pinned.set({ kind: 'livre' });
      this.findingNotice.set(
        `O achado ${wanted} não faz parte desta avaliação. Ele pode não ter sido avaliado nesta coleta.`,
      );
      return;
    }
    this.selected.set(wanted);
  }

  clearError(): void {
    this.error.set(null);
  }

  /** Executa o assessment de demonstração. */
  runDemo(): void {
    this.execute(() => this.knight.runDemo());
  }

  /** Executa a coleta real da fonte indicada (ex.: Entra ID). Falha real NÃO cai para Demo. */
  runSource(source: KnightSourceType): void {
    this.execute(() => this.knight.runSource(source));
  }

  private execute(run: () => ReturnType<KnightService['runDemo']>): void {
    this.running.set(true);
    this.error.set(null);
    run().subscribe({
      next: (a) => {
        this.assessment.set(a);
        this.running.set(false);
        this.linkNotice.set(null);
        this.findingNotice.set(null);
        // A tela passou a mostrar OUTRA avaliação: o endereço muda junto, explicitamente. Deixar `?run=`
        // apontando para a coleta anterior enquanto a tela mostra a nova é exatamente a divergência que
        // esta correção existe para impedir. O achado aberto só sobrevive se existir na avaliação nova.
        this.pinnedRun.set(a.id);
        this.pinned.set({ kind: 'livre' });
        const aberto = this.selected();
        const mantem = aberto && a.indicators.some((i) => i.indicatorId === aberto) ? aberto : null;
        this.selected.set(mantem);
        this.syncQueryParam(mantem);
        // A procedência pode ter mudado (demo -> coleta real): a fila de ações é relida sob a nova.
        this.reloadPlans();
        // A coleta acabou de reescrever o snapshot compartilhado — relê a MESMA fotografia (sem novo Graph).
        this.reloadRisk();
      },
      error: (e: Error) => {
        // Mantém o assessment anterior visível; NUNCA substitui por Demo numa falha real.
        this.error.set(e.message);
        this.running.set(false);
      },
    });
  }
}

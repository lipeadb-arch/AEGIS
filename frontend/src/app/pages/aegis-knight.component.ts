import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { map } from 'rxjs';
import { environment } from '../../environments/environment';
import {
  EMPTY_FILTERS,
  KnightAffectedSummary,
  KnightAssessment,
  KnightControlFilters,
  KnightIndicator,
  isFinding,
  KnightLatest,
  KnightSourceType,
  KnightSources,
  KnightUnfinishedRun,
  connectionBadgeLabel,
  connectionStateOf,
  isProblemState,
  sourceStateLabel,
  sourceTypeLabel,
} from '../models/knight.models';
import { IdentityRiskPanelComponent } from '../components/identity/identity-risk-panel.component';
import { KnightFindingDetailComponent } from '../components/knight/finding-detail.component';
import { KnightOverviewComponent } from '../components/knight/knight-overview.component';
import { KnightControlsComponent } from '../components/knight/knight-controls.component';
import { IdentityEvidenceProjection } from '../models/identity-risk.models';
import { IdentityRiskService } from '../services/identity-risk.service';
import { KnightRunTimeoutError, KnightService } from '../services/knight.service';
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
import { PostureExportFormat } from '../models/posture-history.models';

/**
 * AegisKnightComponent — SMART. Assessment de postura do AEGIS KNIGHT.
 *
 * Ao abrir, lê as FONTES disponíveis e o ÚLTIMO assessment (somente leitura — NÃO executa análise). A coleta
 * real nasce em Configurações → Integrações ("Sincronizar agora"); esta tela não a dispara. A demonstração
 * sintética existe só em builds de desenvolvimento, fora da jornada normal.
 *
 * [AEGIS-KNIGHT-MULTICLOUD-01] Duas abas: "Visão geral" (nota, aprovação, cobertura, distribuição, prioridades,
 * objetos e limitações — cada gráfico abre a lista filtrada) e "Controles e findings" (pesquisa e filtros
 * combináveis + o detalhe do controle com o plano de ação). A aba e o controle aberto vivem no endereço.
 */
@Component({
  selector: 'app-aegis-knight',
  standalone: true,
  imports: [DatePipe, RouterLink, IdentityRiskPanelComponent, KnightFindingDetailComponent, KnightOverviewComponent, KnightControlsComponent],
  template: `
    <section class="page knight">
      <header class="page-head">
        <div class="titles">
          <p class="page-eyebrow">Assessment de postura</p>
          <h1>
            AEGIS KNIGHT
            <span class="badge" [class]="badgeState()">{{ badgeLabel() }}</span>
          </h1>
          <p class="page-desc">
            Avaliação determinística das configurações de segurança dos provedores conectados: o que favorece
            exposição, os objetos envolvidos e o que fazer. Vereditos por regras; interpretação assistida por IA.
          </p>
          <p class="page-meta">
            Cobertura atual: identidade (Microsoft Entra ID, Google Workspace) · a coleta é feita em Configurações → Integrações
          </p>
        </div>
        <div class="page-actions">
          <a class="btn ghost" routerLink="/history">Histórico auditável</a>
          <a class="btn ghost" routerLink="/settings/integrations">Integrações</a>
          <!-- [AEGIS-MVP-PRODUCT-03] Publica EXATAMENTE a avaliação aberta. Sem o runId, o servidor
               congelaria a mais recente — e o relatório sairia de uma coleta diferente da que está na tela. -->
          <!-- [AEGIS-KNIGHT-DURABLE-01] Só uma avaliação CONCLUÍDA é publicável — o servidor recusa as demais. -->
          @if (assessment(); as pub) {
            @if (!unfinishedView()) {
            <button type="button" class="btn real" (click)="publishReport(pub.id)" [disabled]="publishing()">
              {{ publishing() ? 'Publicando…' : 'Publicar relatório desta avaliação' }}
            </button>
            }
          }
        </div>
      </header>

      @if (loading()) {
        <div class="panel"><div class="state" role="status"><span class="spinner" aria-hidden="true"></span><p>Carregando fontes e última avaliação…</p></div></div>
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
            <!-- [AEGIS-KNIGHT-MULTICLOUD-01] Os três arquivos saem da MESMA fotografia, completa (não um recorte). -->
            @if (publishedId(); as snap) {
              <span class="dl">
                <button type="button" class="btn real" (click)="downloadReport(snap, 'html')" [disabled]="downloading() !== null">
                  {{ downloading() === 'html' ? 'Baixando…' : 'Baixar relatório HTML' }}
                </button>
                <button type="button" class="btn ghost" (click)="downloadReport(snap, 'csv')" [disabled]="downloading() !== null">CSV</button>
                <button type="button" class="btn ghost" (click)="downloadReport(snap, 'pdf')" [disabled]="downloading() !== null">PDF</button>
              </span>
            }
            <a class="btn ghost" routerLink="/history">Abrir histórico</a>
          </div>
        }
        @if (downloadError(); as derr) {
          <div class="banner err"><span>{{ derr }}</span></div>
        }

        <!-- [AEGIS-KNIGHT-DURABLE-01] Uma execução cuja conclusão não foi registrada não é um resultado
             concluído — mas pode ter gravado vereditos determinísticos. O texto depende de existir, ou não,
             uma avaliação concluída abaixo. Inspecionar a execução é uma leitura por Id. -->
        @if (unfinishedAttempt(); as tent) {
          <div class="banner err">
            <span>{{ unfinishedAttemptMessage() }}</span>
            <button type="button" class="btn ghost" (click)="openRun(tent.id)">
              Inspecionar a execução não finalizada
            </button>
          </div>
        }

        <!-- Corte do NAVEGADOR: o desfecho no servidor é desconhecido. A saída oferecida é CONSULTAR a
             última avaliação disponível — uma leitura, que não identifica a tentativa e não dispara coleta. -->
        @if (timeoutNotice(); as tmsg) {
          <div class="banner pinned">
            <span>{{ tmsg }}</span>
            <button type="button" class="btn ghost" (click)="consultLatestAfterTimeout()" [disabled]="busy()">
              Consultar a última avaliação disponível
            </button>
          </div>
        }
        @if (consultNotice(); as cmsg) {
          <div class="banner pinned">
            <span>{{ cmsg }}</span>
            <button type="button" class="btn ghost" (click)="consultNotice.set(null)">Fechar</button>
          </div>
        }

        @if (assessment(); as a) {
          @if (unfinishedView()) {
            <!-- [AEGIS-KNIGHT-DURABLE-01] Aberta por Id: o registro é mostrado como é. Resultado registrado,
                 conclusão da execução e narrativa consultiva são três coisas diferentes. -->
            <div class="banner err">
              <span>
                <b>Execução não finalizada.</b> Iniciada em {{ a.startedAt | date: 'dd/MM/yyyy HH:mm' }}
                ({{ sourceTypeLabel(a.sourceType) }}), sem conclusão registrada. Os resultados determinísticos
                que ela gravou aparecem abaixo apenas para inspeção — <b>não</b> formam uma avaliação
                concluída, não podem ser publicados nem originar ou validar planos de ação, e não há
                narrativa consultiva registrada.
              </span>
              <button type="button" class="btn ghost" (click)="openLatest()">Ver a última avaliação concluída</button>
            </div>
          } @else if (pinnedRun()) {
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
            @if (unfinishedView()) {
              <span>Iniciada em: <b>{{ a.startedAt | date: 'dd/MM/yyyy HH:mm' }}</b></span>
              <span class="sep">·</span>
              <span>Execução: <b>não finalizada</b></span>
            } @else {
              <span>Avaliação concluída: <b>{{ (a.completedAt || a.startedAt) | date: 'dd/MM/yyyy HH:mm' }}</b></span>
            }
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

          <div class="tabbar" role="tablist" aria-label="Seções da avaliação">
            <button type="button" role="tab" id="knight-tab-overview" [class.on]="tab() === 'overview'"
                    [attr.aria-selected]="tab() === 'overview'" [attr.tabindex]="tab() === 'overview' ? 0 : -1"
                    (click)="setTab('overview')" (keydown)="tabKey($event)">Visão geral</button>
            <button type="button" role="tab" id="knight-tab-controls" [class.on]="tab() === 'controls'"
                    [attr.aria-selected]="tab() === 'controls'" [attr.tabindex]="tab() === 'controls' ? 0 : -1"
                    (click)="setTab('controls')" (keydown)="tabKey($event)">
              Controles e findings <span class="tab-count">{{ findingsCount() }}</span>
            </button>
          </div>

          @if (tab() === 'overview') {
            <div role="tabpanel" aria-labelledby="knight-tab-overview" class="tabpanel">
              <app-knight-overview [assessment]="a" [summary]="summary()" [summaryState]="summaryState()"
                                   (filter)="openControls($event)" (open)="openControl($event)" />

              <!-- [AEGIS-MVP-PRODUCT-02] O painel abaixo lê o snapshot ATUAL da Evidence Fabric e diz isso por
                   conta própria — ele NÃO pertence à avaliação aberta acima. -->
              <app-identity-risk-panel [projection]="riskProjection()" />

              <div class="panel ai">
                <div class="hd">
                  <h3>Interpretação e priorização assistidas por IA</h3>
                  @if (unfinishedView()) {
                    <span class="src fallback">Execução não finalizada</span>
                  } @else {
                  <span class="src" [class.fallback]="!a.advisoryFromAi">
                    {{ a.advisoryFromAi ? 'Gerado por IA' : 'Fallback determinístico (IA indisponível)' }}
                  </span>
                  }
                </div>
                <p class="muted ai-note">Consultiva: não altera nota, resultado, severidade nem mapeamento.</p>
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
                  @if (ai.collectionGaps.length) {
                    <div class="ai-block"><h4>Lacunas de coleta</h4><ul>
                      @for (g of ai.collectionGaps; track $index) { <li>{{ g }}</li> }
                    </ul></div>
                  }
                } @else {
                  <p class="summary-txt muted">
                    {{ unfinishedView()
                      ? 'Nenhuma narrativa consultiva foi registrada: a execução não foi finalizada.'
                      : 'Sem interpretação disponível para esta avaliação.' }}
                  </p>
                }
              </div>
            </div>
          } @else {
            <div role="tabpanel" aria-labelledby="knight-tab-controls" class="tabpanel ctl-layout">
              <app-knight-controls [assessment]="a" [filters]="filters()" [selected]="selected()"
                                   (filtersChange)="setFilters($event)" (select)="selectFinding($event)" />
              <!-- Detalhe de UM controle (componente dedicado): o problema, por que importa, onde, o que fazer. -->
              @if (selectedIndicator(); as ind) {
                <app-knight-finding-detail
                  [assessment]="a"
                  [runFinalized]="!unfinishedView()"
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
            </div>
          }
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
            <b>{{ unfinishedAttempt() ? 'Nenhuma avaliação concluída.' : 'Nenhuma avaliação executada ainda.' }}</b>
            <span>
              A avaliação nasce da sincronização de uma fonte conectada. Configure o conector e use
              “Sincronizar agora” em Configurações → Integrações.
            </span>
            @if (entraConfigured()) {
              <span>Fonte real configurada: <b>Microsoft Entra ID</b>.</span>
            }
            @if (googleConfigured()) {
              <span>Fonte real configurada: <b>Google Workspace</b>.</span>
            }
            @if (!entraConfigured() && !googleConfigured()) {
              <span>Nenhuma fonte real configurada ainda.</span>
            }
            <div class="empty-actions">
              <a class="btn primary" routerLink="/settings/integrations">Ir para Integrações</a>
            </div>
          </div>
          }
        }
      }

      <!-- Demonstração sintética: só em builds de DESENVOLVIMENTO, fora da jornada normal. A build de produção não
           a oferece — a avaliação nasce da sincronização em Integrações. -->
      @if (devTools) {
        <details class="dev-tools">
          <summary>Ferramentas de desenvolvimento</summary>
          <p>Demonstração com dados 100% sintéticos (demo.example.com). Não aparece na build de produção.</p>
          <button type="button" class="btn ghost" (click)="runDemo()" [disabled]="busy()">
            {{ running() ? 'Executando…' : 'Executar avaliação demo' }}
          </button>
        </details>
      }
    </section>
  `,
  styles: [
    `
      /* Página, cabeçalho, painéis, botões e estados: sistema visual global (styles.css). */
      .page-head h1 {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sp-3);
      }
      .badge.Demo {
        color: var(--amber);
      }
      .badge.Connected {
        color: var(--cyan);
      }
      .badge.NotConfigured {
        color: var(--muted);
      }
      /* Coleta real: ação secundária com acento ciano (a demonstração é a ação principal). */
      .btn.real {
        color: var(--cyan);
        background: var(--tint-cyan);
        border-color: rgba(38, 224, 255, 0.45);
      }
      .btn.real:hover:not(:disabled) {
        border-color: var(--cyan);
      }

      .source-line {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sp-2);
        font-size: var(--fs-sm);
        color: var(--text-2);
      }
      .source-line b {
        color: var(--text);
        font-weight: 600;
      }
      .source-line.problem b {
        color: var(--amber);
      }
      .source-line .sep {
        color: var(--muted);
      }
      .src-tag {
        padding: 2px var(--sp-2);
        border: 1px solid rgba(38, 224, 255, 0.4);
        border-radius: 6px;
        font-weight: 600;
        color: var(--cyan);
      }
      .src-tag.demo {
        color: var(--amber);
        border-color: rgba(255, 176, 32, 0.4);
      }

      .banner {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        justify-content: space-between;
        gap: var(--sp-3);
        padding: var(--sp-3) var(--sp-4);
        border-radius: var(--radius);
        font-size: var(--fs-sm);
        line-height: var(--lh);
      }
      .banner.err {
        border: 1px solid rgba(255, 45, 111, 0.35);
        border-left: 3px solid var(--red);
        background: var(--tint-red);
        color: #ffc2d4;
      }
      .banner.pinned {
        border: 1px solid rgba(38, 224, 255, 0.35);
        border-left: 3px solid var(--cyan);
        background: var(--tint-cyan);
      }
      .banner.pinned b {
        color: var(--cyan);
      }
      .demo-note {
        padding: 10px 14px;
        border-left: 3px solid var(--amber);
        border-radius: 0 var(--radius-sm) var(--radius-sm) 0;
        background: var(--tint-amber);
        font-size: var(--fs-sm);
        line-height: var(--lh);
        color: var(--text-2);
      }
      .demo-note b {
        color: var(--amber);
      }
      .demo-note.real-problem {
        border-left-color: var(--cyan);
        background: var(--tint-cyan);
      }
      .demo-note.real-problem b {
        color: var(--cyan);
      }
      code {
        padding: 1px 5px;
        border-radius: var(--radius-xs);
        background: rgba(255, 255, 255, 0.06);
        color: var(--text);
      }

      .tabpanel { display: flex; flex-direction: column; gap: var(--sp-4); margin-top: var(--sp-4); }
      .ctl-layout > app-knight-finding-detail { display: block; }
      .dl { display: inline-flex; flex-wrap: wrap; gap: var(--sp-2); }
      .ai-note { margin: 0 0 var(--sp-3); font-size: var(--fs-meta); }
      .dev-tools { margin-top: var(--sp-6); padding: var(--sp-3) var(--sp-4); border: 1px dashed var(--line-strong);
        border-radius: var(--radius); color: var(--text-2); font-size: var(--fs-sm); }
      .dev-tools summary { cursor: pointer; }
      .ai .src {
        padding: 3px 10px;
        border: 1px solid rgba(38, 224, 255, 0.4);
        border-radius: var(--radius-pill);
        font-size: var(--fs-caps);
        font-weight: 600;
        color: var(--cyan);
      }
      .ai .src.fallback {
        color: var(--amber);
        border-color: rgba(255, 176, 32, 0.4);
      }
      .summary-txt {
        margin-bottom: 14px;
        font-size: var(--fs-body);
        line-height: var(--lh-relaxed);
      }
      .summary-txt.muted {
        color: var(--muted);
      }
      .ai-block {
        margin-bottom: 14px;
      }
      .ai-block h4 {
        margin: 0 0 6px;
        font-size: var(--fs-caps);
        font-weight: 600;
        letter-spacing: var(--tracking-caps);
        text-transform: uppercase;
        color: var(--cyan);
      }
      .ai-block ol,
      .ai-block ul {
        margin: 0;
        padding-left: 20px;
      }
      .ai-block li {
        margin-bottom: 6px;
        font-size: var(--fs-sm);
        line-height: 1.55;
      }
      .ai-block .ids {
        font-family: var(--mono);
        font-size: var(--fs-caps);
        color: var(--muted);
      }

      /* Estados que ocupam o painel inteiro (falha, vazio): alinhados à esquerda, com a ação logo abaixo. */
      .panel.state {
        align-items: flex-start;
        gap: 10px;
        padding: var(--sp-5);
        text-align: left;
      }
      .panel.state b {
        font-size: var(--fs-body);
        color: var(--text);
      }
      .panel.state span {
        color: var(--text-2);
      }
      .panel.state.err {
        border-color: rgba(255, 45, 111, 0.4);
      }
      .panel.state.err b {
        color: var(--red-text);
      }
      .empty-actions {
        display: flex;
        flex-wrap: wrap;
        gap: 10px;
        margin-top: var(--sp-1);
      }

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

  /**
   * [AEGIS-KNIGHT-DURABLE-01] A tentativa mais recente que NÃO concluiu, quando existe uma. Fica fora de
   * `assessment` de propósito: ela não tem veredito, e ocupar o lugar do resultado seria apresentar uma
   * execução abandonada como a foto atual da postura.
   */
  readonly unfinishedAttempt = signal<KnightUnfinishedRun | null>(null);

  /** O navegador cortou a espera de uma execução. Não dispara nada sozinho — só oferece a CONSULTA. */
  readonly timeoutNotice = signal<string | null>(null);

  /** O que a consulta posterior ao tempo limite encontrou — sempre sem afirmar que é a tentativa. */
  readonly consultNotice = signal<string | null>(null);

  /**
   * A avaliação exibida no instante em que a tentativa começou. É a única referência que a tela tem: a
   * requisição cortada não devolveu identificador, então só dá para dizer se a consulta trouxe a MESMA
   * avaliação ou OUTRA — nunca que a outra é a tentativa.
   */
  private shownBeforeAttempt: string | null = null;

  /**
   * [AEGIS-KNIGHT-DURABLE-01] A avaliação aberta é uma execução SEM conclusão registrada (aberta por Id).
   * Os dados gravados podem ser inspecionados, mas não são apresentados como avaliação concluída.
   */
  readonly unfinishedView = computed(() => {
    const a = this.assessment();
    return !!a && a.status !== 'Completed';
  });

  /** Texto do aviso da tentativa não finalizada — depende de haver avaliação concluída abaixo. */
  readonly unfinishedAttemptMessage = computed(() => {
    const t = this.unfinishedAttempt();
    if (!t) return null;
    const base =
      `Há uma execução iniciada em ${formatDateTime(t.startedAt)} (${sourceTypeLabel(t.sourceType)}) cuja ` +
      'conclusão não foi registrada. Ela pode ter gravado resultados determinísticos, mas não foi finalizada ' +
      'e não tem narrativa consultiva registrada.';
    const a = this.assessment();
    return a && a.status === 'Completed'
      ? `${base} O resultado mostrado abaixo é o da última avaliação concluída, iniciada em ` +
          `${formatDateTime(a.startedAt)}, antes dela.`
      : `${base} Não há avaliação concluída para mostrar.`;
  });

  readonly sources = signal<KnightSources | null>(null);
  readonly loading = signal(true); // 1ª carga (fontes + último)
  readonly running = signal(false); // execução (demo ou real)
  readonly error = signal<string | null>(null);

  /** A demonstração sintética só existe em builds de desenvolvimento — nunca na jornada normal de produção. */
  protected readonly devTools = !environment.production;

  // Helpers de apresentação (funções puras do modelo) expostos ao template.
  protected readonly sourceTypeLabel = sourceTypeLabel;
  protected readonly sourceStateLabel = sourceStateLabel;
  protected readonly isProblemState = isProblemState;

  readonly badgeState = computed(() =>
    connectionStateOf(this.assessment(), this.entraConfigured() || this.googleConfigured()),
  );
  readonly badgeLabel = computed(() => connectionBadgeLabel(this.badgeState()));

  // ---- [AEGIS-KNIGHT-MULTICLOUD-01] Abas, filtros e resumo de objetos afetados ----------------------
  readonly tab = signal<'overview' | 'controls'>('overview');
  readonly filters = signal<KnightControlFilters>(EMPTY_FILTERS);
  /** Findings = reprovados + mitigados (atenção) — o número na aba. */
  readonly findingsCount = computed(() => (this.assessment()?.indicators ?? []).filter(isFinding).length);
  /** Ocorrências × objetos únicos DESTA avaliação — lido do servidor, nunca estimado na tela. */
  readonly summary = signal<KnightAffectedSummary | null>(null);
  readonly summaryState = signal<'loading' | 'ok' | 'error'>('loading');
  private summaryRun: string | null = null;

  setTab(t: 'overview' | 'controls'): void {
    if (this.tab() === t) return;
    this.tab.set(t);
    this.syncQueryParam(this.selected());
  }

  /** Setas esquerda/direita alternam as abas (padrão WAI-ARIA de tablist). */
  tabKey(ev: KeyboardEvent): void {
    if (ev.key !== 'ArrowLeft' && ev.key !== 'ArrowRight') return;
    ev.preventDefault();
    const next = this.tab() === 'overview' ? 'controls' : 'overview';
    this.setTab(next);
    const el = typeof document !== 'undefined' ? document.getElementById(`knight-tab-${next}`) : null;
    el?.focus();
  }

  /** Um gráfico da visão geral abre a lista de controles JÁ filtrada pelo recorte clicado. */
  openControls(f: Partial<KnightControlFilters>): void {
    this.filters.set({ ...EMPTY_FILTERS, ...f });
    this.setTab('controls');
  }

  /** Uma prioridade da visão geral abre o controle na aba de controles. */
  openControl(indicatorId: string): void {
    this.tab.set('controls');
    if (this.selected() !== indicatorId) this.selectFinding(indicatorId);
    else this.syncQueryParam(indicatorId);
  }

  setFilters(f: KnightControlFilters): void {
    this.filters.set(f);
  }

  /** Relê o resumo de afetados da avaliação exibida; resposta de outra avaliação é descartada. */
  private loadSummary(a: KnightAssessment | null): void {
    if (!a) {
      this.summaryRun = null;
      this.summary.set(null);
      return;
    }
    if (this.summaryRun === a.id && this.summaryState() !== 'error') return;
    const runId = a.id;
    this.summaryRun = runId;
    this.summary.set(null);
    this.summaryState.set('loading');
    this.knight.getAffectedSummary(runId).subscribe({
      next: (s) => {
        if (this.summaryRun !== runId) return;
        this.summary.set(s);
        this.summaryState.set('ok');
      },
      error: () => {
        if (this.summaryRun !== runId) return;
        this.summaryState.set('error');
      },
    });
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
  /** A fotografia recém-publicada DESTA avaliação — os downloads saem dela, por Id (nunca "a mais recente"). */
  readonly publishedId = signal<string | null>(null);
  readonly downloading = signal<PostureExportFormat | null>(null);
  readonly downloadError = signal<string | null>(null);

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
    this.publishedId.set(null);
    this.downloadError.set(null);
    this.history.publish({ type: 'Knight', runId }).subscribe({
      next: (d) => {
        this.publishing.set(false);
        // A publicação só vale para a avaliação que continua aberta — outra resposta não oferece download.
        if (this.assessment()?.id === runId) this.publishedId.set(d.summary.id);
        this.publishNotice.set(
          `Relatório publicado a partir desta avaliação (${d.summary.id}). O conteúdo foi congelado: ` +
            'reexportá-lo depois traz exatamente o que foi publicado agora. HTML, CSV e PDF saem desta mesma fotografia.',
        );
      },
      error: (e: Error) => {
        this.publishing.set(false);
        this.error.set(e.message);
      },
    });
  }



  /**
   * [AEGIS-KNIGHT-MULTICLOUD-01] Baixa um formato da fotografia PUBLICADA, pelo Id dela. Um download por vez;
   * o arquivo é tratado como Blob e o object URL é sempre revogado.
   */
  downloadReport(snapshotId: string, format: PostureExportFormat): void {
    if (this.downloading() !== null) return;
    this.downloading.set(format);
    this.downloadError.set(null);
    this.history.exportSnapshot(snapshotId, format).subscribe({
      next: (file) => {
        this.downloading.set(null);
        const url = URL.createObjectURL(file.blob);
        try {
          const link = document.createElement('a');
          link.href = url;
          link.download = file.filename;
          document.body.appendChild(link);
          link.click();
          link.remove();
        } finally {
          setTimeout(() => URL.revokeObjectURL(url), 1500);
        }
      },
      error: (e: Error) => {
        this.downloading.set(null);
        this.downloadError.set(`Não foi possível baixar o arquivo: ${e.message}`);
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
    this.tab.set('controls');
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
      queryParams: {
        finding: indicatorId,
        run: this.pinnedRun(),
        plan: this.pinnedPlanId(),
        tab: this.tab() === 'controls' ? 'controls' : null,
      },
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

  /**
   * [AEGIS-KNIGHT-DURABLE-01] Abre UMA execução pelo Id, inclusive uma não finalizada. Mudar só o
   * `?run=` não basta: a rota é a mesma e a tela não seria relida.
   */
  openRun(id: string): void {
    this.selected.set(null);
    this.pinned.set({ kind: 'livre' });
    void this.router
      .navigate([], {
        relativeTo: this.route,
        queryParams: { finding: null, run: id, plan: null },
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
    const q = this.route.snapshot.queryParamMap;
    this.tab.set(q.get('tab') === 'controls' || q.get('finding') ? 'controls' : 'overview');

    this.loading.set(true);
    this.error.set(null);
    this.knight.getSources().subscribe({
      next: (s) => this.sources.set(s),
      error: () => this.sources.set(null), // fontes é secundário; não bloqueia a tela
    });

    // [AEGIS-KNIGHT-DURABLE-01] A leitura da última avaliação devolve DUAS coisas: o resultado concluído e,
    // à parte, a tentativa que não concluiu depois dele. Abrir por Id continua alcançando qualquer execução
    // — inclusive uma não concluída, que é mostrada com o estado real, sem virar "resultado".
    this.consultNotice.set(null);
    const wanted$ = requested
      ? this.knight.getById(requested).pipe(
          map((a) => ({ assessment: a, unfinishedAttempt: null }) as KnightLatest),
        )
      : this.knight.getLatestState();

    wanted$.subscribe({
      next: ({ assessment: a, unfinishedAttempt }) => {
        this.assessment.set(a);
        this.unfinishedAttempt.set(unfinishedAttempt);
        this.loading.set(false);
        this.publishedId.set(null);
        this.loadSummary(a);
        this.applyDeepLink(a);
        // Só agora a PROCEDÊNCIA é conhecida — ler a fila antes traria ações de outra fonte/modo.
        this.reloadPlans();
        this.reloadPinnedPlan();
      },
      error: (e: Error) => {
        this.loading.set(false);
        this.unfinishedAttempt.set(null);
        if (requested) {
          this.assessment.set(null);
          this.loadSummary(null);
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
    this.tab.set('controls');
  }

  clearError(): void {
    this.error.set(null);
  }

  /** Executa o assessment de demonstração (ferramenta de desenvolvimento; fora da jornada normal). */
  runDemo(): void {
    this.execute(() => this.knight.runDemo());
  }

  /**
   * Executa a coleta real da fonte indicada. Não é oferecida na tela — a jornada normal é "Sincronizar agora"
   * em Integrações (pedido durável) —, mas a semântica de tempo limite do PR #75 continua protegida por testes.
   * Falha real NÃO cai para Demo.
   */
  runSource(source: KnightSourceType): void {
    this.execute(() => this.knight.runSource(source));
  }

  private execute(run: () => ReturnType<KnightService['runDemo']>): void {
    this.running.set(true);
    this.error.set(null);
    this.timeoutNotice.set(null);
    this.consultNotice.set(null);
    this.shownBeforeAttempt = this.assessment()?.id ?? null;
    run().subscribe({
      next: (a) => {
        this.assessment.set(a);
        this.publishedId.set(null);
        this.loadSummary(a);
        this.unfinishedAttempt.set(null);
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
        this.running.set(false);
        // [AEGIS-KNIGHT-DURABLE-01] Quem desistiu foi o NAVEGADOR. O que houve no servidor é desconhecido
        // e a resposta não trouxe identificador. Não é falha da avaliação e não se resolve repetindo a
        // coleta — a saída oferecida é CONSULTAR a última avaliação disponível, por decisão de quem está
        // na tela.
        if (e instanceof KnightRunTimeoutError) {
          this.timeoutNotice.set(
            'O navegador deixou de aguardar a resposta desta execução e o desfecho dela não foi confirmado: ' +
              'ela pode ter sido concluída, ainda estar em andamento ou não ter sido registrada. ' +
              'Consultar a última avaliação disponível é só uma leitura: não coleta nada na fonte e não ' +
              'identifica a tentativa interrompida.',
          );
          return;
        }
        // Mantém o assessment anterior visível; NUNCA substitui por Demo numa falha real.
        this.error.set(e.message);
      },
    });
  }

  /**
   * [AEGIS-KNIGHT-DURABLE-01] Depois do tempo limite do navegador, CONSULTA a última avaliação disponível.
   * É uma leitura — jamais uma segunda execução. A requisição cortada não devolveu identificador, e fonte
   * ou horário não distinguem a tentativa de outra execução concorrente; por isso a tela afirma só o que a
   * leitura comprova: se a última avaliação é a MESMA já exibida, OUTRA, ou nenhuma. O desfecho da tentativa
   * interrompida permanece não confirmado em todos os casos.
   */
  consultLatestAfterTimeout(): void {
    const before = this.shownBeforeAttempt;
    this.loading.set(true);
    this.error.set(null);
    this.knight.getLatestState().subscribe({
      next: ({ assessment: a, unfinishedAttempt }) => {
        this.loading.set(false);
        this.timeoutNotice.set(null);
        this.unfinishedAttempt.set(unfinishedAttempt);
        const pendente = 'O desfecho da tentativa interrompida continua não confirmado. Nada foi coletado.';

        if (!a) {
          this.consultNotice.set(`Não há avaliação concluída disponível. ${pendente}`);
          return;
        }
        const quem =
          `${sourceTypeLabel(a.sourceType)} (${a.isDemo ? 'demonstração' : 'coleta real'}), ` +
          `iniciada em ${formatDateTime(a.startedAt)} · avaliação ${a.id.slice(0, 8)}`;

        if (a.id === before) {
          this.consultNotice.set(
            `A última avaliação disponível é a mesma já exibida antes da tentativa: ${quem}. ` +
              `Nenhuma avaliação concluída mais recente foi encontrada. ${pendente}`,
          );
          return;
        }

        // OUTRA avaliação ocupa a posição de última. Ela passa a ser a exibida — com procedência, data e
        // endereço próprios — mas NÃO é apresentada como a tentativa: outra execução pode tê-la produzido.
        this.assessment.set(a);
        this.publishedId.set(null);
        this.loadSummary(a);
        this.pinnedRun.set(a.id);
        this.pinned.set({ kind: 'livre' });
        const aberto = this.selected();
        const mantem = aberto && a.indicators.some((i) => i.indicatorId === aberto) ? aberto : null;
        this.selected.set(mantem);
        this.syncQueryParam(mantem);
        this.reloadPlans();
        this.reloadRisk();
        this.consultNotice.set(
          `Exibindo a última avaliação disponível: ${quem}. Ela é diferente da exibida antes da tentativa, ` +
            'mas não é possível confirmar que corresponde à tentativa interrompida — outra execução pode ' +
            `tê-la produzido. ${pendente}`,
        );
      },
      error: () => {
        // A consulta falhou: a avaliação exibida não muda e a consulta continua à mão.
        this.loading.set(false);
        this.consultNotice.set(
          'Não foi possível consultar a última avaliação agora. A avaliação exibida não mudou e o desfecho ' +
            'da tentativa interrompida continua não confirmado. Nada foi coletado.',
        );
      },
    });
  }
}

/** Data/hora local no mesmo formato do template (dd/MM/yyyy HH:mm). */
function formatDateTime(iso: string): string {
  const d = new Date(iso);
  const p = (n: number) => String(n).padStart(2, '0');
  return `${p(d.getDate())}/${p(d.getMonth() + 1)}/${d.getFullYear()} ${p(d.getHours())}:${p(d.getMinutes())}`;
}

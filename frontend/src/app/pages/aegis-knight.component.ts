import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { map } from 'rxjs';
import { environment } from '../../environments/environment';
import {
  EMPTY_FILTERS,
  KnightAffectedSummary,
  KnightReferenceCoverage,
  KnightAssessment,
  KnightConsolidatedSource,
  KnightControlFilters,
  KnightIndicator,
  isFinding,
  KnightLatest,
  KnightSourceLatest,
  KnightSourceType,
  KnightSources,
  KnightUnfinishedRun,
  connectionBadgeLabel,
  connectionStateOf,
  isProblemState,
  consolidatedCollection,
  sourceStateLabel,
  sourceTypeLabel,
  CONSOLIDATION_CANDIDATES,
  KnightGlossary,
  KnightAssessmentScope,
  outOfScopeSources,
  sourceSlug,
} from '../models/knight.models';
import { IdentityRiskPanelComponent } from '../components/identity/identity-risk-panel.component';
import { KnightFindingDetailComponent } from '../components/knight/finding-detail.component';
import { KnightOverviewComponent } from '../components/knight/knight-overview.component';
import { KnightControlsComponent } from '../components/knight/knight-controls.component';
import { KnightGlossaryComponent } from '../components/knight/knight-glossary.component';
import { KnightCoverageComponent } from '../components/knight/knight-coverage.component';

/** [AEGIS-KNIGHT-PRESENTATION-01] Abas da avaliação. */
type KnightPageTab = 'overview' | 'controls' | 'coverage' | 'glossary';
/** [AEGIS-KNIGHT-SCOPE-01] Abas da seção: "Geral" (o consolidado do escopo) e uma por fonte em escopo. */
type KnightViewKey = 'geral' | KnightSourceType;
import { IdentityEvidenceProjection } from '../models/identity-risk.models';
import { advisoryOrigin } from '../models/knight-summary.models';
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
import { AuthService } from '../services/auth.service';
import { canManageActionPlans } from '../models/remediation.models';
import { HistoryChangedError, PostureHistoryService } from '../services/posture-history.service';
import { FrozenPostureHistory, HistoryWindowRequest, KnightConsolidatedSourceSelection, PostureExportFormat } from '../models/posture-history.models';
import { HistoryPreviewComponent } from '../components/history-preview.component';

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
/** [AEGIS-ASSESSMENT-VISUALS-01] O que uma prévia de publicação vai publicar: uma execução ou a composição pinada. */
type PublishTarget = { kind: 'run'; runId: string } | { kind: 'consolidated'; selection: KnightConsolidatedSourceSelection[] };

@Component({
  selector: 'app-aegis-knight',
  standalone: true,
  imports: [DatePipe, RouterLink, IdentityRiskPanelComponent, KnightFindingDetailComponent, KnightOverviewComponent, KnightControlsComponent, KnightGlossaryComponent, KnightCoverageComponent, HistoryPreviewComponent],
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
            exposição, as contas, aplicativos e configurações envolvidas e o que fazer. Vereditos por regras determinísticas; interpretação consultiva à parte.
          </p>
          <p class="page-meta">
            Fontes: Microsoft Entra ID, Microsoft 365 (Teams, Exchange Online, Defender para Office 365, Purview, SharePoint e
            OneDrive, Intune, Fabric), Microsoft Azure (Resource Manager) e Google Workspace · a coleta é feita em Configurações →
            Integrações
          </p>
        </div>
        <div class="page-actions">
          <a class="btn ghost" routerLink="/history">Histórico auditável</a>
          <a class="btn ghost" routerLink="/settings/integrations">Integrações</a>
          <!-- [AEGIS-MVP-PRODUCT-03] Publica EXATAMENTE a avaliação aberta. Sem o runId, o servidor
               congelaria a mais recente — e o relatório sairia de uma coleta diferente da que está na tela. -->
          <!-- [AEGIS-KNIGHT-DURABLE-01] Só uma avaliação CONCLUÍDA é publicável — o servidor recusa as demais. -->
          @if (assessment(); as pub) {
            @if (!unfinishedView() && consolidatedMode()) {
              <button type="button" class="btn real" (click)="publishConsolidatedReport()"
                      [disabled]="publishing() || !consolidatedPublishable()"
                      [title]="consolidatedPublishable() ? '' : 'Marque ao menos uma fonte avaliada para publicar.'">
                {{ publishing() ? 'Publicando…' : 'Publicar relatório consolidado' }}
              </button>
            } @else if (!unfinishedView()) {
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

        <!-- [AEGIS-ASSESSMENT-VISUALS-01] Publicar mostra antes o histórico que o relatório vai congelar; a confirmação
             confere a impressão digital desta prévia (o servidor recusa uma série diferente). -->
        @if (pubPreview(); as pp) {
          <app-history-preview [history]="pp.history" [loading]="pp.loading" [error]="pp.error" [window]="pp.window"
                               [notice]="pp.notice" (windowChange)="changePublishWindow($event)">
            <button type="button" class="btn real" (click)="confirmPublish()" [disabled]="publishing() || pp.loading || !pp.history">
              {{ publishing() ? 'Publicando…' : 'Confirmar publicação com este histórico' }}
            </button>
            <button type="button" class="btn ghost" (click)="cancelPublish()" [disabled]="publishing()">Cancelar</button>
          </app-history-preview>
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

        <!-- [AEGIS-KNIGHT-SCOPE-01] A seção abre em "Geral" (o consolidado do escopo salvo do tenant) e depois mostra uma aba por
             fonte EM ESCOPO, cada uma com a própria avaliação. Trocar de aba é uma LEITURA: nada é coletado, e a avaliação da
             aba anterior continua existindo. Uma fonte fora do escopo não tem aba; ela aparece em "Geral" como "Fora do escopo". -->
        @if (hasViews()) {
          <nav class="panel sources" aria-label="Avaliação KNIGHT: geral e por fonte">
            <div class="viewbar" role="tablist" aria-label="Avaliação: geral e por fonte">
              <button type="button" role="tab" class="src" [id]="viewTabId('geral')" [class.on]="view() === 'geral'"
                      [attr.aria-selected]="view() === 'geral'" [attr.tabindex]="view() === 'geral' ? 0 : -1"
                      aria-controls="knight-view-panel" (click)="selectView('geral')" (keydown)="viewKey($event)">
                <b>Geral</b>
                <span class="when">Consolidado do escopo</span>
              </button>
              @for (s of viewTabs(); track s.source) {
                <button type="button" role="tab" class="src" [id]="viewTabId(s.source)" [class.on]="view() === s.source"
                        [attr.aria-selected]="view() === s.source" [attr.tabindex]="view() === s.source ? 0 : -1"
                        aria-controls="knight-view-panel" (click)="selectView(s.source)" (keydown)="viewKey($event)">
                  <b>{{ s.label }}</b>
                  @if (s.assessment; as sa) {
                    <span class="when">Coletado em {{ sa.startedAt | date: 'dd/MM/yyyy HH:mm' }}</span>
                  } @else {
                    <span class="when">Sem avaliação concluída</span>
                  }
                </button>
              }
            </div>
            @if (view() === 'geral') {
              <!-- O seletor "Compor com" é o ESCOPO salvo do tenant: a mesma escolha vale ao recarregar, no painel, na prévia
                   e na publicação do consolidado. Desmarcar uma fonte a retira dos totais; o conector e as avaliações dela ficam. -->
              <div class="consolidated-picker" role="group" aria-labelledby="knight-scope-title">
                <p class="sources-title" id="knight-scope-title">Compor com</p>
                @for (c of consolidatedCandidates(); track c.source) {
                  <label class="pick" [class.off]="!c.inScope">
                    <input type="checkbox" [checked]="c.inScope" [disabled]="!canEditScope() || scopeSaving() || viewLoading()"
                           (change)="toggleConsolidatedSource(c.source, $event)" />
                    {{ c.label }}
                    @if (!c.inScope) { <span class="when">fora do escopo</span> }
                    @else if (!c.hasAssessment) { <span class="when">sem avaliação concluída</span> }
                  </label>
                }
              </div>
              <p class="sources-note" role="status">{{ scopeSaving() ? 'Salvando o escopo da avaliação…' : scopeNote() }}</p>
              @if (scopeError(); as serr) {
                <p class="sources-note err" role="alert">{{ serr }} O escopo salvo não mudou.</p>
              }
            } @else {
              <p class="sources-note">
                Cada fonte tem nota, cobertura, achados e limitações próprios — eles não são somados aqui. O relatório publicado
                nesta aba é o da fonte aberta; o consolidado fica em “Geral”.
              </p>
            }
          </nav>
        }

        <div id="knight-view-panel" [attr.role]="hasViews() ? 'tabpanel' : null"
             [attr.aria-labelledby]="hasViews() ? viewTabId(view()) : null" class="view-panel">
        @if (viewLoading()) {
          <div class="panel"><div class="state" role="status"><span class="spinner" aria-hidden="true"></span><p>{{ viewLoadingText() }}</p></div></div>
        } @else if (viewError()) {
          <div class="panel state err" role="alert">
            <b>{{ viewError() }}</b>
            <span>A avaliação desta aba não foi carregada agora. As demais abas não foram afetadas.</span>
            <button type="button" class="btn ghost" (click)="retryView()">Tentar novamente</button>
          </div>
        } @else {
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
            } @else if (a.sourceType === 'Consolidated') {
              <span>Execução: <b>concluída</b></span>
              <span class="sep">·</span>
              <span>Dados de: <b>{{ (a.completedAt || a.startedAt) | date: 'dd/MM/yyyy HH:mm' }}</b></span>
            } @else {
              <span>Avaliação concluída: <b>{{ (a.completedAt || a.startedAt) | date: 'dd/MM/yyyy HH:mm' }}</b></span>
            }
          </div>

          @if (a.isDemo) {
            <p class="demo-note">
              <b>Modo demonstração.</b> Dados 100% sintéticos (domínio <code>demo.example.com</code>). O Aegis
              <b>não</b> está conectado ao Entra ID, AD local ou Okta.
            </p>
          } @else if (a.sourceType === 'Consolidated' && a.sources) {
            @if (isProblemState(a.sourceState)) {
              <p class="demo-note real-problem">
                <b>Execução concluída, coleta parcial.</b> {{ consolidatedText(a.sources) }}
              </p>
            }
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
            <button type="button" role="tab" id="knight-tab-coverage" [class.on]="tab() === 'coverage'"
                    [attr.aria-selected]="tab() === 'coverage'" [attr.tabindex]="tab() === 'coverage' ? 0 : -1"
                    (click)="setTab('coverage')" (keydown)="tabKey($event)">Cobertura e verificação manual</button>
            <button type="button" role="tab" id="knight-tab-glossary" [class.on]="tab() === 'glossary'"
                    [attr.aria-selected]="tab() === 'glossary'" [attr.tabindex]="tab() === 'glossary' ? 0 : -1"
                    (click)="setTab('glossary')" (keydown)="tabKey($event)">Glossário</button>
          </div>

          @if (tab() === 'overview') {
            <div role="tabpanel" aria-labelledby="knight-tab-overview" class="tabpanel">
              <app-knight-overview [assessment]="a" [summary]="summary()" [summaryState]="summaryState()" [coverage]="referenceCoverage()"
                                   (filter)="openControls($event)" (open)="openControl($event)">

              <!-- [AEGIS-KNIGHT-RESULTS-PRESENTATION-01] Interpretação consultiva: a origem é a do conteúdo que existe (IA ou resumo
                   determinístico do AEGIS). Sem conteúdo, um estado vazio curto — nunca um "fallback" anunciado e vazio. -->
              @let origin = advisoryOrigin(a, !unfinishedView());
              <div class="panel ai">
                <div class="hd">
                  <h3>Interpretação consultiva</h3>
                  @if (unfinishedView()) {
                    <span class="src fallback">Execução não finalizada</span>
                  } @else if (origin.label) {
                    <span class="src" [class.fallback]="origin.kind !== 'ai'">{{ origin.label }}</span>
                  }
                </div>
                @if (origin.kind !== 'none' && a.advisory; as ai) {
                <p class="muted ai-note">Consultiva: não altera nota, resultado, severidade nem mapeamento.</p>
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
                        <li>{{ act.action }} @if (act.indicatorIds.length) { <span class="ids">[{{ act.indicatorIds.join(', ') }}]</span> }</li>
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
                      : 'Nenhuma interpretação registrada para esta avaliação. Os resultados acima são determinísticos e não dependem dela.' }}
                  </p>
                }
              </div>

              <!-- [AEGIS-MVP-PRODUCT-02] O painel abaixo lê o snapshot ATUAL da Evidence Fabric e diz isso por
                   conta própria — ele NÃO pertence à avaliação aberta acima. -->
              <app-identity-risk-panel [projection]="riskProjection()" />
              </app-knight-overview>
            </div>
          } @else if (tab() === 'coverage') {
            <div role="tabpanel" aria-labelledby="knight-tab-coverage" class="tabpanel">
              <app-knight-coverage [coverage]="referenceCoverage()" [state]="coverageState()" [assessment]="a"
                                   (open)="openControl($event)" (recorded)="loadCoverage()" />
            </div>
          } @else if (tab() === 'glossary') {
            <div role="tabpanel" aria-labelledby="knight-tab-glossary" class="tabpanel">
              <app-knight-glossary [assessment]="a" [glossary]="glossary()" [state]="glossaryState()" (open)="openControl($event)" />
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
                  [glossary]="glossary()?.terms ?? []"
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
            @if (viewEmptyText(); as ve) {
              <b>{{ ve }}</b>
            } @else {
              <b>{{ unfinishedAttempt() ? 'Nenhuma avaliação concluída.' : 'Nenhuma avaliação executada ainda.' }}</b>
            }
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
        </div>
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

      /* [AEGIS-KNIGHT-COVERAGE-02] Seletor de FONTE avaliada. Deliberadamente sóbrio: é navegação entre
         avaliações que coexistem, não um filtro que soma ou mistura números de fontes diferentes. */
      .sources {
        display: grid;
        gap: var(--sp-2);
      }
      .sources-title {
        margin: 0;
        font-size: 0.78rem;
        letter-spacing: 0.08em;
        text-transform: uppercase;
        color: var(--muted);
      }
      .sources ul {
        display: flex;
        flex-wrap: wrap;
        gap: var(--sp-2);
        margin: 0;
        padding: 0;
        list-style: none;
      }
      .sources .src {
        display: grid;
        gap: 2px;
        padding: var(--sp-2) var(--sp-3);
        text-align: left;
        color: var(--text);
        background: var(--surface-2);
        border: 1px solid var(--line);
        border-radius: var(--radius-2);
        cursor: pointer;
      }
      .sources .src.on {
        color: var(--cyan);
        background: var(--tint-cyan);
        border-color: rgba(38, 224, 255, 0.45);
      }
      .sources .src .when {
        font-size: 0.8rem;
        color: var(--muted);
      }
      .sources .src.on .when {
        color: inherit;
        opacity: 0.85;
      }
      .sources-note {
        margin: 0;
        font-size: 0.82rem;
        color: var(--muted);
      }
      .sources-note.err {
        color: var(--red-text);
      }
      /* [AEGIS-KNIGHT-SCOPE-01] Abas da seção (Geral + fontes): mesma aparência sóbria do seletor anterior. */
      .viewbar {
        display: flex;
        flex-wrap: wrap;
        gap: var(--sp-2);
      }
      .view-panel {
        display: flex;
        flex-direction: column;
        gap: var(--sp-4);
      }
      .sources-hd {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: var(--sp-2);
      }
      .consolidated-picker {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sp-3);
      }
      .consolidated-picker .pick {
        display: flex;
        align-items: center;
        gap: 6px;
        font-size: 0.85rem;
        color: var(--text);
        cursor: pointer;
      }
      .consolidated-picker .pick.off {
        color: var(--muted);
        cursor: not-allowed;
      }
      .consolidated-picker .when {
        font-size: 0.78rem;
        color: var(--muted);
      }
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
  private readonly auth = inject(AuthService);

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

  /**
   * [AEGIS-KNIGHT-COVERAGE-02] A última avaliação de CADA fonte. Com Microsoft Entra ID e Microsoft Teams
   * avaliados, "a última avaliação" deixou de ter resposta única: mostrar só a sincronização mais recente
   * faria a avaliação da outra fonte sumir da tela a cada coleta. A lista fica aqui, a fonte exibida é
   * escolhida explicitamente, e cada bloco traz a própria nota, cobertura e data — nunca somadas entre fontes.
   */
  readonly latestBySource = signal<KnightSourceLatest[]>([]);

  // ---- [AEGIS-KNIGHT-SCOPE-01] Abas da seção e escopo salvo ---------------------------------------------------------
  // "Geral" é a primeira aba e mostra o consolidado do escopo salvo do tenant; depois vem uma aba por fonte EM ESCOPO. Cada
  // troca de aba é uma leitura com número de pedido próprio: uma resposta de uma aba anterior (ou de uma releitura superada)
  // é descartada, mesmo que chegue depois de a tela já mostrar outra coisa.

  /** A aba aberta. */
  readonly view = signal<KnightViewKey>('geral');
  /** A avaliação da aba está sendo lida. */
  readonly viewLoading = signal(false);
  /** A leitura da aba falhou (estado próprio, com nova tentativa). */
  readonly viewError = signal<string | null>(null);
  private viewSeq = 0;

  /** Escopo salvo do tenant, lido junto com as avaliações por fonte. */
  readonly scope = signal<KnightAssessmentScope | null>(null);
  readonly outOfScope = computed(() => outOfScopeSources(this.scope()));
  readonly scopeSaving = signal(false);
  readonly scopeError = signal<string | null>(null);
  private scopeSeq = 0;
  /** Gate de apresentação; o servidor continua sendo a autoridade (403 para quem não pode). */
  readonly canEditScope = computed(() => canManageActionPlans(this.auth.activeRole()));

  /** As abas só existem na leitura por fonte — um link para UMA execução (`?run=`) mostra só ela. */
  readonly hasViews = computed(() => !this.pinnedRun() && this.latestBySource().length > 0);

  /** Abas de fonte: as que já produziram alguma avaliação, menos as fora do escopo. */
  readonly viewTabs = computed(() => {
    const out = this.outOfScope();
    return this.latestBySource().filter((s) => !out.includes(s.source));
  });

  /** Modo consolidado = aba "Geral" na leitura por fonte. */
  readonly consolidatedMode = computed(() => this.hasViews() && this.view() === 'geral');
  readonly consolidatedLoading = computed(() => this.viewLoading() && this.view() === 'geral');

  // [AEGIS-KNIGHT-COVERAGE-04] As candidatas vêm do catálogo único de fontes — nunca uma lista fixa de três.
  private static readonly CONSOLIDATED_CANDIDATES: KnightSourceType[] = CONSOLIDATION_CANDIDATES;

  /**
   * A publicação só é possível com uma composição ESTÁVEL: nenhuma leitura em voo (a resposta ainda pode trocar
   * o que está na tela) e ao menos uma fonte efetivamente INCLUÍDA na avaliação EXIBIDA — nunca a seleção dos
   * checkboxes por si só, que pode estar momentaneamente à frente da resposta do servidor.
   */
  readonly consolidatedPublishable = computed(
    () => !this.consolidatedLoading() && !this.scopeSaving() && (this.assessment()?.sources ?? []).some((s) => s.included && !!s.sourceRunId),
  );

  readonly consolidatedCandidates = computed(() => {
    const bySource = new Map(this.latestBySource().map((s) => [s.source, s]));
    const out = this.outOfScope();
    return AegisKnightComponent.CONSOLIDATED_CANDIDATES.map((source) => {
      const bloco = bySource.get(source);
      return {
        source,
        label: bloco?.label ?? sourceTypeLabel(source),
        hasAssessment: !!bloco?.assessment && !bloco.assessment.isDemo,
        inScope: !out.includes(source),
      };
    });
  });

  /** A composição pedida ao servidor: as fontes EM ESCOPO com avaliação concluída (o servidor aplica o escopo de novo). */
  readonly consolidatedSelection = computed(() =>
    this.consolidatedCandidates().filter((c) => c.inScope && c.hasAssessment).map((c) => c.source),
  );

  isSourceIncluded(source: KnightSourceType): boolean {
    return !this.outOfScope().includes(source);
  }

  /** Frase do escopo: salvo (quando e por quem) ou padrão, e o que a escolha faz. */
  readonly scopeNote = computed(() => {
    const sc = this.scope();
    const base = sc?.updatedAt
      ? `Escopo salvo para este tenant em ${formatDateTime(sc.updatedAt)}${sc.updatedByName ? ` por ${sc.updatedByName}` : ''}.`
      : 'Escopo padrão: todas as fontes elegíveis.';
    const out = this.outOfScope().length;
    return `${base} A nota consolidada aplica a mesma fórmula do KNIGHT sobre os controles das fontes marcadas — não é a média ` +
      'das notas por fonte. ' +
      (out > 0 ? `${out === 1 ? 'Uma fonte está fora' : `${out} fontes estão fora`} do escopo: os controles, achados e limitações dela(s) ` +
        'não entram nos totais; o conector e as avaliações continuam. ' : '') +
      'A escolha vale ao recarregar, no painel, na prévia e na publicação.' +
      (this.canEditScope() ? '' : ' Alterar o escopo exige o papel Manager ou TenantAdmin.');
  });

  readonly viewLoadingText = computed(() =>
    this.view() === 'geral' ? 'Carregando o consolidado do escopo…' : `Carregando a avaliação de ${sourceTypeLabel(this.view() as KnightSourceType)}…`,
  );

  /** Estado vazio dito NA aba: fonte sem avaliação concluída, ou "Geral" sem nenhuma fonte do escopo avaliada. */
  readonly viewEmptyText = computed<string | null>(() => {
    if (!this.hasViews()) return null;
    const v = this.view();
    if (v === 'geral') return 'Nenhuma fonte do escopo tem avaliação concluída para compor o consolidado.';
    return `${sourceTypeLabel(v)}: ${this.unfinishedAttempt() ? 'nenhuma avaliação concluída.' : 'sem avaliação concluída.'}`;
  });

  viewTabId(v: KnightViewKey): string {
    return `knight-view-${v === 'geral' ? 'geral' : sourceSlug(v)}`;
  }

  /** Setas, Home e End percorrem as abas da seção (padrão WAI-ARIA de tablist); a aba focada é aberta. */
  viewKey(ev: KeyboardEvent): void {
    const order: KnightViewKey[] = ['geral', ...this.viewTabs().map((s) => s.source)];
    const at = Math.max(0, order.indexOf(this.view()));
    const next =
      ev.key === 'ArrowRight' ? order[(at + 1) % order.length]
        : ev.key === 'ArrowLeft' ? order[(at + order.length - 1) % order.length]
          : ev.key === 'Home' ? order[0]
            : ev.key === 'End' ? order[order.length - 1]
              : null;
    if (next === null) return;
    ev.preventDefault();
    this.selectView(next);
    const el = typeof document !== 'undefined' ? document.getElementById(this.viewTabId(next)) : null;
    el?.focus();
  }

  /**
   * Abre uma aba. "Geral" lê o consolidado do escopo; uma fonte lê a avaliação concluída DELA por Id (nota, cobertura,
   * achados e limitações). Cada chamada recebe um número de pedido: só a resposta do pedido mais recente grava na tela.
   * `initial` é a 1ª abertura (aplica os links do endereço); `force` relê a mesma aba (depois de salvar o escopo, ou de um erro).
   */
  selectView(v: KnightViewKey, opts: { initial?: boolean; force?: boolean } = {}): void {
    const { initial = false, force = false } = opts;
    if (!initial && !force && v === this.view() && !this.viewError() && (this.viewLoading() || this.assessment())) return;
    const seq = ++this.viewSeq;
    // O resultado exibido vai mudar: a prévia aberta (e qualquer resposta dela em voo) deixa de valer.
    this.discardPublishPreview();
    this.view.set(v);
    this.viewError.set(null);
    this.error.set(null);
    this.publishedId.set(null);
    this.publishNotice.set(null);
    if (!initial) {
      this.selected.set(null);
      this.pinned.set({ kind: 'livre' });
      this.findingNotice.set(null);
      this.syncViewParam(v);
    }
    if (v === 'geral') {
      this.loadGeral(seq, initial);
      return;
    }
    const bloco = this.latestBySource().find((s) => s.source === v);
    this.unfinishedAttempt.set(bloco?.unfinishedAttempt ?? null);
    if (!bloco?.assessment) {
      this.viewLoading.set(false);
      this.showView(null, initial);
      return;
    }
    this.assessment.set(null);
    this.viewLoading.set(true);
    this.knight.getById(bloco.assessment.id).subscribe({
      next: (a) => {
        if (seq !== this.viewSeq) return; // aba superada — outra troca já aconteceu
        this.viewLoading.set(false);
        this.showView(a, initial);
      },
      error: (e: Error) => {
        if (seq !== this.viewSeq) return;
        this.viewLoading.set(false);
        this.viewError.set(`${sourceTypeLabel(v)}: ${e.message}`);
      },
    });
  }

  /** Uma execução foi fixada por outra via (nova execução, consulta): a leitura de aba em voo não grava mais na tela. */
  private cancelView(): void {
    this.viewSeq++;
    this.viewLoading.set(false);
    this.viewError.set(null);
  }

  /** Relê a aba aberta depois de uma falha — decisão de quem está na tela. */
  retryView(): void {
    this.selectView(this.view(), { force: true });
  }

  /**
   * [AEGIS-KNIGHT-CONSOLIDATED-02] Lê a composição consolidada do escopo ATUAL. Protegida contra respostas OBSOLETAS pelo
   * número de pedido da aba: uma troca de aba, de escopo ou uma releitura descarta qualquer resposta anterior ainda em voo.
   */
  private loadGeral(seq: number, initial: boolean): void {
    // [AEGIS-ASSESSMENT-VISUALS-01] Outra composição: a prévia aberta era da composição anterior.
    this.discardPublishPreview();
    this.unfinishedAttempt.set(null);
    const selection = this.consolidatedSelection();
    if (selection.length === 0) {
      this.viewLoading.set(false);
      this.showView(null, initial);
      return;
    }
    this.assessment.set(null);
    this.viewLoading.set(true);
    this.knight.getConsolidated(selection).subscribe({
      next: (a) => {
        if (seq !== this.viewSeq) return; // pedido superado — outra aba, outro escopo ou outra releitura
        this.viewLoading.set(false);
        this.showView(a, initial);
      },
      error: (e: Error) => {
        if (seq !== this.viewSeq) return;
        this.viewLoading.set(false);
        this.viewError.set(`Geral: ${e.message}`);
      },
    });
  }

  /** Mostra o resultado da aba: resumo de afetados e ações relidos para ELE; na 1ª abertura, os links do endereço. */
  private showView(a: KnightAssessment | null, initial: boolean): void {
    this.assessment.set(a);
    // [AEGIS-KNIGHT-COVERAGE-04] A composição não tem execução própria, mas cada achado tem a da SUA fonte: o resumo de
    // afetados é o das execuções incluídas e as ações são as da fonte do achado — as mesmas da visão por fonte.
    this.loadSummary(a);
    if (initial) {
      this.applyDeepLink(a);
      this.applyFilterLink();
    }
    // Só agora a PROCEDÊNCIA é conhecida — ler a fila antes traria ações de outra fonte/modo.
    this.reloadPlans();
    if (initial) this.reloadPinnedPlan();
  }

  /** A aba vive no endereço (`?fonte=`), para recarregar, voltar ou compartilhar a mesma aba. */
  private syncViewParam(v: KnightViewKey): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { fonte: v === 'geral' ? null : sourceSlug(v), modo: null, finding: null, plan: null },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  /**
   * [AEGIS-KNIGHT-SCOPE-01] Marca/desmarca uma fonte em "Compor com" e SALVA o escopo do tenant. Enquanto a gravação corre, o
   * seletor fica bloqueado; o checkbox só muda quando o servidor confirma (uma recusa deixa o escopo salvo como estava).
   */
  toggleConsolidatedSource(source: KnightSourceType, ev?: Event): void {
    const out = this.outOfScope();
    // O checkbox reflete o escopo SALVO: volta ao estado atual até o servidor confirmar a mudança.
    const box = ev?.target as HTMLInputElement | undefined;
    if (box) box.checked = !out.includes(source);
    if (this.scopeSaving() || !this.canEditScope()) return;
    const nextOut = out.includes(source) ? out.filter((s) => s !== source) : [...out, source];
    const inScope = AegisKnightComponent.CONSOLIDATED_CANDIDATES.filter((s) => !nextOut.includes(s));
    this.discardPublishPreview();
    this.scopeSaving.set(true);
    this.scopeError.set(null);
    const seq = ++this.scopeSeq;
    this.knight.saveScope(inScope).subscribe({
      next: (saved) => {
        if (seq !== this.scopeSeq) return;
        this.scopeSaving.set(false);
        this.scope.set(saved);
        const v = this.view();
        // A composição mudou: "Geral" é relido; uma aba de fonte que saiu do escopo deixa de existir.
        if (v === 'geral') this.selectView('geral', { force: true });
        else if (this.outOfScope().includes(v)) this.selectView('geral');
      },
      error: (e: Error) => {
        if (seq !== this.scopeSeq) return;
        this.scopeSaving.set(false);
        this.scopeError.set(e.message);
      },
    });
  }

  /**
   * Publica o relatório consolidado PINANDO a execução exata de cada fonte que a tela mostra como incluída
   * agora — nunca a seleção dos checkboxes isolada, que pode estar à frente da resposta do servidor. Bloqueada
   * enquanto a composição está carregando ou sem nenhuma fonte incluída (ver `consolidatedPublishable`).
   */
  publishConsolidatedReport(): void {
    const target = this.currentPublishTarget();
    if (target?.kind !== 'consolidated' || target.selection.length === 0) return;
    this.openPublishPreview(target);
  }

  /** Publica a composição pinada conferindo o histórico da prévia. */
  private publishConsolidatedWith(selection: KnightConsolidatedSourceSelection[], history: FrozenPostureHistory, window: HistoryWindowRequest): void {
    const seq = ++this.publishSeq;
    this.publishing.set(true);
    this.publishNotice.set(null);
    this.error.set(null);
    this.publishedId.set(null);
    this.downloadError.set(null);
    this.history.publishConsolidated({ selection, historyUntil: window.until, historyMonths: window.months, expectedHistoryFingerprint: history.basisFingerprint }).subscribe({
      next: (d) => {
        if (seq !== this.publishSeq) return this.stalePublication(d.summary.id);
        this.publishing.set(false);
        this.pubPreview.set(null);
        this.publishedId.set(d.summary.id);
        this.publishNotice.set(
          `Relatório consolidado publicado (${d.summary.id}). O conteúdo foi congelado: reexportá-lo depois ` +
            'traz exatamente o que foi publicado agora. HTML, CSV e PDF saem desta mesma fotografia.',
        );
      },
      error: (e: Error) => {
        if (seq === this.publishSeq) this.publishFailed(e);
      },
    });
  }

  readonly sources = signal<KnightSources | null>(null);
  readonly loading = signal(true); // 1ª carga (fontes + último)
  readonly running = signal(false); // execução (demo ou real)
  readonly error = signal<string | null>(null);

  /** A demonstração sintética só existe em builds de desenvolvimento — nunca na jornada normal de produção. */
  protected readonly devTools = !environment.production;

  // Helpers de apresentação (funções puras do modelo) expostos ao template.
  protected readonly sourceTypeLabel = sourceTypeLabel;
  protected readonly sourceStateLabel = sourceStateLabel;
  protected readonly advisoryOrigin = advisoryOrigin;
  protected readonly isProblemState = isProblemState;
  protected consolidatedText(sources: KnightConsolidatedSource[]): string {
    return consolidatedCollection(sources).text;
  }

  readonly badgeState = computed(() =>
    connectionStateOf(this.assessment(), this.entraConfigured() || this.googleConfigured()),
  );
  readonly badgeLabel = computed(() => connectionBadgeLabel(this.badgeState()));

  // ---- [AEGIS-KNIGHT-MULTICLOUD-01] Abas, filtros e resumo de objetos afetados ----------------------
  readonly tab = signal<KnightPageTab>('overview');
  /** [AEGIS-KNIGHT-PRESENTATION-01] Glossário único do servidor — lido uma vez; o mesmo das exportações. */
  readonly glossary = signal<KnightGlossary | null>(null);
  readonly glossaryState = signal<'loading' | 'ok' | 'error'>('loading');
  readonly filters = signal<KnightControlFilters>(EMPTY_FILTERS);
  /** Findings = reprovados + mitigados (atenção) — o número na aba. */
  readonly findingsCount = computed(() => (this.assessment()?.indicators ?? []).filter(isFinding).length);
  /** Ocorrências × objetos únicos DESTA avaliação — lido do servidor, nunca estimado na tela. */
  readonly summary = signal<KnightAffectedSummary | null>(null);
  readonly summaryState = signal<'loading' | 'ok' | 'error'>('loading');
  private summaryRun: string | null = null;
  /** [AEGIS-KNIGHT-COVERAGE-01] Cobertura do catálogo (leitura do catálogo no servidor — não coleta nada). */
  readonly referenceCoverage = signal<KnightReferenceCoverage | null>(null);
  readonly coverageState = signal<'loading' | 'ok' | 'error'>('loading');

  /** [AEGIS-KNIGHT-CLOSURE-01] Relê a cobertura do catálogo (com o resultado manual vigente do cliente). */
  loadCoverage(): void {
    this.knight.getReferenceCoverage().subscribe({
      next: (c) => {
        this.referenceCoverage.set(c);
        this.coverageState.set('ok');
      },
      error: () => {
        if (!this.referenceCoverage()) this.coverageState.set('error');
      },
    });
  }

  setTab(t: KnightPageTab): void {
    if (this.tab() === t) return;
    this.tab.set(t);
    if (t === 'glossary') this.loadGlossary();
    this.syncQueryParam(this.selected());
  }

  private loadGlossary(): void {
    if (this.glossary()) return;
    this.glossaryState.set('loading');
    this.knight.getGlossary().subscribe({
      next: (g) => {
        this.glossary.set(g);
        this.glossaryState.set('ok');
      },
      error: () => this.glossaryState.set('error'),
    });
  }

  /** Setas esquerda/direita percorrem as abas em ciclo (padrão WAI-ARIA de tablist). */
  tabKey(ev: KeyboardEvent): void {
    if (ev.key !== 'ArrowLeft' && ev.key !== 'ArrowRight') return;
    ev.preventDefault();
    const order: KnightPageTab[] = ['overview', 'controls', 'coverage', 'glossary'];
    const step = ev.key === 'ArrowRight' ? 1 : order.length - 1;
    const next = order[(order.indexOf(this.tab()) + step) % order.length];
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
    // [AEGIS-KNIGHT-COVERAGE-04] No consolidado não há execução própria: o resumo é o da COMPOSIÇÃO — as execuções
    // reais das fontes incluídas, com cada objeto contado uma vez.
    const composition = a.sourceType === 'Consolidated'
      ? (a.sources ?? []).filter((s) => s.included && !!s.sourceRunId).map((s) => s.sourceRunId!)
      : null;
    const runId = composition ? 'composicao:' + composition.join(',') : a.id;
    if (this.summaryRun === runId && this.summaryState() !== 'error') return;
    this.summaryRun = runId;
    this.summary.set(null);
    if (composition && composition.length === 0) {
      this.summaryState.set('ok');
      return;
    }
    this.summaryState.set('loading');
    const request = composition ? this.knight.getCompositionAffectedSummary(composition) : this.knight.getAffectedSummary(runId);
    request.subscribe({
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
  private plansSeq = 0;
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
  readonly originSource = computed<KnightOriginSource | null>(() => {
    const t = this.assessment()?.sourceType;
    return !t || t === 'Consolidated' ? null : t;
  });

  /**
   * [AEGIS-KNIGHT-COVERAGE-04] A procedência de UM achado: a da avaliação exibida, ou — no relatório consolidado — a
   * fonte real do indicador. É por ela que a ação ativa é encontrada e que uma ação indicada é aceita.
   */
  private planSourceOf(indicatorId: string | null): KnightOriginSource | null {
    const a = this.assessment();
    if (!a || !indicatorId) return this.originSource();
    if (a.sourceType !== 'Consolidated') return this.originSource();
    const t = a.indicators.find((i) => i.indicatorId === indicatorId)?.sourceType;
    return !t || t === 'Consolidated' ? null : t;
  }
  readonly originMode = computed<KnightOriginMode | null>(() => {
    const a = this.assessment();
    return a ? (a.isDemo ? 'Demo' : 'Live') : null;
  });

  /** Ação ATIVA do achado aberto, NESTA procedência, se houver. */
  readonly activePlan = computed<ActionPlan | null>(() => {
    const id = this.selected();
    return id ? activePlanFor(this.activePlans(), id, this.planSourceOf(id), this.originMode()) : null;
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
    const consolidado = this.assessment()?.sourceType === 'Consolidated';
    if ((!fonte && !consolidado) || !modo) return;
    // No consolidado, as ações de TODAS as fontes reais desta procedência (coleta real × demonstração); cada achado
    // escolhe a da própria fonte em `activePlan`.
    // [AEGIS-KNIGHT-SCOPE-01] Troca rápida de aba: só a resposta da leitura mais recente grava a fila.
    const seq = ++this.plansSeq;
    this.remediation.list(consolidado ? { activeOnly: true, mode: modo } : { activeOnly: true, sourceType: fonte!, mode: modo }).subscribe({
      next: (plans) => {
        if (seq === this.plansSeq) this.activePlans.set(plans);
      },
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
        const recusa = pinnedPlanRejection(p, this.selected(), this.planSourceOf(this.selected()), this.originMode());
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
    this.openPublishPreview({ kind: 'run', runId });
  }

  /** Publica a avaliação aberta conferindo o histórico da prévia. */
  private publishRunWith(runId: string, history: FrozenPostureHistory, window: HistoryWindowRequest): void {
    const seq = ++this.publishSeq;
    this.publishing.set(true);
    this.publishNotice.set(null);
    this.error.set(null);
    this.publishedId.set(null);
    this.downloadError.set(null);
    this.history.publish({ type: 'Knight', runId, historyUntil: window.until, historyMonths: window.months, expectedHistoryFingerprint: history.basisFingerprint }).subscribe({
      next: (d) => {
        if (seq !== this.publishSeq) return this.stalePublication(d.summary.id);
        this.publishing.set(false);
        this.pubPreview.set(null);
        // A publicação só vale para a avaliação que continua aberta — outra resposta não oferece download.
        if (this.assessment()?.id === runId) this.publishedId.set(d.summary.id);
        this.publishNotice.set(
          `Relatório publicado a partir desta avaliação (${d.summary.id}). O conteúdo foi congelado: ` +
            'reexportá-lo depois traz exatamente o que foi publicado agora. HTML, CSV e PDF saem desta mesma fotografia.',
        );
      },
      error: (e: Error) => {
        if (seq === this.publishSeq) this.publishFailed(e);
      },
    });
  }

  // ---- [AEGIS-ASSESSMENT-VISUALS-01] Prévia do histórico congelado na publicação ----------------------------------

  /** Publicação em preparo: o que será publicado, a prévia do histórico e o período escolhido. */
  readonly pubPreview = signal<{
    target: PublishTarget;
    history: FrozenPostureHistory | null;
    loading: boolean;
    error: string | null;
    notice: string | null;
    window: HistoryWindowRequest;
  } | null>(null);
  private previewSeq = 0;
  /** Publicação ENVIADA vigente: uma resposta de envio anterior a uma troca do resultado exibido é tratada à parte. */
  private publishSeq = 0;

  /**
   * O que a tela exibe AGORA como publicável: a execução aberta ou, no consolidado, a execução exata de cada fonte
   * incluída (nunca os checkboxes isolados). Nulo enquanto a composição carrega ou sem nada publicável.
   */
  private currentPublishTarget(): PublishTarget | null {
    const a = this.assessment();
    if (!a || this.unfinishedView()) return null;
    if (!this.consolidatedMode()) return { kind: 'run', runId: a.id };
    if (!this.consolidatedPublishable()) return null;
    const selection = (a.sources ?? [])
      .filter((s) => s.included && s.sourceRunId)
      .map((s) => ({ source: s.source, runId: s.sourceRunId! }));
    return { kind: 'consolidated', selection };
  }

  private static sameTarget(a: PublishTarget, b: PublishTarget | null): boolean {
    if (!b || a.kind !== b.kind) return false;
    if (a.kind === 'run' || b.kind === 'run') return a.kind === 'run' && b.kind === 'run' && a.runId === b.runId;
    const key = (t: { selection: KnightConsolidatedSourceSelection[] }) =>
      t.selection.map((s) => `${s.source}:${s.runId}`).sort().join('|');
    return key(a) === key(b);
  }

  /**
   * [AEGIS-ASSESSMENT-VISUALS-01] O resultado exibido mudou (outra fonte, entrada/saída do consolidado, outra composição,
   * outra execução): a prévia aberta e qualquer resposta dela em voo deixam de valer. Uma publicação já ENVIADA pode
   * terminar no servidor, mas a resposta dela não mexe mais na tela (ver `stalePublication`).
   */
  private discardPublishPreview(): void {
    this.previewSeq++;
    this.pubPreview.set(null);
    if (this.publishing()) {
      this.publishSeq++;
      this.publishing.set(false);
    }
  }

  /** Resposta de uma publicação enviada antes da troca: dita como da seleção ANTERIOR, sem download nem prévia apagada. */
  private stalePublication(snapshotId: string): void {
    this.publishNotice.set(
      `Uma publicação enviada antes da troca do resultado exibido foi concluída (${snapshotId}). Ela corresponde ao ` +
        'resultado exibido anteriormente — não ao atual — e está no histórico auditável.',
    );
  }

  private openPublishPreview(target: PublishTarget): void {
    this.publishNotice.set(null);
    this.publishedId.set(null);
    this.error.set(null);
    this.pubPreview.set({ target, history: null, loading: true, error: null, notice: null, window: { months: 12, until: null } });
    this.loadPublishPreview();
  }

  /** (Re)lê a prévia; uma resposta de um pedido anterior (outro período ou outra avaliação) é descartada. */
  private loadPublishPreview(notice: string | null = null): void {
    const pp = this.pubPreview();
    if (!pp) return;
    const seq = ++this.previewSeq;
    this.pubPreview.set({ ...pp, history: null, loading: true, error: null, notice });
    const req = pp.target.kind === 'run'
      ? this.history.historyPreview(pp.target.runId, pp.window)
      : this.history.consolidatedHistoryPreview(pp.target.selection, pp.window);
    req.subscribe({
      next: (h) => {
        const cur = this.pubPreview();
        if (seq === this.previewSeq && cur) this.pubPreview.set({ ...cur, history: h, loading: false });
      },
      error: (e: Error) => {
        const cur = this.pubPreview();
        if (seq === this.previewSeq && cur) this.pubPreview.set({ ...cur, loading: false, error: e.message });
      },
    });
  }

  changePublishWindow(window: HistoryWindowRequest): void {
    const pp = this.pubPreview();
    if (!pp) return;
    this.pubPreview.set({ ...pp, window });
    this.loadPublishPreview();
  }

  confirmPublish(): void {
    const pp = this.pubPreview();
    if (!pp?.history || pp.loading || this.publishing()) return;
    // Confere no MÉTODO, não só no botão: a prévia só confirma o resultado que a tela continua exibindo.
    if (!AegisKnightComponent.sameTarget(pp.target, this.currentPublishTarget())) {
      this.discardPublishPreview();
      this.error.set('O resultado exibido mudou desde a prévia: nada foi publicado. Abra a prévia novamente para o resultado atual.');
      return;
    }
    if (pp.target.kind === 'run') this.publishRunWith(pp.target.runId, pp.history, pp.window);
    else this.publishConsolidatedWith(pp.target.selection, pp.history, pp.window);
  }

  cancelPublish(): void {
    this.previewSeq++;
    this.pubPreview.set(null);
  }

  /** A série mudou entre a prévia e a confirmação: nada foi publicado; a prévia nova aparece com o aviso. */
  private publishFailed(e: Error): void {
    this.publishing.set(false);
    if (e instanceof HistoryChangedError && this.pubPreview()) {
      this.loadPublishPreview(`${e.message} A prévia abaixo já mostra a série atual.`);
      return;
    }
    this.error.set(e.message);
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
        tab: this.tab() === 'overview' ? null : this.tab(),
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
    this.loadGlossary();
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
    this.tab.set(q.get('finding') || q.get('tab') === 'controls' ? 'controls'
      : q.get('tab') === 'glossary' ? 'glossary' : q.get('tab') === 'coverage' ? 'coverage' : 'overview');

    this.loading.set(true);
    this.error.set(null);
    this.loadCoverage();
    this.knight.getSources().subscribe({
      next: (s) => this.sources.set(s),
      error: () => this.sources.set(null), // fontes é secundário; não bloqueia a tela
    });

    // [AEGIS-KNIGHT-DURABLE-01] A leitura da última avaliação devolve DUAS coisas: o resultado concluído e,
    // à parte, a tentativa que não concluiu depois dele. Abrir por Id continua alcançando qualquer execução
    // — inclusive uma não concluída, que é mostrada com o estado real, sem virar "resultado".
    this.consultNotice.set(null);
    this.discardPublishPreview();
    this.viewSeq++; // uma aba ainda em voo pertence à leitura anterior
    this.viewLoading.set(false);
    this.viewError.set(null);

    const show = {
      next: ({ assessment: a, unfinishedAttempt }: KnightLatest) => {
        this.discardPublishPreview();
        this.assessment.set(a);
        this.unfinishedAttempt.set(unfinishedAttempt);
        this.loading.set(false);
        this.publishedId.set(null);
        this.loadSummary(a);
        this.applyDeepLink(a);
        this.applyFilterLink();
        // Só agora a PROCEDÊNCIA é conhecida — ler a fila antes traria ações de outra fonte/modo.
        this.reloadPlans();
        this.reloadPinnedPlan();
      },
      error: (e: Error) => {
        this.loading.set(false);
        this.unfinishedAttempt.set(null);
        if (requested) {
          this.discardPublishPreview();
          this.assessment.set(null);
          this.loadSummary(null);
          this.selected.set(null);
          this.linkNotice.set('A avaliação indicada no endereço não está disponível para este tenant.');
          return;
        }
        this.error.set(e.message);
      },
    };

    if (requested) {
      this.knight.getById(requested).pipe(map((a) => ({ assessment: a, unfinishedAttempt: null }) as KnightLatest)).subscribe(show);
    } else {
      // [AEGIS-KNIGHT-SCOPE-01] Sem um Id pedido, a leitura é POR FONTE (com o escopo salvo) e a tela abre numa aba: "Geral" por
      // padrão, a do endereço (`?fonte=`) quando pedida. A leitura antiga (`latest-state`) fica como recuo se o servidor for
      // anterior à leitura por fonte — nunca como o caminho normal, porque ela responde "a mais recente de qualquer fonte".
      this.knight.getLatestBySource().subscribe({
        next: ({ sources, scope }) => {
          this.latestBySource.set(sources);
          this.scope.set(scope ?? null);
          this.loading.set(false);
          this.selectView(this.initialView(), { initial: true });
        },
        error: () => {
          this.latestBySource.set([]);
          this.scope.set(null);
          this.knight.getLatestState().subscribe(show);
        },
      });
    }
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

  /**
   * [AEGIS-ASSESSMENT-VISUALS-01] Um gráfico dos Dashboards aponta para um RECORTE dos controles (?status=Exposed&service=…).
   * O antigo `?modo=consolidado` cai na aba "Geral", que já é a primeira. Valores desconhecidos são ignorados — nunca viram outro filtro.
   */
  private applyFilterLink(): void {
    const q = this.route.snapshot.queryParamMap;
    const statuses = ['findings', 'Passed', 'Exposed', 'Mitigated', 'NotEvaluated', 'Error', 'NotApplicable'];
    const severities = ['Critical', 'High', 'Medium', 'Low', 'Informational'];
    const status = q.get('status');
    const severity = q.get('severity');
    const f: Partial<KnightControlFilters> = {
      status: status && statuses.includes(status) ? (status as KnightControlFilters['status']) : '',
      severity: severity && severities.includes(severity) ? (severity as KnightControlFilters['severity']) : '',
      service: q.get('service') ?? '',
      domain: q.get('domain') ?? '',
      platform: q.get('platform') ?? '',
    };
    if (f.status || f.severity || f.service || f.domain || f.platform) this.openControls(f);
  }

  clearError(): void {
    this.error.set(null);
  }

  /**
   * [AEGIS-KNIGHT-COVERAGE-02] Qual fonte a tela abre por padrão: a que tem o resultado CONCLUÍDO mais
   * recente. Não há mistura nem soma — é uma escolha de qual avaliação mostrar primeiro, e as outras
   * continuam a um clique. Sem nenhum resultado concluído, vale a fonte com a tentativa mais recente, para
   * que a pendência apareça em vez de a tela ficar muda.
   */
  private pickSource(sources: readonly KnightSourceLatest[]): KnightSourceLatest | null {
    const instante = (s: KnightSourceLatest) =>
      Date.parse(s.assessment?.startedAt ?? s.unfinishedAttempt?.startedAt ?? '') || 0;
    const comResultado = sources.filter((s) => s.assessment !== null);
    const candidatas = comResultado.length > 0 ? comResultado : [...sources];
    return candidatas.sort((a, b) => instante(b) - instante(a))[0] ?? null;
  }

  /**
   * [AEGIS-KNIGHT-SCOPE-01] A aba em que a seção abre: a do endereço (`?fonte=`) quando existe; senão "Geral", desde que alguma
   * fonte do escopo tenha avaliação concluída; sem isso, a fonte com o resultado mais recente — para que a pendência apareça
   * em vez de um consolidado vazio.
   */
  private initialView(): KnightViewKey {
    const fonte = this.route.snapshot.queryParamMap.get('fonte');
    const tabs = this.viewTabs();
    if (fonte === 'geral') return 'geral';
    const pedida = fonte ? tabs.find((s) => s.slug === fonte || sourceSlug(s.source) === fonte) : undefined;
    if (pedida) return pedida.source;
    if (this.consolidatedSelection().length > 0) return 'geral';
    return this.pickSource(tabs)?.source ?? 'geral';
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
        this.cancelView();
        this.discardPublishPreview();
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
        this.cancelView();
        this.discardPublishPreview();
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

import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, ParamMap, Router, RouterLink } from '@angular/router';
import { AgentStateService } from '../services/agent-state.service';
import { PriorityService } from '../services/priority.service';
import { PriorityWorkspace } from '../models/priority.models';
import { postureLabel } from '../models/workspace.models';
import {
  findingSituation,
  findingTitle,
  scoreDisplay,
  severityLabel,
  statusLabel,
} from '../models/knight.models';
import {
  EXPOSURE_REACH_UNKNOWN,
  POSTURE_RECOMMENDATIONS_LABEL,
  categoryPt,
  recommendationLifecyclePt,
  recommendationReading,
  tierPt,
} from '../models/posture-exposure.models';
import { vulnerabilityReading } from '../models/vulnerability.models';
import {
  ActionPlan,
  KnightOriginMode,
  KnightOriginSource,
  actionResult,
  actionSituation,
  activePlanFor,
  originLabel,
  planLink,
  planSubject,
} from '../models/remediation.models';
import { RemediationService } from '../services/remediation.service';
import { CrossSourceSituationsComponent } from '../components/cross-source/cross-source-situations.component';
import {
  DevicePriorityCaseSelection,
  DevicePriorityComponent,
  DevicePriorityPolicyComponent,
} from '../components/device-priority/device-priority.component';
import {
  AssetDevicePriority,
  DEVICE_PRIORITY_BAND_FILTERS,
  DevicePriorityBand,
  DevicePriorityFilter,
  DevicePriorityList,
  bandCountsText,
  bandTone,
  casesText,
  devicePriorityListView,
  devicePriorityRangeText,
  devicePrioritySummaryText,
  pageAfterRefresh,
  detailOutsideListNote,
  dispositionText,
  tieText,
} from '../models/device-priority.models';
import {
  CROSS_SOURCE_HEADING,
  CROSS_SOURCE_STATE_FILTERS,
  CrossSourceFilter,
  CrossSourceSituationList,
  CrossSourceState,
  CrossSourceSituationItem,
  acquisitionSpanText,
  evidenceBasisLabel,
  crossSourceListView,
  crossSourceStateTone,
  crossSourceSummaryText,
  cveCountText,
  cvePreviewText,
  pageRangeText,
  ruleShortLabel,
  truncationNote,
} from '../models/cross-source.models';

/**
 * [AEGIS-MVP-PRIORITIES-01] Central de Prioridades — visão operacional que REÚNE, sem combinar num único
 * score, o AEGIS Score (controles NIST), a fila de recomendações de postura (Microsoft Secure Score) e a fila de
 * vulnerabilidades em ativos. Consome a superfície somente leitura `GET /api/v1/priorities` (read model composto).
 *
 * Invariante metodológica: postura (cobertura de controles), recomendações de postura (diferença de pontos da fonte) e
 * vulnerabilidades (fraquezas em ativos) são dimensões DISTINTAS — apresentadas em DUAS FILAS separadas, cada
 * uma com a ordenação determinística já testada no backend. Provider-neutral: cada fila mostra a própria fonte
 * real. A IA é consultiva (reutiliza o Auditor Virtual) — não cria/altera score, CVE, exploit, lifecycle,
 * finding, evidência ou estado de remediação. Honestidade: "Ainda não coletado" ≠ "coletado sem achados";
 * estados loading/erro/vazio explícitos; ZERO fallback demonstrativo.
 */
@Component({
  selector: 'app-priorities',
  standalone: true,
  imports: [RouterLink, CrossSourceSituationsComponent, DevicePriorityComponent, DevicePriorityPolicyComponent],
  template: `
    <section class="page">
      <header class="page-head">
        <div>
          <p class="page-eyebrow">Operação</p>
          <h1>Central de Prioridades</h1>
          <p class="page-desc">
            AEGIS Score (controles NIST CSF avaliados), recomendações de postura das fontes conectadas,
            vulnerabilidades identificadas em ativos e findings do assessment AEGIS KNIGHT são dimensões
            <strong>relacionadas, porém distintas</strong>. Elas <strong>não formam um único score</strong>: cada fila
            mantém a própria ordem e a própria fonte. A prioridade de tratamento vale só para vulnerabilidades em
            dispositivos — não é uma ordem universal entre identidades, documentação e dispositivos.
          </p>
          @if (data()) {
            <p class="page-meta">Leitura de {{ fmtDate(data()!.generatedAt) }}</p>
          }
        </div>
        <div class="page-actions">
          <!-- Sem nenhuma leitura, uma análise pressuporia evidência que não existe. -->
          <button
            type="button"
            class="primary"
            (click)="analyzeWithAi()"
            [disabled]="loading() || !!error() || !hasAnyReading()"
            [title]="hasAnyReading() ? '' : 'Disponível quando houver ao menos uma leitura ou avaliação'"
          >
            Analisar prioridades com IA
          </button>
          <button type="button" class="ghost" (click)="reload()" [disabled]="loading()">
            {{ loading() ? 'Carregando…' : 'Atualizar' }}
          </button>
        </div>
      </header>

      @if (loading()) {
        <div class="panel"><div class="state" role="status"><span class="spinner" aria-hidden="true"></span><p>Carregando prioridades…</p></div></div>
      } @else if (error()) {
        <div class="panel">
          <div class="state error">
            <p class="err">⚠ {{ error() }}</p>
            <button type="button" class="ghost" (click)="retry()">Tentar novamente</button>
          </div>
        </div>
      } @else if (data()) {
        <!-- ---------- Resumo (indicadores existentes, sem novo cálculo) ---------- -->
        <div class="cards">
          <!-- [AEGIS-LANGUAGE-STATES-01] Sem leitura, os cartões mostram "—" e o estado — nunca 0. -->
          <div class="card">
            <span class="metric-label">AEGIS Score · NIST CSF</span>
            <span class="metric-value" [class.is-na]="posture()!.percentage === null">
              {{ postureText() }}
            </span>
            <span class="metric-unit">
              {{ posture()!.evaluationState === 'Evaluated' ? 'avaliado' : 'não avaliado' }} · cobertura
              {{ num(posture()!.coveragePercentage) }}% dos controles elegíveis
            </span>
          </div>
          <div class="card">
            <span class="metric-label">{{ recommendationsLabel }}</span>
            @if (exposureReading().hasData) {
              <span class="metric-value">{{ exposures()!.summary.totalOpen }}</span>
              <span class="metric-unit">pendentes · fonte: {{ exposures()!.summary.sourceLabel }}</span>
            } @else {
              <span class="metric-value is-na">—</span>
              <span class="metric-unit">{{ readingShort(exposureReading().state) }} · {{ exposures()!.summary.sourceLabel }}</span>
            }
          </div>
          <div class="card">
            <span class="metric-label">Vulnerabilidades</span>
            @if (vulnReading().hasData) {
              <span class="metric-value">{{ vulns()!.summary.distinctCvesOpen }}</span>
              <span class="metric-unit">
                problema(s) distinto(s) em aberto · {{ vulns()!.summary.totalOpen }} ocorrência(s) em ativos
              </span>
            } @else {
              <span class="metric-value is-na">—</span>
              <span class="metric-unit">{{ readingShort(vulnReading().state) }}</span>
            }
          </div>
          <div class="card">
            <span class="metric-label">Ativos afetados</span>
            @if (vulnReading().hasData) {
              <span class="metric-value">{{ vulns()!.summary.affectedAssetsOpen }}</span>
              <span class="metric-unit">com vulnerabilidade em aberto</span>
            } @else {
              <span class="metric-value is-na">—</span>
              <span class="metric-unit">{{ readingShort(vulnReading().state) }}</span>
            }
          </div>
          <div class="card wide">
            <span class="metric-label">Coleta das fontes</span>
            <div class="collect">
              <span class="collect-row">
                <span class="collect-k">{{ recommendationsLabel }}</span>
                @if (exposures()!.summary.lastCollectedAt) {
                  <span class="collect-v"
                    [class.warn-text]="exposureReading().lastAttemptFailed || exposureReading().lastAttemptDegraded">
                    {{ fmtDate(exposures()!.summary.lastCollectedAt) }}
                    @if (exposureReading().lastAttemptFailed) { · tentativa recente falhou }
                    @else if (exposureReading().lastAttemptDegraded) { · coleta recente com restrições }
                  </span>
                } @else {
                  <span class="collect-v muted">{{ readingShort(exposureReading().state) }}</span>
                }
              </span>
              <span class="collect-row">
                <span class="collect-k">Vulnerabilidades</span>
                @if (vulnReading().hasData && vulns()!.summary.lastCollectedAt) {
                  <span class="collect-v">{{ fmtDate(vulns()!.summary.lastCollectedAt) }}</span>
                } @else {
                  <span class="collect-v muted">{{ readingShort(vulnReading().state) }}</span>
                }
              </span>
            </div>
          </div>
        </div>

        <!-- ---------- [AEGIS-MVP-PRODUCT-03] Sub-abas da Central ----------
             Achados e planos são leituras da MESMA central, não duas entradas de menu: acrescentar "Planos de
             ação" à navegação principal separaria o problema do trabalho que o endereça. -->
        <div class="tabbar" role="tablist">
          <button type="button" role="tab" [class.on]="tab() === 'achados'" [attr.aria-selected]="tab() === 'achados'"
            (click)="setTab('achados')">
            Achados
          </button>
          <button type="button" role="tab" [class.on]="tab() === 'planos'" [attr.aria-selected]="tab() === 'planos'"
            (click)="setTab('planos')">
            Planos de ação
            <!-- Contagem só com leitura válida: depois de uma falha, o número anterior não descreve a lista atual. -->
            @if (plans().length && !plansError()) { <span class="tab-count">{{ plans().length }}</span> }
          </button>
        </div>

        @if (tab() === 'planos') {
          <div class="queue">
            <div class="section-head">
              <div>
                <h2>Planos de ação</h2>
                <p class="section-desc">
                  Planos nascidos de achados de identidade (AEGIS KNIGHT) e de casos de vulnerabilidade em
                  dispositivos. <strong>A etapa do plano e o que foi comprovado são coisas distintas</strong>:
                  concluir um plano é decisão de gestão sobre o trabalho; só a validação registra o que foi
                  verificado — e, para casos de dispositivo, a verificação técnica automática ainda não está disponível.
                </p>
              </div>
            </div>
            <div class="panel">
              @if (plansError(); as pe) {
                <div class="state error">
                  <p class="err">⚠ {{ pe }}</p>
                  <button type="button" class="ghost" (click)="loadPlans()">Tentar novamente</button>
                </div>
              } @else if (plans().length === 0) {
                <div class="state empty">
                  <p class="muted">
                    Nenhum plano de ação registrado. Crie um a partir de um achado de identidade ou de um caso na
                    prioridade de tratamento de dispositivos.
                  </p>
                </div>
              } @else {
                <table class="data-table">
                  <thead>
                    <tr>
                      <th>Ação</th>
                      <th class="c-origin">Origem</th>
                      <th class="c-tier">Responsável</th>
                      <th class="c-when">Prazo</th>
                      <th class="c-state">Situação do plano</th>
                      <th>Validação registrada</th>
                      <th class="c-next">Próxima providência</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (p of plans(); track p.id) {
                      <tr class="row">
                        <td>
                          <!-- [AEGIS-MVP-PRODUCT-03] O link identifica O PLANO, não apenas o problema. Sem o
                               identificador da ação, abrir a linha de uma ação ENCERRADA levaria ao ciclo
                               ATIVO do mesmo problema — outro trabalho, com a mesma aparência de resposta.
                               [AEGIS-JOURNEY-01] Casos de dispositivo abrem o detalhe na Central com a CVE e o plano. -->
                          @if (link(p); as l) {
                            <a class="title link" [routerLink]="l.commands" [queryParams]="l.queryParams">{{ p.title }}</a>
                          } @else {
                            <strong class="title">{{ p.title }}</strong>
                          }
                          <span class="meta mono">{{ subject(p) }}</span>
                        </td>
                        <td class="c-origin">
                          <span class="meta" [class.demo]="p.originMode === 'Demo'">{{ origin(p) }}</span>
                        </td>
                        <td class="c-tier">{{ p.responsiblePerson || '—' }}</td>
                        <td class="c-when">
                          <span class="meta" [class.late]="p.isOverdue">{{ p.dueDate || 'sem prazo' }}</span>
                        </td>
                        <td class="c-state"><span class="badge" [class.bad]="p.isOverdue">{{ situation(p) }}</span></td>
                        <td><span class="meta">{{ result(p) }}</span></td>
                        <td class="c-when"><span class="meta">{{ p.nextStep }}</span></td>
                      </tr>
                    }
                  </tbody>
                </table>
              }
            </div>
          </div>
        }

        @if (tab() === 'achados') {
        <!-- ---------- Fila de recomendações de postura (Microsoft Secure Score) ---------- -->
        <div class="queue">
          <div class="section-head">
            <div>
              <h2>{{ recommendationsLabel }}</h2>
              <p class="section-desc">
                Recomendações pendentes na ordem da própria fonte (rank, depois maior diferença de pontos). Fonte:
                <strong>{{ exposures()!.summary.sourceLabel }}</strong>. A diferença de pontos não comprova, sozinha,
                configuração insegura nem exposição de ativo.
              </p>
            </div>
            <a class="linknav" routerLink="/exposures">Ver todas →</a>
          </div>
          @if (exposureReading().hasData && exposureReading().notice) {
            <p class="notice warn" role="status">{{ exposureReading().notice }}</p>
          }
          <div class="panel">
            @if (!exposureReading().hasData) {
              <div class="state empty">
                <p class="muted">{{ exposureReading().notice }}</p>
              </div>
            } @else if (exposures()!.top.length === 0) {
              <div class="state empty">
                <p class="muted">
                  Nenhuma recomendação pendente na última coleta da fonte — pontuação da fonte, não validação
                  independente das configurações.
                </p>
              </div>
            } @else {
              <table class="data-table">
                <thead>
                  <tr>
                    <th class="c-rank">Ordem (fonte)</th>
                    <th>Recomendação</th>
                    <th class="c-gap" title="Pontos que a fonte ainda não credita">Diferença</th>
                    <th class="c-tier">Nível (fonte)</th>
                    <th class="c-state">Estado</th>
                    <th class="c-when">Pendente na fonte</th>
                  </tr>
                </thead>
                <tbody>
                  @for (x of exposures()!.top; track x.id) {
                    <tr class="row">
                      <td class="c-rank">{{ x.sourceRank ?? '—' }}</td>
                      <td>
                        <strong class="title">{{ x.displayTitle }}</strong>
                        <span class="meta">{{ x.service || '—' }} · {{ cat(x.category) || '—' }} · {{ reachUnknown }}</span>
                        @if (x.whyItMatters) {
                          <span class="rem">{{ x.whyItMatters }}</span>
                        }
                        @if (x.firstAction) {
                          <span class="rem"><em>Ação:</em> {{ x.firstAction }}</span>
                        }
                      </td>
                      <td class="c-gap"><span class="gap">{{ num(x.gap) }}</span></td>
                      <td class="c-tier">{{ tier(x.tier) || '—' }}</td>
                      <td class="c-state">
                        <span class="badge" [class.ok]="x.lifecycleState === 'Resolved'">
                          {{ recommendationLifecycle(x.lifecycleState) }}
                        </span>
                      </td>
                      <td class="c-when">
                        <span class="meta">de {{ fmtDate(x.firstSeenAt) }}</span>
                        <span class="meta">até {{ fmtDate(x.lastSeenAt) }}</span>
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            }
          </div>
        </div>

        <!-- ---------- Fila de vulnerabilidades ---------- -->
        <div class="queue">
          <div class="section-head">
            <div>
              <h2>Vulnerabilidades em ativos</h2>
              <p class="section-desc">
                Vulnerabilidades identificadas pelas fontes, agrupadas por problema e ordenadas por exploit informado,
                severidade técnica (CVSS/EPSS) e criticidade cadastrada do ativo — não é risco de negócio calculado.
                @if (vulns()!.summary.sources.length > 0) {
                  Fontes: <strong>{{ sourceNames() }}</strong>.
                }
              </p>
            </div>
            <a class="linknav" routerLink="/vulnerabilities">Ver todas →</a>
          </div>
          @if (vulnReading().hasData && vulnReading().notice) {
            <p class="notice warn" role="status">{{ vulnReading().notice }}</p>
          }
          <div class="panel">
            @if (!vulnReading().hasData) {
              <div class="state empty">
                <p class="muted">{{ vulnReading().notice }}</p>
              </div>
            } @else if (vulns()!.top.length === 0) {
              <div class="state empty">
                <p class="muted">Nenhuma vulnerabilidade aberta na última leitura das fontes.</p>
              </div>
            } @else {
              <table class="data-table">
                <thead>
                  <tr>
                    <th>Problema</th>
                    <th>Por que importa</th>
                    <th class="c-cvss">Alcance</th>
                    <th>Exploit (fonte)</th>
                    <th>Fontes</th>
                  </tr>
                </thead>
                <tbody>
                  @for (g of vulns()!.top; track g.cveId) {
                    <tr class="row" [class.resolved]="g.effectiveLifecycle === 'Resolved'">
                      <td>
                        <strong class="title">{{ g.displayTitle }}</strong>
                        <span class="meta mono">{{ g.cveId }} · {{ g.severityLabel }}</span>
                        <span class="rem"><em>Ação:</em> {{ g.firstAction }}</span>
                      </td>
                      <td><span class="meta">{{ g.whyItMatters }}</span></td>
                      <td class="c-cvss">
                        <strong>{{ g.openAssetCount }}</strong>
                        <span class="meta">ativo(s) aberto(s)</span>
                      </td>
                      <td class="c-exploit">
                        @if (g.exploitVerified) {
                          <span class="badge bad">{{ g.exploitLabel }}</span>
                        } @else if (g.publicExploit) {
                          <span class="badge warn">{{ g.exploitLabel }}</span>
                        } @else {
                          <span class="dim">{{ g.exploitLabel }}</span>
                        }
                      </td>
                      <td class="c-src">
                        @for (p of g.providers; track p) {
                          <span class="badge src">{{ p }}</span>
                        } @empty {
                          <span class="dim">—</span>
                        }
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            }
          </div>
        </div>

        <!-- ---------- [AEGIS-RISK-PRIORITIZATION-01] Prioridade de tratamento — vulnerabilidades em dispositivos ----------
             Leitura PRÓPRIA (carga, falha e filtros independentes): uma falha aqui não derruba a Central e não é lida como
             "nenhuma prioridade". Um item por dispositivo, apontando o caso que determinou a posição. -->
        <div class="queue">
          <div class="section-head">
            <div>
              <h2>Prioridade de tratamento · vulnerabilidades em dispositivos</h2>
              <p class="section-desc">
                Um item por dispositivo, na ordem da política determinística e versionada do AEGIS: severidade técnica e
                exploit informados pela fonte, antecipados no máximo uma faixa por contexto comprovado (criticidade
                declarada ou situação entre fontes identificada). <strong>Não é avaliação completa dos riscos do
                ambiente nem probabilidade de incidente</strong>, e não altera scores nem as outras filas.
              </p>
            </div>
          </div>
          @if (dp(); as dl) {
            @if (dl.summary.truncationNote) {
              <p class="notice warn" role="status">{{ dl.summary.truncationNote }}</p>
            }
            @if (dl.summary.outOfScopeNote) {
              <p class="notice warn" role="status">{{ dl.summary.outOfScopeNote }}</p>
            }
            <!-- Completude da coleta, distinta da contagem: com casos na fila, diz se a ausência fora dela é conclusiva. -->
            @if (dl.summary.absenceNote && dl.summary.candidateAssets > 0) {
              <p class="notice warn" role="status">{{ dl.summary.absenceNote }}</p>
            }
          }
          @if (dpRefreshError(); as e) {
            <div class="notice warn" role="alert">
              {{ e }}
              <button type="button" class="ghost" (click)="loadDevicePriority({ background: true })">Tentar novamente</button>
            </div>
          }
          <div class="panel">
            @if (dpView().kind === 'loading') {
              <div class="state" role="status"><span class="spinner" aria-hidden="true"></span><p>Calculando a prioridade de tratamento…</p></div>
            } @else if (dpView().kind === 'error') {
              <div class="state error">
                <p class="err">⚠ {{ dpText() }}</p>
                <button type="button" class="ghost" (click)="loadDevicePriority()">Tentar novamente</button>
              </div>
            } @else if (dpView().kind === 'noSource' || dpView().kind === 'neverCollected' || dpView().kind === 'noCandidates'
                || dpView().kind === 'noCandidatesUnverified' || dpView().kind === 'onlyDispositions') {
              <div class="state empty"><p class="muted">{{ dpText() }}</p></div>
            } @else {
              @let dl = dp()!;
              <p class="xs-summary">{{ dpSummary() }}</p>
              <div class="xs-filters" role="group" aria-label="Filtrar por faixa">
                @for (f of dpBands; track f.label) {
                  <button type="button" class="filter-chip" [class.on]="dpFilter().band === f.value"
                    [attr.aria-pressed]="dpFilter().band === f.value" (click)="setDpBand(f.value)">{{ f.label }}</button>
                }
              </div>
              @if (dpView().kind === 'onlyInsufficient' || dpView().kind === 'filterEmpty') {
                <div class="state empty"><p class="muted">{{ dpText() }}</p></div>
              } @else {
                <table class="data-table dp-table">
                  <thead>
                    <tr>
                      <th class="c-rank">#</th>
                      <th>Dispositivo</th>
                      <th>Prioridade de tratamento</th>
                      <th>Por que nesta posição</th>
                      <th>Contexto</th>
                      <th></th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (it of dl.items; track it.assetId) {
                      <tr class="row">
                        <td class="c-rank">{{ it.position }}</td>
                        <td>
                          <strong class="title">{{ it.assetName }}</strong>
                          @if (it.nameIsPlaceholder) { <span class="meta">nome não coletado pela fonte</span> }
                          <span class="meta">{{ cases(it.prioritizableCases) }} · {{ bandCounts(it.casesByBand) }}</span>
                          @if (it.insufficientCases > 0) { <span class="meta">{{ it.insufficientCases }} sem severidade informada</span> }
                          @if (it.dispositionCases > 0) { <span class="meta">{{ it.dispositionCases }} com disposição registrada (fora da fila)</span> }
                        </td>
                        <td class="dp-band">
                          <span class="badge dp-{{ dpTone(it.band) }}">{{ it.bandLabel }}</span>
                          @if (tie(it.tiedAssets); as t) { <span class="meta dp-tie">{{ t }}</span> }
                        </td>
                        <td class="dp-why">
                          @if (it.determiningCase; as c) {
                            <span class="meta mono">{{ c.cveId }} · {{ c.severityLabel }} · {{ c.exploitLabel }}</span>
                          }
                          <span class="xs-rem">{{ it.positionReason }}</span>
                          <span class="xs-rem"><em>Próxima ação:</em> {{ it.nextAction }}</span>
                        </td>
                        <td class="dp-ctx">
                          <span class="meta">{{ it.deviceContextLabel }}</span>
                          <span class="meta">{{ it.criticalityLabel }}</span>
                          <span class="meta">{{ it.informationLabel }}</span>
                          @if (it.caveats.length) { <span class="meta warn-text">{{ it.caveats.length }} ressalva(s)</span> }
                        </td>
                        <td>
                          <button type="button" class="ghost xs-open" (click)="toggleDp(it.assetId, it.assetName)"
                            [attr.aria-expanded]="dpExpanded() === it.assetId">
                            {{ dpExpanded() === it.assetId ? 'Fechar' : 'Detalhe' }}
                          </button>
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
                <div class="pager">
                  <span class="meta range">{{ dpRange() }}</span>
                  @if (dpRefreshing()) { <span class="meta" role="status">Atualizando a fila…</span> }
                  <button type="button" class="ghost" (click)="goDp(dl.page - 1)" [disabled]="dl.page <= 1">‹ Anterior</button>
                  <button type="button" class="ghost" (click)="goDp(dl.page + 1)" [disabled]="dl.page * dl.pageSize >= dl.total">Próxima ›</button>
                </div>
              }
              @if (dpDispositions(); as dt) {
                <p class="meta xs-policy">Casos fora da fila por disposição registrada (evidência preservada): {{ dt }}.</p>
              }
              <div class="xs-policy"><app-device-priority-policy [policy]="dl.policy" /></div>
              <p class="meta xs-policy">{{ dl.scope }} Calculado em {{ fmtDate(dl.evaluatedAt) }}.</p>
            }
            <!-- O detalhe NÃO depende da linha da tabela nem do estado da fila: quando a releitura esvazia o filtro (o
                 dispositivo mudou de faixa), quando a fila está vazia ou quando o dispositivo foi aberto pelo endereço
                 (plano de um caso que saiu da fila), o detalhe continua aberto. Fora da cadeia de estados, a instância é
                 preservada — sem nova leitura nem nova declaração. [AEGIS-JOURNEY-01] Caso e plano vêm do endereço. -->
            @if (dpExpanded(); as id) {
              <div class="xs-detail-panel">
                <div class="dp-detail-head">
                  <p class="meta">Detalhe de <strong>{{ dpExpandedName() || 'dispositivo indicado no endereço' }}</strong></p>
                  <button type="button" class="ghost xs-open dp-detail-close" (click)="closeDp()">Fechar detalhe</button>
                </div>
                @if (dpDetailNote(); as note) {
                  <p class="meta" role="status">{{ note }}</p>
                }
                <app-device-priority
                  [assetId]="id"
                  [selectedCve]="dpCase()"
                  [pinnedPlanId]="dpPlan()"
                  (criticalityDeclared)="onDeviceCriticalityDeclared()"
                  (caseSelected)="onDpCaseSelected($event)"
                  (loaded)="onDpLoaded($event)"
                  (planChanged)="onDevicePlanChanged()" />
              </div>
            }
          </div>
        </div>

        <!-- ---------- [AEGIS-CROSS-SOURCE-01] Situações identificadas entre fontes ----------
             Leitura PRÓPRIA (carga, falha e filtros independentes das filas): uma falha aqui não derruba a Central, e
             uma falha da Central não é lida como "nenhuma situação". Não é fila de prioridade nem ranking de risco. -->
        <div class="queue">
          <div class="section-head">
            <div>
              <h2>{{ xsHeading }}</h2>
              <p class="section-desc">
                Condições informadas por fontes diferentes que coexistem no <strong>mesmo dispositivo</strong> —
                vulnerabilidades do Microsoft Defender × conformidade ou criptografia do Microsoft Intune — segundo regras
                explícitas e versionadas. <strong>Não é fila de prioridade nem ranking de risco</strong>: a lista segue a
                ordem alfabética do ativo e nada aqui altera scores ou as filas desta Central.
              </p>
            </div>
          </div>
          @if (xs(); as l) {
            @if (xsTruncation(); as tn) {
              <p class="notice warn" role="status">{{ tn }}</p>
            }
          }
          <div class="panel">
            @if (xsView().kind === 'loading') {
              <div class="state" role="status"><span class="spinner" aria-hidden="true"></span><p>Avaliando as regras entre fontes…</p></div>
            } @else if (xsView().kind === 'error') {
              <div class="state error">
                <p class="err">⚠ {{ xsText() }}</p>
                <button type="button" class="ghost" (click)="loadCrossSource()">Tentar novamente</button>
              </div>
            } @else if (xsView().kind === 'noSource' || xsView().kind === 'neverCollected' || xsView().kind === 'noPopulation') {
              <div class="state empty"><p class="muted">{{ xsText() }}</p></div>
            } @else {
              @let l = xs()!;
              <p class="xs-summary">{{ xsSummary() }}</p>
              <div class="xs-tally-wrap">
                <table class="data-table xs-tally">
                  <caption>Estado por regra — unidade: ativos</caption>
                  <thead>
                    <tr>
                      <th>Regra</th>
                      <th>Identificadas</th>
                      <th>com ressalvas</th>
                      <th>Combinação não identificada</th>
                      <th>com ressalvas</th>
                      <th>Evidência insuficiente</th>
                      <th>Vínculo em conflito</th>
                      <th>Registros contraditórios</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (t of l.summary.byRule; track t.ruleCode) {
                      <tr>
                        <td>
                          <strong class="title">{{ ruleShort(t.ruleCode) }}</strong>
                          <span class="meta mono">{{ t.ruleCode }} · v{{ t.ruleVersion }}</span>
                        </td>
                        <td>{{ t.identified }}</td>
                        <td>{{ t.identifiedWithCaveats }}</td>
                        <td>{{ t.notIdentified }}</td>
                        <td>{{ t.notIdentifiedWithCaveats }}</td>
                        <td>{{ t.insufficientEvidence }}</td>
                        <td>{{ t.linkConflict }}</td>
                        <td>{{ t.contradictoryEvidence }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>

              <div class="xs-filters" role="group" aria-label="Filtrar situações">
                @for (f of xsStates; track f.value) {
                  <button type="button" class="filter-chip" [class.on]="xsFilter().state === f.value"
                    [attr.aria-pressed]="xsFilter().state === f.value" (click)="setXsState(f.value)">{{ f.label }}</button>
                }
                <select class="xs-select" [value]="xsFilter().rule ?? ''" (change)="setXsRule($any($event.target).value)"
                  aria-label="Regra">
                  <option value="">Todas as regras</option>
                  @for (t of l.summary.byRule; track t.ruleCode) {
                    <option [value]="t.ruleCode">{{ ruleShort(t.ruleCode) }}</option>
                  }
                </select>
              </div>

              @if (xsEmpty()) {
                <!-- Zero conclusivo, inconclusivo ou misto (e o recorte do teto) vêm dos totais por estado. -->
                <div class="state empty"><p class="muted">{{ xsText() }}</p></div>
              } @else {
                <table class="data-table">
                  <thead>
                    <tr>
                      <th>Ativo</th>
                      <th>Regra</th>
                      <th>Situação</th>
                      <th>CVEs (distintas)</th>
                      <th class="c-when">Aquisições</th>
                      <th></th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (it of l.items; track it.assetId + it.ruleCode) {
                      <tr class="row">
                        <td>
                          <strong class="title">{{ it.assetName }}</strong>
                          @if (it.nameIsPlaceholder) { <span class="meta">nome não coletado pela fonte</span> }
                          <span class="xs-rem">{{ it.summary }}</span>
                        </td>
                        <td>
                          <span class="meta">{{ ruleShort(it.ruleCode) }}</span>
                          <span class="meta mono">{{ it.ruleCode }} · v{{ it.ruleVersion }}</span>
                        </td>
                        <td class="xs-state">
                          <span class="badge xs-{{ xsTone(it.state) }}">{{ it.stateLabel }}</span>
                          @if (it.caveats.length) { <span class="meta">{{ it.caveats.length }} ressalva(s)</span> }
                        </td>
                        <td class="xs-cves">
                          <strong>{{ cveCount(it.openCveCount) }}</strong>
                          <span class="meta mono">{{ cvePreview(it) }}</span>
                        </td>
                        <td class="c-when">
                          <span class="meta xs-basis">{{ basisLabel(it.evidenceBasis) }}</span>
                          <span class="meta">Defender: {{ spanText(it.vulnerabilityAcquisitions, it.evidenceBasis) }}</span>
                          <span class="meta">Intune: {{ spanText(it.deviceManagementAcquisitions, it.evidenceBasis) }}</span>
                        </td>
                        <td>
                          <button type="button" class="ghost xs-open" (click)="toggleXs(it.assetId + '|' + it.ruleCode)"
                            [attr.aria-expanded]="xsExpanded() === it.assetId + '|' + it.ruleCode">
                            {{ xsExpanded() === it.assetId + '|' + it.ruleCode ? 'Fechar' : 'Evidências' }}
                          </button>
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
                <div class="pager">
                  <span class="meta range">{{ xsRange() }}</span>
                  <button type="button" class="ghost" (click)="goXs(l.page - 1)" [disabled]="l.page <= 1">‹ Anterior</button>
                  <button type="button" class="ghost" (click)="goXs(l.page + 1)" [disabled]="l.page * l.pageSize >= l.total">Próxima ›</button>
                </div>
                <!-- O detalhe aberto fica ABAIXO da lista (não dentro de uma linha), para não alargar a tabela. -->
                @if (xsExpandedItem(); as ex) {
                  <div class="xs-detail-panel">
                    <p class="meta">Evidências de <strong>{{ ex.assetName }}</strong> · {{ ruleShort(ex.ruleCode) }}</p>
                    <app-cross-source-situations [assetId]="ex.assetId" />
                  </div>
                }
              }
              <p class="meta xs-policy">{{ l.policy.description }} Calculado em {{ fmtDate(l.evaluatedAt) }}.</p>
            }
          </div>
        </div>

        <!-- ---------- [AEGIS-MVP-PRODUCT-02] Fila de achados de identidade (AEGIS KNIGHT) ---------- -->
        <div class="queue">
          <div class="section-head">
            <div>
              <h2>Achados de identidade</h2>
              <p class="section-desc">
                Vereditos do <strong>AEGIS KNIGHT</strong> sobre a postura de identidade — régua e score
                próprios, <strong>não somados</strong> ao AEGIS Score nem às vulnerabilidades. Aqui aparecem os
                indicadores com veredito <strong>Exposto</strong> (a regra encontrou a condição na coleta); abrir um
                deles leva à mesma avaliação, com os objetos que sustentam o resultado.
              </p>
            </div>
            <a class="linknav" [routerLink]="['/identity']" [queryParams]="{ run: knight()!.runId }">
              Ver avaliação →
            </a>
          </div>
          <div class="panel">
            @if (!knight()!.runId) {
              <div class="state empty">
                <p class="muted">
                  Nenhuma avaliação de identidade executada ainda. Abra o <strong>AEGIS KNIGHT</strong> para
                  executar uma avaliação — ausência de avaliação não é ausência de risco.
                </p>
              </div>
            } @else {
              <div class="knight-bar">
                <span class="kb-item">
                  <span class="kb-k">Origem</span>
                  <span class="kb-v" [class.demo]="knight()!.isDemo">
                    {{ knight()!.isDemo ? 'Demonstração (dados sintéticos)' : knight()!.sourceLabel }}
                  </span>
                </span>
                <span class="kb-item">
                  <span class="kb-k">Score KNIGHT</span>
                  <span class="kb-v">{{ knightScore() }}<span class="kb-s"> · escala própria</span></span>
                </span>
                <span class="kb-item">
                  <span class="kb-k">Cobertura (indicadores avaliados)</span>
                  <span class="kb-v">{{ num(knight()!.coverage) }}%</span>
                </span>
                <span class="kb-item">
                  <span class="kb-k">Coleta</span>
                  <span class="kb-v">{{ fmtDate(knight()!.collectedAt) }}</span>
                </span>
              </div>

              @if (knight()!.top.length === 0) {
                <div class="state empty">
                  <p class="muted">
                    Nenhum achado de identidade exposto nesta avaliação.
                    @if (knight()!.notEvaluatedCount > 0) {
                      {{ knight()!.notEvaluatedCount }} indicador(es) seguem <strong>não avaliados</strong> —
                      isso reduz a cobertura e não é o mesmo que conformidade.
                    }
                  </p>
                </div>
              } @else {
                <table class="data-table">
                  <thead>
                    <tr>
                      <th>Achado</th>
                      <th class="c-tier">Severidade</th>
                      <th class="c-state">Situação</th>
                      <th class="c-cvss">Afetados</th>
                      <th class="c-when">Coleta</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (f of knight()!.top; track f.indicatorId) {
                      <tr class="row">
                        <td>
                          <!-- [AEGIS-MVP-PRODUCT-02] O parametro run viaja junto: sem ele, o clique abriria o mesmo
                               indicador da avaliação MAIS RECENTE, que pode não ser a que produziu esta linha. -->
                          <a
                            class="title link"
                            [routerLink]="['/identity']"
                            [queryParams]="{ finding: f.indicatorId, run: knight()!.runId }">
                            {{ findingTitle(f) }}
                          </a>
                          <span class="meta mono">{{ f.indicatorId }}</span>
                          <span class="rem">{{ findingSituation(f) }}</span>
                        </td>
                        <td class="c-tier"><span class="badge sev" [class]="f.severity">{{ sev(f.severity) }}</span></td>
                        <td class="c-state"><span class="badge">{{ st(f.status) }}</span></td>
                        <td class="c-cvss">
                          <strong>{{ f.affectedObjectCount }}</strong>
                          @if (f.hasAffectedDetail) {
                            <a
                              class="meta link"
                              [routerLink]="['/identity']"
                              [queryParams]="{ finding: f.indicatorId, run: knight()!.runId }">
                              ver afetados
                            </a>
                          } @else {
                            <span class="meta dim">detalhe não preservado</span>
                          }
                        </td>
                        <td class="c-when">
                          <span class="meta">{{ fmtDate(f.collectedAt) }}</span>
                          <!-- [AEGIS-MVP-PRODUCT-03] Com ação ativa, ABRE a existente; sem ela, cria. O link
                               leva à MESMA avaliação do achado, com a origem preservada. -->
                          <a
                            class="meta link"
                            [routerLink]="['/identity']"
                            [queryParams]="{ finding: f.indicatorId, run: knight()!.runId }">
                            {{ planFor(f.indicatorId) ? 'Abrir plano' : 'Criar plano de ação' }}
                          </a>
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              }
            }
          </div>
        </div>

        }

        <p class="foot-note">
          Fatos vêm das fontes de cada fila; a IA apenas explica, correlaciona e recomenda — não altera score,
          CVE, exploit, lifecycle, finding, evidência ou estado de remediação.
        </p>
      }
    </section>
  `,
  styles: [
    `
      /* Página, cabeçalho, abas, cartões, tabelas, filtros, badges, avisos, estados e botões: sistema visual global
         (styles.css). Aqui só o que é próprio da Central. */
      .card.wide {
        grid-column: span 2;
      }
      .collect {
        display: flex;
        flex-direction: column;
        gap: 6px;
        margin-top: var(--sp-1);
      }
      .collect-row {
        display: flex;
        justify-content: space-between;
        gap: var(--sp-3);
        font-size: var(--fs-sm);
      }
      .collect-k {
        color: var(--text-2);
      }
      .collect-v {
        font-weight: 500;
        text-align: right;
      }
      .collect-v.muted {
        font-weight: 400;
      }

      /* Cada fila: cabeçalho de seção + painel com a tabela (rolagem lateral local). */
      .queue {
        display: flex;
        flex-direction: column;
        gap: var(--sp-3);
      }
      .queue > .panel {
        padding: var(--sp-2) var(--sp-3) var(--sp-3);
        overflow-x: auto;
      }
      .dim {
        color: var(--muted);
      }
      .rem,
      .xs-rem {
        display: block;
        max-width: 60ch;
        margin-top: 3px;
        font-size: var(--fs-meta);
        line-height: 1.45;
        color: var(--text-2);
      }
      .rem em,
      .xs-rem em {
        font-style: normal;
        color: var(--muted);
      }
      .meta.late {
        color: var(--red-text);
      }
      .meta.demo,
      .kb-v.demo {
        color: var(--amber);
      }
      /* [AEGIS-JOURNEY-01] Aba de planos com duas origens: origem e providência QUEBRAM linha, nunca alargam a tabela. */
      .c-origin {
        min-width: 12rem;
        max-width: 18rem;
      }
      .c-origin .meta,
      .c-next .meta {
        white-space: normal;
        overflow-wrap: anywhere;
      }
      .c-next {
        min-width: 14rem;
      }
      .c-rank {
        width: 4rem;
        color: var(--text-2);
      }
      .c-cvss,
      .c-gap,
      .c-tier,
      .c-state,
      .c-when {
        white-space: nowrap;
      }
      .gap {
        font-weight: 600;
        color: var(--amber);
      }
      .badge.src {
        margin: 0 4px 4px 0;
      }
      .row.resolved {
        opacity: 0.6;
      }
      a.link {
        color: var(--cyan);
        text-decoration: none;
      }
      a.link:hover {
        text-decoration: underline;
        text-underline-offset: 3px;
      }
      .notice .ghost {
        margin-left: var(--sp-2);
      }
      .foot-note {
        max-width: 92ch;
        font-size: var(--fs-meta);
        line-height: var(--lh);
        color: var(--muted);
      }

      /* [AEGIS-MVP-PRODUCT-02] Barra de contexto da avaliação KNIGHT: origem, score PRÓPRIO e cobertura. */
      .knight-bar {
        display: flex;
        flex-wrap: wrap;
        gap: var(--sp-3) 28px;
        margin-bottom: var(--sp-1);
        padding: var(--sp-3) var(--sp-3) var(--sp-4);
        border-bottom: 1px solid var(--line-2);
      }
      .kb-item {
        display: flex;
        flex-direction: column;
        gap: 2px;
      }
      .kb-k {
        font-size: var(--fs-caps);
        font-weight: 600;
        letter-spacing: var(--tracking-caps);
        text-transform: uppercase;
        color: var(--muted);
      }
      .kb-v {
        font-size: var(--fs-body);
        font-weight: 500;
      }
      .kb-s {
        font-size: var(--fs-meta);
        font-weight: 400;
        color: var(--muted);
      }
      .badge.sev.Critical {
        color: var(--red-text);
      }
      .badge.sev.High {
        color: var(--amber);
      }

      /* [AEGIS-CROSS-SOURCE-01] Situações entre fontes · [AEGIS-RISK-PRIORITIZATION-01] prioridade de tratamento. */
      .xs-summary {
        margin: var(--sp-2) var(--sp-1) var(--sp-3);
        font-size: var(--fs-sm);
      }
      .xs-tally-wrap {
        overflow-x: auto;
        margin-bottom: var(--sp-3);
      }
      .xs-filters {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sp-2);
        margin: var(--sp-1) var(--sp-1) var(--sp-3);
      }
      .xs-select {
        min-height: var(--control-h-sm);
        font-size: var(--fs-meta);
      }
      .badge.xs-attention,
      .badge.dp-attention {
        color: var(--magenta);
      }
      .badge.xs-warn,
      .badge.dp-warn {
        color: var(--amber);
      }
      .badge.dp-info {
        color: var(--cyan);
      }
      /* Rótulos longos de faixa e situação: caixa normal e quebra de linha — legíveis e sem alargar a tabela. */
      .xs-state .badge,
      .dp-band .badge {
        max-width: 14rem;
        white-space: normal;
        text-transform: none;
        letter-spacing: 0;
        font-size: var(--fs-meta);
        line-height: 1.35;
      }
      .xs-open {
        white-space: nowrap;
      }
      .xs-policy {
        margin: var(--sp-3) var(--sp-1) var(--sp-1);
      }
      .xs-cves {
        min-width: 8rem;
      }
      .xs-cves .meta {
        white-space: normal;
        overflow-wrap: anywhere;
      }
      .xs-detail-panel {
        margin: var(--sp-3) var(--sp-1) 0;
        padding-top: var(--sp-3);
        border-top: 1px solid var(--line);
      }
      .dp-tie {
        max-width: 16rem;
        margin-top: var(--sp-1);
        white-space: normal;
      }
      .dp-ctx {
        width: 17rem;
      }
      .dp-ctx .meta {
        margin-bottom: 2px;
        white-space: normal;
      }
      .dp-why {
        min-width: 26rem;
      }
      .dp-why .xs-rem {
        max-width: none;
      }
      .dp-why .meta.mono {
        white-space: normal;
        overflow-wrap: anywhere;
      }
      .dp-detail-head {
        display: flex;
        justify-content: space-between;
        align-items: center;
        gap: var(--sp-3);
        margin-bottom: var(--sp-2);
      }

      @media (max-width: 720px) {
        .card.wide {
          grid-column: auto;
        }
        .dp-why {
          min-width: 18rem;
        }
      }
    `,
  ],
})
export class PrioritiesComponent {
  private readonly api = inject(PriorityService);
  private readonly agent = inject(AgentStateService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  private readonly remediation = inject(RemediationService);

  /**
   * Sub-aba ativa: os achados (as filas) ou os planos que os endereçam. [AEGIS-JOURNEY-01] Acompanhada no endereço,
   * como a faixa, a página, o dispositivo aberto, a CVE e o plano — abrir o link ou recarregar volta ao mesmo contexto.
   */
  protected readonly tab = signal<'achados' | 'planos'>('achados');

  /**
   * Planos de ação do tenant — achados do KNIGHT e casos de dispositivo. Uma leitura ÚNICA serve às duas sub-abas e ao
   * botão de cada achado — a mesma autoridade que a tela do KNIGHT usa, de modo que os dois lugares não discordem sobre
   * "existe ação ativa?".
   */
  protected readonly plans = signal<ActionPlan[]>([]);
  protected readonly plansError = signal<string | null>(null);

  protected readonly situation = actionSituation;
  protected readonly result = actionResult;
  protected readonly origin = originLabel;
  protected readonly link = planLink;
  protected readonly subject = planSubject;

  protected setTab(tab: 'achados' | 'planos'): void {
    this.tab.set(tab);
    this.syncUrl();
  }

  /** Ação ATIVA de um achado — decide entre "Criar plano de ação" e "Abrir plano". */
  /**
   * A ação ATIVA de um achado NESTA procedência. A fonte e o modo da avaliação exibida entram na busca
   * porque o indicador sozinho não identifica o problema: sem esse recorte, uma ação nascida do cenário de
   * demonstração responderia por um achado real — e ainda bloquearia a criação da ação real.
   */
  protected planFor(indicatorId: string): ActionPlan | null {
    const k = this.knight();
    if (!k) return null;
    const fonte = (k.sourceType as KnightOriginSource | null) ?? null;
    const modo: KnightOriginMode | null = k.isDemo ? 'Demo' : fonte ? 'Live' : null;
    return activePlanFor(this.plans(), indicatorId, fonte, modo);
  }

  protected readonly data = signal<PriorityWorkspace | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly posture = computed(() => this.data()?.posture ?? null);
  protected readonly exposures = computed(() => this.data()?.configurationExposures ?? null);
  protected readonly vulns = computed(() => this.data()?.vulnerabilities ?? null);
  /**
   * [AEGIS-MVP-PRODUCT-02] Fila de achados de identidade. A Central apenas EXIBE o que a autoridade KNIGHT já
   * decidiu — severidade, veredito, evidência e contagem não são recalculados aqui, e o score KNIGHT não é
   * combinado com postura ou vulnerabilidades.
   */
  protected readonly knight = computed(() => this.data()?.identityFindings ?? null);
  // Mesma camada de apresentação da tela do KNIGHT: as duas superfícies não podem chamar o mesmo achado por
  // nomes diferentes, nem uma afirmar mais forte do que a outra sobre a mesma coleta.
  protected readonly findingTitle = findingTitle;
  protected readonly findingSituation = findingSituation;

  /** Score do KNIGHT para exibição: "—" quando null (sem avaliação), nunca "0". */
  protected readonly knightScore = computed(() => scoreDisplay(this.knight()?.score ?? null));

  protected readonly sev = severityLabel;
  protected readonly st = statusLabel;

  protected readonly postureText = computed(() => postureLabel(this.posture()?.percentage ?? null));

  /**
   * [AEGIS-LANGUAGE-STATES-01] O que a Central pode afirmar sobre cada fonte — a MESMA derivação das telas de
   * Recomendações de postura e Vulnerabilidades (e do backend): sem integração × sem coleta × leitura
   * disponível, com a ressalva de falha recente ou de escopo parcial junto dos números.
   */
  protected readonly exposureReading = computed(() => recommendationReading(this.exposures()?.summary));
  protected readonly vulnReading = computed(() => vulnerabilityReading(this.vulns()?.summary));

  /** Há alguma leitura ou avaliação que sustente uma análise? Sem nenhuma, a IA não é oferecida. */
  protected readonly hasAnyReading = computed(
    () =>
      this.exposureReading().hasData ||
      this.vulnReading().hasData ||
      this.posture()?.evaluationState === 'Evaluated' ||
      !!this.knight()?.runId,
  );

  protected readonly recommendationsLabel = POSTURE_RECOMMENDATIONS_LABEL;
  protected readonly recommendationLifecycle = recommendationLifecyclePt;

  /** Rótulo curto de um estado sem leitura, para cartões e linha de coleta. */
  protected readonly readingShort = (state: string): string => {
    switch (state) {
      case 'NotConfigured':
      case 'NoSource':
        return 'Sem integração';
      case 'FailedBeforeFirstCollection':
        return 'Coleta falhou';
      default:
        return 'Ainda não coletado';
    }
  };

  /** Fontes distintas de vulnerabilidade configuradas (provider-neutral: nomes reais, não hardcoded). */
  protected readonly sourceNames = computed(() =>
    (this.vulns()?.summary.sources ?? []).map((s) => s.provider).join(', '));

  // [AEGIS-MVP-LANGUAGE-02 §5] A narrativa de vulnerabilidade (título/porquê/exploit/1ª ação/severidade) é
  // AUTORIDADE do backend e chega pronta em cada VulnerabilityGroup — o frontend NÃO recompõe. Restam helpers de
  // APRESENTAÇÃO puros que traduzem enums da fonte de EXPOSIÇÃO (categoria/tier) que não têm rótulo pronto.
  protected readonly cat = categoryPt;
  protected readonly tier = tierPt;
  protected readonly reachUnknown = EXPOSURE_REACH_UNKNOWN;

  // ---- [AEGIS-RISK-PRIORITIZATION-01] Prioridade de tratamento em dispositivos (leitura própria, separada das filas) ----
  protected readonly dpBands = DEVICE_PRIORITY_BAND_FILTERS;
  protected readonly dp = signal<DevicePriorityList | null>(null);
  protected readonly dpLoading = signal(false);
  protected readonly dpError = signal<string | null>(null);
  protected readonly dpFilter = signal<DevicePriorityFilter>({ band: null, page: 1, pageSize: 10 });
  protected readonly dpExpanded = signal<string | null>(null);
  protected readonly dpView = computed(() => devicePriorityListView(this.dp(), this.dpLoading(), this.dpError()));
  protected readonly dpText = computed(() => {
    const v = this.dpView();
    return 'text' in v ? v.text : '';
  });
  protected readonly dpSummary = computed(() => (this.dp() ? devicePrioritySummaryText(this.dp()!.summary) : ''));
  protected readonly dpRange = computed(() => (this.dp() ? devicePriorityRangeText(this.dp()!) : ''));
  protected readonly dpDispositions = computed(() => (this.dp() ? dispositionText(this.dp()!.summary.dispositions) : null));
  protected readonly dpTone = bandTone;
  protected readonly bandCounts = bandCountsText;
  protected readonly cases = casesText;
  protected readonly tie = tieText;

  protected readonly dpRefreshing = signal(false);
  protected readonly dpRefreshError = signal<string | null>(null);
  /** Nome do dispositivo do detalhe aberto — guardado na abertura, porque a linha pode sair da lista depois da releitura. */
  protected readonly dpExpandedName = signal<string | null>(null);
  /** [AEGIS-JOURNEY-01] CVE e plano abertos no detalhe — acompanhados no endereço. */
  protected readonly dpCase = signal<string | null>(null);
  protected readonly dpPlan = signal<string | null>(null);
  /** O detalhe foi aberto pelo endereço (visão geral ou lista de planos), não por uma linha desta página. */
  private readonly dpFromLink = signal(false);
  /**
   * Por que o detalhe aberto não está na lista exibida; nula com a linha visível. Aberto pelo endereço, o dispositivo
   * pode simplesmente não estar nesta página — ou ter saído da fila (plano de um caso já sem prioridade atual).
   */
  protected readonly dpDetailNote = computed(() => {
    const l = this.dp();
    const id = this.dpExpanded();
    if (id && l && this.dpFromLink() && !l.items.some((i) => i.assetId === id))
      return (
        'Este dispositivo não está nesta página da fila: pode estar em outra página, em outra faixa ou fora da fila. ' +
        'O detalhe abaixo é a leitura atual dele.'
      );
    return detailOutsideListNote(l, id);
  });
  /** Só a resposta da ÚLTIMA leitura pedida é aplicada — filtro, página ou atualização depois de uma declaração. */
  private dpSeq = 0;

  /**
   * @param opts.background releitura sem apagar a fila nem fechar o detalhe (depois de uma declaração confirmada): a
   * tabela antiga fica visível com "Atualizando…", e uma falha diz que a fila pode estar desatualizada.
   * @param opts.keepDetail leitura inicial ou vinda do endereço: o detalhe indicado continua aberto.
   */
  protected loadDevicePriority(opts: { background?: boolean; keepDetail?: boolean } = {}): void {
    const seq = ++this.dpSeq;
    if (opts.background) {
      this.dpRefreshing.set(true);
    } else {
      this.dpLoading.set(true);
      if (!opts.keepDetail) this.closeDetailState();
    }
    this.dpError.set(null);
    this.dpRefreshError.set(null);
    this.api.devices(this.dpFilter()).subscribe({
      next: (list) => {
        if (seq !== this.dpSeq) return;   // resposta tardia de um filtro ou página anterior
        const page = opts.background ? pageAfterRefresh(list) : null;
        if (page !== null) {
          // A página ficou vazia porque a ordem ou a faixa mudou: uma correção para a última página válida.
          this.dpFilter.update((f) => ({ ...f, page }));
          this.loadDevicePriority(opts);
          return;
        }
        this.dp.set(list);
        this.dpLoading.set(false);
        this.dpRefreshing.set(false);
      },
      error: (err: Error) => {
        if (seq !== this.dpSeq) return;
        this.dpLoading.set(false);
        this.dpRefreshing.set(false);
        if (opts.background && this.dp()) {
          this.dpRefreshError.set(
            'A declaração foi registrada, mas a fila não pôde ser atualizada agora — posição, faixas e totais exibidos ' +
              'podem estar desatualizados.',
          );
          return;
        }
        this.dp.set(null);
        this.dpError.set(err.message);
      },
    });
  }

  /** Declaração CONFIRMADA pelo servidor no detalhe aberto: a fila é relida preservando filtro, página e detalhe. */
  protected onDeviceCriticalityDeclared(): void {
    this.loadDevicePriority({ background: true });
  }

  protected setDpBand(band: DevicePriorityBand | null): void {
    this.dpFilter.update((f) => ({ ...f, band, page: 1 }));
    this.loadDevicePriority();
    this.syncUrl();
  }

  protected goDp(page: number): void {
    if (page < 1) return;
    this.dpFilter.update((f) => ({ ...f, page }));
    this.loadDevicePriority();
    this.syncUrl();
  }

  protected toggleDp(assetId: string, name: string): void {
    const open = this.dpExpanded() !== assetId;
    if (open) {
      this.dpExpanded.set(assetId);
      this.dpExpandedName.set(name);
      this.dpCase.set(null);
      this.dpPlan.set(null);
      this.dpFromLink.set(false);
    } else {
      this.closeDetailState();
    }
    this.syncUrl();
  }

  /** Fecha o detalhe mesmo quando a linha dele já não está na tabela (filtro esvaziado). Não relê nada. */
  protected closeDp(): void {
    this.closeDetailState();
    this.syncUrl();
  }

  private closeDetailState(): void {
    this.dpExpanded.set(null);
    this.dpExpandedName.set(null);
    this.dpCase.set(null);
    this.dpPlan.set(null);
    this.dpFromLink.set(false);
  }

  /** [AEGIS-JOURNEY-01] Caso (e plano) escolhido no detalhe: o endereço acompanha, para recarregar no mesmo ponto. */
  protected onDpCaseSelected(sel: DevicePriorityCaseSelection): void {
    this.dpCase.set(sel.cveId);
    this.dpPlan.set(sel.planId);
    this.syncUrl();
  }

  /** O nome do dispositivo aberto pelo endereço só é conhecido quando o detalhe lê a prioridade dele. */
  protected onDpLoaded(d: AssetDevicePriority): void {
    if (d.assetId === this.dpExpanded()) this.dpExpandedName.set(d.assetName);
  }

  /** Um plano de caso de dispositivo mudou: a lista de planos é relida; a prioridade não (plano não a altera). */
  protected onDevicePlanChanged(): void {
    this.loadPlans();
  }

  /**
   * O endereço é a fonte do contexto da Central: aba, faixa, página, dispositivo aberto, CVE e plano. Recarregar a
   * página, abrir o link direto ou voltar no navegador leva ao mesmo ponto. Parâmetro desconhecido é ignorado.
   */
  private applyUrl(q: ParamMap): { filterChanged: boolean } {
    this.tab.set(q.get('tab') === 'planos' ? 'planos' : 'achados');
    const bandParam = q.get('band');
    const band = DEVICE_PRIORITY_BAND_FILTERS.some((b) => b.value !== null && b.value === bandParam)
      ? (bandParam as DevicePriorityBand)
      : null;
    const pageNum = Number(q.get('page'));
    const page = Number.isInteger(pageNum) && pageNum > 1 ? pageNum : 1;
    const f = this.dpFilter();
    const filterChanged = f.band !== band || f.page !== page;
    if (filterChanged) this.dpFilter.set({ ...f, band, page });

    const device = q.get('device');
    if (device !== this.dpExpanded()) {
      this.dpExpandedName.set(null);
      this.dpFromLink.set(!!device);
    }
    this.dpExpanded.set(device);
    this.dpCase.set(device ? q.get('cve') : null);
    this.dpPlan.set(device ? q.get('plan') : null);
    return { filterChanged };
  }

  private syncUrl(): void {
    const f = this.dpFilter();
    const device = this.dpExpanded();
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: {
        tab: this.tab() === 'planos' ? 'planos' : null,
        band: f.band,
        page: f.page > 1 ? f.page : null,
        device,
        cve: device ? this.dpCase() : null,
        plan: device ? this.dpPlan() : null,
      },
      replaceUrl: true,
    });
  }

  // ---- [AEGIS-CROSS-SOURCE-01] Situações identificadas entre fontes (leitura própria, separada das filas) ----
  protected readonly xsHeading = CROSS_SOURCE_HEADING;
  protected readonly xsStates = CROSS_SOURCE_STATE_FILTERS;
  protected readonly xs = signal<CrossSourceSituationList | null>(null);
  protected readonly xsLoading = signal(false);
  protected readonly xsError = signal<string | null>(null);
  protected readonly xsFilter = signal<CrossSourceFilter>({ state: 'identified', rule: null, page: 1, pageSize: 10 });
  protected readonly xsExpanded = signal<string | null>(null);
  protected readonly xsView = computed(() => crossSourceListView(this.xs(), this.xsLoading(), this.xsError()));
  protected readonly xsText = computed(() => {
    const v = this.xsView();
    return 'text' in v ? v.text : '';
  });
  protected readonly xsEmpty = computed(() =>
    ['zeroConclusive', 'zeroInconclusive', 'zeroMixed', 'filterEmpty'].includes(this.xsView().kind));
  protected readonly basisLabel = evidenceBasisLabel;
  protected spanText(
    span: CrossSourceSituationItem['vulnerabilityAcquisitions'], basis: CrossSourceSituationItem['evidenceBasis'],
  ): string {
    return acquisitionSpanText(span, (d) => this.fmtDate(d), basis);
  }
  protected readonly xsSummary = computed(() => (this.xs() ? crossSourceSummaryText(this.xs()!.summary) : ''));
  protected readonly xsTruncation = computed(() => (this.xs() ? truncationNote(this.xs()!.summary) : null));
  protected readonly xsRange = computed(() => {
    const l = this.xs();
    return l ? pageRangeText(l.total, l.page, l.pageSize, l.total === 1 ? 'situação' : 'situações') : '';
  });
  protected readonly xsExpandedItem = computed(() => {
    const key = this.xsExpanded();
    return key ? (this.xs()?.items.find((i) => `${i.assetId}|${i.ruleCode}` === key) ?? null) : null;
  });
  protected readonly xsTone = crossSourceStateTone;
  protected readonly ruleShort = ruleShortLabel;
  protected readonly cveCount = cveCountText;
  protected readonly cvePreview = cvePreviewText;

  protected loadCrossSource(): void {
    this.xsLoading.set(true);
    this.xsError.set(null);
    this.xsExpanded.set(null);
    this.api.correlations(this.xsFilter()).subscribe({
      next: (list) => {
        this.xs.set(list);
        this.xsLoading.set(false);
      },
      error: (err: Error) => {
        this.xs.set(null);
        this.xsError.set(err.message);
        this.xsLoading.set(false);
      },
    });
  }

  protected setXsState(state: CrossSourceState): void {
    this.xsFilter.update((f) => ({ ...f, state, page: 1 }));
    this.loadCrossSource();
  }

  protected setXsRule(rule: string): void {
    this.xsFilter.update((f) => ({ ...f, rule: rule || null, page: 1 }));
    this.loadCrossSource();
  }

  protected goXs(page: number): void {
    if (page < 1) return;
    this.xsFilter.update((f) => ({ ...f, page }));
    this.loadCrossSource();
  }

  protected toggleXs(key: string): void {
    this.xsExpanded.set(this.xsExpanded() === key ? null : key);
  }

  constructor() {
    // [AEGIS-JOURNEY-01] O endereço decide o contexto: a primeira leitura já nasce com aba, filtro, página, dispositivo,
    // CVE e plano do link. Mudanças posteriores (voltar no navegador, link de um plano nesta mesma tela) reaplicam o
    // contexto; a fila só é relida quando filtro ou página mudaram.
    let first = true;
    this.route.queryParamMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((q) => {
      const { filterChanged } = this.applyUrl(q);
      if (first) {
        first = false;
        this.load();
        return;
      }
      if (filterChanged) this.loadDevicePriority({ keepDetail: true });
    });
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.loadPlans();
    this.loadDevicePriority({ keepDetail: true });
    this.loadCrossSource();
    this.api.get().subscribe({
      next: (workspace) => {
        this.data.set(workspace);
        this.loading.set(false);
      },
      error: (err: Error) => {
        this.data.set(null);
        this.error.set(err.message);
        this.loading.set(false);
      },
    });
  }

  /**
   * Lê os planos. Uma falha aqui NÃO derruba a Central: as filas de achados continuam válidas, e a sub-aba de
   * planos mostra o erro em vez de uma lista vazia que se leria como "não há ação alguma".
   */
  protected loadPlans(): void {
    this.plansError.set(null);
    // [AEGIS-JOURNEY-01] As duas origens de remediação — achados do KNIGHT e casos de dispositivo; nunca o registro de riscos.
    this.remediation.list({ origin: 'all' }).subscribe({
      next: (plans) => this.plans.set(plans),
      error: (err: Error) => this.plansError.set(err.message),
    });
  }

  protected reload(): void {
    this.load();
  }

  protected retry(): void {
    this.load();
  }

  /**
   * Reutiliza o Auditor Virtual GLOBAL, semeando UMA pergunta contextual COMBINADA. O backend já inclui, no
   * contexto tenant-scoped, a postura, as principais exposições e as principais vulnerabilidades abertas — e
   * sabe que os fatos das fontes são AUTORITATIVOS e a resposta é CONSULTIVA. Nenhum código de IA é alterado.
   */
  protected analyzeWithAi(): void {
    this.agent.requestAudit(
      'Analise em conjunto o AEGIS Score (controles NIST CSF avaliados), as recomendações de postura pendentes ' +
        'das fontes conectadas e as vulnerabilidades identificadas nos ativos. Aponte relações apenas quando ' +
        'houver evidência no contexto e proponha uma sequência de investigação e remediação. Não combine as ' +
        'escalas, não trate diferença de pontos como exposição confirmada nem CVSS como risco de negócio, e diga ' +
        'quando uma fonte não tiver leitura. Preserve separadamente fatos das fontes, inferências e ' +
        'recomendações. Não crie nem altere score, CVE, exploit, lifecycle, finding, evidência ou estado de ' +
        'remediação.',
    );
  }

  protected num(n: number): string {
    return Number.isInteger(n) ? String(n) : n.toFixed(1);
  }

  protected pctEpss(n: number): string {
    return `${Math.round(n * 100)}%`;
  }

  protected fmtDate(iso: string | null | undefined): string {
    if (!iso) return '—';
    const d = new Date(iso);
    return isNaN(d.getTime()) ? '—' : d.toLocaleString('pt-BR');
  }
}

import { Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
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
} from '../models/remediation.models';
import { RemediationService } from '../services/remediation.service';
import { CrossSourceSituationsComponent } from '../components/cross-source/cross-source-situations.component';
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
  imports: [RouterLink, CrossSourceSituationsComponent],
  template: `
    <section class="page">
      <header class="page-head">
        <div>
          <h1>Central de Prioridades</h1>
          <p class="sub">
            AEGIS Score (controles NIST CSF avaliados), recomendações de postura do Microsoft Secure Score,
            vulnerabilidades identificadas em ativos e achados de identidade do AEGIS KNIGHT são dimensões
            <strong>relacionadas, porém distintas</strong>. Elas <strong>não formam um único score</strong> nem uma
            prioridade de risco calculada: cada fila mantém a própria ordem e a própria fonte.
          </p>
          @if (data()) {
            <p class="freshness">Leitura de {{ fmtDate(data()!.generatedAt) }}</p>
          }
        </div>
        <div class="head-actions">
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
        <div class="panel"><p class="muted">Carregando prioridades…</p></div>
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
            <span class="card-label">AEGIS Score · NIST CSF</span>
            <span class="card-value" [class.muted]="posture()!.percentage === null">
              {{ postureText() }}
            </span>
            <span class="card-meta">
              {{ posture()!.evaluationState === 'Evaluated' ? 'avaliado' : 'não avaliado' }} · cobertura
              {{ num(posture()!.coveragePercentage) }}% dos controles elegíveis
            </span>
          </div>
          <div class="card">
            <span class="card-label">{{ recommendationsLabel }}</span>
            @if (exposureReading().hasData) {
              <span class="card-value">{{ exposures()!.summary.totalOpen }}</span>
              <span class="card-meta">pendentes · fonte: {{ exposures()!.summary.sourceLabel }}</span>
            } @else {
              <span class="card-value muted">—</span>
              <span class="card-meta">{{ readingShort(exposureReading().state) }} · {{ exposures()!.summary.sourceLabel }}</span>
            }
          </div>
          <div class="card">
            <span class="card-label">Vulnerabilidades</span>
            @if (vulnReading().hasData) {
              <span class="card-value">{{ vulns()!.summary.distinctCvesOpen }}</span>
              <span class="card-meta">
                problema(s) distinto(s) em aberto · {{ vulns()!.summary.totalOpen }} ocorrência(s) em ativos
              </span>
            } @else {
              <span class="card-value muted">—</span>
              <span class="card-meta">{{ readingShort(vulnReading().state) }}</span>
            }
          </div>
          <div class="card">
            <span class="card-label">Ativos afetados</span>
            @if (vulnReading().hasData) {
              <span class="card-value">{{ vulns()!.summary.affectedAssetsOpen }}</span>
              <span class="card-meta">com vulnerabilidade em aberto</span>
            } @else {
              <span class="card-value muted">—</span>
              <span class="card-meta">{{ readingShort(vulnReading().state) }}</span>
            }
          </div>
          <div class="card wide">
            <span class="card-label">Coleta das fontes</span>
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
        <div class="subtabs" role="tablist">
          <button type="button" role="tab" [class.on]="tab() === 'achados'" (click)="tab.set('achados')">
            Achados
          </button>
          <button type="button" role="tab" [class.on]="tab() === 'planos'" (click)="tab.set('planos')">
            Planos de ação
            @if (plans().length) { <span class="n">{{ plans().length }}</span> }
          </button>
        </div>

        @if (tab() === 'planos') {
          <div class="queue">
            <div class="queue-head">
              <div>
                <h2>Planos de ação</h2>
                <p class="queue-sub">
                  Ações nascidas de achados de identidade. <strong>A etapa do plano e o resultado no achado
                  são coisas distintas</strong>: encerrar uma ação é uma decisão de gestão sobre o trabalho;
                  o que aconteceu com a exposição só uma validação com evidência pode dizer.
                </p>
              </div>
            </div>
            <div class="panel">
              @if (plansError(); as pe) {
                <div class="state error"><p class="err">⚠ {{ pe }}</p></div>
              } @else if (plans().length === 0) {
                <div class="state empty">
                  <p class="muted">
                    Nenhum plano de ação registrado. Abra um achado de identidade abaixo e crie a primeira ação.
                  </p>
                </div>
              } @else {
                <table class="grid-table">
                  <thead>
                    <tr>
                      <th>Ação</th>
                      <th class="c-tier">Origem</th>
                      <th class="c-tier">Responsável</th>
                      <th class="c-when">Prazo</th>
                      <th class="c-state">Situação do plano</th>
                      <th>Resultado no achado</th>
                      <th class="c-when">Próxima providência</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (p of plans(); track p.id) {
                      <tr class="row">
                        <td>
                          <!-- [AEGIS-MVP-PRODUCT-03] O link identifica O PLANO, não apenas o achado. Sem o
                               identificador da ação, abrir a linha de uma ação ENCERRADA levaria ao ciclo
                               ATIVO do mesmo indicador — outro trabalho, com a mesma aparência de resposta. -->
                          <a
                            class="title link"
                            [routerLink]="['/identity']"
                            [queryParams]="{ finding: p.knightIndicatorId, run: p.originRunId, plan: p.id }">
                            {{ p.title }}
                          </a>
                          <span class="meta mono">{{ p.knightIndicatorId }}</span>
                        </td>
                        <td class="c-tier">
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
          <div class="queue-head">
            <div>
              <h2>{{ recommendationsLabel }}</h2>
              <p class="queue-sub">
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
              <table class="grid-table">
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
          <div class="queue-head">
            <div>
              <h2>Vulnerabilidades em ativos</h2>
              <p class="queue-sub">
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
              <table class="grid-table">
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

        <!-- ---------- [AEGIS-CROSS-SOURCE-01] Situações identificadas entre fontes ----------
             Leitura PRÓPRIA (carga, falha e filtros independentes das filas): uma falha aqui não derruba a Central, e
             uma falha da Central não é lida como "nenhuma situação". Não é fila de prioridade nem ranking de risco. -->
        <div class="queue">
          <div class="queue-head">
            <div>
              <h2>{{ xsHeading }}</h2>
              <p class="queue-sub">
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
              <p class="muted">Avaliando as regras entre fontes…</p>
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
                <table class="grid-table xs-tally">
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
                  <button type="button" class="xs-chip" [class.on]="xsFilter().state === f.value"
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
                <table class="grid-table">
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
                <div class="xs-pager">
                  <span class="meta">{{ xsRange() }}</span>
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
          <div class="queue-head">
            <div>
              <h2>Achados de identidade</h2>
              <p class="queue-sub">
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
                <table class="grid-table">
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
      /* Alias local da cor de acento (dual-neon cyan): encurta os ~14 usos de color-mix e mantém o
         fallback #26e0ff quando --hud-cyan não está definido. Custom property herda para todo o componente. */
      :host { --c: var(--hud-cyan, #26e0ff); }
      .page { padding: 1.25rem 1.5rem 2rem; display: flex; flex-direction: column; gap: 1.1rem; }
      .page-head { display: flex; justify-content: space-between; align-items: flex-start; gap: 1rem; flex-wrap: wrap; }
      h1 { margin: 0; font-size: 1.35rem; }
      h2 { margin: 0; font-size: 1.02rem; }
      .sub { margin: 0.35rem 0 0; max-width: 82ch; opacity: 0.72; font-size: 0.85rem; line-height: 1.45; }
      .freshness { margin: 0.4rem 0 0; font-size: 0.72rem; opacity: 0.55; }
      .head-actions { display: flex; gap: 0.5rem; flex-wrap: wrap; }
      .muted { opacity: 0.65; font-size: 0.85rem; }
      .dim { opacity: 0.4; }
      .err { color: #ff6b8a; font-size: 0.85rem; }

      .cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(12rem, 1fr)); gap: 0.75rem; }
      .card, .panel { background: color-mix(in srgb, var(--c) 4%, transparent); border: 1px solid color-mix(in srgb, var(--c) 22%, transparent); border-radius: 8px; }
      .card { padding: 0.8rem 0.95rem; display: flex; flex-direction: column; gap: 0.2rem; }
      .card.wide { grid-column: span 2; min-width: 0; }
      .card-label { font-size: 0.66rem; text-transform: uppercase; letter-spacing: 0.1em; opacity: 0.6; }
      .card-value { font-size: 1.6rem; font-weight: 600; }
      .card-value.muted { font-size: 1.05rem; opacity: 0.7; }
      .card-meta { font-size: 0.72rem; opacity: 0.6; }
      .collect { display: flex; flex-direction: column; gap: 0.25rem; margin-top: 0.15rem; }
      .collect-row { display: flex; justify-content: space-between; gap: 0.75rem; font-size: 0.78rem; }
      .collect-k { opacity: 0.62; }
      .collect-v { font-size: 0.76rem; }
      .collect-v.muted { font-family: inherit; }

      .queue { display: flex; flex-direction: column; gap: 0.5rem; }
      .subtabs { display: flex; gap: 0.4rem; border-bottom: 1px solid color-mix(in srgb, var(--c) 18%, transparent); }
      .subtabs button { cursor: pointer; background: none; border: none; border-bottom: 2px solid transparent; color: inherit; opacity: 0.6; font: inherit; font-size: 0.85rem; padding: 0.45rem 0.85rem; }
      .subtabs button.on { opacity: 1; color: var(--c); border-bottom-color: var(--c); }
      .subtabs .n { font-size: 0.68rem; margin-left: 0.35rem; opacity: 0.75; }
      .meta.late { color: #ff6b8a; }
      .meta.demo { color: #ffb020; opacity: 0.95; }
      /* [AEGIS-MVP-PRODUCT-02] Barra de contexto da avaliação KNIGHT: origem, score PRÓPRIO e cobertura. */
      .knight-bar { display: flex; gap: 1.4rem; flex-wrap: wrap; padding: 0.55rem 0.7rem 0.75rem; }
      .kb-item { display: flex; flex-direction: column; gap: 0.1rem; }
      .kb-k { font-size: 0.64rem; text-transform: uppercase; letter-spacing: 0.09em; opacity: 0.55; }
      .kb-v { font-size: 0.86rem; }
      .kb-v.demo { color: #ffb020; }
      .kb-s { font-size: 0.68rem; opacity: 0.55; }
      .badge.sev.Critical { border-color: #ff6b8a; color: #ff6b8a; }
      .badge.sev.High { border-color: #ffb020; color: #ffb020; }
      a.link { color: var(--c); text-decoration: none; }
      a.link:hover { text-decoration: underline; }
      .queue-head { display: flex; justify-content: space-between; align-items: flex-end; gap: 1rem; flex-wrap: wrap; }
      .queue-sub { margin: 0.2rem 0 0; max-width: 78ch; opacity: 0.68; font-size: 0.8rem; }
      .linknav { color: var(--c); text-decoration: none; font-size: 0.8rem; white-space: nowrap; border: 1px solid color-mix(in srgb, var(--c) 30%, transparent); border-radius: 5px; padding: 0.35rem 0.7rem; }
      .linknav:hover { background: color-mix(in srgb, var(--c) 12%, transparent); }

      .panel { padding: 0.6rem; overflow-x: auto; }
      .state { padding: 1.4rem 1rem; text-align: center; display: flex; flex-direction: column; gap: 0.75rem; align-items: center; }

      .grid-table { width: 100%; border-collapse: collapse; font-size: 0.85rem; }
      .grid-table th { text-align: left; font-size: 0.66rem; text-transform: uppercase; letter-spacing: 0.08em; opacity: 0.6; padding: 0.4rem 0.6rem; border-bottom: 1px solid color-mix(in srgb, var(--c) 18%, transparent); }
      .grid-table td { padding: 0.5rem 0.6rem; vertical-align: top; border-bottom: 1px solid color-mix(in srgb, var(--c) 8%, transparent); }
      .row.resolved { opacity: 0.6; }
      .title { display: block; line-height: 1.3; }
      .rem { display: block; font-size: 0.74rem; opacity: 0.66; margin-top: 0.15rem; max-width: 60ch; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
      .meta { font-size: 0.72rem; opacity: 0.62; display: block; }
      .asset { color: var(--c); text-decoration: none; }
      .asset:hover { text-decoration: underline; }
      .c-rank { width: 3.5rem; opacity: 0.85; }
      .c-cvss, .c-epss, .c-sev, .c-gap, .c-tier, .c-state, .c-when { white-space: nowrap; }
      .freshness, .collect-v, .mono { font-family: ui-monospace, monospace; }
      .gap, .badge.warn, .sev-medium { color: #f5a524; }
      .gap { font-weight: 600; }
      .mono { font-size: 0.82rem; }
      .badge { font-size: 0.62rem; padding: 0.05rem 0.4rem; border-radius: 3px; border: 1px solid currentColor; text-transform: uppercase; letter-spacing: 0.05em; color: #9aa7c7; }
      .badge.ok { color: var(--c); }
      .badge.bad { color: #ff3d6a; }
      .badge.src { margin-right: 0.2rem; }
      .badge.lc { margin-top: 0.2rem; display: inline-block; }
      .sev-tag { font-size: 0.72rem; padding: 0.1rem 0.45rem; border-radius: 3px; border: 1px solid currentColor; }
      .sev-critical { color: #ff3d6a; }
      .sev-high { color: #ff8a5c; }
      .sev-low { color: #26e0ff; }
      .sev-desconhecida { color: #9aa7c7; }

      .notice { margin: 0; padding: 0.5rem 0.75rem; border-radius: 6px; font-size: 0.78rem; line-height: 1.4; }
      .notice.warn, .warn-text { color: #f5a524; }
      .notice.warn { background: color-mix(in srgb, #f5a524 9%, transparent); border: 1px solid color-mix(in srgb, #f5a524 30%, transparent); }
      .foot-note { margin: 0.2rem 0 0; font-size: 0.74rem; opacity: 0.55; max-width: 90ch; line-height: 1.4; }

      button.primary, button.ghost { color: inherit; border-radius: 5px; font: inherit; cursor: pointer; }
      button.primary { background: color-mix(in srgb, var(--c) 18%, transparent); border: 1px solid var(--c); padding: 0.45rem 1rem; font-size: 0.82rem; }
      button.ghost { background: transparent; border: 1px solid color-mix(in srgb, var(--c) 30%, transparent); padding: 0.4rem 0.8rem; font-size: 0.8rem; }
      button:disabled { opacity: 0.5; cursor: not-allowed; }

      /* [AEGIS-CROSS-SOURCE-01] Situações entre fontes: resumo por regra × estado, filtros e lista agrupada. */
      .xs-summary { margin: 0.2rem 0.4rem 0.5rem; font-size: 0.82rem; }
      .xs-tally-wrap { overflow-x: auto; margin-bottom: 0.6rem; }
      .xs-tally caption { text-align: left; font-size: 0.66rem; text-transform: uppercase; letter-spacing: 0.08em; opacity: 0.55; padding: 0 0.6rem 0.3rem; }
      .xs-filters { display: flex; flex-wrap: wrap; gap: 0.4rem; align-items: center; margin: 0.2rem 0.4rem 0.6rem; }
      .xs-chip { cursor: pointer; font: inherit; font-size: 0.74rem; color: inherit; opacity: 0.7; background: transparent; border: 1px solid color-mix(in srgb, var(--c) 25%, transparent); border-radius: 999px; padding: 0.2rem 0.7rem; }
      .xs-chip.on { opacity: 1; color: var(--c); border-color: var(--c); background: color-mix(in srgb, var(--c) 10%, transparent); }
      .xs-select { font: inherit; font-size: 0.76rem; color: inherit; background: transparent; border: 1px solid color-mix(in srgb, var(--c) 25%, transparent); border-radius: 5px; padding: 0.2rem 0.5rem; }
      .badge.xs-attention { color: #ff3d9a; }
      .badge.xs-warn { color: #f5a524; }
      .xs-open { font-size: 0.74rem; padding: 0.25rem 0.6rem; white-space: nowrap; }
      .xs-pager { display: flex; gap: 0.5rem; align-items: center; justify-content: flex-end; padding: 0.5rem 0.4rem 0; }
      .xs-policy { margin: 0.6rem 0.4rem 0.2rem; }
      /* Colunas próprias que QUEBRAM linha: prévia de CVEs, selo e resumo nunca alargam o painel. */
      .xs-rem { display: block; font-size: 0.74rem; opacity: 0.66; margin-top: 0.15rem; max-width: 52ch; line-height: 1.4; }
      .xs-state .badge { display: inline-block; white-space: normal; max-width: 14rem; line-height: 1.35; }
      .xs-cves { min-width: 8rem; }
      .xs-cves .meta { white-space: normal; overflow-wrap: anywhere; }
      .xs-detail-panel { margin: 0.7rem 0.4rem 0; padding-top: 0.4rem; border-top: 1px solid color-mix(in srgb, var(--c) 18%, transparent); }

      @media (max-width: 720px) { .card.wide { grid-column: span 1; } }
    `,
  ],
})
export class PrioritiesComponent {
  private readonly api = inject(PriorityService);
  private readonly agent = inject(AgentStateService);

  private readonly remediation = inject(RemediationService);

  /** Sub-aba ativa: os achados (as três filas) ou os planos que os endereçam. */
  protected readonly tab = signal<'achados' | 'planos'>('achados');

  /**
   * Planos de ação do tenant. Uma leitura ÚNICA serve às duas sub-abas e ao botão de cada achado — a mesma
   * autoridade que a tela do KNIGHT usa, de modo que os dois lugares não discordem sobre "existe ação ativa?".
   */
  protected readonly plans = signal<ActionPlan[]>([]);
  protected readonly plansError = signal<string | null>(null);

  protected readonly situation = actionSituation;
  protected readonly result = actionResult;
  protected readonly origin = originLabel;

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
    this.load();
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.loadPlans();
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
  private loadPlans(): void {
    this.plansError.set(null);
    this.remediation.list({}).subscribe({
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
        'do Microsoft Secure Score e as vulnerabilidades identificadas nos ativos. Aponte relações apenas quando ' +
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

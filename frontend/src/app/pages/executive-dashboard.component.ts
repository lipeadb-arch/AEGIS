import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BlastRadiusSummary, ExecutiveDashboard, GapBalance } from '../models/dashboard.models';
import {
  DashboardDevicePriorityItem,
  DashboardIdentityGap,
  DashboardMetric,
  DashboardOverview,
  devicePriorityCardView,
  devicePriorityItemParams,
  devicePriorityUnitLines,
  hasReading,
  metricFreshness,
  identityCapabilityLabel,
  identityOutcomeLabel,
  stateLabel,
  workspaceFromOverview,
} from '../models/dashboard-overview.models';
import { ComplianceHistoryPoint, buildGapBalance, trendToSparkline } from '../models/scoring.models';
import { environmentStage } from '../models/workspace.models';
import { POSTURE_RECOMMENDATIONS_LABEL } from '../models/posture-exposure.models';
import { PostureSummaryComponent } from '../components/scoring/posture-summary.component';
import { EnvironmentFirstComponent } from '../components/environment-first.component';
import { DashboardService } from '../services/dashboard.service';
import { AegisScoreService } from '../services/aegis-score.service';
import { AuthService } from '../services/auth.service';
import { ScoringService } from '../services/scoring.service';
import { SparklineComponent } from '../components/scoring/sparkline.component';
import { GapBalanceComponent } from '../components/scoring/gap-balance.component';
import { BlastRadiusSummaryComponent } from '../components/scoring/blast-radius-summary.component';
import { icrColor } from '../lib/scales';
import { IcrGaugeComponent } from '../components/icr-gauge.component';
import { RiskHeatmapComponent } from '../components/risk-heatmap.component';
import { GapChartComponent } from '../components/gap-chart.component';
import { RiskLevelsComponent } from '../components/risk-levels.component';
import { ExposureCardComponent } from '../components/exposure-card.component';
import { MaturityGaugeComponent } from '../components/maturity-gauge.component';
import { MaturityBarsComponent, FunctionScore } from '../components/maturity-bars.component';
import { IconComponent } from '../components/icon.component';

/**
 * [AEGIS-MVP-PRODUCT-01] VISÃO GERAL — a tela inicial do AEGIS.
 *
 * O defeito corrigido: a tela decidia "tem postura?" por UMA dimensão (maturidade CMMI + registro de riscos
 * legado). Num ambiente com telemetria REAL — ativos inventariados, exposições e vulnerabilidades coletadas,
 * identidade lida — porém sem assessment de maturidade, o painel inteiro exibia "Nenhuma postura medida" e
 * escondia tudo o que já havia sido observado, enquanto o /scoring/workspace mostrava controles avaliados.
 *
 * A correção NÃO foi acrescentar uma condição ao antigo `hasPosture()` e liberar os gráficos legados zerados.
 * A tela passou a ser PARTICIONADA por dimensão, e CADA painel exige a PRÓPRIA evidência para aparecer:
 *
 *  1. Ambiente observado — ativos, exposições, vulnerabilidades, identidade. Métricas independentes com
 *     origem e estado próprios; ausência de coleta é `null` ("Ainda não coletado"), NUNCA zero.
 *  2. Postura avaliada — a autoridade determinística (aegis-score-v1) e a cobertura por natureza de prova.
 *  3. Risco de negócio — maturidade, ICR e registro de riscos. Só é montado (e só então busca os gráficos
 *     legados) quando a PRÓPRIA dimensão tem avaliação. Sem isso, a seção diz "ainda não avaliado" — e não
 *     esconde nada das dimensões acima.
 *  4. O que merece atenção — as duas filas prioritárias, com a ordem e a fonte das autoridades.
 *  5. Identidade e saúde das fontes — o que continua disponível numa coleta parcial e o que está antigo.
 *
 * Nenhum score novo: KNIGHT, NIST, CVSS e maturidade NÃO são combinados. Nada é recalculado no cliente e
 * nenhuma coleta externa é acionada ao abrir a tela — a leitura composta chega numa requisição.
 */
@Component({
  selector: 'app-executive-dashboard',
  standalone: true,
  imports: [
    DatePipe,
    RouterLink,
    SparklineComponent,
    GapBalanceComponent,
    BlastRadiusSummaryComponent,
    IcrGaugeComponent,
    MaturityGaugeComponent,
    MaturityBarsComponent,
    RiskHeatmapComponent,
    GapChartComponent,
    RiskLevelsComponent,
    ExposureCardComponent,
    PostureSummaryComponent,
    EnvironmentFirstComponent,
    IconComponent,
  ],
  template: `
    <div class="page">
      <header class="page-head">
        <div>
          <p class="page-eyebrow">Operação</p>
          <h1>Visão geral</h1>
          @if (data(); as d) {
            <p class="page-meta">
              {{ d.clientName }}
              @if (d.generatedAt) {
                · leitura de {{ d.generatedAt | date: 'dd/MM HH:mm' }}
              }
            </p>
          }
        </div>
        <div class="page-actions">
          <a class="ghost" routerLink="/priorities">Ver prioridades</a>
          <button type="button" class="ghost" (click)="reload()" [disabled]="loading()">
            <app-icon name="refresh" />
            {{ loading() ? 'Carregando…' : 'Atualizar' }}
          </button>
        </div>
      </header>

      <!-- A leitura é o bloco PRIMÁRIO porque o alias 'as' só é permitido nele (NG5002). Carga e falha vêm
           enquanto não há leitura REAL, a tela mostra estado, nunca números remanescentes. -->
      @if (data(); as d) {

        <!-- ============================ 1) AMBIENTE OBSERVADO ============================ -->
        <section class="section">
          <div class="section-head">
            <h2>O que já foi observado</h2>
            <a class="linknav" routerLink="/assets">Ver ambiente →</a>
          </div>

          <!-- Cada cartão: rótulo → valor → unidade → estado e origem da leitura. Sem leitura, "—" e o estado — nunca 0. -->
          <div class="metric-grid">
            @for (m of environmentMetrics(); track m.key) {
              <a
                class="metric"
                [class.is-void]="!hasReading(m.metric)"
                [class.is-partial]="m.metric.state === 'Partial'"
                [class.is-stale]="m.fresh.stale"
                [routerLink]="m.link"
              >
                <span class="metric-label">{{ m.label }}</span>
                @if (hasReading(m.metric)) {
                  <span class="metric-value">{{ m.metric.value }}</span>
                } @else {
                  <span class="metric-value is-na">—</span>
                }
                <!-- [AEGIS-MVP-PRODUCT-02] A UNIDADE do número, quando ela não é óbvia pelo rótulo. O cartão
                     de identidade contava CAPACIDADES coletadas e parecia contar contas. -->
                <span class="metric-unit">{{ m.unit }}</span>
                <span class="metric-foot">
                  <span class="metric-state">{{ m.fresh.label }}</span>
                  <span class="metric-src" [title]="m.metric.sourceLabel">{{ m.metric.sourceLabel }}</span>
                </span>
              </a>
            }
          </div>

          <!-- Uma nota por métrica com ressalva, agrupada junto dos cartões: explica o vazio e a parcialidade sem
               poluir cada cartão. -->
          @if (metricNotes().length > 0) {
            <div class="notice metric-notes" role="note">
              <ul>
                @for (n of metricNotes(); track n.label) {
                  <li><b>{{ n.label }}</b> — {{ n.note }}</li>
                }
              </ul>
            </div>
          }
        </section>

        <!-- ============================ 2) POSTURA AVALIADA ============================ -->
        <section class="section">
          <div class="section-head">
            <h2>Quanto foi avaliado</h2>
            <a class="linknav" routerLink="/controls">Ver controles →</a>
          </div>

          <div class="grid-2">
            <div class="panel">
              <app-posture-summary [posture]="d.posture" label="AEGIS Score" />
              <!-- [AEGIS-LANGUAGE-STATES-01] Três escalas convivem no produto; esta é só uma delas. -->
              <p class="scale-note">
                Pontos obtidos nos controles NIST CSF avaliados pelo AEGIS. Não é índice de fornecedor (como o Microsoft
                Secure Score), nem o score do AEGIS KNIGHT, nem probabilidade de incidente.
              </p>
              <!-- Tendência só sob postura AVALIADA: uma curva ao lado de "Não avaliado" afirmaria evolução
                   de um score que não existe. -->
              @if (d.posture.evaluationState === 'Evaluated' && trend().length > 1) {
                <div class="trend-strip">
                  <app-sparkline [points]="trend()" />
                  <span class="ts-meta">
                    <b class="ts-delta" [class.up]="(trendDelta() ?? 0) > 0" [class.down]="(trendDelta() ?? 0) < 0">
                      {{ (trendDelta() ?? 0) > 0 ? '▲' : (trendDelta() ?? 0) < 0 ? '▼' : '■' }}
                      {{ trendDelta() }} p.p.
                    </b>
                    <em>últimos {{ trend().length }} dias</em>
                  </span>
                </div>
              }
            </div>

            <div class="panel">
              <div class="hd">
                <h3>Controles avaliados por natureza da prova</h3>
                <span class="hint">avaliados / elegíveis · cobertura não é conformidade</span>
              </div>
              <ul class="coverage">
                @for (c of coverage(); track c.label) {
                  <li>
                    <span class="c-k">{{ c.label }}</span>
                    <span class="c-bar" aria-hidden="true"><i [style.width.%]="c.percent"></i></span>
                    <span class="c-v">{{ c.evaluated }}/{{ c.eligible }}</span>
                  </li>
                }
              </ul>
            </div>
          </div>
        </section>

        <!-- ============================ 3) O QUE MERECE ATENÇÃO ============================ -->
        <section class="section">
          <div class="section-head">
            <h2>O que merece atenção</h2>
            <a class="linknav" routerLink="/priorities">Central de Prioridades →</a>
          </div>

          <!-- [AEGIS-JOURNEY-01] Prioridade de tratamento em dispositivos — a MESMA leitura da Central. Dispositivos, casos
               e planos aparecem em linhas próprias, cada uma com a sua unidade (nunca somadas), e a parcialidade é dita
               junto dos números. Cada dispositivo abre o detalhe na Central com o caso determinante selecionado. -->
          <div class="panel">
            <div class="hd">
              <h3>Prioridade de tratamento · vulnerabilidades em dispositivos</h3>
              @if (d.devicePriority?.policyCode; as code) {
                <span class="hint">política <span class="mono">{{ code }} v{{ d.devicePriority!.policyVersion }}</span></span>
              }
            </div>
            @if (dpCard().kind === 'data' && d.devicePriority; as dp) {
              <ul class="dp-units">
                <li><span class="u-k">Dispositivos</span><span class="u-v">{{ dpLines()!.devices }}</span></li>
                <li><span class="u-k">Casos</span><span class="u-v">{{ dpLines()!.cases }}</span></li>
                @if (dpLines()!.disposed; as fora) {
                  <li><span class="u-k">Fora da fila</span><span class="u-v">{{ fora }}</span></li>
                }
                <li><span class="u-k">Planos</span><span class="u-v">{{ dpLines()!.plans }}</span></li>
              </ul>
              @if (dp.truncationNote) {
                <p class="notice warn dp-note">{{ dp.truncationNote }}</p>
              }
              @if (dp.absenceNote) {
                <p class="notice warn dp-note">{{ dp.absenceNote }}</p>
              }
              <ul class="queue">
                @for (i of dp.top; track i.assetId) {
                  <li>
                    <a class="q-t q-link" routerLink="/priorities" [queryParams]="itemParams(i)">
                      {{ i.position }}. {{ i.assetName }}
                    </a>
                    <span class="q-m">
                      {{ i.bandLabel }}@if (i.cveId) { · caso determinante <span class="mono">{{ i.cveId }}</span> }@if (i.nameIsPlaceholder) { · nome não coletado pela fonte }
                    </span>
                    <span class="q-m" [class.has-plan]="!!i.activePlanId">
                      {{ i.activePlanId ? 'Plano de tratamento ativo para este caso' : 'Sem plano de tratamento para este caso' }}
                    </span>
                  </li>
                }
              </ul>
              <p class="dp-foot">
                Ordem de tratamento pela política determinística do AEGIS — não é probabilidade de comprometimento nem avaliação
                completa dos riscos. Calculada em {{ dp.evaluatedAt | date: 'dd/MM HH:mm' }}.
              </p>
              <div class="dp-links">
                <a class="linknav" routerLink="/priorities">Fila completa →</a>
                <a class="linknav" routerLink="/priorities" [queryParams]="{ tab: 'planos' }">Planos de ação →</a>
              </div>
            } @else {
              <p class="panel-empty" [class.is-fail]="dpCard().kind === 'unavailable' || dpCard().kind === 'missing'">
                {{ dpCardText() }}
              </p>
              @if (dpLines(); as l) {
                @if (d.devicePriority!.plans.active + d.devicePriority!.plans.completed > 0) {
                  <p class="dp-foot">Planos de casos de dispositivo: {{ l.plans }}</p>
                }
              }
            }
          </div>

          <div class="grid-2">
            <div class="panel">
              <div class="hd">
                <h3>{{ recommendationsLabel }}</h3>
                <span class="hint">pendentes · {{ d.configurationExposures.summary.sourceLabel }}</span>
              </div>
              @if (d.configurationExposures.top.length > 0) {
                <ul class="queue">
                  @for (e of d.configurationExposures.top; track e.id) {
                    <li>
                      <span class="q-t">{{ e.displayTitle || e.title }}</span>
                      <span class="q-m">{{ e.plainSummary || e.category || 'Pendente na fonte' }}</span>
                    </li>
                  }
                </ul>
              } @else {
                <p class="panel-empty">{{ exposureEmptyText() }}</p>
              }
            </div>

            <div class="panel">
              <div class="hd">
                <h3>Vulnerabilidades</h3>
                <span class="hint">agrupadas por problema</span>
              </div>
              @if (d.vulnerabilities.top.length > 0) {
                <ul class="queue">
                  @for (v of d.vulnerabilities.top; track v.cveId) {
                    <li>
                      <span class="q-t">{{ v.displayTitle }}</span>
                      <span class="q-m">
                        {{ v.severityLabel }} · {{ v.openAssetCount }} ativo(s) em aberto
                      </span>
                    </li>
                  }
                </ul>
              } @else {
                <p class="panel-empty">{{ vulnerabilityEmptyText() }}</p>
              }
            </div>
          </div>
        </section>

        <!-- ============================ 4) IDENTIDADE ============================ -->
        <section class="section">
          <div class="section-head">
            <h2>Identidade</h2>
            <a class="linknav" routerLink="/identity">AEGIS KNIGHT →</a>
          </div>

          <div class="panel">
            @if (hasReading(d.identity)) {
              <p class="lead">
                Leitura de <b>{{ d.identity.sourceLabel }}</b>
                @if (d.identity.collectedAt) {
                  em {{ d.identity.collectedAt | date: 'dd/MM HH:mm' }}
                }
                — {{ d.identity.capabilitiesCollected.length }} de
                {{ d.identity.capabilitiesCollected.length + d.identity.capabilitiesMissing.length }}
                capacidades entregues.
              </p>

              <div class="caps">
                <div>
                  <span class="badge ok cap-k">Disponível agora</span>
                  <ul>
                    @for (c of d.identity.capabilitiesCollected; track c) {
                      <li>{{ identityCapabilityLabel(c) }}</li>
                    }
                  </ul>
                </div>
                @if (d.identity.capabilitiesMissing.length > 0) {
                  <div>
                    <!-- Parcialidade NÃO é "integração sem dados": o que falta é nomeado com o motivo real. -->
                    <span class="badge warn cap-k">Ainda indisponível</span>
                    <ul>
                      @for (g of d.identity.capabilitiesMissing; track g.capability) {
                        <li>
                          {{ identityCapabilityLabel(g.capability) }}
                          <em>{{ identityOutcomeLabel(g.outcome) }}</em>
                        </li>
                      }
                    </ul>
                  </div>
                }
              </div>

              @if (d.identity.controlsAwaitingEvidence > 0) {
                <p class="foot">
                  {{ d.identity.controlsAwaitingEvidence }} controle(s) de identidade seguem
                  <b>não avaliados</b>: a telemetria existe, mas não cobre o requisito deles.
                </p>
              }
            } @else {
              <p class="panel-empty">{{ identityEmptyText() }}</p>
            }
          </div>
        </section>

        <!-- ============================ 5) SAÚDE DAS FONTES ============================ -->
        <section class="section">
          <div class="section-head">
            <h2>Fontes conectadas</h2>
            @if (isTenantAdmin()) {
              <a class="linknav" routerLink="/settings/integrations">Gerenciar integrações →</a>
            }
          </div>

          <div class="panel">
            @if (d.sources.items.length > 0) {
              <p class="lead">
                {{ d.sources.healthy }} de {{ d.sources.enabled }} fontes habilitadas operacionais.
                @if (d.sources.attention > 0) {
                  <b class="warn">{{ d.sources.attention }} precisa(m) de atenção.</b>
                }
              </p>
              <ul class="sources">
                @for (s of d.sources.items; track s.id) {
                  <li [class.attention]="needsAttention(s)">
                    <span class="s-n">{{ s.displayName }}</span>
                    <span class="s-c">{{ s.capability }}</span>
                    <span class="s-s">{{ sourceStateText(s) }}</span>
                  </li>
                }
              </ul>
            } @else {
              <p class="panel-empty">Nenhuma integração configurada neste ambiente ainda.</p>
            }
          </div>
        </section>

        <!-- ============================ 6) RISCO DE NEGÓCIO ============================ -->
        <!-- Dimensão SEPARADA: vem de avaliação assistida, não de telemetria. Cada painel abaixo só aparece
             com a PRÓPRIA evidência — nenhum gráfico legado é liberado zerado. -->
        <section class="section">
          <div class="section-head">
            <div>
              <h2>Risco de negócio</h2>
              <p class="section-desc">Maturidade, criticidade e registro de riscos</p>
            </div>
          </div>

          @if (!hasBusinessRisk()) {
            <div class="panel">
              <p class="panel-empty">
                Ainda não avaliado. Maturidade, índice de criticidade e registro de riscos vêm de uma
                avaliação conduzida com o cliente — <b>não são deriváveis da telemetria acima</b>, e a
                ausência deles não altera nada do que já foi observado.
              </p>
            </div>
          } @else {
            @if (d.businessRisk.riskRegisterState === 'Available') {
              <div class="cards">
                <app-exposure-card
                  label="Processos críticos com risco alto ou crítico"
                  [value]="d.businessRisk.criticalProcessesExposed ?? 0"
                  tone="danger"
                />
                <app-exposure-card
                  label="Planos de ação vencidos"
                  [value]="d.businessRisk.overdueActionPlans ?? 0"
                  tone="danger"
                />
              </div>
            }

            <div class="grid-2">
              @if (d.businessRisk.maturityState === 'Available') {
                <div class="panel">
                  <div class="hd">
                    <h3>Maturidade geral</h3>
                    <span class="hint">CMMI 1–5 · alvo {{ (d.businessRisk.targetMaturity ?? 0).toFixed(1) }}</span>
                  </div>
                  <app-maturity-gauge [value]="d.businessRisk.overallMaturity ?? 0" [max]="chartScale()" />
                </div>

                <div class="panel">
                  <div class="hd">
                    <h3>Maturidade por função</h3>
                    <span class="hint">escala 0–{{ chartScale().toFixed(1) }}</span>
                  </div>
                  @if (maturityBars().length > 0) {
                    <app-maturity-bars [data]="maturityBars()" [max]="chartScale()" />
                  } @else {
                    <p class="panel-empty">Carregando o detalhe por função…</p>
                  }
                </div>
              }

              @if (d.businessRisk.icrState === 'Available' && legacy()?.icr; as icr) {
                <div class="panel">
                  <div class="hd">
                    <h3>Índice de criticidade</h3>
                    <span class="hint" [style.color]="icrColor(icr.band)">{{ icr.band }}</span>
                  </div>
                  <app-icr-gauge [icr]="icr" />
                </div>
              }

              @if ((legacy()?.riskByLevel?.length ?? 0) > 0) {
                <div class="panel">
                  <div class="hd"><h3>Riscos por nível</h3></div>
                  <app-risk-levels [data]="legacy()!.riskByLevel" />
                </div>
              }

              @if ((legacy()?.riskHeatmap?.length ?? 0) > 0) {
                <div class="panel">
                  <div class="hd"><h3>Matriz de risco</h3></div>
                  <app-risk-heatmap [data]="legacy()!.riskHeatmap" />
                </div>
              }

              @if ((legacy()?.topGaps?.length ?? 0) > 0) {
                <div class="panel">
                  <div class="hd">
                    <h3>Maiores lacunas por categoria</h3>
                    <span class="hint">distância até o alvo</span>
                  </div>
                  <app-gap-chart [data]="legacy()!.topGaps" />
                </div>
              }

              @if (gapBalance(); as gb) {
                <div class="panel">
                  <div class="hd">
                    <h3>Origem das lacunas</h3>
                    <span class="hint">tecnologia × processo</span>
                  </div>
                  <app-gap-balance [balance]="gb" />
                </div>
              }

              @if (blastRadius(); as br) {
                <div class="panel">
                  <div class="hd">
                    <h3>Impacto potencial</h3>
                    <span class="hint">pior cenário conhecido</span>
                  </div>
                  <app-blast-radius-summary [summary]="br" />
                </div>
              }
            </div>
          }
        </section>

        <!-- ============================ 7) PRÓXIMO PASSO ============================ -->
        <!-- Jornada environment-first: reusa a MESMA leitura composta (nenhuma chamada extra). Aparece só
             enquanto AINDA orienta (etapas A–C). Na etapa "medido" ela repetia a cobertura por natureza que a
             seção "Quanto foi avaliado" já apresenta — duas leituras do mesmo número na mesma tela. -->
        @if (onboardingView(); as w) {
          <app-environment-first [posture]="w" [isTenantAdmin]="isTenantAdmin()" />
        }
      } @else if (loading()) {
        <section class="panel state" role="status">
          <span class="spinner" aria-hidden="true"></span>
          <b>Consolidando a leitura do ambiente…</b>
        </section>
      } @else {
        <!-- Falha operacional: NUNCA cair em exemplo nem reaproveitar a carga anterior — e sem mandar o
             cliente investigar console, endereço de API ou identificador técnico. -->
        <section class="panel state error" role="alert">
          <h3>Não foi possível carregar a visão geral</h3>
          <p>
            O serviço não respondeu agora. <b>Nenhum indicador é exibido</b> — números remanescentes seriam
            lidos como a leitura atual deste ambiente.
          </p>
          <button type="button" class="primary" (click)="reload()">Tentar novamente</button>
        </section>
      }
    </div>
  `,
  styles: [
    `
      /* Página, cabeçalho, seções, painéis, botões, cartões de métrica e estados vêm do sistema visual global
         (styles.css). Aqui só o que é próprio da Visão geral. */

      /* ---------- Notas das métricas: um aviso só, junto dos cartões ---------- */
      .metric-notes ul {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: var(--sp-1);
      }

      /* ---------- Postura avaliada ---------- */
      .scale-note {
        margin-top: var(--sp-3);
        max-width: 72ch;
        font-size: var(--fs-meta);
        line-height: var(--lh);
        color: var(--muted);
      }
      .trend-strip {
        display: flex;
        align-items: center;
        gap: var(--sp-3);
        margin-top: var(--sp-4);
        padding-top: var(--sp-3);
        border-top: 1px solid var(--line-2);
      }
      .ts-meta {
        display: flex;
        flex-direction: column;
        gap: 2px;
        min-width: 0;
      }
      .ts-delta {
        font-size: var(--fs-sm);
        font-weight: 600;
        color: var(--text-2);
      }
      .ts-delta.up {
        color: var(--cyan);
      }
      .ts-delta.down {
        color: var(--red-text);
      }
      .ts-meta em {
        font-style: normal;
        font-size: var(--fs-meta);
        color: var(--muted);
      }

      /* Cobertura por natureza da prova. NEUTRO de propósito: cobertura não é conformidade. */
      .coverage {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 14px;
      }
      .coverage li {
        display: grid;
        grid-template-columns: minmax(0, 1fr) clamp(72px, 32%, 220px) 56px;
        align-items: center;
        gap: var(--sp-3);
      }
      /* O rótulo QUEBRA em vez de truncar: o nome da natureza da prova é justamente o que se lê aqui. */
      .c-k {
        min-width: 0;
        font-size: var(--fs-sm);
        line-height: 1.35;
        color: var(--text);
      }
      .c-bar {
        display: block;
        height: 6px;
        border-radius: 3px;
        background: rgba(122, 145, 190, 0.16);
        overflow: hidden;
      }
      .c-bar i {
        display: block;
        height: 100%;
        border-radius: 3px;
        background: var(--cyan);
        opacity: 0.6;
      }
      .c-v {
        font-size: var(--fs-sm);
        font-weight: 600;
        text-align: right;
        color: var(--text-2);
      }

      /* ---------- Filas ---------- */
      .queue {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
      }
      .queue li {
        display: flex;
        flex-direction: column;
        gap: 3px;
        padding: var(--sp-3) 0;
        border-bottom: 1px solid var(--line-2);
      }
      .queue li:first-child {
        padding-top: 0;
      }
      .queue li:last-child {
        padding-bottom: 0;
        border-bottom: 0;
      }
      .q-t {
        font-size: var(--fs-body);
        font-weight: 500;
        line-height: 1.4;
        color: var(--text);
      }
      .q-m {
        font-size: var(--fs-meta);
        line-height: 1.45;
        color: var(--muted);
      }
      .q-m.has-plan {
        color: var(--cyan);
      }
      .q-link {
        align-self: flex-start;
        text-decoration: none;
      }
      .q-link:hover {
        color: var(--cyan);
        text-decoration: underline;
        text-underline-offset: 3px;
      }
      .panel-empty {
        font-size: var(--fs-sm);
        line-height: var(--lh-relaxed);
        color: var(--text-2);
      }
      .panel-empty b {
        color: var(--text);
        font-weight: 600;
      }
      .panel-empty.is-fail {
        color: var(--amber);
      }
      .lead {
        margin-bottom: 14px;
        font-size: var(--fs-body);
        line-height: 1.55;
      }
      .lead b {
        font-weight: 600;
      }
      .lead .warn {
        color: var(--amber);
      }
      .foot {
        margin-top: 14px;
        padding-top: var(--sp-3);
        border-top: 1px solid var(--line-2);
        font-size: var(--fs-sm);
        line-height: 1.55;
        color: var(--text-2);
      }
      .foot b {
        color: var(--text);
      }

      /* ---------- Prioridade de tratamento em dispositivos ---------- */
      .dp-units {
        list-style: none;
        margin: 0 0 var(--sp-4);
        padding: 0;
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(min(100%, 220px), 1fr));
        gap: var(--sp-2);
      }
      .dp-units li {
        display: flex;
        flex-direction: column;
        gap: 2px;
        min-width: 0;
        padding: 10px var(--sp-3);
        border: 1px solid var(--line-2);
        border-radius: var(--radius);
        background: rgba(122, 145, 190, 0.04);
      }
      .u-k {
        font-size: var(--fs-caps);
        font-weight: 600;
        letter-spacing: var(--tracking-caps);
        text-transform: uppercase;
        color: var(--muted);
      }
      .u-v {
        font-size: var(--fs-sm);
        line-height: 1.45;
        color: var(--text);
      }
      .dp-note {
        margin-bottom: var(--sp-3);
      }
      .dp-foot {
        margin-top: 14px;
        font-size: var(--fs-meta);
        line-height: var(--lh);
        color: var(--muted);
      }
      .dp-links {
        display: flex;
        flex-wrap: wrap;
        gap: var(--sp-5);
        margin-top: 10px;
      }

      /* ---------- Identidade ---------- */
      .caps {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(min(100%, 260px), 1fr));
        gap: var(--sp-4) var(--sp-6);
      }
      .cap-k {
        margin-bottom: 10px;
      }
      .caps ul {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: var(--sp-2);
      }
      .caps li {
        font-size: var(--fs-sm);
        line-height: 1.45;
      }
      .caps li em {
        display: block;
        font-style: normal;
        font-size: var(--fs-meta);
        color: var(--muted);
      }

      /* ---------- Fontes ---------- */
      .sources {
        list-style: none;
        margin: 0;
        padding: 0;
      }
      .sources li {
        display: grid;
        grid-template-columns: minmax(0, 1.3fr) minmax(0, 1fr) minmax(0, 1.2fr);
        gap: var(--sp-3);
        align-items: baseline;
        padding: 10px 0;
        border-bottom: 1px solid var(--line-2);
      }
      .sources li:first-child {
        padding-top: 0;
      }
      .sources li:last-child {
        padding-bottom: 0;
        border-bottom: 0;
      }
      .s-n {
        font-size: var(--fs-sm);
        font-weight: 500;
        overflow-wrap: anywhere;
      }
      .s-c {
        font-family: var(--mono);
        font-size: var(--fs-meta);
        color: var(--muted);
        overflow-wrap: anywhere;
      }
      .s-s {
        font-size: var(--fs-meta);
        color: var(--text-2);
      }
      .sources li.attention .s-s {
        color: var(--amber);
      }

      /* Telas estreitas: nada rola lateralmente — as grades já colapsam sozinhas. */
      @media (max-width: 720px) {
        .sources li {
          grid-template-columns: 1fr;
          gap: 2px;
        }
        .coverage li {
          grid-template-columns: minmax(0, 1fr) 64px 48px;
        }
      }
    `,
  ],
})
export class ExecutiveDashboardComponent implements OnInit {
  private readonly svc = inject(DashboardService);
  private readonly scoreSvc = inject(AegisScoreService);
  private readonly scoringSvc = inject(ScoringService);
  private readonly auth = inject(AuthService);

  /** Papel no tenant ativo — gate de visibilidade das ações administrativas (o backend também barra). */
  readonly isTenantAdmin = this.auth.isTenantAdmin;

  /** Leitura composta. Começa NULA: a tela nunca nasce com uma postura de exemplo. */
  readonly data = signal<DashboardOverview | null>(null);
  readonly loading = signal(true);
  readonly loadError = signal(false);

  /**
   * Painéis LEGADOS de maturidade/risco (radar por Função, matriz, lacunas por categoria, ICR). Só são
   * buscados quando a leitura composta confirma que a dimensão de risco de negócio TEM avaliação — sem isso
   * a chamada seria puro desperdício e os gráficos apareceriam zerados, que é justamente o defeito corrigido.
   */
  readonly legacy = signal<ExecutiveDashboard | null>(null);
  readonly gapBalance = signal<GapBalance | null>(null);

  /** Pior raio conhecido; `null` = nunca calculado (204) ou indisponível — o painel se omite. */
  readonly blastRadius = signal<BlastRadiusSummary | null>(null);

  /** Série do AEGIS Score. Vazia = a faixa de tendência se omite. */
  readonly trend = signal<ComplianceHistoryPoint[]>([]);
  readonly trendDelta = computed(() => {
    const t = this.trend();
    return t.length > 1 ? Math.round(t[t.length - 1].compliancePercent - t[0].compliancePercent) : null;
  });

  // Expostos ao template.
  protected readonly recommendationsLabel = POSTURE_RECOMMENDATIONS_LABEL;
  protected readonly hasReading = hasReading;
  protected readonly stateLabel = stateLabel;
  protected readonly identityCapabilityLabel = identityCapabilityLabel;
  protected readonly identityOutcomeLabel = identityOutcomeLabel;
  protected readonly icrColor = icrColor;

  /** As cinco métricas do ambiente, com rótulo e destino de investigação. */
  readonly environmentMetrics = computed(() => {
    const d = this.data();
    if (!d) return [];
    const e = d.environment;
    // A etiqueta de cada cartão nasce de `metricFreshness`, não do estado cru: `Available` prova que EXISTE
    // leitura, nunca que ela é recente. Sem `observedAt` o cartão diz que a data é desconhecida; com data
    // antiga, diz que está desatualizada — pelo mesmo limiar que a lista de fontes usa.
    const at = d.generatedAt;
    return [
      // [AEGIS-LANGUAGE-STATES-01] A unidade de cada número dita junto dele: "recomendações pendentes" é diferença
      // de pontos da fonte (Microsoft Secure Score), não configuração exposta comprovada.
      { key: 'assets', label: 'Ativos', metric: e.assets, link: '/assets' , unit: null },
      {
        key: 'exposures',
        label: POSTURE_RECOMMENDATIONS_LABEL,
        metric: e.configurationExposures,
        link: '/exposures',
        unit: 'pendentes na fonte',
      },
      { key: 'vulns', label: 'Vulnerabilidades', metric: e.vulnerabilities, link: '/vulnerabilities' , unit: 'problemas distintos em aberto' },
      { key: 'affected', label: 'Ativos afetados', metric: e.affectedAssets, link: '/vulnerabilities' , unit: 'com vulnerabilidade em aberto' },
      // A quantidade aqui é de CAPACIDADES de identidade coletadas (o snapshot é agregado e sem PII) — não é
      // o número de contas do diretório. O rótulo e a unidade dizem isso, em vez de deixar o número mentir.
      {
        key: 'identity',
        label: 'Identidade',
        metric: e.identity,
        link: '/identity',
        unit: 'capacidades coletadas · não é o nº de contas',
      },
    ].map((m) => ({ ...m, fresh: metricFreshness(m.metric, at) }));
  });

  /** Explicações agrupadas das métricas sem leitura — o vazio explicado uma vez, não em cada cartão. */
  readonly metricNotes = computed(() =>
    this.environmentMetrics()
      .filter((m) => m.metric.note)
      .map((m) => ({ label: m.label, note: m.metric.note as string })),
  );

  /** Cobertura por natureza da prova — percentuais JÁ apurados pelo backend (nada é recalculado aqui). */
  readonly coverage = computed(() => {
    const ec = this.data()?.evidenceCoverage;
    if (!ec) return [];
    return [
      { label: 'Ambiente e telemetria', slice: ec.telemetry },
      { label: 'Governança e evidência dirigida', slice: ec.documentation },
      { label: 'Evidência híbrida', slice: ec.both },
      { label: 'Avaliação orientada', slice: ec.notAutomated },
    ]
      .filter((c) => c.slice.eligibleControls > 0)
      .map((c) => ({
        label: c.label,
        percent: c.slice.coveragePercentage,
        evaluated: c.slice.evaluatedControls,
        eligible: c.slice.eligibleControls,
      }));
  });

  /**
   * Projeção do workspace reconstruída a partir da MESMA leitura composta, para alimentar o bloco
   * environment-first sem uma segunda requisição ao /scoring/workspace.
   */
  readonly workspaceView = computed(() => {
    const d = this.data();
    return d ? workspaceFromOverview(d) : null;
  });

  /**
   * A jornada environment-first ainda tem o que orientar? Nas etapas A–C ela conduz o próximo passo; na
   * etapa "medido" o conteúdo dela é a MESMA cobertura por natureza já exibida em "Quanto foi avaliado".
   */
  readonly onboardingView = computed(() => {
    const w = this.workspaceView();
    return w && environmentStage(w) !== 'measured' ? w : null;
  });

  /** A dimensão de risco de negócio tem alguma avaliação própria? Governa a seção inteira. */
  readonly hasBusinessRisk = computed(() => {
    const b = this.data()?.businessRisk;
    if (!b) return false;
    return b.maturityState === 'Available'
      || b.icrState === 'Available'
      || b.riskRegisterState === 'Available';
  });

  /** Maturidade por Função NIST, na ordem do catálogo que o backend devolve. */
  readonly maturityBars = computed<FunctionScore[]>(() =>
    (this.legacy()?.maturityByFunction ?? []).map((f) => ({
      code: f.function,
      label: f.functionName.replace(/\s*\(.*\)$/, ''),
      value: f.current,
    })),
  );

  /**
   * ESCALA dos gráficos de maturidade — GEOMETRIA, não métrica: precisa comportar a maior barra E o maior
   * alvo INDIVIDUAL, senão o marcador de alvo de uma Função mais exigente sai da área útil. Piso 4 para a
   * régua CMMI não colapsar. Não confundir com o ALVO agregado, que é menor que o maior alvo por Função.
   */
  readonly chartScale = computed(() => {
    const l = this.legacy();
    const target = this.data()?.businessRisk.targetMaturity ?? 0;
    if (!l) return Math.max(4, target);
    return Math.max(
      4,
      target,
      ...l.maturityByFunction.map((f) => f.target),
      ...l.maturityByFunction.map((f) => f.current),
    );
  });

  /**
   * Fila vazia de recomendações: "sem integração" ≠ "sem coleta" ≠ "coletado sem pendência" — a diferença muda
   * a decisão. Zero pendências é pontuação da fonte, não validação independente das configurações.
   */
  readonly exposureEmptyText = computed(() => {
    const m = this.data()?.environment.configurationExposures;
    switch (m?.state) {
      case 'NoSource':
        return 'Nenhuma fonte de recomendações de postura configurada neste ambiente (suportada hoje: Microsoft Secure Score).';
      case 'NeverCollected':
        return m.note ?? 'Integração configurada; nenhuma coleta concluída ainda.';
      default:
        return 'Nenhuma recomendação pendente na última coleta da fonte.';
    }
  });

  /** Vazio de identidade: "sem fonte" e "fonte conectada sem coleta" pedem ações diferentes. */
  readonly identityEmptyText = computed(() =>
    this.data()?.identity.state === 'NoSource'
      ? 'Nenhuma fonte de identidade conectada neste ambiente.'
      : 'Fonte de identidade conectada, ainda sem coleta concluída.',
  );

  readonly vulnerabilityEmptyText = computed(() => {
    const m = this.data()?.environment.vulnerabilities;
    switch (m?.state) {
      case 'NoSource':
        return 'Nenhuma fonte de vulnerabilidades configurada neste ambiente.';
      case 'NeverCollected':
        return m.note ?? 'Fonte de vulnerabilidades configurada; nenhuma coleta concluída ainda.';
      default:
        // Com escopo parcial, a nota da métrica (bloco "O que já foi observado") já diz que o zero não é o todo.
        return 'Nenhuma vulnerabilidade aberta na última leitura das fontes.';
    }
  });

  /** [AEGIS-JOURNEY-01] Estado do cartão de prioridade de tratamento — falha, sem fonte e ausência com textos próprios. */
  readonly dpCard = computed(() => devicePriorityCardView(this.data()?.devicePriority));
  readonly dpCardText = computed(() => {
    const v = this.dpCard();
    return 'text' in v ? v.text : '';
  });
  /** Dispositivos, casos e planos em linhas próprias, cada uma com a sua unidade. */
  readonly dpLines = computed(() => {
    const dp = this.data()?.devicePriority;
    return dp ? devicePriorityUnitLines(dp) : null;
  });

  /** Endereço da Central que abre o detalhe do dispositivo com o caso determinante (e o plano ativo) selecionados. */
  protected itemParams(i: DashboardDevicePriorityItem): Record<string, string | null> {
    return devicePriorityItemParams(i);
  }

  ngOnInit(): void {
    this.load();
  }

  reload(): void {
    this.load();
  }

  /** Uma fonte precisa de atenção? MESMA régua do backend (o número do resumo e a lista não divergem). */
  needsAttention(s: { enabled: boolean; everSynced: boolean; status: string; staleDays: number | null }): boolean {
    if (!s.enabled) return false;
    if (!s.everSynced) return true;
    if (s.status !== 'Healthy') return true;
    return (s.staleDays ?? 0) >= 7;
  }

  /** Texto operacional do estado de uma fonte — sem jargão de implementação. */
  sourceStateText(s: {
    enabled: boolean;
    everSynced: boolean;
    status: string;
    staleDays: number | null;
  }): string {
    if (!s.enabled) return 'Desabilitada';
    if (!s.everSynced) return 'Nunca sincronizou';
    if (s.status === 'Failed') return 'Falha na última coleta';
    if (s.status === 'Degraded') return 'Coleta degradada';
    const days = s.staleDays ?? 0;
    if (days >= 7) return `Leitura de ${days} dias atrás`;
    if (days >= 1) return `Leitura de ${days} dia(s) atrás`;
    return 'Leitura de hoje';
  }

  /**
   * Carrega (ou recarrega) a visão geral. LIMPA a tela ANTES de disparar: nenhum valor da carga anterior —
   * nem de outro ambiente após uma troca — sobrevive a um novo pedido, muito menos a uma falha.
   */
  private load(): void {
    this.loading.set(true);
    this.loadError.set(false);
    this.data.set(null);
    this.legacy.set(null);
    this.gapBalance.set(null);
    this.blastRadius.set(null);
    this.trend.set([]);

    // Caminho crítico: UMA requisição traz o quadro inteiro, já composto pelo backend.
    this.svc.fetchOverview().subscribe({
      next: (d) => {
        this.data.set(d);
        this.loadError.set(false);
        this.loading.set(false);
        this.loadBusinessRiskDetail(d);
      },
      error: (err) => {
        console.error('Falha ao carregar a visão geral:', err);
        this.data.set(null);
        this.loadError.set(true);
        this.loading.set(false);
      },
    });

    // Tendência: painel SECUNDÁRIO com estado próprio — falhar aqui não derruba a tela.
    this.scoreSvc.fetchTrend(30).subscribe({
      next: (t) => this.trend.set(trendToSparkline(t)),
      error: (err) => console.warn('Tendência indisponível (a faixa se omite):', err),
    });

    // Raio de explosão: 204 → null (nunca calculado). O painel simplesmente não aparece.
    this.svc.fetchBlastRadiusSummary().subscribe({
      next: (s) => this.blastRadius.set(s),
      error: (err) => console.warn('Raio de impacto indisponível:', err),
    });
  }

  /**
   * Detalhe de maturidade/risco. Só dispara quando a leitura composta CONFIRMA que a dimensão tem avaliação:
   * num ambiente sem assessment nenhuma dessas requisições sai, e nenhum gráfico legado é montado zerado.
   */
  private loadBusinessRiskDetail(d: DashboardOverview): void {
    const b = d.businessRisk;
    if (b.maturityState !== 'Available' && b.icrState !== 'Available' && b.riskRegisterState !== 'Available') {
      return;
    }

    this.svc.fetchExecutive().subscribe({
      next: (l) => this.legacy.set(l),
      error: (err) => console.warn('Detalhe de maturidade/risco indisponível:', err),
    });

    // Balanço de lacunas — deriva da MESMA matriz de controles que as telas de Função consomem.
    this.scoringSvc.getDashboard().subscribe({
      next: (rows) => this.gapBalance.set(buildGapBalance(rows)),
      error: (err) => console.warn('Balanço de lacunas indisponível:', err),
    });
  }
}

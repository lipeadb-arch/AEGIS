import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BlastRadiusSummary, ExecutiveDashboard, GapBalance } from '../models/dashboard.models';
import {
  DashboardIdentityGap,
  DashboardMetric,
  DashboardOverview,
  hasReading,
  identityCapabilityLabel,
  identityOutcomeLabel,
  stateLabel,
  workspaceFromOverview,
} from '../models/dashboard-overview.models';
import { ComplianceHistoryPoint, buildGapBalance, trendToSparkline } from '../models/scoring.models';
import { environmentStage } from '../models/workspace.models';
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
  ],
  template: `
    <div class="page">
      <header class="page-head">
        <div class="ph-title">
          <h1>Visão geral</h1>
          @if (data(); as d) {
            <p class="ph-sub">
              {{ d.clientName }}
              @if (d.generatedAt) {
                <span class="ph-when">· leitura de {{ d.generatedAt | date: 'dd/MM HH:mm' }}</span>
              }
            </p>
          }
        </div>
        <div class="ph-actions">
          <a class="ghost" routerLink="/priorities">Ver prioridades</a>
          <button type="button" class="ghost" (click)="reload()" [disabled]="loading()">
            {{ loading() ? 'Carregando…' : 'Atualizar' }}
          </button>
        </div>
      </header>

      <!-- A leitura é o bloco PRIMÁRIO porque o alias 'as' só é permitido nele (NG5002). Carga e falha vêm
           enquanto não há leitura REAL, a tela mostra estado, nunca números remanescentes. -->
      @if (data(); as d) {

        <!-- ============================ 1) AMBIENTE OBSERVADO ============================ -->
        <section class="block">
          <div class="block-head">
            <h2>O que já foi observado</h2>
            <a class="linknav" routerLink="/assets">Ver ambiente →</a>
          </div>

          <div class="metrics">
            @for (m of environmentMetrics(); track m.key) {
              <a class="metric" [class.is-void]="!hasReading(m.metric)" [class.is-partial]="m.metric.state === 'Partial'" [routerLink]="m.link">
                <span class="m-k">{{ m.label }}</span>
                @if (hasReading(m.metric)) {
                  <span class="m-v">{{ m.metric.value }}</span>
                } @else {
                  <span class="m-v is-na">—</span>
                }
                <span class="m-state">{{ stateLabel(m.metric.state) }}</span>
                <span class="m-src">{{ m.metric.sourceLabel }}</span>
              </a>
            }
          </div>

          <!-- Uma nota por métrica sem leitura, agrupada: explica o vazio sem poluir cada cartão. -->
          @if (metricNotes().length > 0) {
            <ul class="notes">
              @for (n of metricNotes(); track n.label) {
                <li><b>{{ n.label }}</b> · {{ n.note }}</li>
              }
            </ul>
          }
        </section>

        <!-- ============================ 2) POSTURA AVALIADA ============================ -->
        <section class="block">
          <div class="block-head">
            <h2>Quanto foi avaliado</h2>
            <a class="linknav" routerLink="/controls">Ver controles →</a>
          </div>

          <div class="grid two">
            <div class="panel">
              <app-posture-summary [posture]="d.posture" label="AEGIS Score" />
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
                <h3>Cobertura por natureza da prova</h3>
                <span class="hint">cobertura não é conformidade</span>
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
        <section class="block">
          <div class="block-head">
            <h2>O que merece atenção</h2>
            <a class="linknav" routerLink="/priorities">Central de Prioridades →</a>
          </div>

          <div class="grid two">
            <div class="panel">
              <div class="hd">
                <h3>Exposições de configuração</h3>
                <span class="hint">{{ d.configurationExposures.summary.sourceLabel }}</span>
              </div>
              @if (d.configurationExposures.top.length > 0) {
                <ul class="queue">
                  @for (e of d.configurationExposures.top; track e.id) {
                    <li>
                      <span class="q-t">{{ e.displayTitle || e.title }}</span>
                      <span class="q-m">{{ e.plainSummary || e.category || 'Configuração exposta' }}</span>
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
        <section class="block">
          <div class="block-head">
            <h2>Identidades</h2>
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
                <div class="cap-col">
                  <span class="cap-k ok">Disponível agora</span>
                  <ul>
                    @for (c of d.identity.capabilitiesCollected; track c) {
                      <li>{{ identityCapabilityLabel(c) }}</li>
                    }
                  </ul>
                </div>
                @if (d.identity.capabilitiesMissing.length > 0) {
                  <div class="cap-col">
                    <!-- Parcialidade NÃO é "integração sem dados": o que falta é nomeado com o motivo real. -->
                    <span class="cap-k mid">Ainda indisponível</span>
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
        <section class="block">
          <div class="block-head">
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
        <section class="block">
          <div class="block-head">
            <div class="bh-title">
              <h2>Risco de negócio</h2>
              <span class="bh-sub">maturidade, criticidade e registro de riscos</span>
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
                  label="Processos críticos expostos"
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

            <div class="grid two">
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
        <section class="panel state">
          <span class="scan" aria-hidden="true"></span>
          <b>Consolidando a leitura do ambiente…</b>
        </section>
      } @else {
        <!-- Falha operacional: NUNCA cair em exemplo nem reaproveitar a carga anterior — e sem mandar o
             cliente investigar console, endereço de API ou identificador técnico. -->
        <section class="panel state is-error">
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
      /* ---------- Página ---------- */
      .page {
        /* Folga inferior generosa: o FAB do Auditor flutua no canto inferior direito, e sem esta reserva
           o último cartão da página ficaria permanentemente sob ele. */
        padding: 20px 26px 104px;
        max-width: 1320px;
      }
      .page-head {
        display: flex;
        flex-wrap: wrap;
        gap: 12px;
        align-items: flex-end;
        justify-content: space-between;
        margin-bottom: 22px;
      }
      .page-head h1 {
        margin: 0;
        font-family: var(--display);
        font-size: 22px;
        font-weight: 700;
        letter-spacing: 0.02em;
        color: var(--text);
      }
      .ph-sub {
        margin: 5px 0 0;
        font-family: var(--mono);
        font-size: 12px;
        color: var(--muted);
      }
      .ph-when {
        opacity: 0.8;
      }
      .ph-actions {
        display: flex;
        gap: 8px;
        flex: none;
      }
      .ghost,
      .primary {
        appearance: none;
        cursor: pointer;
        font-family: var(--mono);
        font-size: 11.5px;
        letter-spacing: 0.03em;
        text-decoration: none;
        padding: 8px 14px;
        border-radius: 8px;
        border: 1px solid var(--line);
        background: rgba(122, 145, 190, 0.06);
        color: var(--text);
        transition: 0.15s;
      }
      .ghost:hover:not(:disabled) {
        border-color: color-mix(in srgb, var(--cyan) 40%, var(--line));
        background: rgba(38, 224, 255, 0.08);
      }
      .ghost:disabled {
        opacity: 0.55;
        cursor: progress;
      }
      .primary {
        border-color: var(--cyan);
        background: rgba(38, 224, 255, 0.12);
      }

      /* ---------- Blocos ---------- */
      .block {
        margin: 0 0 26px;
      }
      .block-head {
        display: flex;
        align-items: baseline;
        justify-content: space-between;
        gap: 12px;
        margin: 0 0 12px;
        /* Título e ação não colidem: o link encolhe antes do título. */
        flex-wrap: wrap;
      }
      .bh-title {
        display: flex;
        flex-direction: column;
        gap: 3px;
        min-width: 0;
      }
      /* Subtítulo do bloco: ABAIXO do título, nunca na borda oposta (ali competia por leitura com ele). */
      .bh-sub {
        font-family: var(--mono);
        font-size: 10.5px;
        color: var(--muted);
      }
      .block-head h2 {
        margin: 0;
        font-family: var(--sans);
        font-size: 14px;
        font-weight: 600;
        letter-spacing: 0.06em;
        text-transform: uppercase;
        color: var(--muted);
      }
      .linknav {
        font-family: var(--mono);
        font-size: 11.5px;
        color: var(--cyan);
        text-decoration: none;
        white-space: nowrap;
      }
      .linknav:hover {
        text-decoration: underline;
      }

      .panel {
        border: 1px solid var(--line);
        border-radius: 12px;
        background: var(--panel);
        padding: 16px 18px;
      }
      /* O título nunca é espremido pela dica: em painel estreito ela desce para a linha de baixo. */
      .panel .hd {
        display: flex;
        align-items: baseline;
        justify-content: space-between;
        flex-wrap: wrap;
        gap: 4px 10px;
        margin: 0 0 12px;
      }
      .panel .hd h3 {
        margin: 0;
        font-family: var(--sans);
        font-size: 14px;
        font-weight: 600;
        color: var(--text);
      }
      .panel .hint {
        font-family: var(--mono);
        font-size: 10.5px;
        color: var(--muted);
        white-space: nowrap;
      }
      .panel-empty {
        margin: 0;
        font-family: var(--sans);
        font-size: 12.5px;
        line-height: 1.6;
        color: var(--muted);
      }
      .panel-empty b {
        color: var(--text);
        font-weight: 600;
      }
      .lead {
        margin: 0 0 12px;
        font-family: var(--sans);
        font-size: 13px;
        line-height: 1.6;
        color: var(--text);
      }
      .lead b {
        font-weight: 600;
      }
      .lead .warn {
        color: var(--amber);
      }
      .foot {
        margin: 12px 0 0;
        padding-top: 10px;
        border-top: 1px solid var(--line-2);
        font-family: var(--sans);
        font-size: 12.5px;
        line-height: 1.55;
        color: var(--muted);
      }

      /* ---------- Estado da página ---------- */
      .state {
        display: flex;
        flex-direction: column;
        gap: 10px;
        align-items: flex-start;
        font-family: var(--sans);
        font-size: 13px;
        color: var(--text);
      }
      .state h3 {
        margin: 0;
        font-family: var(--sans);
        font-size: 15px;
        font-weight: 600;
      }
      .state p {
        margin: 0;
        max-width: 62ch;
        line-height: 1.6;
        color: var(--muted);
      }
      .state.is-error {
        border-left: 3px solid var(--red);
      }
      .state .scan {
        display: inline-block;
        width: 11px;
        height: 11px;
        border-radius: 50%;
        border: 2px solid rgba(38, 224, 255, 0.25);
        border-top-color: var(--cyan);
        animation: exec-spin 0.75s linear infinite;
      }
      @keyframes exec-spin {
        to {
          transform: rotate(360deg);
        }
      }
      @media (prefers-reduced-motion: reduce) {
        .state .scan {
          animation: none;
        }
      }

      /* ---------- Métricas do ambiente ---------- */
      /* auto-fit + minmax: em 1366px cabem 5 colunas; abaixo disso quebra sozinho, sem rolagem lateral. */
      .metrics {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(176px, 1fr));
        gap: 12px;
      }
      .metric {
        display: flex;
        flex-direction: column;
        gap: 3px;
        padding: 14px 16px;
        border: 1px solid var(--line);
        border-radius: 12px;
        background: var(--panel);
        text-decoration: none;
        transition: 0.15s;
        min-width: 0;
      }
      .metric:hover {
        border-color: color-mix(in srgb, var(--cyan) 35%, var(--line));
      }
      .m-k {
        font-family: var(--mono);
        font-size: 10px;
        letter-spacing: 0.12em;
        text-transform: uppercase;
        color: var(--muted);
      }
      .m-v {
        font-family: var(--display);
        font-weight: 700;
        font-size: 26px;
        line-height: 1.15;
        color: var(--text);
      }
      .m-v.is-na {
        color: var(--muted);
        opacity: 0.6;
      }
      .m-state {
        font-family: var(--mono);
        font-size: 10.5px;
        color: var(--cyan);
        opacity: 0.85;
      }
      .metric.is-void .m-state {
        color: var(--muted);
      }
      .metric.is-partial .m-state {
        color: var(--amber);
      }
      .m-src {
        font-family: var(--mono);
        font-size: 10px;
        color: var(--muted);
        opacity: 0.7;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
      .notes {
        list-style: none;
        margin: 12px 0 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 5px;
      }
      .notes li {
        font-family: var(--sans);
        font-size: 12px;
        line-height: 1.55;
        color: var(--muted);
      }
      .notes b {
        color: var(--text);
        font-weight: 600;
      }

      /* ---------- Grades ---------- */
      .grid.two {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(340px, 1fr));
        gap: 14px;
      }
      .cards {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(230px, 1fr));
        gap: 12px;
        margin-bottom: 14px;
      }

      /* ---------- Cobertura ---------- */
      .coverage {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 10px;
      }
      .coverage li {
        display: grid;
        grid-template-columns: minmax(0, 1fr) clamp(64px, 18%, 128px) 58px;
        align-items: center;
        gap: 10px;
      }
      /* O rótulo QUEBRA em vez de truncar: em painéis estreitos "Avaliação orientada" virava
         "Avaliação orient…", e o nome da natureza da prova é justamente o que se lê aqui. */
      .c-k {
        font-family: var(--sans);
        font-size: 12.5px;
        line-height: 1.35;
        color: var(--text);
        min-width: 0;
      }
      .c-bar {
        display: block;
        height: 6px;
        border-radius: 3px;
        background: rgba(122, 145, 190, 0.14);
        overflow: hidden;
      }
      /* NEUTRO de propósito: cobertura não é conformidade — nada de verde/vermelho aqui. */
      .c-bar i {
        display: block;
        height: 100%;
        background: var(--cyan);
        opacity: 0.55;
      }
      .c-v {
        font-family: var(--mono);
        font-size: 11px;
        color: var(--muted);
        text-align: right;
      }

      /* ---------- Filas ---------- */
      .queue {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 10px;
      }
      .queue li {
        display: flex;
        flex-direction: column;
        gap: 2px;
        padding-bottom: 10px;
        border-bottom: 1px solid var(--line-2);
      }
      .queue li:last-child {
        border-bottom: none;
        padding-bottom: 0;
      }
      .q-t {
        font-family: var(--sans);
        font-size: 13px;
        line-height: 1.45;
        color: var(--text);
      }
      .q-m {
        font-family: var(--mono);
        font-size: 10.5px;
        color: var(--muted);
      }

      /* ---------- Identidade ---------- */
      .caps {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
        gap: 14px;
      }
      .cap-k {
        display: block;
        font-family: var(--mono);
        font-size: 10px;
        letter-spacing: 0.12em;
        text-transform: uppercase;
        margin-bottom: 7px;
      }
      .cap-k.ok {
        color: var(--cyan);
      }
      .cap-k.mid {
        color: var(--amber);
      }
      .caps ul {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 5px;
      }
      .caps li {
        font-family: var(--sans);
        font-size: 12.5px;
        line-height: 1.5;
        color: var(--text);
      }
      .caps li em {
        display: block;
        font-style: normal;
        font-family: var(--mono);
        font-size: 10.5px;
        color: var(--muted);
      }

      /* ---------- Fontes ---------- */
      .sources {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 8px;
      }
      .sources li {
        display: grid;
        grid-template-columns: minmax(0, 1.2fr) minmax(0, 1fr) minmax(0, 1.3fr);
        gap: 10px;
        align-items: baseline;
        padding-bottom: 8px;
        border-bottom: 1px solid var(--line-2);
      }
      .sources li:last-child {
        border-bottom: none;
        padding-bottom: 0;
      }
      .s-n {
        font-family: var(--sans);
        font-size: 12.5px;
        color: var(--text);
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
      .s-c,
      .s-s {
        font-family: var(--mono);
        font-size: 10.5px;
        color: var(--muted);
      }
      .sources li.attention .s-s {
        color: var(--amber);
      }

      /* ---------- Tendência ---------- */
      .trend-strip {
        display: flex;
        align-items: center;
        gap: 12px;
        margin-top: 14px;
        padding-top: 12px;
        border-top: 1px solid var(--line-2);
      }
      .ts-meta {
        display: flex;
        flex-direction: column;
        gap: 1px;
        min-width: 0;
      }
      .ts-delta {
        font-family: var(--display);
        font-weight: 700;
        font-size: 13px;
        color: var(--muted);
      }
      .ts-delta.up {
        color: var(--cyan);
      }
      .ts-delta.down {
        color: var(--red);
      }
      .ts-meta em {
        font-style: normal;
        font-family: var(--mono);
        font-size: 10px;
        color: var(--muted);
        opacity: 0.75;
      }

      /* Telas estreitas: nada rola lateralmente — as grades já colapsam sozinhas. */
      @media (max-width: 720px) {
        .page {
          padding: 16px 14px 48px;
        }
        .sources li {
          grid-template-columns: 1fr;
          gap: 2px;
        }
        .coverage li {
          grid-template-columns: minmax(0, 1fr) 60px 52px;
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
    return [
      { key: 'assets', label: 'Ativos', metric: e.assets, link: '/assets' },
      { key: 'exposures', label: 'Configurações expostas', metric: e.configurationExposures, link: '/exposures' },
      { key: 'vulns', label: 'Vulnerabilidades', metric: e.vulnerabilities, link: '/vulnerabilities' },
      { key: 'affected', label: 'Ativos afetados', metric: e.affectedAssets, link: '/vulnerabilities' },
      { key: 'identity', label: 'Identidades', metric: e.identity, link: '/identity' },
    ];
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

  /** Fila vazia de exposições: "nunca coletado" ≠ "coletado sem achados" — a diferença muda a decisão. */
  readonly exposureEmptyText = computed(() =>
    this.data()?.environment.configurationExposures.state === 'NeverCollected'
      ? 'Ainda não coletado — nenhuma leitura de configuração foi feita neste ambiente.'
      : 'Nenhuma exposição de configuração aberta na última leitura.',
  );

  /** Vazio de identidade: "sem fonte" e "fonte conectada sem coleta" pedem ações diferentes. */
  readonly identityEmptyText = computed(() =>
    this.data()?.identity.state === 'NoSource'
      ? 'Nenhuma fonte de identidade conectada neste ambiente.'
      : 'Fonte de identidade conectada, ainda sem coleta concluída.',
  );

  readonly vulnerabilityEmptyText = computed(() =>
    this.data()?.environment.vulnerabilities.state === 'NeverCollected'
      ? 'Ainda não coletado — nenhuma varredura de vulnerabilidades chegou a este ambiente.'
      : 'Nenhuma vulnerabilidade aberta na última leitura.',
  );

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

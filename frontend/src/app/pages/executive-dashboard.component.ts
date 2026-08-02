import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { BlastRadiusSummary, ExecutiveDashboard, GapBalance } from '../models/dashboard.models';
import { ComplianceHistoryPoint, buildGapBalance, trendToSparkline } from '../models/scoring.models';
import { WorkspacePosture } from '../models/workspace.models';
import { PostureSummaryComponent } from '../components/scoring/posture-summary.component';
import { DashboardService } from '../services/dashboard.service';
import { AegisScoreService } from '../services/aegis-score.service';
import { ScoringService } from '../services/scoring.service';
import { SparklineComponent } from '../components/scoring/sparkline.component';
import { GapBalanceComponent } from '../components/scoring/gap-balance.component';
import { BlastRadiusSummaryComponent } from '../components/scoring/blast-radius-summary.component';
import { icrColor } from '../lib/scales';
import { environment } from '../../environments/environment';
import { IcrGaugeComponent } from '../components/icr-gauge.component';
import { RiskHeatmapComponent } from '../components/risk-heatmap.component';
import { GapChartComponent } from '../components/gap-chart.component';
import { RiskLevelsComponent } from '../components/risk-levels.component';
import { ExposureCardComponent } from '../components/exposure-card.component';
import { MaturityGaugeComponent } from '../components/maturity-gauge.component';
import { MaturityBarsComponent, FunctionScore } from '../components/maturity-bars.component';

@Component({
  selector: 'app-executive-dashboard',
  standalone: true,
  imports: [
    DatePipe,
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
  ],
  template: `
    <div class="app">
      <header class="topbar">
        <div class="brand">
          <span class="mark">Aegis <b>Score</b></span>
          <span class="sub">Auditoria de Maturidade Cibernética · Synapse OS</span>
        </div>
        <!-- Cliente e ICR só aparecem com dados REAIS. Durante carga ou erro não há postura para
             exibir, e um nome/ICR remanescente leria como a leitura atual de outro cliente. -->
        @if (data(); as d) {
          <div class="client">
            <span class="label">Cliente</span>
            <span class="name">{{ d.clientName }}</span>
            @if (generatedAt()) {
              <span class="label" title="Instante da apuração no servidor">
                apurado {{ generatedAt() | date: 'dd/MM HH:mm' }}
              </span>
            }
            <span class="icr-pill" title="Índice de Criticidade de Risco Cibernético">
              <span class="dot" [style.background]="icrColor(d.icr.band)"></span>
              <span class="v" [style.color]="icrColor(d.icr.band)">
                {{ d.icr.score.toFixed(0) }}
              </span>
              <span class="b">ICR · {{ d.icr.band }}</span>
            </span>
          </div>
        }
      </header>

      <!-- AEGIS Score (aegis-score-v1): a postura DETERMINÍSTICA da projeção única (/scoring/workspace),
           independente da maturidade CMMI/ICR abaixo — que é OUTRA métrica. Tem estado próprio de carga. -->
      <section class="aegis-band">
        @if (workspace(); as w) {
          <div class="band-grid">
            <app-posture-summary [posture]="w.overall" label="AEGIS Score" />
            <div class="band-cards">
              <div class="bcard" [class.hot]="w.overall.nonCompliantControls > 0">
                <span class="bk">Não conformes</span>
                <span class="bv">{{ w.overall.nonCompliantControls }}</span>
                <span class="bsub">{{ w.overall.notEvaluatedControls }} não avaliados</span>
              </div>
              <div class="bcard">
                <span class="bk">Conectores</span>
                <span class="bv">{{ w.connectors.healthy }}/{{ w.connectors.total }} saudáveis</span>
                <span class="bsub">
                  @if (w.connectors.neverSynced > 0) {
                    {{ w.connectors.neverSynced }} nunca sincronizados
                  } @else if (w.connectors.failed > 0) {
                    {{ w.connectors.failed }} com falha
                  } @else { operacionais }
                </span>
              </div>
              <div class="bcard">
                <span class="bk">Última sincronização</span>
                <span class="bv">{{ w.connectors.lastSyncAt ? (w.connectors.lastSyncAt | date: 'dd/MM HH:mm') : '—' }}</span>
                <span class="bsub">
                  {{ w.overall.latestEvidenceAt ? ('evidência ' + (w.overall.latestEvidenceAt | date: 'dd/MM')) : 'sem evidência' }}
                </span>
              </div>
            </div>
          </div>
        } @else if (!workspaceLoaded()) {
          <p class="band-pulse">Consolidando o AEGIS Score…</p>
        } @else {
          <p class="band-pulse">AEGIS Score indisponível — a API não respondeu. O console traz o erro.</p>
        }
      </section>

      @if (loading()) {
        <div class="notice">
          <span class="scan" aria-hidden="true"></span>
          <b>Consolidando a postura do cliente…</b>
        </div>
      } @else if (loadError()) {
        <!-- Falha operacional da API: NUNCA cair em dados de demonstração nem reaproveitar a carga
             anterior. Sem indicadores, estado de erro explícito (distinto do estado vazio) e uma
             nova tentativa. Zerar ou reusar a resposta anterior apresentaria dados fictícios — ou
             de outro tenant — como se fossem a postura real deste cliente. -->
        <section class="empty-state is-error">
          <span class="es-mark" aria-hidden="true">
            <svg viewBox="0 0 24 24" width="26" height="26" fill="none" stroke="currentColor" stroke-width="1.3">
              <path d="M12 3.5 2.4 20.2h19.2L12 3.5Z" stroke-linejoin="round" />
              <path d="M12 10v4M12 16.8v.2" stroke-linecap="round" />
            </svg>
          </span>
          <h3>Não foi possível carregar a postura do cliente</h3>
          <p>
            A API não respondeu em <code>{{ apiBase }}</code>. Nenhum indicador é exibido — <b>um
            painel de exemplo ou os números da carga anterior seriam lidos como a postura real</b>.
            Confira o endereço da API e o <code>tenantId</code> do cliente; o console traz o erro
            completo.
          </p>
          <button type="button" class="retry" (click)="reload()">Tentar novamente</button>
        </section>
      } @else {
        @if (data(); as d) {

      <!-- Tenant provisionado e ainda sem medição: um painel de zeros leria como "nenhum risco".
           O estado vazio diz o que falta e por onde começar, em vez de fingir uma leitura. -->
      @if (!hasPosture()) {
        <section class="empty-state">
          <span class="es-mark" aria-hidden="true">
            <svg viewBox="0 0 24 24" width="26" height="26" fill="none" stroke="currentColor" stroke-width="1.3">
              <path d="M12 3 4 6.5v5c0 4.6 3.2 8.4 8 9.5 4.8-1.1 8-4.9 8-9.5v-5L12 3Z" stroke-linejoin="round" />
              <path d="M12 9.5v4M12 16.2v.2" stroke-linecap="round" />
            </svg>
          </span>
          <h3>Nenhuma postura medida para {{ d.clientName }}</h3>
          <p>
            O tenant está provisionado, mas ainda não recebeu evidência. Os indicadores abaixo só
            passam a significar algo depois da primeira medição — <b>zero aqui não é ausência de
            risco, é ausência de leitura</b>.
          </p>
          <ul class="es-steps">
            <li><b>Ligue um conector</b> de telemetria para as Funções técnicas (PR · DE · RS · RC).</li>
            <li><b>Suba as políticas</b> no Document Hub para cobrir a Função Govern (GV).</li>
            <li><b>Rode um assessment</b> para popular a maturidade CMMI por Função.</li>
          </ul>
        </section>
      } @else {

      <p class="eyebrow">Exposição do negócio</p>
      <section class="cards">
        <app-exposure-card
          label="Processos críticos expostos"
          [value]="d.exposure.criticalProcessesExposed"
          tone="danger"
        />
        <app-exposure-card
          label="Controles inefetivos"
          [value]="d.exposure.ineffectiveControls"
          tone="warn"
        />
        <app-exposure-card
          label="Planos de ação vencidos"
          [value]="d.exposure.overdueActionPlans"
          tone="danger"
        />
      </section>

      <!-- Painéis principais (Cenário Hollywood): gauge de maturidade + histograma por função. -->
      <div class="grid main">
        <div class="panel">
          <div class="hd">
            <h3>Maturidade Geral</h3>
            <span class="hint">CMMI 1–5 · alvo {{ targetMaturity().toFixed(1) }}</span>
          </div>
          <app-maturity-gauge [value]="overallMaturity()" [max]="chartScale()" />

          <!-- A DERIVADA do risco: o gauge diz onde estamos, a curva diz para onde vamos. Carrega
               depois do painel principal e se omite sozinha com menos de 2 snapshots. -->
          @if (trend().length > 1) {
            <div class="trend-strip">
              <app-sparkline [points]="trend()" />
              <span class="ts-meta">
                <b
                  class="ts-delta"
                  [class.up]="(trendDelta() ?? 0) > 0"
                  [class.down]="(trendDelta() ?? 0) < 0"
                >
                  {{ (trendDelta() ?? 0) > 0 ? '▲' : (trendDelta() ?? 0) < 0 ? '▼' : '■' }}
                  {{ trendDelta() }} p.p.
                </b>
                <em>Aegis Score · {{ trend().length }} dias</em>
              </span>
            </div>
          }
        </div>

        <div class="panel">
          <div class="hd">
            <h3>Maturidade por Função</h3>
            <!-- 1 casa: com toFixed(0) a escala 4,42 virava "0–4" ao lado de um gauge marcado 4,4. -->
            <span class="hint">escala 0–{{ chartScale().toFixed(1) }} · alvo {{ targetMaturity().toFixed(1) }}</span>
          </div>
          <app-maturity-bars [data]="maturityBars()" [max]="chartScale()" />
        </div>
      </div>

      <!-- As duas perguntas de diretoria: "o que falta se compra ou se escreve?" e "quanto custa se
           cair?". Painéis SECUNDÁRIOS — carregam por conta própria, fora do caminho do FCP. -->
      <div class="grid main">
        <div class="panel">
          <div class="hd">
            <h3>Onde está o esforço</h3>
            <span class="hint">ferramenta (capex) × processo (opex)</span>
          </div>
          @if (gapBalance()) {
            <app-gap-balance [balance]="gapBalance()" />
          } @else {
            <p class="panel-empty">Consolidando lacunas de evidência…</p>
          }
        </div>

        <div class="panel">
          <div class="hd">
            <h3>Custo do fracasso</h3>
            <span class="hint">raio de explosão · pior cenário</span>
          </div>
          @if (blastLoaded()) {
            <app-blast-radius-summary [summary]="blastRadius()" />
          } @else {
            <p class="panel-empty">Calculando o alcance do pior cenário…</p>
          }
        </div>
      </div>

      <div class="grid trio">
        <div class="panel">
          <div class="hd"><h3>Índice de Criticidade (ICR)</h3></div>
          <app-icr-gauge [icr]="d.icr" />
          <div class="hd" style="margin-top:18px">
            <h3 style="font-size:13px;color:var(--muted)">Riscos por nível</h3>
          </div>
          @if (d.riskByLevel.length > 0) {
            <app-risk-levels [data]="d.riskByLevel" />
          } @else {
            <p class="panel-empty">Nenhum risco classificado ainda.</p>
          }
        </div>

        <div class="panel">
          <div class="hd">
            <h3>Maiores gaps por categoria</h3>
            <span class="hint">distância até o alvo</span>
          </div>
          @if (d.topGaps.length > 0) {
            <app-gap-chart [data]="d.topGaps" />
          } @else {
            <p class="panel-empty">Sem categorias avaliadas — os gaps surgem com o primeiro assessment.</p>
          }
        </div>

        <div class="panel">
          <div class="hd"><h3>Matriz de risco</h3></div>
          @if (d.riskHeatmap.length > 0) {
            <app-risk-heatmap [data]="d.riskHeatmap" />
          } @else {
            <p class="panel-empty">Sem riscos avaliados — a matriz aparece após o primeiro assessment.</p>
          }
        </div>
      </div>
      }
        }
      }
    </div>
  `,
  styles: [
    `
      /* Banda do AEGIS Score (projeção única) — acima da maturidade, com seu próprio estado de carga. */
      .aegis-band {
        margin: 0 0 20px;
      }
      .aegis-band .band-pulse {
        font-family: var(--mono);
        font-size: 12px;
        color: var(--muted);
        margin: 0;
        padding: 14px 2px;
      }
      .aegis-band .band-grid {
        display: grid;
        grid-template-columns: minmax(260px, 340px) 1fr;
        gap: 14px;
        align-items: stretch;
      }
      .aegis-band .band-cards {
        display: grid;
        grid-template-columns: repeat(3, 1fr);
        gap: 12px;
      }
      .aegis-band .bcard {
        display: flex;
        flex-direction: column;
        gap: 3px;
        justify-content: center;
        padding: 12px 14px;
        border: 1px solid var(--line);
        border-radius: 12px;
        background: rgba(122, 145, 190, 0.03);
      }
      .aegis-band .bcard.hot {
        border-color: rgba(255, 45, 111, 0.4);
        background: rgba(255, 45, 111, 0.05);
      }
      .aegis-band .bk {
        font-family: var(--mono);
        font-size: 10px;
        letter-spacing: 0.12em;
        text-transform: uppercase;
        color: var(--muted);
      }
      .aegis-band .bv {
        font-family: var(--display);
        font-weight: 700;
        font-size: 19px;
        color: var(--text);
      }
      .aegis-band .bcard.hot .bv {
        color: var(--red);
      }
      .aegis-band .bsub {
        font-family: var(--mono);
        font-size: 10.5px;
        color: var(--muted);
      }
      @media (max-width: 860px) {
        .aegis-band .band-grid {
          grid-template-columns: 1fr;
        }
      }

      /* Estado vazio do painel executivo — mesma linguagem HUD do resto do Synapse OS. */
      .empty-state {
        border: 1px solid var(--line);
        border-left: 3px solid var(--cyan);
        border-radius: 12px;
        background: linear-gradient(90deg, rgba(38, 224, 255, 0.05), rgba(38, 224, 255, 0.01));
        padding: 22px 24px;
        margin-top: 8px;
        max-width: 760px;
      }
      /* Estado de ERRO — mesma família visual, porém em tom de alerta (vermelho) e com nova
         tentativa, para NUNCA ser confundido com "resposta vazia". A falha não vira zero nem exemplo. */
      .empty-state.is-error {
        border-left-color: var(--red);
        background: linear-gradient(90deg, rgba(255, 45, 111, 0.06), rgba(255, 45, 111, 0.01));
      }
      .empty-state.is-error .es-mark {
        color: var(--red);
      }
      .empty-state.is-error b {
        color: var(--red);
      }
      .es-mark {
        display: inline-flex;
        color: var(--cyan);
        opacity: 0.85;
      }
      .empty-state h3 {
        margin: 10px 0 8px;
        font-family: var(--display);
        font-size: 17px;
        font-weight: 600;
        color: var(--text);
      }
      .empty-state p {
        margin: 0 0 14px;
        font-family: var(--sans);
        font-size: 13px;
        line-height: 1.6;
        color: var(--text);
        opacity: 0.86;
      }
      .empty-state b {
        color: var(--cyan);
        font-weight: 600;
      }
      .es-steps {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 8px;
      }
      .es-steps li {
        position: relative;
        padding-left: 18px;
        font-family: var(--mono);
        font-size: 11.5px;
        line-height: 1.55;
        color: var(--muted);
      }
      .es-steps li::before {
        content: '▸';
        position: absolute;
        left: 0;
        color: var(--cyan);
      }
      .es-steps b {
        color: var(--text);
        font-weight: 600;
      }
      /* Botão de nova tentativa no estado de erro. */
      .retry {
        appearance: none;
        cursor: pointer;
        font-family: var(--mono);
        font-size: 11.5px;
        letter-spacing: 0.02em;
        color: var(--text);
        background: rgba(255, 45, 111, 0.08);
        border: 1px solid var(--red);
        border-radius: 8px;
        padding: 8px 16px;
        transition: background 0.15s ease;
      }
      .retry:hover {
        background: rgba(255, 45, 111, 0.16);
      }

      /* Faixa de tendência sob o gauge: a curva + a variação ponta a ponta. */
      .trend-strip {
        display: flex;
        align-items: center;
        gap: 12px;
        margin-top: 12px;
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
      /* Subir é bom (cyan), cair é ruim (vermelho) — a MESMA régua do resto do produto. */
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

      /* Painel individual sem dados: nota discreta, nunca um gráfico vazio sem explicação. */
      .panel-empty {
        margin: 6px 0 0;
        font-family: var(--mono);
        font-size: 11.5px;
        line-height: 1.55;
        color: var(--muted);
      }

      .notice .scan {
        display: inline-block;
        width: 11px;
        height: 11px;
        margin-right: 8px;
        vertical-align: -1px;
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
        .notice .scan {
          animation: none;
        }
      }
    `,
  ],
})
export class ExecutiveDashboardComponent implements OnInit {
  private readonly svc = inject(DashboardService);
  private readonly scoreSvc = inject(AegisScoreService);
  private readonly scoringSvc = inject(ScoringService);

  // Começa NULO: o dashboard operacional nunca nasce com uma postura de exemplo. Só recebe conteúdo
  // de uma resposta REAL da API; qualquer falha o mantém nulo e aciona o estado de erro (ver `load`).
  data = signal<ExecutiveDashboard | null>(null);
  loadError = signal(false);

  // ---- AEGIS Score (aegis-score-v1) da projeção única — independente da maturidade acima ----
  workspace = signal<WorkspacePosture | null>(null);
  workspaceLoaded = signal(false);

  // ---- Maturidade: valores AUTORITATIVOS do backend ----
  // O ExposureCardsDto já traz `overallMaturity`/`targetMaturity` prontos, calculados pelo
  // MaturityScoringService. Eram campos ÓRFÃOS — o backend os enviava e o frontend os ignorava.
  //
  // ⚠️ Recalcular aqui a média de `maturityByFunction` (o que esta tela fazia) DIVERGE do servidor, e
  // não por arredondamento: o radar preenche com 0 toda Função sem avaliação (`agg?.CurrentScore ?? 0`
  // no DashboardController), enquanto o rollup só promedia as Funções que TÊM dados. Num tenant com só
  // Govern avaliado em 3.0, o servidor reporta 3.0 e a média local diria 0.5 — o gauge C-Level mostraria
  // um quinto da maturidade real. Consumir o número do servidor é a única fonte de verdade.

  /**
   * ALVO de maturidade — a MÉTRICA que o rollup do servidor apurou. É o número que a diretoria lê
   * ("alvo 4,2"), e por isso vem do backend, não de uma conta local. Sem dados carregados → 0
   * (não é exibido: os painéis só renderizam sob resposta real).
   */
  readonly targetMaturity = computed(() => this.data()?.exposure.targetMaturity ?? 0);

  /** Maturidade geral — o `overall` do rollup, tal como o servidor o calculou. */
  readonly overallMaturity = computed(() => this.data()?.exposure.overallMaturity ?? 0);

  /**
   * ESCALA dos gráficos — GEOMETRIA, não métrica. Precisa comportar a maior barra E o maior alvo
   * INDIVIDUAL, senão o marcador de alvo de uma Função mais exigente é desenhado fora da área útil.
   *
   * ⚠️ Não confundir com <see cref="targetMaturity"/>: o alvo agregado (4,18 na demo) é MENOR que o
   * maior alvo por Função (4,42 em RC) — usá-lo como teto cortaria justamente a Função de meta mais
   * alta. Piso 4 para a régua CMMI não colapsar num tenant zerado.
   */
  readonly chartScale = computed(() => {
    const d = this.data();
    if (!d) return 4;
    return Math.max(
      4,
      d.exposure.targetMaturity,
      ...d.maturityByFunction.map((f) => f.target),
      ...d.maturityByFunction.map((f) => f.current),
    );
  });

  /** Maturidade por Função NIST, na ordem do catálogo que o backend já devolve. */
  readonly maturityBars = computed<FunctionScore[]>(() =>
    (this.data()?.maturityByFunction ?? []).map((f) => ({
      code: f.function,
      label: f.functionName.replace(/\s*\(.*\)$/, ''), // "GOVERN (GV)" → "GOVERN"
      value: f.current,
    })),
  );

  // ---- Estado da tela ----

  /** Ainda aguardando a resposta em voo: evita pintar números antes da hora. Começa `true`. */
  readonly loading = signal(true);

  /**
   * O tenant tem postura avaliada? Um cliente recém-provisionado responde 200 com tudo zerado, e é
   * PIOR que um erro: gauges em 0, painéis em branco e cartões de exposição zerados leem como
   * "nenhum risco", quando o correto é "nada foi medido ainda". Num painel de diretoria essa
   * diferença decide orçamento. Sem dados carregados → `false`.
   */
  readonly hasPosture = computed(() => {
    const d = this.data();
    if (!d) return false;
    return d.maturityByFunction.some((f) => f.current > 0)
      || d.topGaps.length > 0
      || d.riskByLevel.length > 0
      || d.riskHeatmap.length > 0
      || d.exposure.criticalProcessesExposed > 0
      || d.exposure.ineffectiveControls > 0
      || d.exposure.overdueActionPlans > 0;
  });

  /** Instante da apuração — o backend já enviava `generatedAt` e a tela não o exibia. */
  readonly generatedAt = computed(() => this.data()?.generatedAt ?? null);

  // Exposto ao template para colorir a pílula do ICR.
  protected readonly icrColor = icrColor;
  // Exposto ao template para orientar o diagnóstico quando a carga falha.
  protected readonly apiBase = environment.apiBase;

  // ---- Painéis SECUNDÁRIOS: três cargas independentes, deliberadamente NÃO combinadas ----
  // Nada de forkJoin/combineLatest aqui: encadear as quatro chamadas faria a tela inteira esperar a
  // mais lenta, e o FCP do dashboard principal é o que a diretoria vê primeiro. Cada painel tem o
  // próprio signal e acende quando o seu dado chega; falha de um não derruba os outros.

  /** Série do Aegis Score já no formato do SparklineComponent (vazia = sparkline se omite). */
  readonly trend = signal<ComplianceHistoryPoint[]>([]);
  /** Variação ponta a ponta da série, em pontos percentuais. `null` enquanto não há 2 pontos. */
  readonly trendDelta = computed(() => {
    const t = this.trend();
    return t.length > 1 ? Math.round(t[t.length - 1].compliancePercent - t[0].compliancePercent) : null;
  });

  /** Balanço CAPEX × OPEX das lacunas; `null` enquanto carrega. */
  readonly gapBalance = signal<GapBalance | null>(null);

  /** Pior raio conhecido; `null` = nunca calculado (204) OU ainda carregando — ver `blastLoaded`. */
  readonly blastRadius = signal<BlastRadiusSummary | null>(null);
  readonly blastLoaded = signal(false);

  ngOnInit(): void {
    this.load();
  }

  /** Nova tentativa a partir do estado de erro — mesma limpeza e recarga total de `load`. */
  reload(): void {
    this.load();
  }

  /**
   * Carrega (ou recarrega) a postura do tenant. LIMPA a tela ANTES de disparar: nenhum valor da carga
   * anterior — nem de outro tenant após um switch — pode sobreviver a um novo pedido, muito menos a
   * uma falha. Se a nova carga falhar, o que fica é o estado de erro, jamais números remanescentes ou
   * dados de exemplo.
   */
  private load(): void {
    this.loading.set(true);
    this.loadError.set(false);
    this.data.set(null);
    this.trend.set([]);
    this.gapBalance.set(null);
    this.blastRadius.set(null);
    this.blastLoaded.set(false);
    this.workspace.set(null);
    this.workspaceLoaded.set(false);

    // 1) Caminho crítico: o dashboard principal. É o único que governa `loading` e `loadError`.
    this.svc.fetchExecutive().subscribe({
      next: (d) => {
        this.data.set(d);
        this.loadError.set(false);
        this.loading.set(false);
      },
      error: (err) => {
        // Falha operacional: mantém os dados NULOS e sinaliza erro. Sem fallback de demonstração e
        // sem reaproveitar a resposta anterior — o estado de erro é a única coisa que a tela mostra.
        console.error('Falha ao carregar o dashboard executivo:', err);
        this.data.set(null);
        this.loadError.set(true);
        this.loading.set(false);
      },
    });

    // 2) Tendência (a DERIVADA do risco). Reusa o AegisScoreService que já consome /scoring/trend —
    //    duplicar o método no DashboardService criaria dois clientes para o mesmo endpoint.
    this.scoreSvc.fetchTrend(30).subscribe({
      next: (t) => this.trend.set(trendToSparkline(t)),
      error: (err) => console.warn('Tendência indisponível (painel se omite):', err),
    });

    // 3) Balanço de lacunas — deriva da MESMA matriz que os painéis de pilar já consomem.
    this.scoringSvc.getDashboard().subscribe({
      next: (rows) => this.gapBalance.set(buildGapBalance(rows)),
      error: (err) => console.warn('Balanço de lacunas indisponível:', err),
    });

    // 4) Raio de explosão. 204 → null (nunca calculado); `blastLoaded` separa isso de "carregando".
    this.svc.fetchBlastRadiusSummary().subscribe({
      next: (s) => {
        this.blastRadius.set(s);
        this.blastLoaded.set(true);
      },
      error: (err) => {
        console.warn('Raio de explosão indisponível:', err);
        this.blastLoaded.set(true);
      },
    });

    // 5) AEGIS Score (projeção única do workspace) — a postura determinística. Estado próprio de carga:
    //    a banda mostra "Não avaliado" (nunca 0%) num tenant sem evidência e um erro sanitizado se a API falhar.
    this.scoreSvc.fetchWorkspace().subscribe({
      next: (w) => {
        this.workspace.set(w);
        this.workspaceLoaded.set(true);
      },
      error: (err) => {
        console.warn('AEGIS Score (workspace) indisponível:', err);
        this.workspace.set(null);
        this.workspaceLoaded.set(true);
      },
    });
  }
}

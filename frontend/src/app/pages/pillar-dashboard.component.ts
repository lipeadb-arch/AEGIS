import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { environment } from '../../environments/environment';
import { AegisPillarChecklistComponent } from '../components/scoring/aegis-pillar-checklist.component';
import { ControlComplianceCardComponent } from '../components/scoring/control-compliance-card.component';
import { PostureSummaryComponent } from '../components/scoring/posture-summary.component';
import {
  PILLARS,
  PillarKey,
  TenantControlStateDto,
  buildPillarGapAnalysis,
  buildPillarView,
  formatDuration,
} from '../models/scoring.models';
import { FunctionPosture, functionOf } from '../models/workspace.models';
import { ScoringService } from '../services/scoring.service';
import { AegisScoreService } from '../services/aegis-score.service';

/**
 * PillarDashboardComponent — SMART. Orquestrador ÚNICO dos 4 painéis de pilar (Protect/Detect/Respond/
 * Recover): recebe a Função NIST como input, busca a matriz filtrada no ScoringService, mantém o estado
 * local em Signals e DELEGA a UI aos Dumb Components (gauge + lista de controles). Toda a derivação de
 * apresentação vive em `computed`.
 *
 * DRY: os 4 painéis NÃO duplicam esta lógica — cada rota é um wrapper de uma linha que injeta seu `pillar`
 * (ver protect/detect/respond/recover-dashboard.component.ts).
 */
@Component({
  selector: 'app-pillar-dashboard',
  standalone: true,
  imports: [PostureSummaryComponent, ControlComplianceCardComponent, AegisPillarChecklistComponent],
  template: `
    <section class="page pillar">
      <header class="page-head">
        <div>
          <p class="page-eyebrow">NIST CSF 2.0 · {{ meta().code }}</p>
          <h1>{{ meta().label }} <span class="code">{{ meta().code }}</span></h1>
          <p class="page-desc">{{ meta().description }}</p>
          <p class="page-meta">{{ meta().blurb }}</p>
        </div>
      </header>

      @if (loading()) {
        <div class="panel">
          <div class="state" role="status"><span class="spinner" aria-hidden="true"></span><p>Carregando a matriz de conformidade…</p></div>
        </div>
      } @else if (error()) {
        <!-- Mensagem OPERACIONAL: o cliente não deve ser mandado conferir endereço de API nem console. -->
        <div class="panel state err">
          <b>Não foi possível carregar a postura desta função.</b>
          <span>O serviço não respondeu agora. Recarregue a página em alguns instantes.</span>
        </div>
      } @else {
        <!-- HUD de resposta a incidente: só nas Funções com linha do tempo (DE/RS/RC).
             [AEGIS-MVP-PRODUCT-01] Sem NENHUM tempo medido, dois cartões grandes e vazios dominavam o topo
             de três páginas seguidas. Agora eles só ocupam esse espaço quando há medição; sem ela, resta
             uma linha discreta que continua dizendo a verdade — não medido ≠ zero. -->
        @if (meta().showsResponseMetrics) {
          @if (hasResponseMetrics()) {
            <div class="hud">
              <div class="hud-card" [class.void]="view().mttdMinutes === null">
                <span class="hud-k">MTTD</span>
                <span class="hud-v">{{ mttd() }}</span>
                <span class="hud-l">Tempo médio de detecção</span>
              </div>
              <div class="hud-card" [class.void]="view().mttrMinutes === null">
                <span class="hud-k">MTTR</span>
                <span class="hud-v">{{ mttr() }}</span>
                <span class="hud-l">Tempo médio de resposta</span>
              </div>
            </div>
          } @else {
            <p class="hud-none">
              Tempo médio de detecção e de resposta ainda não medidos nesta função.
            </p>
          }
        }

        <div class="grid">
          <!-- Resumo de postura: MESMA autoridade (aegis-score-v1, via /scoring/workspace) do Dashboard e
               das demais Funções — score anulável (Não avaliado ≠ 0%) + cobertura. Nunca recalculado aqui. -->
          <div class="panel summary">
            @switch (postureState()) {
              @case ('loaded') {
                <app-posture-summary [posture]="posture()!" [label]="meta().label" [code]="meta().code" />
              }
              @case ('loading') {
                <span class="pulse">Carregando o resumo de postura…</span>
              }
              @case ('notFound') {
                <span class="pulse">Sem catálogo ativo para esta Função.</span>
              }
              @case ('error') {
                <div class="posture-err">
                  <span>Não foi possível carregar o resumo de postura.</span>
                  <button type="button" class="ghost sm" (click)="loadWorkspacePosture()">Tentar novamente</button>
                </div>
              }
            }
          </div>

          <!-- Painel único em DUAS abas: por veredito (Controles) e por cobertura de prova (Pontos
               Cegos). Eram dois blocos empilhados mostrando a mesma matriz — a aba elimina a
               redundância sem esconder nenhuma das duas leituras. -->
          <div class="panel list">
            <div class="tabbar" role="tablist">
              <button
                type="button" role="tab" class="tab"
                [class.on]="tab() === 'controls'" [attr.aria-selected]="tab() === 'controls'"
                (click)="tab.set('controls')"
              >
                Controles
              </button>
              <button
                type="button" role="tab" class="tab blind"
                [class.on]="tab() === 'blind'" [attr.aria-selected]="tab() === 'blind'"
                (click)="tab.set('blind')"
              >
                Pontos Cegos
                @if (blindCount() > 0) {
                  <i>{{ blindCount() }}</i>
                }
              </button>
              <span class="hint">
                {{ tab() === 'controls' ? 'não conformes no topo' : 'sem prova para avaliar' }}
              </span>
            </div>

            @if (tab() === 'controls') {
              <app-control-compliance-card [controls]="view().controls" />
            } @else {
              <app-aegis-pillar-checklist [pillar]="pillar()" />
            }
          </div>
        </div>
      }
    </section>
  `,
  styles: [
    `
      /* Página, cabeçalho, painéis, abas, botões e estados: sistema visual global (styles.css). */
      .page-head .code {
        margin-left: var(--sp-2);
        padding: 2px var(--sp-2);
        border-radius: 6px;
        background: var(--tint-cyan);
        font-family: var(--mono);
        font-size: 15px;
        font-weight: 600;
        color: var(--cyan);
        vertical-align: middle;
      }

      /* HUD de resposta (DE/RS/RC): só ocupa espaço quando há medição — não medido ≠ zero. */
      .hud {
        display: flex;
        flex-wrap: wrap;
        gap: var(--sp-3);
      }
      .hud-card {
        display: flex;
        flex-direction: column;
        gap: 2px;
        min-width: 180px;
        padding: var(--sp-3) var(--sp-4);
        border: 1px solid var(--line);
        border-left: 3px solid var(--cyan);
        border-radius: var(--radius);
        background: var(--panel);
      }
      .hud-k {
        font-size: var(--fs-caps);
        font-weight: 600;
        letter-spacing: var(--tracking-caps);
        text-transform: uppercase;
        color: var(--cyan);
      }
      .hud-v {
        font-size: 22px;
        font-weight: 700;
      }
      .hud-l {
        font-size: var(--fs-meta);
        color: var(--text-2);
      }
      .hud-none {
        font-size: var(--fs-sm);
        color: var(--text-2);
      }
      .hud-card.void {
        border-left-color: var(--line-strong);
      }
      .hud-card.void .hud-k,
      .hud-card.void .hud-v {
        color: var(--muted);
      }

      .grid {
        display: grid;
        grid-template-columns: 320px minmax(0, 1fr);
        gap: var(--sp-4);
        align-items: start;
      }
      .summary {
        display: flex;
        flex-direction: column;
        gap: 14px;
      }
      .posture-err {
        display: flex;
        flex-direction: column;
        align-items: flex-start;
        gap: var(--sp-2);
        font-size: var(--fs-sm);
        color: var(--text-2);
      }
      .pulse {
        font-size: var(--fs-sm);
        color: var(--text-2);
      }

      /* Abas do painel: veredito × cobertura de prova. A dica fica à direita da barra. */
      .tabbar {
        margin-bottom: var(--sp-4);
      }
      .tabbar > button.blind.on {
        color: var(--red-text);
        border-bottom-color: var(--red);
      }
      .tabbar i {
        padding: 1px 7px;
        border: 1px solid rgba(255, 45, 111, 0.45);
        border-radius: var(--radius-pill);
        font-style: normal;
        font-size: var(--fs-caps);
        font-weight: 600;
        color: var(--red-text);
      }
      .tabbar .hint {
        align-self: center;
        margin-left: auto;
        padding-left: var(--sp-3);
      }

      /* Falha ocupando o painel: alinhada à esquerda, com a orientação logo abaixo. */
      .panel.state {
        align-items: flex-start;
        gap: 10px;
        padding: var(--sp-5);
        text-align: left;
      }
      .panel.state b {
        font-size: var(--fs-body);
        color: var(--red-text);
      }
      .panel.state span {
        color: var(--text-2);
      }
      @media (max-width: 1100px) {
        .grid {
          grid-template-columns: 1fr;
        }
      }
    `,
  ],
})
export class PillarDashboardComponent implements OnInit {
  private readonly svc = inject(ScoringService);
  private readonly scoreSvc = inject(AegisScoreService);

  /** Função NIST deste painel — injetada pelo wrapper da rota (Protect/Detect/Respond/Recover). */
  readonly pillar = input.required<PillarKey>();

  /** Estado local em Signals (sem NgRx). */
  private readonly controls = signal<TenantControlStateDto[]>([]);
  readonly loading = signal(true);
  readonly error = signal(false);

  /** Postura desta Função pela projeção única do workspace (score/cobertura/contagens — autoridade backend). */
  readonly posture = signal<FunctionPosture | null>(null);
  /** Estado do cabeçalho de postura: distingue carregando · carregado · Função ausente · erro (com retry). */
  readonly postureState = signal<'loading' | 'loaded' | 'notFound' | 'error'>('loading');

  /** Derivações reativas: metadados do pilar e a view agregada que alimenta os Dumb Components. */
  readonly meta = computed(() => PILLARS[this.pillar()]);
  readonly view = computed(() => buildPillarView(this.meta(), this.controls()));

  /** MTTD/MTTR do pilar já formatados para o HUD ("18 min", "2h 30m", "—" sem medição). */
  /** Há ALGUMA medição de tempo de resposta? Governa a apresentação (cartões × linha discreta). */
  readonly hasResponseMetrics = computed(
    () => this.view().mttdMinutes !== null || this.view().mttrMinutes !== null,
  );

  readonly mttd = computed(() => formatDuration(this.view().mttdMinutes));
  readonly mttr = computed(() => formatDuration(this.view().mttrMinutes));

  /** Aba ativa do painel de controles: por veredito ou por cobertura de prova. */
  readonly tab = signal<'controls' | 'blind'>('controls');

  /**
   * Contagem de pontos cegos, para o badge da aba. Derivada da MESMA matriz já carregada — o checklist
   * recarrega por conta própria, mas o badge não pode esperar por ele para aparecer.
   */
  readonly blindCount = computed(
    () => buildPillarGapAnalysis(this.meta(), this.controls()).blindSpots.length,
  );

  /** Exposto ao template para orientar o diagnóstico no estado de erro. */
  protected readonly apiBase = environment.apiBase;

  ngOnInit(): void {
    // Matriz de controles do pilar (o serviço já filtra pelo prefixo). Estado de erro elegante, sem crash.
    this.svc.getPillarControls(this.pillar()).subscribe({
      next: (list) => {
        this.controls.set(list);
        this.loading.set(false);
      },
      error: () => {
        this.error.set(true);
        this.loading.set(false);
      },
    });

    this.loadWorkspacePosture();
  }

  /** Carrega o cabeçalho de postura pela projeção única, com estados explícitos (chamado no init e no retry). */
  loadWorkspacePosture(): void {
    this.postureState.set('loading');
    this.scoreSvc.fetchWorkspace().subscribe({
      next: (w) => {
        const f = functionOf(w, this.meta().code) ?? null;
        this.posture.set(f);
        this.postureState.set(f ? 'loaded' : 'notFound');
      },
      error: () => {
        this.posture.set(null);
        this.postureState.set('error');
      },
    });
  }
}

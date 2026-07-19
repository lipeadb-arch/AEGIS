import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { environment } from '../../environments/environment';
import { AegisPillarChecklistComponent } from '../components/scoring/aegis-pillar-checklist.component';
import { ControlComplianceCardComponent } from '../components/scoring/control-compliance-card.component';
import { ScoreGaugeComponent } from '../components/scoring/score-gauge.component';
import {
  PILLARS,
  PillarKey,
  TenantControlStateDto,
  buildPillarGapAnalysis,
  buildPillarView,
  formatDuration,
} from '../models/scoring.models';
import { ScoringService } from '../services/scoring.service';

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
  imports: [ScoreGaugeComponent, ControlComplianceCardComponent, AegisPillarChecklistComponent],
  template: `
    <section class="pillar">
      <p class="eyebrow">NIST CSF 2.0 · {{ meta().code }}</p>
      <header class="head">
        <h1>{{ meta().label }} <span class="code">{{ meta().code }}</span></h1>
        <p class="blurb">{{ meta().blurb }}</p>
        <p class="description">{{ meta().description }}</p>
      </header>

      @if (loading()) {
        <div class="panel state">
          <span class="pulse">Carregando a matriz de conformidade…</span>
        </div>
      } @else if (error()) {
        <div class="panel state err">
          <b>Não foi possível carregar a postura deste pilar.</b>
          <span>Verifique se a API está no ar em <code>{{ apiBase }}</code> e recarregue.</span>
        </div>
      } @else {
        <!-- HUD de resposta a incidente: só nas Funções com linha do tempo (DE/RS/RC). -->
        @if (meta().showsResponseMetrics) {
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
        }

        <div class="grid">
          <!-- Resumo: gauge de conformidade + contagens por status -->
          <div class="panel summary">
            <app-score-gauge [percent]="view().compliancePct" />
            <div class="counts">
              <div class="count">
                <span class="n">{{ view().total }}</span><span class="l">Controles</span>
              </div>
              <div class="count ok">
                <span class="n">{{ view().compliant }}</span><span class="l">Conformes</span>
              </div>
              <div class="count partial">
                <span class="n">{{ view().partial }}</span><span class="l">Parciais</span>
              </div>
              <div class="count fail" [class.hot]="view().nonCompliant > 0">
                <span class="n">{{ view().nonCompliant }}</span><span class="l">Não conformes</span>
              </div>
            </div>
          </div>

          <!-- Painel único em DUAS abas: por veredito (Controles) e por cobertura de prova (Pontos
               Cegos). Eram dois blocos empilhados mostrando a mesma matriz — a aba elimina a
               redundância sem esconder nenhuma das duas leituras. -->
          <div class="panel list">
            <div class="hd tabs" role="tablist">
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
      :host {
        display: block;
        padding: 28px 32px 60px;
      }
      .head {
        margin: 0 0 22px;
      }
      .head h1 {
        font-family: var(--sans);
        font-size: 24px;
        color: var(--text);
        margin: 0 0 4px;
      }
      .head .code {
        font-family: var(--mono);
        font-size: 13px;
        color: var(--cyan);
        margin-left: 8px;
      }
      .head .blurb {
        color: var(--muted);
        font-size: 13px;
        margin: 0;
        font-family: var(--mono);
        letter-spacing: 0.02em;
      }
      /* Subtítulo tático: parágrafo de leitura (sans), mutado e contido — informa sem disputar com os
         gauges/cards. A fonte sans + max-width o distinguem do blurb (mono, categorias). */
      .head .description {
        color: var(--muted);
        font-family: var(--sans);
        font-size: 13.5px;
        line-height: 1.6;
        margin: 12px 0 0;
        max-width: 820px;
      }

      /* HUD de resposta: cards pequenos, lidos antes do gauge — é a métrica que o CISO cobra. */
      .hud {
        display: flex;
        gap: 12px;
        flex-wrap: wrap;
        margin: 0 0 18px;
      }
      .hud-card {
        display: flex;
        flex-direction: column;
        gap: 2px;
        min-width: 172px;
        padding: 11px 14px;
        border: 1px solid var(--line);
        border-left: 3px solid var(--cyan);
        border-radius: 10px;
        background: rgba(122, 145, 190, 0.03);
      }
      .hud-k {
        font-family: var(--mono);
        font-size: 10px;
        letter-spacing: 0.14em;
        text-transform: uppercase;
        color: var(--cyan);
      }
      .hud-v {
        font-family: var(--display);
        font-weight: 700;
        font-size: 20px;
        color: var(--text);
      }
      .hud-l {
        font-family: var(--mono);
        font-size: 10px;
        color: var(--muted);
      }
      /* Sem medição: o card se apaga e mostra "—". Nunca um zero — zero minutos seria uma detecção
         instantânea, o oposto de "ninguém mediu". */
      .hud-card.void {
        border-left-color: var(--line);
        opacity: 0.7;
      }
      .hud-card.void .hud-k,
      .hud-card.void .hud-v {
        color: var(--muted);
      }

      .grid {
        display: grid;
        grid-template-columns: 300px 1fr;
        gap: 18px;
        align-items: start;
      }

      .summary {
        display: flex;
        flex-direction: column;
        gap: 14px;
      }
      .counts {
        display: grid;
        grid-template-columns: 1fr 1fr;
        gap: 10px;
      }
      .count {
        display: flex;
        flex-direction: column;
        gap: 2px;
        padding: 10px 12px;
        border: 1px solid var(--line);
        border-radius: 10px;
        background: rgba(122, 145, 190, 0.03);
      }
      .count .n {
        font-family: var(--display);
        font-weight: 700;
        font-size: 22px;
        color: var(--text);
      }
      .count .l {
        font-family: var(--mono);
        font-size: 10px;
        text-transform: uppercase;
        letter-spacing: 0.12em;
        color: var(--muted);
      }
      .count.ok .n {
        color: var(--cyan);
      }
      .count.partial .n {
        color: var(--amber);
      }
      /* Não conformes: contido quando é 0, ACESO quando há risco. */
      .count.fail.hot {
        border-color: rgba(255, 45, 111, 0.45);
        background: rgba(255, 45, 111, 0.07);
        box-shadow: inset 0 0 22px -14px rgba(255, 45, 111, 0.7);
      }
      .count.fail.hot .n {
        color: var(--red);
        text-shadow: 0 0 18px rgba(255, 45, 111, 0.5);
      }

      .list .hd {
        display: flex;
        align-items: baseline;
        justify-content: space-between;
        margin-bottom: 14px;
      }
      /* Abas do painel: veredito × cobertura de prova. A dica migra para a direita da barra. */
      .list .hd.tabs {
        gap: 4px;
        justify-content: flex-start;
        border-bottom: 1px solid var(--line-2);
        padding-bottom: 0;
      }
      .list .hd.tabs .hint {
        margin-left: auto;
        padding-bottom: 9px;
      }
      .list .tab {
        display: inline-flex;
        align-items: center;
        gap: 7px;
        background: none;
        border: 0;
        border-bottom: 2px solid transparent;
        padding: 4px 12px 9px;
        margin-bottom: -1px;
        cursor: pointer;
        font-family: var(--mono);
        font-size: 11px;
        letter-spacing: 0.06em;
        text-transform: uppercase;
        color: var(--muted);
        transition: color 0.15s ease, border-color 0.15s ease;
      }
      .list .tab i {
        font-style: normal;
        font-family: var(--display);
        font-size: 11px;
        border: 1px solid rgba(255, 45, 111, 0.45);
        border-radius: 999px;
        padding: 1px 7px;
        color: var(--red);
      }
      .list .tab:hover {
        color: var(--text);
      }
      .list .tab.on {
        color: var(--cyan);
        border-bottom-color: var(--cyan);
      }
      .list .tab.blind.on {
        color: var(--red);
        border-bottom-color: var(--red);
      }
      .list h3 {
        margin: 0;
        font-size: 14px;
        font-weight: 600;
        position: relative;
        padding-left: 13px;
      }
      .list h3::before {
        content: '';
        position: absolute;
        left: 0;
        top: 2px;
        bottom: 2px;
        width: 3px;
        border-radius: 2px;
        background: var(--neon);
        box-shadow: 0 0 10px rgba(38, 224, 255, 0.6);
      }
      .list .hint {
        font-family: var(--mono);
        font-size: 11px;
        color: var(--muted);
      }

      /* Estados de carga/erro — elegantes, nunca um crash. */
      .state {
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .pulse {
        font-family: var(--mono);
        font-size: 12px;
        color: var(--muted);
        letter-spacing: 0.08em;
        animation: pulse 1.4s ease-in-out infinite;
      }
      .state.err {
        border-color: rgba(255, 45, 111, 0.4);
        box-shadow: inset 0 0 40px -24px rgba(255, 45, 111, 0.7);
      }
      .state.err b {
        color: #ffe3ee;
        font-size: 14px;
      }
      .state.err span {
        font-family: var(--mono);
        font-size: 12px;
        color: var(--muted);
      }
      .state.err code {
        color: var(--text);
        background: rgba(255, 255, 255, 0.06);
        padding: 1px 5px;
        border-radius: 4px;
      }
      @keyframes pulse {
        0%,
        100% {
          opacity: 0.35;
        }
        50% {
          opacity: 0.75;
        }
      }

      @media (max-width: 900px) {
        .grid {
          grid-template-columns: 1fr;
        }
      }
      @media (prefers-reduced-motion: reduce) {
        .pulse {
          animation: none;
        }
      }
    `,
  ],
})
export class PillarDashboardComponent implements OnInit {
  private readonly svc = inject(ScoringService);

  /** Função NIST deste painel — injetada pelo wrapper da rota (Protect/Detect/Respond/Recover). */
  readonly pillar = input.required<PillarKey>();

  /** Estado local em Signals (sem NgRx). */
  private readonly controls = signal<TenantControlStateDto[]>([]);
  readonly loading = signal(true);
  readonly error = signal(false);

  /** Derivações reativas: metadados do pilar e a view agregada que alimenta os Dumb Components. */
  readonly meta = computed(() => PILLARS[this.pillar()]);
  readonly view = computed(() => buildPillarView(this.meta(), this.controls()));

  /** MTTD/MTTR do pilar já formatados para o HUD ("18 min", "2h 30m", "—" sem medição). */
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
    // Uma chamada; o serviço já filtra pelo prefixo do pilar. Estado de erro elegante, sem crash.
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
  }
}

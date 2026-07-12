import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { environment } from '../../environments/environment';
import { ControlComplianceCardComponent } from '../components/scoring/control-compliance-card.component';
import { ScoreGaugeComponent } from '../components/scoring/score-gauge.component';
import { PILLARS, PillarKey, TenantControlStateDto, buildPillarView } from '../models/scoring.models';
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
  imports: [ScoreGaugeComponent, ControlComplianceCardComponent],
  template: `
    <section class="pillar">
      <p class="eyebrow">NIST CSF 2.0 · {{ meta().code }}</p>
      <header class="head">
        <h1>{{ meta().label }} <span class="code">{{ meta().code }}</span></h1>
        <p class="blurb">{{ meta().blurb }}</p>
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

          <!-- Lista de controles: NonCompliant primeiro -->
          <div class="panel list">
            <div class="hd">
              <h3>Controles</h3>
              <span class="hint">não conformes no topo</span>
            </div>
            <app-control-compliance-card [controls]="view().controls" />
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

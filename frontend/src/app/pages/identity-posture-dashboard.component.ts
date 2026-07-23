import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { environment } from '../../environments/environment';
import { IdentityExposureCardComponent } from '../components/identity/identity-exposure-card.component';
import { ScoreGaugeComponent } from '../components/scoring/score-gauge.component';
import { IdentityVerdict, buildIdentityPostureView } from '../models/identity.models';
import { AgentStateService } from '../services/agent-state.service';
import { IdentityService } from '../services/identity.service';

/**
 * IdentityPostureDashboardComponent — SMART. Tela de Postura de Identidade (Microsoft Entra ID), no padrão
 * do PillarDashboard: busca no serviço, guarda o estado em Signals e DELEGA a UI aos Dumb Components
 * (gauge + tabela tática de exposição). Toda a derivação de apresentação vive em `computed`.
 *
 * Consome a esteira ATIVA (POST /telemetry/identity/entra-id): o botão dispara a análise; o toggle
 * "Rede Isolada (OT)" reenvia o contexto de controle compensatório — demonstrando AO VIVO a IA perdoar a
 * falta de MFA de contas de serviço/OT quando o ativo está isolado (NonCompliant → Compensated). O botão
 * "Auditar Lacunas" abre o Copiloto GRC (escopo PR) já pedindo a entrevista de governança de identidade.
 */
@Component({
  selector: 'app-identity-posture-dashboard',
  standalone: true,
  imports: [ScoreGaugeComponent, IdentityExposureCardComponent],
  template: `
    <section class="identity">
      <p class="eyebrow">NIST CSF 2.0 · PR.AA / GV.RR · Microsoft Entra ID</p>

      <header class="head">
        <div class="titles">
          <h1>Postura de Identidade <span class="code">Entra ID</span></h1>
          <p class="blurb">Indicadores de exposição de identidade, avaliados por IA.</p>
        </div>

        <div class="actions">
          <button
            type="button"
            class="toggle"
            role="switch"
            [attr.aria-checked]="networkIsolation()"
            [class.on]="networkIsolation()"
            (click)="toggleIsolation()"
            [disabled]="running()"
            title="Ativos OT/legado em rede isolada (controle compensatório)"
          >
            <span class="knob" aria-hidden="true"></span>
            <span class="tlabel">Rede Isolada (OT)</span>
          </button>

          <button type="button" class="btn run" (click)="runAnalysis()" [disabled]="running()">
            {{ running() ? 'Analisando…' : 'Executar Análise' }}
          </button>

          <button type="button" class="btn audit" (click)="auditGaps()" [disabled]="running()">
            ◈ Auditar Lacunas
          </button>
        </div>
      </header>

      @if (loading()) {
        <div class="panel state"><span class="pulse">Coletando a postura de identidade do Entra ID…</span></div>
      } @else if (error()) {
        <div class="panel state err">
          <b>Não foi possível executar a análise de identidade.</b>
          <span>Verifique se a API está no ar em <code>{{ apiBase }}</code> e tente novamente.</span>
        </div>
      } @else {
        <div class="grid">
          <!-- Resumo: gauge da postura IAM + contagens por status -->
          <div class="panel summary">
            <app-score-gauge [percent]="view().posturePct" caption="POSTURA IAM" />
            <div class="counts">
              <div class="count">
                <span class="n">{{ view().total }}</span><span class="l">Controles</span>
              </div>
              <div class="count ok">
                <span class="n">{{ view().compliant }}</span><span class="l">Conformes</span>
              </div>
              <div class="count comp" [class.hot]="view().compensated > 0">
                <span class="n">{{ view().compensated }}</span><span class="l">Compensados</span>
              </div>
              <div class="count fail" [class.hot]="view().nonCompliant > 0">
                <span class="n">{{ view().nonCompliant }}</span><span class="l">Não conformes</span>
              </div>
            </div>
            @if (view().compensated > 0) {
              <p class="comp-note">
                <b>{{ view().compensated }}</b> falha(s) de MFA justificada(s) por
                <span class="amber">isolamento de rede</span> — risco compensado, não cego.
              </p>
            }
          </div>

          <!-- Tabela tática de exposição -->
          <div class="panel list">
            <div class="hd">
              <h3>Identity Exposure</h3>
              <span class="hint">não conformes no topo · clique para a evidência</span>
            </div>
            <app-identity-exposure-card [findings]="view().findings" />
          </div>
        </div>
      }
    </section>
  `,
  styles: [
    `
      :host { display: block; padding: 28px 32px 60px; }

      .head {
        display: flex; align-items: flex-end; justify-content: space-between;
        gap: 18px; flex-wrap: wrap; margin: 0 0 22px;
      }
      .head h1 { font-family: var(--sans); font-size: 24px; color: var(--text); margin: 0 0 4px; }
      .head .code { font-family: var(--mono); font-size: 13px; color: var(--cyan); margin-left: 8px; }
      .head .blurb {
        color: var(--muted); font-size: 13px; margin: 0; font-family: var(--mono); letter-spacing: 0.02em;
      }
      .actions { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }

      /* Toggle HUD de isolamento de rede — o gatilho do controle compensatório. */
      .toggle {
        display: inline-flex; align-items: center; gap: 9px; cursor: pointer;
        font-family: var(--mono); font-size: 11px; letter-spacing: 0.04em; color: var(--muted);
        background: var(--panel-2); border: 1px solid var(--line); border-radius: 999px; padding: 7px 13px 7px 9px;
        transition: 0.18s;
      }
      .toggle .knob {
        position: relative; width: 30px; height: 16px; border-radius: 999px;
        background: rgba(122, 145, 190, 0.25); transition: 0.18s; flex: none;
      }
      .toggle .knob::after {
        content: ''; position: absolute; top: 2px; left: 2px; width: 12px; height: 12px; border-radius: 50%;
        background: var(--muted); transition: 0.18s;
      }
      .toggle.on { color: var(--amber); border-color: rgba(255, 176, 32, 0.5); }
      .toggle.on .knob { background: rgba(255, 176, 32, 0.35); }
      .toggle.on .knob::after { left: 16px; background: var(--amber); box-shadow: 0 0 8px var(--amber); }
      .toggle:disabled { opacity: 0.5; cursor: not-allowed; }

      .btn {
        cursor: pointer; font-family: var(--mono); font-size: 12px; font-weight: 600;
        border-radius: 11px; padding: 9px 16px; transition: 0.15s; border: 1px solid transparent;
      }
      .btn:disabled { opacity: 0.5; cursor: not-allowed; }
      .btn.run {
        color: #05070f; background: var(--neon-h);
        box-shadow: 0 0 14px -3px rgba(38, 224, 255, 0.6);
      }
      .btn.run:hover:not(:disabled) { box-shadow: 0 0 22px -3px rgba(38, 224, 255, 0.85); }
      .btn.audit {
        color: var(--magenta); background: rgba(255, 61, 154, 0.08); border-color: rgba(255, 61, 154, 0.4);
      }
      .btn.audit:hover:not(:disabled) { background: rgba(255, 61, 154, 0.15); box-shadow: 0 0 16px -6px var(--magenta); }

      .grid { display: grid; grid-template-columns: 300px 1fr; gap: 18px; align-items: start; }
      .summary { display: flex; flex-direction: column; gap: 14px; }
      .counts { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; }
      .count {
        display: flex; flex-direction: column; gap: 2px; padding: 10px 12px;
        border: 1px solid var(--line); border-radius: 10px; background: rgba(122, 145, 190, 0.03);
      }
      .count .n { font-family: var(--display); font-weight: 700; font-size: 22px; color: var(--text); }
      .count .l {
        font-family: var(--mono); font-size: 10px; text-transform: uppercase; letter-spacing: 0.12em; color: var(--muted);
      }
      .count.ok .n { color: var(--cyan); }
      .count.comp.hot { border-color: rgba(255, 176, 32, 0.45); background: rgba(255, 176, 32, 0.06); }
      .count.comp.hot .n { color: var(--amber); text-shadow: 0 0 16px rgba(255, 176, 32, 0.45); }
      .count.fail.hot {
        border-color: rgba(255, 45, 111, 0.45); background: rgba(255, 45, 111, 0.07);
        box-shadow: inset 0 0 22px -14px rgba(255, 45, 111, 0.7);
      }
      .count.fail.hot .n { color: var(--red); text-shadow: 0 0 18px rgba(255, 45, 111, 0.5); }

      .comp-note {
        margin: 0; font-family: var(--mono); font-size: 11px; line-height: 1.5; color: var(--muted);
        border-left: 2px solid var(--amber); padding-left: 10px;
      }
      .comp-note b { color: var(--amber); }
      .comp-note .amber { color: var(--amber); }

      .list .hd { display: flex; align-items: baseline; justify-content: space-between; margin-bottom: 14px; }
      .list h3 { margin: 0; font-size: 14px; font-weight: 600; position: relative; padding-left: 13px; }
      .list h3::before {
        content: ''; position: absolute; left: 0; top: 2px; bottom: 2px; width: 3px; border-radius: 2px;
        background: var(--neon); box-shadow: 0 0 10px rgba(38, 224, 255, 0.6);
      }
      .list .hint { font-family: var(--mono); font-size: 11px; color: var(--muted); }

      .state { display: flex; flex-direction: column; gap: 6px; }
      .pulse {
        font-family: var(--mono); font-size: 12px; color: var(--muted); letter-spacing: 0.08em;
        animation: pulse 1.4s ease-in-out infinite;
      }
      .state.err { border-color: rgba(255, 45, 111, 0.4); box-shadow: inset 0 0 40px -24px rgba(255, 45, 111, 0.7); }
      .state.err b { color: #ffe3ee; font-size: 14px; }
      .state.err span { font-family: var(--mono); font-size: 12px; color: var(--muted); }
      .state.err code { color: var(--text); background: rgba(255, 255, 255, 0.06); padding: 1px 5px; border-radius: 4px; }
      @keyframes pulse { 0%, 100% { opacity: 0.35; } 50% { opacity: 0.75; } }

      @media (max-width: 900px) { .grid { grid-template-columns: 1fr; } }
      @media (prefers-reduced-motion: reduce) { .pulse { animation: none; } .toggle, .btn { transition: none; } }
    `,
  ],
})
export class IdentityPostureDashboardComponent implements OnInit {
  private readonly identity = inject(IdentityService);
  private readonly agent = inject(AgentStateService);

  private readonly verdicts = signal<IdentityVerdict[]>([]);
  readonly loading = signal(true); // 1ª carga
  readonly running = signal(false); // re-execuções (botão/toggle)
  readonly error = signal<string | null>(null);

  /** Controle compensatório: ativos OT/legado em rede isolada — reenviado ao motor a cada análise. */
  readonly networkIsolation = signal(false);

  /** Derivação reativa: a postura consolidada que alimenta os Dumb Components. */
  readonly view = computed(() => buildIdentityPostureView(this.verdicts()));

  protected readonly apiBase = environment.apiBase;

  ngOnInit(): void {
    // Nasce viva com o cenário CRU (sem isolamento) — o alto risco do cenário sintético de identidade.
    this.runAnalysis();
  }

  /** Dispara a análise do Entra ID com o contexto de rede atual e projeta os vereditos. */
  runAnalysis(): void {
    this.running.set(true);
    this.error.set(null);
    this.identity.runEntraIdAnalysis({ hasNetworkIsolation: this.networkIsolation() }).subscribe({
      next: (v) => {
        this.verdicts.set(v);
        this.loading.set(false);
        this.running.set(false);
      },
      error: (e: Error) => {
        this.error.set(e.message);
        this.loading.set(false);
        this.running.set(false);
      },
    });
  }

  /** Liga/desliga o isolamento de rede e RE-AVALIA — demonstra a compensação ao vivo (falha → mitigado). */
  toggleIsolation(): void {
    this.networkIsolation.update((v) => !v);
    this.runAnalysis();
  }

  /** Abre o Copiloto GRC (escopo PR pela rota) já pedindo a auditoria de governança de identidade. */
  auditGaps(): void {
    this.agent.requestAudit(
      'Auditar as lacunas de governança de identidade (PR.AA-01 e GV.RR-01): contas privilegiadas sem MFA, ' +
        'excesso de administradores e o controle compensatório de isolamento de rede das contas de serviço/OT. ' +
        'Explique por que uma conta OT sem MFA pode ser classificada como Mitigada em vez de Não Conforme.',
    );
  }
}

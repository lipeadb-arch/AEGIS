import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { AgentStateService } from '../services/agent-state.service';
import { AuditorInterviewSeed } from '../services/auditor.service';
import { GovernanceService } from '../services/governance.service';
import { InterviewTurn, StartInterviewRequest } from '../models/governance.models';

/** Fases do cartão: abrindo a sessão → coletando respostas → enviando → concluída / erro. */
type InterviewPhase = 'starting' | 'active' | 'sending' | 'done' | 'error';

/**
 * GrcQuestionCardComponent — a GENERATIVE UI da entrevista GRC, injetada INLINE no fluxo do Copiloto.
 *
 * É um micro-componente AUTOCONTIDO: recebe a SEMENTE do Agentic Routing (o `metadata` do
 * START_INTERVIEW, com a subcategoria NIST alvo) e conduz a entrevista real contra
 * `/governance/interviews` (start → answer → complete), reativando o fluxo GRC que estava órfão na UI.
 * Ao progredir, publica mudanças de cobertura e riscos no AgentStateService (barramento reverso), para as
 * telas de Governança reagirem. O `auditor-chat` não sabe NADA disto — só declara `<app-grc-question-card>`.
 *
 * Padrão da casa: standalone + signals, tema HUD (a cor do Auditor é o magenta/brand), zero libs.
 */
@Component({
  selector: 'app-grc-question-card',
  standalone: true,
  template: `
    <div class="grc-card" [class.is-done]="phase() === 'done'" [class.is-error]="phase() === 'error'">
      <header class="grc-head">
        <span class="badge" aria-hidden="true">◑</span>
        <div class="titles">
          <h5>Entrevista GRC</h5>
          <span class="target">
            @if (targetCode(); as code) {
              Auditando <b>{{ code }}</b>
            } @else {
              Diagnóstico de lacunas · GOVERN
            }
          </span>
        </div>
        <span class="phase" [attr.data-phase]="phase()">{{ phaseLabel() }}</span>
      </header>

      <!-- Abertura roteada pelo Copiloto (transição do chat para a entrevista). -->
      @if (prompt()) {
        <p class="lead">{{ prompt() }}</p>
      }

      <!-- Trilha das respostas já registradas nesta sessão. -->
      @for (ex of exchanges(); track $index) {
        <div class="qa">
          <p class="q"><span class="tag">P{{ $index + 1 }}</span>{{ ex.question }}</p>
          <p class="a">{{ ex.answer }}</p>
        </div>
      }

      <!-- Pergunta corrente + composer (enquanto a sessão está viva). -->
      @if (phase() === 'active' || phase() === 'sending') {
        <div class="qa current">
          <p class="q"><span class="tag">P{{ exchanges().length + 1 }}</span>{{ current() }}</p>
        </div>
        <div class="composer">
          <textarea
            rows="1"
            [value]="answer()"
            (input)="answer.set($any($event.target).value)"
            (keydown.enter)="onEnter($event)"
            [disabled]="phase() === 'sending'"
            placeholder="Responda com evidências concretas…"
          ></textarea>
          <button type="button" class="respond" (click)="submit()" [disabled]="!canAnswer()">
            {{ phase() === 'sending' ? '···' : 'Responder' }}
          </button>
        </div>
      }

      @if (phase() === 'starting') {
        <div class="analyzing">[ Abrindo sessão de auditoria… ]</div>
      }

      @if (phase() === 'done') {
        <div class="done-note">
          <span class="check" aria-hidden="true">✓</span>
          Entrevista concluída — {{ exchanges().length }} resposta(s) registrada(s). Cobertura e riscos
          foram publicados no dossiê de Governança.
        </div>
      }

      @if (phase() === 'error') {
        <p class="err">{{ errorMsg() }}</p>
      }
    </div>
  `,
  styles: [
    `
      :host { display: block; width: 100%; }

      /* Cartão do Auditor: brand/magenta, distinto das bolhas de texto — é um widget, não uma fala. */
      .grc-card {
        border: 1px solid rgba(255, 61, 154, 0.4);
        border-radius: 4px 14px 14px 14px;
        background: linear-gradient(180deg, rgba(255, 61, 154, 0.09), rgba(255, 61, 154, 0.02));
        box-shadow: inset 0 0 34px -22px rgba(255, 61, 154, 0.8);
        padding: 13px 14px;
        display: flex;
        flex-direction: column;
        gap: 10px;
      }
      .grc-card.is-done { border-color: rgba(38, 224, 255, 0.4); box-shadow: inset 0 0 34px -22px rgba(38, 224, 255, 0.7); }
      .grc-card.is-error { border-color: rgba(255, 45, 111, 0.5); }

      /* ---- Cabeçalho ---- */
      .grc-head { display: grid; grid-template-columns: auto 1fr auto; align-items: center; gap: 11px; }
      .badge {
        font-size: 18px; color: var(--magenta);
        filter: drop-shadow(0 0 10px rgba(255, 61, 154, 0.6)); line-height: 1;
      }
      .titles { display: flex; flex-direction: column; gap: 1px; min-width: 0; }
      .titles h5 {
        margin: 0; font-family: var(--display); font-weight: 700; font-size: 12.5px;
        letter-spacing: 0.05em; color: var(--text);
      }
      .target { font-family: var(--mono); font-size: 10.5px; color: var(--muted); }
      .target b { color: var(--magenta); font-weight: 600; letter-spacing: 0.03em; }
      .phase {
        font-family: var(--mono); font-size: 9px; letter-spacing: 0.14em; text-transform: uppercase;
        color: var(--muted); border: 1px solid var(--line); border-radius: 999px; padding: 2px 8px; white-space: nowrap;
      }
      .phase[data-phase='active'] { color: var(--magenta); border-color: rgba(255, 61, 154, 0.4); }
      .phase[data-phase='done'] { color: var(--cyan); border-color: rgba(38, 224, 255, 0.4); }
      .phase[data-phase='error'] { color: var(--red); border-color: rgba(255, 45, 111, 0.5); }

      /* ---- Abertura ---- */
      .lead {
        margin: 0; font-size: 12.5px; line-height: 1.55; color: var(--text); opacity: 0.92;
        border-left: 2px solid rgba(255, 61, 154, 0.4); padding-left: 10px;
      }

      /* ---- Perguntas / respostas ---- */
      .qa { display: flex; flex-direction: column; gap: 5px; }
      .qa .q {
        margin: 0; font-size: 12.5px; line-height: 1.5; color: var(--text);
        display: flex; gap: 8px; align-items: baseline;
      }
      .qa.current .q { color: var(--text); font-weight: 500; }
      .qa .tag {
        font-family: var(--mono); font-size: 9.5px; font-weight: 600; color: var(--magenta);
        border: 1px solid rgba(255, 61, 154, 0.35); border-radius: 5px; padding: 1px 5px; flex: none;
      }
      .qa .a {
        margin: 0 0 0 4px; font-size: 12px; line-height: 1.5; color: var(--muted);
        border-left: 2px solid var(--line); padding-left: 9px; white-space: pre-wrap; word-break: break-word;
      }

      /* ---- Composer ---- */
      .composer { display: flex; gap: 8px; align-items: flex-end; }
      .composer textarea {
        flex: 1; resize: none; max-height: 110px; font-family: var(--sans); font-size: 12.5px; color: var(--text);
        background: var(--panel-2); border: 1px solid var(--line); border-radius: 10px; padding: 9px 11px; line-height: 1.5;
      }
      .composer textarea:focus { outline: none; border-color: rgba(255, 61, 154, 0.5); }
      .composer textarea:disabled { opacity: 0.5; cursor: not-allowed; }
      .respond {
        align-self: stretch; cursor: pointer; font-family: var(--mono); font-size: 11.5px; font-weight: 600;
        color: #05070f; background: var(--magenta); border: 0; border-radius: 10px; padding: 0 15px;
        box-shadow: 0 0 14px -4px rgba(255, 61, 154, 0.7); transition: 0.15s;
      }
      .respond:hover:not(:disabled) { box-shadow: 0 0 22px -3px rgba(255, 61, 154, 0.9); }
      .respond:disabled { opacity: 0.4; cursor: not-allowed; }

      /* ---- Estados ---- */
      .analyzing {
        font-family: var(--mono); font-size: 11px; letter-spacing: 0.06em; color: var(--magenta);
        animation: grc-pulse 1.5s ease-in-out infinite;
      }
      @keyframes grc-pulse { 0%, 100% { opacity: 0.45; } 50% { opacity: 1; } }
      .done-note {
        display: flex; gap: 8px; align-items: flex-start; font-size: 12px; line-height: 1.5; color: var(--text);
        font-family: var(--sans);
      }
      .done-note .check {
        color: #05070f; background: var(--cyan); border-radius: 50%; width: 16px; height: 16px; flex: none;
        display: inline-flex; align-items: center; justify-content: center; font-size: 11px; margin-top: 1px;
      }
      .err { margin: 0; font-family: var(--mono); font-size: 11.5px; color: var(--red); }

      @media (prefers-reduced-motion: reduce) { .analyzing { animation: none; } }
    `,
  ],
})
export class GrcQuestionCardComponent implements OnInit {
  private readonly governance = inject(GovernanceService);
  private readonly agent = inject(AgentStateService);

  /** Semente do roteamento (Metadata do START_INTERVIEW): a subcategoria NIST a investigar. */
  readonly seed = input.required<AuditorInterviewSeed>();
  /** 1ª pergunta que o Copiloto já formulou (o `reply` do START_INTERVIEW) — abertura da entrevista. */
  readonly prompt = input<string>('');

  // ---- Estado (Signals) ----
  readonly phase = signal<InterviewPhase>('starting');
  readonly current = signal<string>('');
  readonly answer = signal<string>('');
  readonly exchanges = signal<ReadonlyArray<{ question: string; answer: string }>>([]);
  readonly errorMsg = signal<string | null>(null);

  private readonly sessionId = signal<string | null>(null);

  // ---- Derivados ----
  readonly targetCode = computed(() => this.seed().targetSubcategoryCode);
  readonly canAnswer = computed(() => this.answer().trim().length > 0 && this.phase() === 'active');

  readonly phaseLabel = computed(() => {
    switch (this.phase()) {
      case 'starting': return 'Abrindo';
      case 'active': return 'Em curso';
      case 'sending': return 'Enviando';
      case 'done': return 'Concluída';
      case 'error': return 'Erro';
    }
  });

  /** Abre a sessão persistida assim que o cartão monta (a semente já veio do roteamento). */
  ngOnInit(): void {
    const code = this.seed().targetSubcategoryCode;
    const req: StartInterviewRequest = { subcategoryCodes: code ? [code] : null };
    this.governance.startInterview(req).subscribe({
      next: (turn) => this.applyTurn(turn),
      error: () => this.fail(),
    });
  }

  /** Registra a resposta corrente e busca a próxima pergunta (ou encerra). */
  submit(): void {
    const id = this.sessionId();
    const text = this.answer().trim();
    if (!id || !text || this.phase() !== 'active') return;

    const question = this.current();
    this.phase.set('sending');
    this.governance.answerInterview(id, { content: text }).subscribe({
      next: (turn) => {
        this.exchanges.update((x) => [...x, { question, answer: text }]);
        this.answer.set('');
        this.applyTurn(turn);
      },
      error: () => this.fail(),
    });
  }

  onEnter(event: Event): void {
    const ke = event as KeyboardEvent;
    if (ke.shiftKey) return; // Shift+Enter → quebra de linha
    ke.preventDefault();
    this.submit();
  }

  /** Projeta um turno do backend no estado do cartão + publica os efeitos colaterais no barramento. */
  private applyTurn(turn: InterviewTurn): void {
    this.sessionId.set(turn.sessionId);

    // Barramento reverso: as telas de cobertura de Governança reagem a esta mudança.
    if (turn.coverageChange) this.agent.notifyCoverageChanged(turn.coverageChange);

    if (turn.isComplete || !turn.question) {
      this.current.set('');
      this.phase.set('done');
      this.publishOutcomes(turn.sessionId);
      return;
    }
    this.current.set(turn.question.content);
    this.phase.set('active');
  }

  /** Ao encerrar, puxa os riscos identificados e publica o último (gancho do futuro módulo de Riscos). */
  private publishOutcomes(id: string): void {
    this.governance.getOutcomes(id).subscribe({
      next: (risks) => {
        const last = risks.at(-1);
        if (last) this.agent.notifyRiskIdentified(last);
      },
      error: () => {
        /* Outcomes são telemetria de bônus — nunca podem quebrar o encerramento visual da entrevista. */
      },
    });
  }

  private fail(): void {
    this.phase.set('error');
    this.errorMsg.set('Não foi possível conduzir a entrevista agora. Tente novamente pelo chat.');
  }
}

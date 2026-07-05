import { DatePipe } from '@angular/common';
import { Component, ElementRef, computed, effect, inject, input, signal, viewChild } from '@angular/core';
import { GovernanceService } from '../services/governance.service';
import { AgentStateService } from '../services/agent-state.service';
import { Gap, IdentifiedRisk, InterviewMessage } from '../models/governance.models';

type ChatPhase = 'idle' | 'starting' | 'active' | 'completed' | 'error';

/**
 * GOVERN → Auditor Virtual (chatbot GRC). Vive dentro do <app-drawer> e conduz a entrevista
 * de fechamento de lacunas: a IA pergunta, o usuário responde, o backend atualiza o ledger de
 * cobertura e — quando a resposta confirma uma lacuna na prática — já materializa um IdentifiedRisk
 * (turn.identifiedRiskId). Este componente detecta isso, puxa o detalhe via getOutcomes e oferece
 * a conversão visual em Risco na base do chat.
 *
 * Padrão da casa: standalone, signals, sem @angular/forms (eventos nativos + [value]).
 * Só consome métodos que já existem no GovernanceService (start/answer/complete/gaps/outcomes).
 */
@Component({
  selector: 'app-auditor-chat',
  standalone: true,
  imports: [DatePipe],
  template: `
    <div class="chat">
      @if (!isGovern()) {
        <!-- Contexto NIST ainda sem agente dedicado (Passo 3: switch preparado para o futuro). -->
        <div class="ctx-soon">
          <div class="cs-icon">🚧</div>
          <h4>Auditor de {{ ctxLabel() }}</h4>
          <p>
            O agente para o pilar <b>{{ ctxCode() }}</b> ainda está em construção. Selecione a aba
            <b>Governança</b> para o diagnóstico de lacunas GRC.
          </p>
        </div>
      } @else {
      <!-- ---- Histórico (rolável) ---- -->
      <div class="chat-scroll" #scroller>
        <!-- Boas-vindas / diagnóstico ainda não iniciado -->
        @if (phase() === 'idle') {
          <div class="welcome">
            <div class="wl-icon">🛡</div>
            <h4>Diagnóstico de Lacunas · GRC</h4>
            <p class="wl-desc">
              Conduzo uma entrevista para fechar as subcategorias do pilar <b>Govern (GV)</b> ainda
              não cobertas pelos seus documentos. Cada resposta atualiza a cobertura e pode registrar
              um risco identificado.
            </p>
            @if (gaps().length) {
              <div class="wl-gaps">
                <span class="wl-gaps-h">{{ gaps().length }} lacuna(s) a investigar</span>
                <ul>
                  @for (g of gapsPreview(); track g.code) {
                    <li><span class="g-code">{{ g.code }}</span> {{ g.description }}</li>
                  }
                </ul>
                @if (gaps().length > 6) {
                  <span class="wl-more">+{{ gaps().length - 6 }} outras</span>
                }
              </div>
            }
            <button type="button" class="btn primary" (click)="start()">Iniciar diagnóstico</button>
          </div>
        }

        @if (phase() === 'starting') {
          <div class="starting">
            <span class="spinner"></span>
            <span>Preparando a entrevista…</span>
          </div>
        }

        <!-- Bolhas de conversa -->
        @for (m of messages(); track m.id) {
          <div class="msg" [class.user]="m.role === 'User'" [class.ai]="m.role !== 'User'">
            <div class="bubble">
              @if (m.role !== 'User' && m.targetSubcategoryCode) {
                <span class="bubble-tag">{{ m.targetSubcategoryCode }}</span>
              }
              <p class="bubble-txt">{{ m.content }}</p>
              <span class="bubble-time">{{ m.sentAt | date: 'HH:mm' }}</span>
            </div>
          </div>
        }

        <!-- IA "digitando" -->
        @if (sending()) {
          <div class="msg ai">
            <div class="bubble typing"><span></span><span></span><span></span></div>
          </div>
        }
      </div>

      <!-- ---- Base do chat ---- -->
      <div class="chat-foot">
        @if (error()) {
          <p class="chat-error">{{ error() }}</p>
        }

        <!-- Passo 4: lacunas detectadas → conversão em Risco Identificado -->
        @if (risks().length) {
          <div class="risk-tray">
            <div class="rt-head">
              <span class="rt-warn">⚠</span> Lacunas identificadas
              <span class="rt-count">{{ risks().length }}</span>
            </div>
            @for (r of risks(); track r.id) {
              <div class="risk-card" [class.done]="isConverted(r.id)">
                <div class="rc-top">
                  <span class="rc-code">{{ r.subcategoryCode }}</span>
                  <span class="rc-title">{{ r.title }}</span>
                </div>
                @if (r.description) {
                  <p class="rc-desc">{{ r.description }}</p>
                }
                @if (r.cause || r.consequence) {
                  <div class="rc-meta">
                    @if (r.cause) {
                      <span><b>Causa:</b> {{ r.cause }}</span>
                    }
                    @if (r.consequence) {
                      <span><b>Consequência:</b> {{ r.consequence }}</span>
                    }
                  </div>
                }
                <div class="rc-actions">
                  @if (isConverted(r.id)) {
                    <span class="rc-done">✓ Convertido em Risco</span>
                  } @else {
                    <button type="button" class="btn danger sm" (click)="convertRisk(r)">
                      Converter em Risco Identificado
                    </button>
                  }
                </div>
              </div>
            }
          </div>
        }

        <!-- Composer (só com a sessão ativa) -->
        @if (phase() === 'active') {
          <div class="composer">
            <textarea
              #composerInput
              rows="1"
              placeholder="Escreva sua resposta…"
              [value]="draft()"
              (input)="draft.set($any($event.target).value)"
              (keydown.enter)="onEnter($event)"
              [disabled]="sending()"
            ></textarea>
            <button type="button" class="btn primary send" (click)="send()" [disabled]="!canSend()">
              Enviar
            </button>
          </div>
          <div class="composer-foot">
            <button type="button" class="link-btn" (click)="finish()">Encerrar entrevista</button>
            <span class="hint">Enter envia · Shift+Enter quebra linha</span>
          </div>
        }

        <!-- Entrevista encerrada -->
        @if (phase() === 'completed') {
          <div class="done-bar">
            <span>✓ Entrevista encerrada. {{ convertedCount() }} de {{ risks().length }} lacuna(s) convertida(s).</span>
            <button type="button" class="btn" (click)="restart()">Novo diagnóstico</button>
          </div>
        }

        @if (phase() === 'error') {
          <div class="done-bar">
            <button type="button" class="btn" (click)="restart()">Tentar novamente</button>
          </div>
        }
      </div>
      }
    </div>
  `,
  styles: [
    `
      .chat { display: flex; flex-direction: column; height: 100%; min-height: 0; }

      /* ---- Contexto sem agente dedicado (placeholder) ---- */
      .ctx-soon { margin: auto; padding: 30px 22px; text-align: center; display: flex; flex-direction: column; align-items: center; gap: 4px; }
      .cs-icon { font-size: 34px; filter: drop-shadow(0 0 14px rgba(38, 224, 255, 0.35)); }
      .ctx-soon h4 { margin: 10px 0 2px; font-family: var(--display); font-weight: 700; font-size: 15px; letter-spacing: 0.06em; color: var(--text); }
      .ctx-soon p { max-width: 320px; margin: 0; font-size: 12.5px; line-height: 1.6; color: var(--muted); }

      /* ---- Histórico ---- */
      .chat-scroll {
        flex: 1; min-height: 0; overflow-y: auto;
        display: flex; flex-direction: column; gap: 12px;
        padding: 18px 18px 6px;
      }
      .chat-scroll::-webkit-scrollbar { width: 8px; }
      .chat-scroll::-webkit-scrollbar-thumb { background: var(--line); border-radius: 8px; }

      /* ---- Boas-vindas ---- */
      .welcome { text-align: center; margin: auto 0; padding: 10px 6px; }
      .wl-icon { font-size: 34px; filter: drop-shadow(0 0 14px rgba(38, 224, 255, 0.5)); }
      .welcome h4 {
        margin: 12px 0 8px; font-family: var(--display); font-weight: 700; font-size: 15px;
        letter-spacing: 0.06em; color: var(--text);
      }
      .wl-desc { font-size: 12.5px; line-height: 1.6; color: var(--muted); margin: 0 auto 16px; max-width: 340px; }
      .wl-gaps { text-align: left; background: var(--panel-2); border: 1px solid var(--line); border-radius: 12px; padding: 12px 14px; margin: 0 auto 18px; max-width: 360px; }
      .wl-gaps-h { font-family: var(--mono); font-size: 10.5px; text-transform: uppercase; letter-spacing: 0.12em; color: var(--amber); }
      .wl-gaps ul { list-style: none; margin: 8px 0 0; padding: 0; display: flex; flex-direction: column; gap: 6px; }
      .wl-gaps li { font-size: 12px; color: var(--text); line-height: 1.4; }
      .g-code { font-family: var(--mono); font-size: 11px; color: var(--cyan-2); margin-right: 6px; }
      .wl-more { display: inline-block; margin-top: 8px; font-family: var(--mono); font-size: 10.5px; color: var(--muted); }

      .starting { display: flex; align-items: center; justify-content: center; gap: 10px; margin: auto 0; font-family: var(--mono); font-size: 12px; color: var(--muted); }
      .spinner { width: 15px; height: 15px; border: 2px solid var(--line); border-top-color: var(--cyan); border-radius: 50%; animation: spin 0.7s linear infinite; }
      @keyframes spin { to { transform: rotate(360deg); } }

      /* ---- Bolhas ---- */
      .msg { display: flex; }
      .msg.user { justify-content: flex-end; }
      .msg.ai { justify-content: flex-start; }
      .bubble {
        max-width: 84%; padding: 10px 13px; font-size: 13px; line-height: 1.5;
        position: relative; border: 1px solid var(--line);
      }
      .msg.ai .bubble { background: var(--panel-2); border-radius: 4px 14px 14px 14px; color: var(--text); }
      .msg.user .bubble {
        background: linear-gradient(180deg, rgba(38, 224, 255, 0.16), rgba(38, 224, 255, 0.06));
        border-color: rgba(38, 224, 255, 0.35); border-radius: 14px 4px 14px 14px; color: var(--text);
      }
      .bubble-txt { margin: 0; white-space: pre-wrap; word-break: break-word; }
      .bubble-tag {
        display: inline-block; margin-bottom: 5px; font-family: var(--mono); font-size: 9.5px;
        letter-spacing: 0.08em; color: var(--cyan-2); text-transform: uppercase;
        border: 1px solid rgba(70, 176, 255, 0.3); border-radius: 999px; padding: 1px 7px;
      }
      .bubble-time { display: block; margin-top: 5px; font-family: var(--mono); font-size: 9.5px; color: var(--muted); text-align: right; }

      .bubble.typing { display: inline-flex; gap: 4px; padding: 12px 14px; }
      .bubble.typing span { width: 6px; height: 6px; border-radius: 50%; background: var(--muted); animation: blink 1.2s infinite both; }
      .bubble.typing span:nth-child(2) { animation-delay: 0.2s; }
      .bubble.typing span:nth-child(3) { animation-delay: 0.4s; }
      @keyframes blink { 0%, 80%, 100% { opacity: 0.25; } 40% { opacity: 1; } }

      /* ---- Base ---- */
      .chat-foot { flex: none; border-top: 1px solid var(--line); padding: 12px 16px 14px; display: flex; flex-direction: column; gap: 10px; }
      .chat-error { margin: 0; font-family: var(--mono); font-size: 11.5px; color: var(--red); }

      /* ---- Passo 4: bandeja de lacunas/riscos ---- */
      .risk-tray {
        border: 1px solid rgba(255, 61, 154, 0.3); border-radius: 12px;
        background: rgba(255, 61, 154, 0.05); padding: 10px 12px;
        max-height: 208px; overflow-y: auto; display: flex; flex-direction: column; gap: 9px;
      }
      .rt-head { font-family: var(--mono); font-size: 11px; text-transform: uppercase; letter-spacing: 0.1em; color: var(--magenta); display: flex; align-items: center; gap: 7px; }
      .rt-warn { font-size: 13px; }
      .rt-count { margin-left: auto; font-family: var(--display); font-weight: 700; color: var(--text); }
      .risk-card { border: 1px solid var(--line); border-radius: 10px; background: var(--panel); padding: 10px 12px; transition: 0.2s; }
      .risk-card.done { border-color: rgba(38, 224, 255, 0.4); background: rgba(38, 224, 255, 0.05); }
      .rc-top { display: flex; align-items: baseline; gap: 8px; }
      .rc-code { font-family: var(--mono); font-size: 10.5px; color: var(--magenta); border: 1px solid rgba(255, 61, 154, 0.3); border-radius: 999px; padding: 1px 7px; flex: none; }
      .rc-title { font-size: 12.5px; font-weight: 600; color: var(--text); }
      .rc-desc { margin: 7px 0 0; font-size: 12px; line-height: 1.5; color: var(--muted); }
      .rc-meta { margin-top: 7px; display: flex; flex-direction: column; gap: 3px; font-size: 11.5px; color: var(--muted); line-height: 1.45; }
      .rc-meta b { color: var(--text); font-weight: 600; }
      .rc-actions { margin-top: 10px; }
      .rc-done { font-family: var(--mono); font-size: 11px; color: var(--cyan); }

      /* ---- Composer ---- */
      .composer { display: flex; gap: 9px; align-items: flex-end; }
      .composer textarea {
        flex: 1; resize: none; max-height: 120px; font-family: var(--sans); font-size: 13px; color: var(--text);
        background: var(--panel-2); border: 1px solid var(--line); border-radius: 11px; padding: 10px 12px; line-height: 1.5;
      }
      .composer textarea:focus { outline: none; border-color: rgba(38, 224, 255, 0.5); }
      .composer textarea:disabled { opacity: 0.5; }
      .composer-foot { display: flex; align-items: center; justify-content: space-between; }
      .link-btn { background: none; border: none; cursor: pointer; font-family: var(--mono); font-size: 11px; color: var(--magenta); padding: 0; }
      .link-btn:hover { text-decoration: underline; }
      .hint { font-family: var(--mono); font-size: 10px; color: var(--muted); letter-spacing: 0.04em; }

      .done-bar { display: flex; align-items: center; justify-content: space-between; gap: 12px; font-family: var(--mono); font-size: 12px; color: var(--cyan); }

      /* ---- Botões (component-scoped) ---- */
      .btn {
        font-family: var(--mono); font-size: 12px; color: var(--text); cursor: pointer; transition: 0.15s;
        background: var(--panel-2); border: 1px solid var(--line); border-radius: 10px; padding: 9px 16px;
      }
      .btn:hover:not(:disabled) { border-color: rgba(38, 224, 255, 0.5); }
      .btn:disabled { opacity: 0.4; cursor: not-allowed; }
      .btn.sm { padding: 6px 12px; font-size: 11px; }
      .btn.primary { color: #05070f; font-weight: 600; border-color: transparent; background: var(--neon-h); box-shadow: 0 0 14px -3px rgba(38, 224, 255, 0.6); }
      .btn.send { align-self: stretch; }
      .btn.danger { color: var(--magenta); border-color: rgba(255, 61, 154, 0.35); background: rgba(255, 61, 154, 0.06); }
      .btn.danger:hover:not(:disabled) { border-color: var(--magenta); }
    `,
  ],
})
export class AuditorChatComponent {
  private readonly svc = inject(GovernanceService);
  private readonly agent = inject(AgentStateService);

  /** Vincula a entrevista a um assessment específico, se houver. */
  assessmentId = input<string | null>(null);

  /** Contexto ativo é GOVERN? Fora dele exibimos o placeholder "em construção". */
  protected readonly isGovern = computed(() => this.agent.activeFunction() === 'Govern');
  /** Rótulo/código do contexto ativo — alimentam o placeholder de contextos futuros. */
  protected readonly ctxLabel = computed(() => this.agent.context().label);
  protected readonly ctxCode = computed(() => this.agent.context().code);

  // ---- Estado ----
  phase = signal<ChatPhase>('idle');
  sessionId = signal<string | null>(null);
  messages = signal<InterviewMessage[]>([]);
  draft = signal('');
  sending = signal(false);
  gaps = signal<Gap[]>([]);
  risks = signal<IdentifiedRisk[]>([]); // lacunas materializadas na sessão
  convertedIds = signal<Set<string>>(new Set());
  error = signal<string | null>(null);

  canSend = computed(
    () => this.draft().trim().length > 0 && !this.sending() && this.phase() === 'active',
  );
  gapsPreview = computed(() => this.gaps().slice(0, 6));
  convertedCount = computed(() => this.convertedIds().size);

  private readonly scroller = viewChild<ElementRef<HTMLDivElement>>('scroller');
  private readonly composerInput = viewChild<ElementRef<HTMLTextAreaElement>>('composerInput');

  constructor() {
    // Passo 3 — o Auditor reage ao estado global. A cada abertura, roteia por Função NIST:
    // em GOVERN semeia o preview de lacunas; as demais ficam preparadas (switch/case).
    effect(() => {
      if (!this.agent.open()) return;
      switch (this.agent.activeFunction()) {
        case 'Govern':
          if (this.phase() === 'idle' && this.gaps().length === 0) this.loadGaps();
          break;
        default:
          // Identify / Protect / Detect / Respond / Recover → agente em construção.
          break;
      }
    });
    // Mantém o histórico rolado até o fim a cada nova mensagem / "digitando".
    effect(() => {
      this.messages();
      this.sending();
      queueMicrotask(() => this.scrollToEnd());
    });
  }

  isConverted(id: string): boolean {
    return this.convertedIds().has(id);
  }

  private loadGaps(): void {
    this.svc.getGaps().subscribe({
      next: (g) => this.gaps.set(g),
      error: () => this.gaps.set([]),
    });
  }

  /** Passo 3 — abre a sessão e recebe a primeira pergunta investigativa da IA. */
  start(): void {
    if (this.phase() === 'starting' || this.phase() === 'active') return;
    this.phase.set('starting');
    this.error.set(null);

    this.svc.startInterview({ assessmentId: this.assessmentId() ?? undefined }).subscribe({
      next: (turn) => {
        this.sessionId.set(turn.sessionId);
        if (turn.question) this.messages.set([turn.question]);
        this.phase.set(turn.isComplete ? 'completed' : 'active');
        this.focusComposer();
      },
      error: (err) => {
        console.error('Falha ao iniciar a entrevista GRC:', err);
        this.error.set(
          err?.status === 400
            ? 'Não há lacunas a investigar — todas as subcategorias GV já estão cobertas.'
            : 'Não foi possível iniciar o Auditor Virtual. Verifique a API e tente novamente.',
        );
        this.phase.set('error');
      },
    });
  }

  /** Passo 3 — registra a resposta e recebe a próxima pergunta (com append otimista da fala). */
  send(): void {
    const text = this.draft().trim();
    const id = this.sessionId();
    if (!text || !id || this.sending() || this.phase() !== 'active') return;

    // O backend só devolve a PRÓXIMA pergunta, não ecoa a resposta — anexamos localmente.
    const userMsg: InterviewMessage = {
      id: `local-${Date.now()}`,
      role: 'User',
      content: text,
      sequence: this.messages().length,
      targetSubcategoryCode: null,
      sentAt: new Date().toISOString(),
    };
    this.messages.update((m) => [...m, userMsg]);
    this.draft.set('');
    this.sending.set(true);
    this.error.set(null);

    this.svc.answerInterview(id, { content: text }).subscribe({
      next: (turn) => {
        this.sending.set(false);
        if (turn.question) this.messages.update((m) => [...m, turn.question!]);
        if (turn.coverageChange) this.agent.notifyCoverageChanged(turn.coverageChange);
        if (turn.identifiedRiskId) this.pullOutcome(id, turn.identifiedRiskId);
        if (turn.isComplete) {
          this.phase.set('completed');
        } else {
          this.focusComposer();
        }
      },
      error: (err) => {
        console.error('Falha ao registrar a resposta na entrevista:', err);
        // Reverte o append otimista e devolve o texto ao campo.
        this.sending.set(false);
        this.messages.update((m) => m.filter((x) => x.id !== userMsg.id));
        this.draft.set(text);
        this.error.set('Não foi possível enviar sua resposta. Tente novamente.');
      },
    });
  }

  /** Passo 4 — puxa o detalhe do IdentifiedRisk recém-criado e o exibe como lacuna convertível. */
  private pullOutcome(sessionId: string, riskId: string): void {
    if (this.risks().some((r) => r.id === riskId)) return;
    this.svc.getOutcomes(sessionId).subscribe({
      next: (risks) => {
        const risk = risks.find((r) => r.id === riskId);
        if (risk && !this.risks().some((r) => r.id === risk.id)) {
          this.risks.update((list) => [...list, risk]);
        }
      },
      error: (err) => console.error('Falha ao carregar o risco identificado:', err),
    });
  }

  /** Passo 4 — conversão visual (só front): consolida o card e avisa o pai. */
  convertRisk(risk: IdentifiedRisk): void {
    if (this.isConverted(risk.id)) return;
    this.convertedIds.update((s) => new Set(s).add(risk.id));
    this.agent.notifyRiskIdentified(risk);
  }

  /** Encerra a sessão manualmente. */
  finish(): void {
    const id = this.sessionId();
    if (!id) {
      this.phase.set('completed');
      return;
    }
    this.svc.completeInterview(id).subscribe({
      next: () => this.phase.set('completed'),
      error: (err) => {
        console.error('Falha ao encerrar a entrevista:', err);
        this.phase.set('completed');
      },
    });
  }

  /** Zera para um novo diagnóstico (preserva o drawer aberto). */
  restart(): void {
    this.phase.set('idle');
    this.sessionId.set(null);
    this.messages.set([]);
    this.risks.set([]);
    this.convertedIds.set(new Set());
    this.draft.set('');
    this.error.set(null);
    this.loadGaps();
  }

  onEnter(event: Event): void {
    const ke = event as KeyboardEvent;
    if (ke.shiftKey) return; // Shift+Enter → quebra de linha
    ke.preventDefault();
    this.send();
  }

  private scrollToEnd(): void {
    const el = this.scroller()?.nativeElement;
    if (el) el.scrollTop = el.scrollHeight;
  }

  private focusComposer(): void {
    queueMicrotask(() => this.composerInput()?.nativeElement.focus());
  }
}

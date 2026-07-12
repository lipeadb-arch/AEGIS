import { DatePipe } from '@angular/common';
import { Component, ElementRef, computed, effect, inject, signal, viewChild } from '@angular/core';
import { AgentStateService } from '../services/agent-state.service';
import { AuditorService } from '../services/auditor.service';

/** Uma fala do chat do Copiloto GRC. `at` (epoch ms) alimenta o rótulo de hora. */
interface ChatMessage {
  readonly role: 'user' | 'assistant';
  readonly content: string;
  readonly at: number;
}

/**
 * GOVERN → Auditor Virtual, agora o COPILOTO GRC ONIPRESENTE. Chat livre, com consciência de contexto:
 * cada mensagem é enviada no escopo da tela ativa (AgentStateService.contextScope → PR/DE/GLOBAL…), que o
 * backend usa para ajustar dinamicamente o System Prompt. Estado 100% em Signals; sem RxJS na view.
 */
@Component({
  selector: 'app-auditor-chat',
  standalone: true,
  imports: [DatePipe],
  template: `
    <div class="copilot">
      <div class="stream" #scroller>
        @if (chatHistory().length === 0 && !isAnalyzing()) {
          <div class="intro">
            <div class="intro-mark">◈</div>
            <h4>Copiloto GRC · {{ scopeLabel() }}</h4>
            <p>
              Pergunte sobre a postura de segurança no escopo ativo
              (<span class="chip">{{ currentScope() }}</span>). Analiso os controles NIST, aponto
              não-conformidades e recomendo prioridades.
            </p>
          </div>
        }

        @for (m of chatHistory(); track $index) {
          <div class="row" [class.me]="m.role === 'user'">
            <div class="bubble" [class.user]="m.role === 'user'" [class.auditor]="m.role === 'assistant'">
              <p class="txt">{{ m.content }}</p>
              <span class="time">{{ m.at | date: 'HH:mm' }}</span>
            </div>
          </div>
        }

        @if (isAnalyzing()) {
          <div class="row">
            <div class="analyzing">[ {{ analyzingLabel() }} ]</div>
          </div>
        }
      </div>

      <div class="foot">
        @if (error()) {
          <p class="err">{{ error() }}</p>
        }

        <div class="composer">
          <textarea
            rows="1"
            [value]="draft()"
            (input)="draft.set($any($event.target).value)"
            (keydown.enter)="onEnter($event)"
            [disabled]="isAnalyzing()"
            [placeholder]="'Pergunte ao Copiloto sobre ' + currentScope() + '…'"
          ></textarea>
          <button type="button" class="send" (click)="send()" [disabled]="!canSend()">
            {{ isAnalyzing() ? '···' : 'Enviar' }}
          </button>
        </div>

        <div class="meta">
          <span class="scope">Escopo ativo: <b>{{ currentScope() }}</b></span>
          <span class="hint">Enter envia · Shift+Enter quebra linha</span>
        </div>
      </div>
    </div>
  `,
  styles: [
    `
      :host { display: flex; flex-direction: column; height: 100%; min-height: 0; }
      .copilot { display: flex; flex-direction: column; height: 100%; min-height: 0; }

      /* ---- Fluxo de mensagens (rolável) ---- */
      .stream {
        flex: 1; min-height: 0; overflow-y: auto;
        display: flex; flex-direction: column; gap: 12px; padding: 18px 18px 8px;
      }
      .stream::-webkit-scrollbar { width: 8px; }
      .stream::-webkit-scrollbar-thumb { background: var(--line); border-radius: 8px; }

      /* ---- Boas-vindas ---- */
      .intro { text-align: center; margin: auto 0; padding: 12px 6px; }
      .intro-mark { font-size: 30px; color: var(--cyan); filter: drop-shadow(0 0 14px rgba(38, 224, 255, 0.55)); }
      .intro h4 {
        margin: 12px 0 8px; font-family: var(--display); font-weight: 700; font-size: 15px;
        letter-spacing: 0.05em; color: var(--text);
      }
      .intro p { max-width: 340px; margin: 0 auto; font-size: 12.5px; line-height: 1.6; color: var(--muted); }
      .chip {
        font-family: var(--mono); font-size: 11px; color: var(--cyan);
        border: 1px solid rgba(38, 224, 255, 0.35); border-radius: 999px; padding: 1px 8px;
      }

      /* ---- Bolhas ---- */
      .row { display: flex; }
      .row.me { justify-content: flex-end; }
      .bubble {
        max-width: 86%; padding: 10px 13px; font-size: 13px; line-height: 1.55;
        border: 1px solid var(--line); background: var(--panel-2); position: relative;
      }
      /* Usuário → cyan sutil. */
      .bubble.user {
        border-color: rgba(38, 224, 255, 0.35); border-radius: 14px 4px 14px 14px;
        background: linear-gradient(180deg, rgba(38, 224, 255, 0.13), rgba(38, 224, 255, 0.04));
      }
      /* Auditor → magenta/brand. */
      .bubble.auditor {
        border-color: rgba(255, 61, 154, 0.35); border-radius: 4px 14px 14px 14px;
        background: linear-gradient(180deg, rgba(255, 61, 154, 0.1), rgba(255, 61, 154, 0.03));
        box-shadow: inset 0 0 26px -18px rgba(255, 61, 154, 0.7);
      }
      .txt { margin: 0; white-space: pre-wrap; word-break: break-word; color: var(--text); }
      .time { display: block; margin-top: 5px; font-family: var(--mono); font-size: 9.5px; color: var(--muted); text-align: right; }

      /* ---- Indicador técnico de análise (sem spinner genérico) ---- */
      .analyzing {
        font-family: var(--mono); font-size: 11.5px; letter-spacing: 0.06em; color: var(--cyan);
        padding: 4px 2px; animation: analyze-pulse 1.5s ease-in-out infinite;
      }
      @keyframes analyze-pulse { 0%, 100% { opacity: 0.4; } 50% { opacity: 1; } }

      /* ---- Base: erro + composer ---- */
      .foot { flex: none; border-top: 1px solid var(--line); padding: 12px 16px 14px; display: flex; flex-direction: column; gap: 9px; }
      .err { margin: 0; font-family: var(--mono); font-size: 11.5px; color: var(--red); }

      .composer { display: flex; gap: 9px; align-items: flex-end; }
      .composer textarea {
        flex: 1; resize: none; max-height: 120px; font-family: var(--sans); font-size: 13px; color: var(--text);
        background: var(--panel-2); border: 1px solid var(--line); border-radius: 11px; padding: 10px 12px; line-height: 1.5;
      }
      .composer textarea:focus { outline: none; border-color: rgba(38, 224, 255, 0.5); }
      .composer textarea:disabled { opacity: 0.5; cursor: not-allowed; }

      .send {
        align-self: stretch; cursor: pointer; font-family: var(--mono); font-size: 12px; font-weight: 600;
        color: #05070f; background: var(--neon-h); border: 1px solid transparent; border-radius: 11px;
        padding: 0 18px; box-shadow: 0 0 14px -3px rgba(38, 224, 255, 0.6); transition: 0.15s;
      }
      .send:hover:not(:disabled) { box-shadow: 0 0 22px -3px rgba(38, 224, 255, 0.85); }
      .send:disabled { opacity: 0.4; cursor: not-allowed; }

      .meta { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
      .scope { font-family: var(--mono); font-size: 10.5px; color: var(--muted); }
      .scope b { color: var(--cyan); }
      .hint { font-family: var(--mono); font-size: 10px; color: var(--muted); letter-spacing: 0.04em; }

      @media (prefers-reduced-motion: reduce) { .analyzing { animation: none; } }
    `,
  ],
})
export class AuditorChatComponent {
  private readonly auditor = inject(AuditorService);
  private readonly agent = inject(AgentStateService);

  // ---- Estado (Signals) ----
  readonly chatHistory = signal<ChatMessage[]>([]);
  readonly isAnalyzing = signal(false);
  readonly draft = signal('');
  readonly error = signal<string | null>(null);

  // ---- Contexto (derivado do AgentStateService) ----
  /** Código da tela ativa (PR/DE/GLOBAL…) — o escopo enviado ao backend. */
  readonly currentScope = computed(() => this.agent.contextScope());
  /** Rótulo PT-BR do contexto ativo (para as boas-vindas). */
  readonly scopeLabel = computed(() => this.agent.context().label);

  readonly canSend = computed(() => this.draft().trim().length > 0 && !this.isAnalyzing());

  /** Indicador de carregamento técnico, alinhado ao escopo (não um spinner genérico). */
  readonly analyzingLabel = computed(() => {
    const s = this.currentScope();
    return s === 'GLOBAL'
      ? 'Analisando a Postura Global do Secure Score...'
      : `Analisando Telemetria e Controles do Pilar ${s}...`;
  });

  private readonly scroller = viewChild<ElementRef<HTMLDivElement>>('scroller');

  constructor() {
    // Auto-scroll reativo (viewChild + Signals): re-dispara quando o histórico cresce ou o indicador
    // de análise aparece/some. O microtask garante que o DOM já foi pintado antes de medir a altura.
    effect(() => {
      this.chatHistory();
      this.isAnalyzing();
      queueMicrotask(() => {
        const el = this.scroller()?.nativeElement;
        if (el) el.scrollTop = el.scrollHeight;
      });
    });
  }

  /** Envia a mensagem no escopo ativo: anexa a fala, ativa a análise, chama o serviço, anexa a resposta. */
  send(): void {
    const text = this.draft().trim();
    if (!text || this.isAnalyzing()) return;

    const scope = this.agent.contextScope();
    // Histórico ANTES desta fala — é o que o backend recebe como contexto (a nova mensagem vai separada).
    const priorHistory = this.chatHistory().map((m) => ({ role: m.role, content: m.content }));

    this.chatHistory.update((h) => [...h, { role: 'user', content: text, at: Date.now() }]);
    this.draft.set('');
    this.error.set(null);
    this.isAnalyzing.set(true);

    this.auditor.chat(scope, text, priorHistory).subscribe({
      next: (res) => {
        this.chatHistory.update((h) => [...h, { role: 'assistant', content: res.reply, at: Date.now() }]);
        this.isAnalyzing.set(false);
      },
      error: () => {
        this.isAnalyzing.set(false);
        this.error.set('O Copiloto está indisponível no momento. Tente novamente.');
      },
    });
  }

  onEnter(event: Event): void {
    const ke = event as KeyboardEvent;
    if (ke.shiftKey) return; // Shift+Enter → quebra de linha
    ke.preventDefault();
    this.send();
  }
}

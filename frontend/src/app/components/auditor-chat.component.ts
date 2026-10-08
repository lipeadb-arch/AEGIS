import { DatePipe } from '@angular/common';
import { Component, ElementRef, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { AuditorChatReply, AuditorSource, chatRequest, natureTone, sameSession, sourceRouterLink } from '../models/auditor.models';
import { AgentStateService } from '../services/agent-state.service';
import { AuditorApiError, AuditorService, BlastRadiusResponse } from '../services/auditor.service';
import { AiModeBannerComponent } from './ai-mode-banner.component';
import { BlastRadiusGraphComponent } from './blast-radius-graph.component';

/** Uma entrada do fluxo — união discriminada por `type`. */
type ChatMessage = UserMessage | AssistantMessage | BlastRadiusMessage;

interface UserMessage {
  readonly type: 'user';
  readonly content: string;
  readonly focus: string;
  readonly at: number;
}

/** Resposta do Auditor: texto conferido no servidor, fontes citadas (abrem o registro), limitações e notas da conferência. */
interface AssistantMessage {
  readonly type: 'assistant';
  readonly content: string;
  readonly focus: string;
  readonly simulated: boolean;
  readonly sources: AuditorSource[];
  readonly limitations: string[];
  readonly notes: string[];
  readonly at: number;
}

interface BlastRadiusMessage {
  readonly type: 'blast_radius';
  readonly at: number;
  readonly data: BlastRadiusResponse;
}

const UUID = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i;

/**
 * [AEGIS-AUDITOR-CONTEXT-01] Auditor Virtual — a MESMA identidade em toda a aplicação. A página aberta dá o foco (dito no topo e em cada
 * pergunta); o servidor monta o contexto do tenant e devolve a resposta com as FONTES citadas (natureza, data, limitação e link para o
 * registro de origem), as limitações do contexto e se veio do motor simulado. Nenhuma resposta grava ou altera resultados.
 *
 * Isolamento na tela: a conversa é da sessão (tenant · conta · época). Trocar de ambiente, sair ou "Nova conversa" limpa o histórico e
 * descarta respostas atrasadas; a troca de página no mesmo ambiente não — a resposta é da pergunta feita naquele foco.
 */
@Component({
  selector: 'app-auditor-chat',
  standalone: true,
  imports: [DatePipe, RouterLink, BlastRadiusGraphComponent, AiModeBannerComponent],
  template: `
    <div class="copilot">
      <app-ai-mode-banner />
      <div class="focusbar">
        <span class="focus">Foco: <b>{{ agent.focusLabel() }}</b></span>
        <button type="button" class="ghost xs" (click)="newConversation()" [disabled]="history().length === 0 && !isAnalyzing()">Nova conversa</button>
      </div>
      <div class="stream" #scroller aria-live="polite">
        @if (history().length === 0 && !isAnalyzing()) {
          <div class="intro">
            <div class="intro-mark">◈</div>
            <h4>Auditor Virtual do AEGIS</h4>
            <p>
              Analiso os registros deste ambiente — avaliações do KNIGHT, a jornada NIST, documentos, inventário, achados, planos e
              publicações — e relaciono as evidências aos requisitos. Indico de onde vem cada informação, o que ela não comprova e o que
              verificar. A validação e as gravações continuam com você.
            </p>
          </div>
        }

        @for (m of history(); track $index) {
          @switch (m.type) {
            @case ('user') {
              <div class="row me">
                <div class="bubble user">
                  <p class="txt">{{ m.content }}</p>
                  <span class="time">{{ m.focus }} · {{ m.at | date: 'HH:mm' }}</span>
                </div>
              </div>
            }
            @case ('assistant') {
              <div class="row">
                <div class="bubble auditor">
                  @if (m.simulated) { <span class="badge warn">Demonstração — motor simulado, sem análise por IA</span> }
                  <p class="txt">{{ m.content }}</p>
                  @if (m.sources.length) {
                    <details class="srcs" open>
                      <summary>Fontes citadas ({{ m.sources.length }})</summary>
                      <ul>
                        @for (s of m.sources; track s.key) {
                          <li>
                            <span class="key">[{{ s.key }}]</span>
                            @if (link(s); as l) {
                              <a [routerLink]="l.commands" [queryParams]="l.queryParams" [fragment]="l.fragment">{{ s.title }}</a>
                            } @else { <span>{{ s.title }}</span> }
                            <span [class]="'badge ' + tone(s.nature)">{{ s.natureLabel }}</span>
                            @if (s.isDemo) { <span class="badge warn">demonstração</span> }
                            @if (s.date) { <span class="muted">{{ s.date }}</span> }
                            @if (s.limitation) { <span class="lim">{{ s.limitation }}</span> }
                          </li>
                        }
                      </ul>
                    </details>
                  }
                  @if (m.notes.length) { <ul class="notes">@for (n of m.notes; track n) { <li>{{ n }}</li> }</ul> }
                  @if (m.limitations.length) {
                    <details class="lims"><summary>Limites do contexto ({{ m.limitations.length }})</summary>
                      <ul>@for (l of m.limitations; track l) { <li>{{ l }}</li> }</ul></details>
                  }
                  <span class="time">{{ m.focus }} · {{ m.at | date: 'HH:mm' }}</span>
                </div>
              </div>
            }
            @case ('blast_radius') {
              <div class="row wide"><app-blast-radius-graph [data]="m.data" /></div>
            }
          }
        }

        @if (isAnalyzing()) {
          <div class="row"><div class="analyzing" role="status">[ Analisando os registros do ambiente… ]</div></div>
        }
      </div>

      <div class="foot">
        @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
        <div class="composer">
          <textarea
            rows="1"
            aria-label="Pergunta ao Auditor Virtual"
            [value]="draft()"
            (input)="draft.set($any($event.target).value)"
            (keydown.enter)="onEnter($event)"
            [disabled]="isAnalyzing()"
            maxlength="4000"
            placeholder="Pergunte sobre as evidências, lacunas ou riscos…"
          ></textarea>
          <button type="button" class="send" (click)="send()" [disabled]="!canSend()">{{ isAnalyzing() ? '···' : 'Enviar' }}</button>
        </div>
        <div class="meta">
          <span class="hint">As respostas são sugestões; nada é gravado pela conversa.</span>
          <span class="hint">Enter envia · Shift+Enter quebra linha</span>
        </div>
      </div>
    </div>
  `,
  styles: [
    `
      :host { display: flex; flex-direction: column; height: 100%; min-height: 0; }
      .copilot { display: flex; flex-direction: column; height: 100%; min-height: 0; }
      .focusbar { display: flex; align-items: center; justify-content: space-between; gap: 8px; padding: 8px 16px; border-bottom: 1px solid var(--line); }
      .focus { font-size: var(--fs-meta); color: var(--muted); overflow-wrap: anywhere; }
      .focus b { color: var(--text); font-weight: 600; }
      .stream { flex: 1; min-height: 0; overflow-y: auto; display: flex; flex-direction: column; gap: 12px; padding: 16px 16px 8px; }
      .intro { text-align: center; margin: auto 0; padding: 12px 6px; }
      .intro-mark { font-size: 30px; color: var(--cyan); }
      .intro h4 { margin: 12px 0 8px; font-weight: 700; font-size: 15px; color: var(--text); }
      .intro p { max-width: 360px; margin: 0 auto; font-size: 12.5px; line-height: 1.6; color: var(--muted); }
      .row { display: flex; }
      .row.me { justify-content: flex-end; }
      .row.wide { width: 100%; }
      .bubble { max-width: 92%; padding: 10px 13px; font-size: 13px; line-height: 1.55; border: 1px solid var(--line); background: var(--panel-2); }
      .bubble.user { border-color: rgba(38, 224, 255, 0.35); border-radius: 14px 4px 14px 14px; }
      .bubble.auditor { border-color: rgba(255, 61, 154, 0.35); border-radius: 4px 14px 14px 14px; display: flex; flex-direction: column; gap: 6px; }
      .txt { margin: 0; white-space: pre-wrap; word-break: break-word; color: var(--text); }
      .time { display: block; font-size: var(--fs-caps); color: var(--muted); text-align: right; }
      .srcs, .lims { font-size: var(--fs-meta); }
      .srcs summary, .lims summary { cursor: pointer; color: var(--text-2); }
      .srcs ul, .lims ul, .notes { margin: 4px 0 0; padding-left: 16px; display: flex; flex-direction: column; gap: 4px; }
      .srcs li { overflow-wrap: anywhere; }
      .srcs .key { font-family: var(--mono); color: var(--cyan-2); margin-right: 4px; }
      .srcs .badge { margin-left: 4px; }
      .lim { display: block; color: var(--muted); }
      .notes { font-size: var(--fs-meta); color: var(--amber); }
      .analyzing { font-size: var(--fs-meta); color: var(--cyan); padding: 4px 2px; }
      .foot { flex: none; border-top: 1px solid var(--line); padding: 12px 16px 14px; display: flex; flex-direction: column; gap: 9px; }
      .err { margin: 0; font-size: var(--fs-meta); color: var(--red); }
      .composer { display: flex; gap: 9px; align-items: flex-end; }
      .composer textarea { flex: 1; resize: none; max-height: 120px; font-size: 13px; color: var(--text); background: var(--panel-2);
        border: 1px solid var(--line); border-radius: 11px; padding: 10px 12px; line-height: 1.5; }
      .composer textarea:disabled { opacity: 0.5; cursor: not-allowed; }
      .send { align-self: stretch; cursor: pointer; font-size: 12px; font-weight: 600; color: #05070f; background: var(--neon-h);
        border: 1px solid transparent; border-radius: 11px; padding: 0 18px; }
      .send:disabled { opacity: 0.4; cursor: not-allowed; }
      .meta { display: flex; align-items: center; justify-content: space-between; gap: 12px; flex-wrap: wrap; }
      .hint { font-size: var(--fs-caps); color: var(--muted); }
    `,
  ],
})
export class AuditorChatComponent {
  private readonly auditor = inject(AuditorService);
  protected readonly agent = inject(AgentStateService);

  readonly history = signal<ChatMessage[]>([]);
  readonly isAnalyzing = signal(false);
  readonly draft = signal('');
  readonly error = signal<string | null>(null);

  protected readonly tone = natureTone;
  protected readonly link = sourceRouterLink;

  private inFlight: Subscription | null = null;
  private readonly scroller = viewChild<ElementRef<HTMLDivElement>>('scroller');

  constructor() {
    effect(() => {
      this.history();
      this.isAnalyzing();
      queueMicrotask(() => {
        const el = this.scroller()?.nativeElement;
        if (el) el.scrollTop = el.scrollHeight;
      });
    });

    // Nova sessão (troca de ambiente, de conta, saída ou "Nova conversa"): o histórico da tela some e a resposta em curso é descartada.
    effect(() => {
      this.agent.session();
      untracked(() => this.clear());
    });

    // Pergunta semeada por uma tela: enviada no foco atual, como se a pessoa a tivesse digitado.
    effect(() => {
      const pending = this.agent.pendingPrompt();
      if (!pending) return;
      this.agent.consumePendingPrompt();
      this.draft.set(pending);
      queueMicrotask(() => this.send());
    });
  }

  canSend(): boolean {
    return this.draft().trim().length > 0 && !this.isAnalyzing();
  }

  newConversation(): void {
    this.agent.newConversation();
  }

  send(): void {
    const text = this.draft().trim();
    if (!text || this.isAnalyzing()) return;
    const focus = this.agent.focus();
    const focusLabel = this.agent.focusLabel();
    const session = this.agent.session();

    this.history.update((h) => [...h, { type: 'user', content: text, focus: focusLabel, at: Date.now() }]);
    this.draft.set('');
    this.error.set(null);
    this.isAnalyzing.set(true);

    // Raio de explosão só com o identificador de um ativo informado pela pessoa (nunca um ativo de demonstração).
    const asset = /raio de (explos|impacto)|blast\s*radius|topologia/i.test(text) ? text.match(UUID)?.[0] ?? null : null;
    if (asset) {
      this.inFlight = this.auditor.assessBlastRadius(asset).subscribe({
        next: (data) => {
          if (!sameSession(session, this.agent.session())) return;
          this.history.update((h) => [...h, { type: 'blast_radius', at: Date.now(), data }]);
          this.isAnalyzing.set(false);
        },
        error: (e: Error) => {
          if (!sameSession(session, this.agent.session())) return;
          this.isAnalyzing.set(false);
          this.error.set(e.message);
        },
      });
      return;
    }

    this.inFlight = this.auditor.chat(chatRequest(text, focus, this.agent.conversationId())).subscribe({
      next: (r: AuditorChatReply) => {
        // Resposta de outra sessão (outro ambiente, outra conta, conversa encerrada) nunca entra na tela atual.
        if (!sameSession(session, this.agent.session())) return;
        this.agent.adoptConversation(r.conversationId, session);
        this.history.update((h) => [
          ...h,
          {
            type: 'assistant', content: r.reply, focus: r.focusLabel || focusLabel, simulated: r.mode === 'Simulated',
            sources: r.sources ?? [], limitations: r.limitations ?? [], notes: r.notes ?? [], at: Date.now(),
          },
        ]);
        this.isAnalyzing.set(false);
      },
      error: (e: AuditorApiError) => {
        if (!sameSession(session, this.agent.session())) return;
        this.isAnalyzing.set(false);
        this.error.set(e.message);
        // Conversa que o servidor não reconhece (outro ambiente ou conta): a próxima pergunta abre outra, sem apagar este aviso.
        if (e.status === 404 && /conversa/i.test(e.message)) this.agent.forgetConversation(session);
      },
    });
  }

  onEnter(event: Event): void {
    const ke = event as KeyboardEvent;
    if (ke.shiftKey) return;
    ke.preventDefault();
    this.send();
  }

  private clear(): void {
    this.inFlight?.unsubscribe();
    this.inFlight = null;
    this.history.set([]);
    this.isAnalyzing.set(false);
    this.error.set(null);
  }
}

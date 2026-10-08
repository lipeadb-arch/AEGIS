import { Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router } from '@angular/router';
import { AuditorFocus, AuditorSessionKey, auditorFocusFromUrl, focusHint } from '../models/auditor.models';
import { AuthService } from './auth.service';

/**
 * AgentStateService — estado global do Auditor Virtual. [AEGIS-AUDITOR-CONTEXT-01]
 *
 * Um único drawer/chat vive no App. Este serviço guarda:
 *   • se o drawer está aberto;
 *   • o FOCO da tela, derivado da URL (página, seleção do NIST, avaliação e controle do KNIGHT) — o foco não muda a identidade do
 *     Auditor nem restringe o que ele analisa;
 *   • a CONVERSA da tela: o identificador devolvido pelo servidor e a sessão (tenant · conta · época). Trocar de ambiente, sair ou
 *     pedir "Nova conversa" abre outra sessão: o histórico da tela é limpo e respostas atrasadas da anterior são descartadas.
 */
@Injectable({ providedIn: 'root' })
export class AgentStateService {
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);

  // ---- Foco (derivado da rota) ----------------------------------------------------------------

  private readonly _focus = signal<AuditorFocus>(auditorFocusFromUrl(this.router.url));
  /** Página e seleções da tela ativa (conferidas no servidor a cada pergunta). */
  readonly focus = this._focus.asReadonly();
  /** Rótulo curto do foco (o servidor devolve o rótulo completo junto da resposta). */
  readonly focusLabel = computed(() => focusHint(this._focus()));

  // ---- Conversa (tenant · conta · época) -------------------------------------------------------

  private readonly _epoch = signal(0);
  private readonly _conversationId = signal<string | null>(null);
  readonly conversationId = this._conversationId.asReadonly();

  /** A sessão em que uma pergunta é feita; a resposta só entra se a sessão ainda for a mesma. */
  readonly session = computed<AuditorSessionKey>(() => ({
    tenantId: this.auth.activeTenantId(),
    accountId: this.auth.accountId(),
    epoch: this._epoch(),
  }));

  constructor() {
    this.router.events.pipe(takeUntilDestroyed()).subscribe((e) => {
      if (e instanceof NavigationEnd) this._focus.set(auditorFocusFromUrl(e.urlAfterRedirects));
    });
    // Troca de ambiente, de conta ou saída: a conversa da tela não atravessa (o servidor também a recusaria).
    effect(() => {
      this.auth.activeTenantId();
      this.auth.accountId();
      untracked(() => this.newConversation());
    });
  }

  /** Encerra a conversa da tela: limpa o histórico exibido e invalida respostas ainda em curso. */
  newConversation(): void {
    this._conversationId.set(null);
    this._epoch.update((n) => n + 1);
  }

  /** A resposta trouxe o identificador da conversa (só vale se a sessão ainda for a da pergunta). */
  adoptConversation(id: string, session: AuditorSessionKey): void {
    const now = this.session();
    if (now.tenantId === session.tenantId && now.accountId === session.accountId && now.epoch === session.epoch) this._conversationId.set(id);
  }

  /** O servidor não reconhece a conversa: a próxima pergunta abre outra (o histórico exibido continua até a pessoa decidir). */
  forgetConversation(session: AuditorSessionKey): void {
    const now = this.session();
    if (now.tenantId === session.tenantId && now.accountId === session.accountId && now.epoch === session.epoch) this._conversationId.set(null);
  }

  // ---- Drawer ---------------------------------------------------------------------------------

  readonly open = signal(false);

  /** Título estável: a mesma identidade em qualquer página. */
  readonly drawerTitle = computed(() => 'Auditor Virtual');

  /** Subtítulo: o foco da tela (não a personalidade). */
  readonly drawerSubtitle = computed(() => `Foco: ${this.focusLabel()}`);

  openAgent(): void {
    this.open.set(true);
  }

  close(): void {
    this.open.set(false);
  }

  toggle(): void {
    this.open.update((v) => !v);
  }

  // ---- Pergunta semeada por uma tela ---------------------------------------------------------------

  /**
   * Prompt semeado por uma tela ("Analisar com o Auditor"). O chat o consome e envia como se a pessoa o tivesse digitado — no foco da
   * página atual, como qualquer outra pergunta (não há fluxo de entrevista separado).
   */
  private readonly _pendingPrompt = signal<string | null>(null);
  readonly pendingPrompt = this._pendingPrompt.asReadonly();

  requestAudit(prompt: string): void {
    this._pendingPrompt.set(prompt);
    this.open.set(true);
  }

  consumePendingPrompt(): string | null {
    const p = this._pendingPrompt();
    if (p !== null) this._pendingPrompt.set(null);
    return p;
  }
}

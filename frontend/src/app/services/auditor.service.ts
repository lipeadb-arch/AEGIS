import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, throwError } from 'rxjs';
import { environment } from '../../environments/environment';
import { AuditorScope } from './agent-state.service';

/** Uma fala do histórico do Copiloto (papel + conteúdo). */
export interface AuditorChatMessage {
  role: 'user' | 'assistant';
  content: string;
}

/** Resposta do Copiloto GRC: a fala do assistente e o escopo que a produziu. */
export interface AuditorChatReply {
  reply: string;
  scope: string;
}

/**
 * Cliente HTTP do Copiloto GRC ONIPRESENTE (POST /api/v1/auditor/chat). O escopo da tela ativa vem do
 * AgentStateService (`contextScope`) e viaja no corpo; o X-Tenant e o Bearer são injetados pelo
 * authInterceptor. Resiliente: normaliza o erro para a UI tratar com elegância (não derruba o chat).
 */
@Injectable({ providedIn: 'root' })
export class AuditorService {
  private readonly http = inject(HttpClient);
  private readonly url = `${environment.apiBase}/api/v1/auditor/chat`;

  /** Um turno do Copiloto no escopo informado (o backend ajusta o System Prompt por ele). */
  chat(scope: AuditorScope, message: string, history: AuditorChatMessage[] = []): Observable<AuditorChatReply> {
    return this.http
      .post<AuditorChatReply>(this.url, { contextScope: scope, message, history })
      .pipe(
        catchError((err) => {
          console.error('Copiloto GRC: falha no /auditor/chat.', err);
          return throwError(() => new Error('O Copiloto está indisponível no momento. Tente novamente.'));
        }),
      );
  }
}

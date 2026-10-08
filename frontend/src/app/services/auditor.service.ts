import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, throwError } from 'rxjs';
import { environment } from '../../environments/environment';
import { AuditorChatReply, AuditorChatRequest, chatErrorMessage } from '../models/auditor.models';

/** Um ativo colateral no raio de explosão (espelha `BlastRadiusNodeDto` do backend). */
export interface BlastRadiusNode {
  impactedAssetId: string;
  distance: number;
  propagatedImpact: number;
  pathStrength: 'Hard' | 'Soft' | 'Redundant' | string;
}

/** Resposta do endpoint de raio de explosão (`POST /risk-assessment/{assetId}/blast-radius`). */
export interface BlastRadiusResponse {
  assessmentId: string;
  rootAssetId: string;
  blastRadiusScore: number;
  riskLevel: 'Baixo' | 'Medio' | 'Alto' | 'Critico' | string;
  impactedAssetCount: number;
  impactedProcessCount: number;
  maxDepth: number;
  computedAt: string;
  impactedNodes: BlastRadiusNode[];
}

/** Erro do chat com o status preservado (404 conversa/seleção, 409 concorrência, 400 validação, 503 IA). */
export class AuditorApiError extends Error {
  constructor(message: string, readonly status: number) {
    super(message);
  }
}

/**
 * [AEGIS-AUDITOR-CONTEXT-01] Cliente do Auditor Virtual (POST /api/v1/auditor/chat). Envia a pergunta, o FOCO da tela (página e seleções)
 * e a conversa em curso; o tenant e a conta vêm do token (interceptors). O histórico NÃO é enviado: o do servidor é o autoritativo.
 */
@Injectable({ providedIn: 'root' })
export class AuditorService {
  private readonly http = inject(HttpClient);
  private readonly url = `${environment.apiBase}/api/v1/auditor/chat`;

  chat(request: AuditorChatRequest): Observable<AuditorChatReply> {
    return this.http.post<AuditorChatReply>(this.url, request).pipe(
      catchError((err) => {
        const title = typeof err?.error?.title === 'string' ? err.error.title.trim() : null;
        return throwError(() => new AuditorApiError(chatErrorMessage(err?.status, title), err?.status ?? 0));
      }),
    );
  }

  /**
   * Calcula o RAIO DE EXPLOSÃO de um ativo do tenant (`POST /risk-assessment/{assetId}/blast-radius`). Só com o identificador do ativo
   * informado pela pessoa — nunca um ativo de demonstração no lugar do ativo do tenant.
   */
  assessBlastRadius(assetId: string): Observable<BlastRadiusResponse> {
    const url = `${environment.apiBase}/api/v1/risk-assessment/${assetId}/blast-radius`;
    return this.http.post<BlastRadiusResponse>(url, {}).pipe(
      catchError(() => throwError(() => new Error('Não foi possível calcular o raio de impacto desse ativo neste ambiente.'))),
    );
  }
}

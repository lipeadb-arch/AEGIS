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

/**
 * Intenção roteada pelo backend (Agentic Routing) — espelha `AuditorIntents.ToWire` no .NET.
 * `COPILOT`: dúvida geral respondida na hora. `START_INTERVIEW`: pedido de auditoria — o `reply` JÁ É a
 * 1ª pergunta do fluxo NIST e o `metadata` semeia a entrevista com a subcategoria investigada.
 */
export type AuditorIntent = 'COPILOT' | 'START_INTERVIEW';

/**
 * Carga estruturada (o `Metadata`) da resposta em START_INTERVIEW: a subcategoria NIST que a entrevista
 * deve investigar. Espelha o record `AuditorInterviewSeed` do backend (serializado em camelCase). Em
 * COPILOT o backend devolve `metadata: null`.
 */
export interface AuditorInterviewSeed {
  targetSubcategoryCode: string | null;
}

/**
 * Resposta do Copiloto GRC com ROTEAMENTO DE INTENÇÃO. `reply` é a fala/pergunta; `scope`, o escopo que a
 * produziu; `intent` diz à UI COMO reagir; `metadata` traz a semente da entrevista (só em START_INTERVIEW).
 */
export interface AuditorChatReply {
  reply: string;
  scope: string;
  intent: AuditorIntent;
  metadata: AuditorInterviewSeed | null;
}

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

  /**
   * Calcula o RAIO DE EXPLOSÃO de um ativo (`POST /risk-assessment/{assetId}/blast-radius`). Corpo opcional
   * com um cenário de ameaça (`scenarioThreatId`). X-Tenant + Bearer injetados pelo interceptor.
   */
  assessBlastRadius(assetId: string, scenarioThreatId?: string): Observable<BlastRadiusResponse> {
    const url = `${environment.apiBase}/api/v1/risk-assessment/${assetId}/blast-radius`;
    return this.http
      .post<BlastRadiusResponse>(url, scenarioThreatId ? { scenarioThreatId } : {})
      .pipe(
        catchError((err) => {
          console.error('Raio de Explosão: falha no /risk-assessment.', err);
          return throwError(() => new Error('Não foi possível calcular o raio de explosão desse ativo.'));
        }),
      );
  }
}

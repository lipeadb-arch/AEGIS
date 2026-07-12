import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import {
  AiAnalysisStatus,
  ConfirmMappingRequest,
  ConnectDocumentRequest,
  DocumentAccepted,
  Gap,
  GovernCoverage,
  GovernanceDocument,
  GovernanceDocumentType,
  IdentifiedRisk,
  InterviewSession,
  InterviewTurn,
  PolicySyncAccepted,
  PostAnswerRequest,
  StartInterviewRequest,
} from '../models/governance.models';

/**
 * GOVERN — cliente HTTP do pilar de Governança. Cobre as três faces do módulo:
 *   • Document Hub      → /api/v1/governance/documents   (ingestão + leitura da IA)
 *   • Cobertura híbrida → /api/v1/governance/coverage     (mapa de gaps GV)
 *   • Auditor Virtual   → /api/v1/governance/interviews   (chatbot GRC)
 *
 * Isolamento por tenant via header X-Tenant (mesmo padrão do AssetService): o backend
 * carimba/filtra por tenant no ambiente — nenhum id de tenant trafega no corpo.
 */
@Injectable({ providedIn: 'root' })
export class GovernanceService {
  private readonly http = inject(HttpClient);

  private readonly documents = `${environment.apiBase}/api/v1/governance/documents`;
  private readonly interviews = `${environment.apiBase}/api/v1/governance/interviews`;
  private readonly coverageUrl = `${environment.apiBase}/api/v1/governance/coverage`;

  // ---- Document Hub -------------------------------------------------------

  /** POST (multipart) — grava o binário + hash SHA-256 e enfileira a leitura da IA. */
  uploadDocument(file: File, title: string, type: GovernanceDocumentType): Observable<DocumentAccepted> {
    const form = new FormData();
    form.append('file', file);
    form.append('title', title);
    form.append('type', type);
    // Sem Content-Type manual: o browser define o boundary do multipart/form-data.
    return this.http.post<DocumentAccepted>(this.documents, form, { headers: this.headers() });
  }

  /** POST /connect — registra um documento vindo de integração (SharePoint/Confluence). */
  connectDocument(req: ConnectDocumentRequest): Observable<{ id: string }> {
    return this.http.post<{ id: string }>(`${this.documents}/connect`, req, { headers: this.headers() });
  }

  /**
   * POST /documents/sync — gatilho MANUAL de sincronização das políticas corporativas: enfileira o tenant
   * para o PolicyIngestionWorker puxar as fontes externas (SharePoint/Google…) via Provider Pattern.
   * Retorna 202 (agendado); a ingestão roda em background — os documentos aparecem na lista em instantes.
   */
  syncPolicies(): Observable<PolicySyncAccepted> {
    return this.http.post<PolicySyncAccepted>(`${this.documents}/sync`, null, { headers: this.headers() });
  }

  /** GET — lista os documentos do tenant, com filtros opcionais por tipo e status de leitura. */
  listDocuments(filter?: {
    type?: GovernanceDocumentType;
    analysisStatus?: AiAnalysisStatus;
  }): Observable<GovernanceDocument[]> {
    let params = new HttpParams();
    if (filter?.type) params = params.set('type', filter.type);
    if (filter?.analysisStatus) params = params.set('analysisStatus', filter.analysisStatus);
    return this.http.get<GovernanceDocument[]>(this.documents, { params, headers: this.headers() });
  }

  /** GET /{id} — um documento com seus mapeamentos NIST. */
  getDocument(id: string): Observable<GovernanceDocument> {
    return this.http.get<GovernanceDocument>(`${this.documents}/${id}`, { headers: this.headers() });
  }

  /** POST /{id}/reanalyze — re-enfileira a leitura da IA (reprocessa o binário). */
  reanalyzeDocument(id: string): Observable<DocumentAccepted> {
    return this.http.post<DocumentAccepted>(`${this.documents}/${id}/reanalyze`, null, {
      headers: this.headers(),
    });
  }

  /** PUT /{id}/mappings/{code} — human-in-the-loop: confirma/ajusta um mapeamento da IA. */
  confirmMapping(id: string, code: string, req: ConfirmMappingRequest): Observable<void> {
    return this.http.put<void>(
      `${this.documents}/${id}/mappings/${encodeURIComponent(code)}`,
      req,
      { headers: this.headers() },
    );
  }

  /** DELETE /{id} — remove o documento, seus mapeamentos e o binário armazenado. */
  deleteDocument(id: string): Observable<void> {
    return this.http.delete<void>(`${this.documents}/${id}`, { headers: this.headers() });
  }

  // ---- Cobertura híbrida --------------------------------------------------

  /** GET /coverage — mapa de cobertura do pilar GOVERN (documentos + entrevistas). */
  getCoverage(): Observable<GovernCoverage> {
    return this.http.get<GovernCoverage>(this.coverageUrl, { headers: this.headers() });
  }

  // ---- Auditor Virtual (GRC) ---------------------------------------------

  /** GET /interviews/gaps — subcategorias GV ainda não cobertas (semeia o diagnóstico). */
  getGaps(): Observable<Gap[]> {
    return this.http.get<Gap[]>(`${this.interviews}/gaps`, { headers: this.headers() });
  }

  /** POST /interviews — abre uma sessão e devolve a primeira pergunta investigativa da IA. */
  startInterview(req: StartInterviewRequest): Observable<InterviewTurn> {
    return this.http.post<InterviewTurn>(this.interviews, req, { headers: this.headers() });
  }

  /** GET /interviews/{id} — sessão + histórico de mensagens (replay do drawer de chat). */
  getInterview(id: string): Observable<InterviewSession> {
    return this.http.get<InterviewSession>(`${this.interviews}/${id}`, { headers: this.headers() });
  }

  /** POST /interviews/{id}/messages — registra a resposta e devolve a próxima pergunta. */
  answerInterview(id: string, req: PostAnswerRequest): Observable<InterviewTurn> {
    return this.http.post<InterviewTurn>(`${this.interviews}/${id}/messages`, req, {
      headers: this.headers(),
    });
  }

  /** POST /interviews/{id}/complete — finaliza a sessão manualmente. */
  completeInterview(id: string): Observable<void> {
    return this.http.post<void>(`${this.interviews}/${id}/complete`, null, { headers: this.headers() });
  }

  /** GET /interviews/{id}/outcomes — riscos identificados pela sessão (trilha de auditoria). */
  getOutcomes(id: string): Observable<IdentifiedRisk[]> {
    return this.http.get<IdentifiedRisk[]>(`${this.interviews}/${id}/outcomes`, {
      headers: this.headers(),
    });
  }

  /** Header comum: escopo de tenant (X-Tenant) — mesmo contrato do AssetService. */
  private headers(): Record<string, string> {
    return { 'X-Tenant': environment.tenantId, Accept: 'application/json' };
  }
}

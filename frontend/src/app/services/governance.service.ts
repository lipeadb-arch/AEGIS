import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import {
  AiAnalysisStatus,
  ConfirmMappingRequest,
  ConnectDocumentRequest,
  DocumentAccepted,
  DocumentIntegrationAvailability,
  GovernCoverage,
  GovernanceDocument,
  GovernanceDocumentType,
  PolicySyncAccepted,
} from '../models/governance.models';

/**
 * GOVERN — cliente HTTP da biblioteca de documentos:
 *   • Document Hub      → /api/v1/governance/documents   (ingestão + leitura da IA)
 *   • Cobertura híbrida → /api/v1/governance/coverage     (mapa de gaps GV)
 * [AEGIS-AUDITOR-CONTEXT-01] A entrevista da abordagem anterior saiu da interface: o Auditor Virtual conversa em /api/v1/auditor/chat.
 *
 * Isolamento por tenant via header X-Tenant (mesmo padrão do AssetService): o backend
 * carimba/filtra por tenant no ambiente — nenhum id de tenant trafega no corpo.
 */
@Injectable({ providedIn: 'root' })
export class GovernanceService {
  private readonly http = inject(HttpClient);

  private readonly documents = `${environment.apiBase}/api/v1/governance/documents`;
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
   * [AEGIS-MVP-PRODUCT-01] GET /documents/integration — o que a ingestão por integração REALMENTE consegue
   * fazer neste ambiente. A tela anunciava "puxe as políticas das fontes corporativas conectadas" tendo por
   * trás apenas um provedor SIMULADO; com esta leitura ela diz a verdade, e o upload manual (real) segue
   * como o caminho de governança.
   */
  documentIntegration(): Observable<DocumentIntegrationAvailability> {
    return this.http.get<DocumentIntegrationAvailability>(
      `${this.documents}/integration`,
      { headers: this.headers() },
    );
  }

  /**
   * POST /documents/sync — gatilho MANUAL de sincronização das políticas corporativas: enfileira o tenant
   * para o PolicyIngestionWorker puxar as fontes externas (SharePoint/Google…) via Provider Pattern.
   * Retorna 202 (agendado); a ingestão roda em background — os documentos aparecem na lista em instantes.
   * Responde 409 quando NENHUMA fonte documental está disponível (nada seria sincronizado).
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

  /** Header comum: escopo de tenant (X-Tenant) — mesmo contrato do AssetService. */
  private headers(): Record<string, string> {
    return { Accept: 'application/json' };
  }
}

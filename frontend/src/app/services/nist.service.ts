import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, throwError, timeout } from 'rxjs';
import { environment } from '../../environments/environment';
import {
  CreateNistAssessmentRequest,
  CreateNistCycleRequest,
  CreateNistFindingRequest,
  LinkNistEvidenceRequest,
  NistAssessment,
  NistAssignee,
  NistAuditEntry,
  NistCycle,
  NistCycleComparison,
  NistEvidenceOverview,
  NistFinding,
  NistFunctionView,
  NistHistoryItem,
  NistImportPreview,
  NistImportResult,
  NistPlanRequest,
  NistProcedure,
  NistProfile,
  NistPublication,
  NistPublicationPreview,
  NistScope,
  NistSubcategoryDetail,
  NistTestMethod,
  SaveNistEvaluationRequest,
  UpdateNistFindingRequest,
  UpdateNistPlanRequest,
  UpdateNistProcedureRequest,
} from '../models/nist.models';
import {
  NistAssistAvailability,
  NistAssistContext,
  NistAssistFocus,
  NistAssistView,
  NistExecutiveSummary,
} from '../models/nist-assist.models';
import { HistoryWindowRequest, parseContentDispositionFilename } from '../models/posture-history.models';

/**
 * Erro da API do NIST com o status preservado (409 = versão desatualizada; 403 = papel; 503 = IA indisponível). [AEGIS-NIST-
 * AI-ASSIST-01] `reason` traz o motivo dito pelo servidor: AssistanceStale (409 — sugestão de contexto anterior), Disabled,
 * Timeout, InvalidResponse ou Unavailable (503).
 */
export class NistApiError extends Error {
  constructor(message: string, readonly status: number, readonly reason: string | null = null) {
    super(message);
  }
}

/** Contexto explícito de trabalho: avaliação → rodada → escopo. Toda leitura e escrita de rodada o carrega inteiro. */
export interface NistCtx {
  assessmentId: string;
  cycleId: string;
  scopeId: string;
}

/** Arquivo baixado como Blob (nunca como string). */
export interface NistFile {
  blob: Blob;
  filename: string;
}

/**
 * [AEGIS-NIST-JOURNEY-01/02] Cliente da jornada do AEGIS NIST (/api/v1/nist/assessments). Bearer e X-Tenant vêm dos
 * interceptors. A mensagem do servidor (pt-BR, sem dado sensível) é repassada ao componente; o status fica no erro para
 * a tela distinguir conflito de versão, papel insuficiente e IA indisponível. A RODADA está em toda rota de trabalho:
 * nenhuma leitura "adivinha" a rodada.
 */
@Injectable({ providedIn: 'root' })
export class NistService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiBase}/api/v1/nist/assessments`;
  private readonly TIMEOUT_MS = 20_000;
  private readonly AI_TIMEOUT_MS = 60_000;

  list(): Observable<NistAssessment[]> {
    return this.http.get<NistAssessment[]>(this.base).pipe(this.handle('Não foi possível carregar as avaliações.'));
  }

  history(): Observable<NistHistoryItem[]> {
    return this.http.get<NistHistoryItem[]>(`${this.base}/history`).pipe(this.handle('Não foi possível carregar o histórico NIST.'));
  }

  assignees(): Observable<NistAssignee[]> {
    return this.http.get<NistAssignee[]>(`${this.base}/assignees`).pipe(this.handle('Não foi possível carregar os usuários do tenant.'));
  }

  create(request: CreateNistAssessmentRequest): Observable<NistAssessment> {
    return this.http.post<NistAssessment>(this.base, request).pipe(this.handle('Não foi possível criar a avaliação.'));
  }

  addScope(assessmentId: string, name: string, description: string | null): Observable<NistScope> {
    return this.http
      .post<NistScope>(`${this.base}/${assessmentId}/scopes`, { name, description })
      .pipe(this.handle('Não foi possível criar o escopo.'));
  }

  createCycle(assessmentId: string, request: CreateNistCycleRequest): Observable<NistCycle> {
    return this.http
      .post<NistCycle>(`${this.base}/${assessmentId}/cycles`, request)
      .pipe(this.handle('Não foi possível criar a rodada.'));
  }

  setCycleStatus(assessmentId: string, cycleId: string, status: 'Open' | 'Closed', expectedVersion: number): Observable<NistCycle> {
    return this.http
      .put<NistCycle>(`${this.base}/${assessmentId}/cycles/${cycleId}/status`, { status, expectedVersion })
      .pipe(this.handle('Não foi possível alterar a situação da rodada.'));
  }

  audit(assessmentId: string, filter: { cycleId?: string | null; scopeId?: string | null; code?: string | null } = {}): Observable<NistAuditEntry[]> {
    return this.http
      .get<NistAuditEntry[]>(`${this.base}/${assessmentId}/audit`, { params: this.params(filter) })
      .pipe(this.handle('Não foi possível carregar a trilha de alterações.'));
  }

  findings(
    assessmentId: string,
    filter: { cycleId?: string | null; scopeId?: string | null; code?: string | null; status?: string | null } = {},
  ): Observable<NistFinding[]> {
    return this.http
      .get<NistFinding[]>(`${this.base}/${assessmentId}/findings`, { params: this.params(filter) })
      .pipe(this.handle('Não foi possível carregar os achados.'));
  }

  publications(assessmentId: string, filter: { cycleId?: string | null; scopeId?: string | null } = {}): Observable<NistPublication[]> {
    return this.http
      .get<NistPublication[]>(`${this.base}/${assessmentId}/publications`, { params: this.params(filter) })
      .pipe(this.handle('Não foi possível carregar as publicações.'));
  }

  compare(assessmentId: string, scopeId: string, baseCycleId: string, targetCycleId: string): Observable<NistCycleComparison> {
    const params = new HttpParams().set('baseCycleId', baseCycleId).set('targetCycleId', targetCycleId);
    return this.http
      .get<NistCycleComparison>(`${this.base}/${assessmentId}/scopes/${scopeId}/compare`, { params })
      .pipe(this.handle('Não foi possível comparar as rodadas.'));
  }

  // ---- Rodada e escopo ----------------------------------------------------------------------------------------

  profile(c: NistCtx): Observable<NistProfile> {
    return this.http.get<NistProfile>(`${this.ctxUrl(c)}/profile`).pipe(this.handle('Não foi possível carregar o resumo da avaliação.'));
  }

  functionView(c: NistCtx, fn: string): Observable<NistFunctionView> {
    return this.http
      .get<NistFunctionView>(`${this.ctxUrl(c)}/functions/${encodeURIComponent(fn)}`)
      .pipe(this.handle('Não foi possível carregar a função.'));
  }

  /** [AEGIS-ASSESSMENT-VISUALS-01] `window` escolhe o período do histórico mostrado e congelado (padrão: 12 meses até agora). */
  publicationPreview(c: NistCtx, window: HistoryWindowRequest | null = null): Observable<NistPublicationPreview> {
    let params = new HttpParams();
    if (window?.until) params = params.set('historyUntil', window.until);
    if (window?.months) params = params.set('historyMonths', window.months);
    return this.http
      .get<NistPublicationPreview>(`${this.ctxUrl(c)}/publication-preview`, { params })
      .pipe(this.handle('Não foi possível montar a prévia da publicação.'));
  }

  /** Publica conferindo as DUAS impressões digitais da prévia: a do conteúdo avaliativo e a do histórico. */
  publish(c: NistCtx, expectedFingerprint: string, expectedHistoryFingerprint: string | null = null,
          window: HistoryWindowRequest | null = null): Observable<NistPublication> {
    return this.http
      .post<NistPublication>(`${this.ctxUrl(c)}/publications`, {
        expectedFingerprint,
        expectedHistoryFingerprint,
        historyUntil: window?.until ?? null,
        historyMonths: window?.months ?? null,
      })
      .pipe(this.handle('Não foi possível publicar a fotografia.'));
  }

  workingCsv(c: NistCtx): Observable<NistFile> {
    return this.http.get(`${this.ctxUrl(c)}/working-csv`, { responseType: 'blob', observe: 'response' }).pipe(
      timeout(this.TIMEOUT_MS),
      map((res) => ({
        blob: res.body ?? new Blob(),
        filename: parseContentDispositionFilename(res.headers.get('Content-Disposition')) ?? 'nist-trabalho.csv',
      })),
      catchError(() => throwError(() => new NistApiError('Não foi possível baixar o CSV de trabalho.', 0))),
    );
  }

  importPreview(c: NistCtx, csv: string, fileName: string | null): Observable<NistImportPreview> {
    return this.http
      .post<NistImportPreview>(`${this.ctxUrl(c)}/import/preview`, { csv, fileName })
      .pipe(this.handle('Não foi possível validar o arquivo.'));
  }

  importApply(c: NistCtx, csv: string, fileName: string | null, token: string): Observable<NistImportResult> {
    return this.http
      .post<NistImportResult>(`${this.ctxUrl(c)}/import/apply`, { csv, fileName, token })
      .pipe(this.handle('Não foi possível aplicar a importação.'));
  }

  // ---- Subcategoria -------------------------------------------------------------------------------------------

  subcategory(c: NistCtx, code: string): Observable<NistSubcategoryDetail> {
    return this.http.get<NistSubcategoryDetail>(this.subUrl(c, code)).pipe(this.handle('Não foi possível carregar a subcategoria.'));
  }

  save(c: NistCtx, code: string, request: SaveNistEvaluationRequest): Observable<NistSubcategoryDetail> {
    return this.http.put<NistSubcategoryDetail>(this.subUrl(c, code), request).pipe(this.handle('Não foi possível gravar a avaliação.'));
  }

  assign(c: NistCtx, code: string, assessorUserId: string | null, reviewerUserId: string | null, expectedVersion: number): Observable<NistSubcategoryDetail> {
    return this.http
      .put<NistSubcategoryDetail>(`${this.subUrl(c, code)}/assignment`, { assessorUserId, reviewerUserId, expectedVersion })
      .pipe(this.handle('Não foi possível designar avaliador e revisor.'));
  }

  review(c: NistCtx, code: string, decision: 'Approved' | 'ChangesRequested', note: string | null, expectedVersion: number): Observable<NistSubcategoryDetail> {
    return this.http
      .post<NistSubcategoryDetail>(`${this.subUrl(c, code)}/review`, { decision, note, expectedVersion })
      .pipe(this.handle('Não foi possível registrar a decisão do revisor.'));
  }

  /**
   * [AEGIS-AUDITOR-CONTEXT-01] Evidências da rodada e escopo, calculadas pelos registros: correlação KNIGHT × NIST, lacunas de evidência,
   * tratamentos ligados, documentos e retratos do inventário vinculados. Leitura — vincular continua sendo `linkEvidence`.
   */
  evidenceOverview(c: NistCtx): Observable<NistEvidenceOverview> {
    return this.http
      .get<NistEvidenceOverview>(`${this.ctxUrl(c)}/evidence-overview`)
      .pipe(this.handle('Não foi possível carregar as evidências da rodada.'));
  }

  linkEvidence(c: NistCtx, code: string, request: LinkNistEvidenceRequest): Observable<NistSubcategoryDetail> {
    return this.http
      .post<NistSubcategoryDetail>(`${this.subUrl(c, code)}/evidence`, request)
      .pipe(this.handle('Não foi possível vincular a evidência.'));
  }

  removeEvidence(c: NistCtx, code: string, evidenceId: string): Observable<NistSubcategoryDetail> {
    return this.http
      .delete<NistSubcategoryDetail>(`${this.subUrl(c, code)}/evidence/${evidenceId}`)
      .pipe(this.handle('Não foi possível retirar a evidência.'));
  }

  // ---- [AEGIS-NIST-AI-ASSIST-01] Assistência de IA ------------------------------------------------------------

  assistAvailability(): Observable<NistAssistAvailability> {
    return this.http.get<NistAssistAvailability>(`${this.base}/assist/availability`).pipe(this.handle('Não foi possível ler o estado da IA.'));
  }

  /** Contexto que a assistência usaria AGORA (fontes, impressão digital, última sugestão) — sem chamar a IA. */
  subcategoryAssistContext(c: NistCtx, code: string): Observable<NistAssistContext> {
    return this.http
      .get<NistAssistContext>(`${this.subUrl(c, code)}/assist/context`)
      .pipe(this.handle('Não foi possível conferir as fontes da assistência.'));
  }

  assistSubcategory(c: NistCtx, code: string, reuse = true): Observable<NistAssistView> {
    return this.http
      .post<NistAssistView>(`${this.subUrl(c, code)}/assist`, { reuse })
      .pipe(this.handle('A IA não respondeu agora. A avaliação segue normalmente sem ela.', this.AI_TIMEOUT_MS));
  }

  findingAssistContext(c: NistCtx, findingId: string, focus: NistAssistFocus): Observable<NistAssistContext> {
    return this.http
      .get<NistAssistContext>(`${this.findingUrl(c, findingId)}/assist/context`, { params: new HttpParams().set('focus', focus) })
      .pipe(this.handle('Não foi possível conferir as fontes da assistência.'));
  }

  assistFinding(c: NistCtx, findingId: string, focus: NistAssistFocus, reuse = true): Observable<NistAssistView> {
    return this.http
      .post<NistAssistView>(`${this.findingUrl(c, findingId)}/assist`, { reuse, focus })
      .pipe(this.handle('A IA não respondeu agora. O achado segue normalmente sem ela.', this.AI_TIMEOUT_MS));
  }

  executiveAssistContext(c: NistCtx): Observable<NistAssistContext> {
    return this.http
      .get<NistAssistContext>(`${this.ctxUrl(c)}/executive-summary/assist/context`)
      .pipe(this.handle('Não foi possível conferir as fontes da assistência.'));
  }

  assistExecutive(c: NistCtx, reuse = true): Observable<NistAssistView> {
    return this.http
      .post<NistAssistView>(`${this.ctxUrl(c)}/executive-summary/assist`, { reuse })
      .pipe(this.handle('A IA não respondeu agora. A publicação segue normalmente sem interpretação.', this.AI_TIMEOUT_MS));
  }

  /** Resumo executivo aceito (nulo quando não há). */
  executiveSummary(c: NistCtx): Observable<NistExecutiveSummary | null> {
    return this.http
      .get<NistExecutiveSummary | null>(`${this.ctxUrl(c)}/executive-summary`)
      .pipe(map((v) => v ?? null), this.handle('Não foi possível carregar o resumo executivo.'));
  }

  saveExecutiveSummary(
    c: NistCtx,
    request: { sections: { key: string; text: string }[]; assistanceId: string | null; expectedVersion: number; acknowledgeStale: boolean },
  ): Observable<NistExecutiveSummary> {
    return this.http
      .put<NistExecutiveSummary>(`${this.ctxUrl(c)}/executive-summary`, request)
      .pipe(this.handle('Não foi possível gravar o resumo executivo.'));
  }

  reviewExecutiveSummary(c: NistCtx, expectedVersion: number, note: string | null): Observable<NistExecutiveSummary> {
    return this.http
      .post<NistExecutiveSummary>(`${this.ctxUrl(c)}/executive-summary/review`, { expectedVersion, note })
      .pipe(this.handle('Não foi possível registrar a revisão do resumo.'));
  }

  withdrawExecutiveSummary(c: NistCtx, expectedVersion: number): Observable<void> {
    return this.http
      .delete<void>(`${this.ctxUrl(c)}/executive-summary`, { params: new HttpParams().set('expectedVersion', expectedVersion) })
      .pipe(this.handle('Não foi possível retirar o resumo executivo.'));
  }

  /** Planeja, de uma vez, procedimentos sugeridos escolhidos pela pessoa (planejar não é realizar). */
  planProceduresFromAssistance(
    c: NistCtx,
    code: string,
    request: { assistanceId: string; procedures: { method: string; procedure: string }[]; acknowledgeStale: boolean },
  ): Observable<NistProcedure[]> {
    return this.http
      .post<NistProcedure[]>(`${this.subUrl(c, code)}/procedures/from-assistance`, request)
      .pipe(this.handle('Não foi possível planejar os procedimentos sugeridos.'));
  }

  addProcedure(c: NistCtx, code: string, method: NistTestMethod, procedure: string): Observable<NistProcedure> {
    return this.http
      .post<NistProcedure>(`${this.subUrl(c, code)}/procedures`, { method, procedure })
      .pipe(this.handle('Não foi possível registrar o procedimento.'));
  }

  updateProcedure(c: NistCtx, code: string, procedureId: string, request: UpdateNistProcedureRequest): Observable<NistProcedure> {
    return this.http
      .put<NistProcedure>(`${this.subUrl(c, code)}/procedures/${procedureId}`, request)
      .pipe(this.handle('Não foi possível gravar o resultado do procedimento.'));
  }

  removeProcedure(c: NistCtx, code: string, procedureId: string, expectedVersion: number): Observable<void> {
    const params = new HttpParams().set('expectedVersion', expectedVersion);
    return this.http
      .delete<void>(`${this.subUrl(c, code)}/procedures/${procedureId}`, { params })
      .pipe(this.handle('Não foi possível retirar o procedimento.'));
  }

  createFinding(c: NistCtx, code: string, request: CreateNistFindingRequest): Observable<NistFinding> {
    return this.http
      .post<NistFinding>(`${this.subUrl(c, code)}/findings`, request)
      .pipe(this.handle('Não foi possível registrar o achado.'));
  }

  finding(c: NistCtx, findingId: string): Observable<NistFinding> {
    return this.http.get<NistFinding>(this.findingUrl(c, findingId)).pipe(this.handle('Não foi possível carregar o achado.'));
  }

  updateFinding(c: NistCtx, findingId: string, request: UpdateNistFindingRequest): Observable<NistFinding> {
    return this.http.put<NistFinding>(this.findingUrl(c, findingId), request).pipe(this.handle('Não foi possível atualizar o achado.'));
  }

  setFindingStatus(c: NistCtx, findingId: string, status: string, note: string | null, expectedVersion: number): Observable<NistFinding> {
    return this.http
      .put<NistFinding>(`${this.findingUrl(c, findingId)}/status`, { status, note, expectedVersion })
      .pipe(this.handle('Não foi possível alterar a situação do achado.'));
  }

  createPlan(c: NistCtx, findingId: string, request: NistPlanRequest): Observable<NistFinding> {
    return this.http
      .post<NistFinding>(`${this.findingUrl(c, findingId)}/plans`, request)
      .pipe(this.handle('Não foi possível criar o plano de tratamento.'));
  }

  updatePlan(c: NistCtx, findingId: string, planId: string, request: UpdateNistPlanRequest): Observable<NistFinding> {
    return this.http
      .put<NistFinding>(`${this.findingUrl(c, findingId)}/plans/${planId}`, request)
      .pipe(this.handle('Não foi possível atualizar o plano.'));
  }

  recordExecution(c: NistCtx, findingId: string, planId: string, expectedVersion: number, notes: string, evidenceReference: string | null): Observable<NistFinding> {
    return this.http
      .post<NistFinding>(`${this.findingUrl(c, findingId)}/plans/${planId}/execution`, { expectedVersion, notes, evidenceReference })
      .pipe(this.handle('Não foi possível registrar a execução.'));
  }

  validatePlan(c: NistCtx, findingId: string, planId: string, expectedVersion: number, evidenceReference: string, note: string | null): Observable<NistFinding> {
    return this.http
      .post<NistFinding>(`${this.findingUrl(c, findingId)}/plans/${planId}/validations`, { expectedVersion, evidenceReference, note })
      .pipe(this.handle('Não foi possível registrar a validação.'));
  }

  private ctxUrl(c: NistCtx): string {
    return `${this.base}/${c.assessmentId}/cycles/${c.cycleId}/scopes/${c.scopeId}`;
  }

  private subUrl(c: NistCtx, code: string): string {
    return `${this.ctxUrl(c)}/subcategories/${encodeURIComponent(code)}`;
  }

  private findingUrl(c: NistCtx, findingId: string): string {
    return `${this.ctxUrl(c)}/findings/${findingId}`;
  }

  private params(filter: Record<string, string | null | undefined>): HttpParams {
    let p = new HttpParams();
    for (const [k, v] of Object.entries(filter)) if (v) p = p.set(k, v);
    return p;
  }

  private handle<T>(fallback: string, ms = this.TIMEOUT_MS) {
    return (source: Observable<T>) =>
      source.pipe(
        timeout(ms),
        catchError((err: unknown) => {
          if (err instanceof HttpErrorResponse) {
            const body = err.error as { message?: string; reason?: string } | string | null;
            const fromServer = typeof body === 'string' ? body : body?.message;
            const reason = typeof body === 'object' && body ? body.reason ?? null : null;
            if (err.status === 403)
              return throwError(() => new NistApiError('Seu papel permite consultar, mas não registrar (requer Manager ou TenantAdmin).', 403));
            if (err.status === 429)
              return throwError(() => new NistApiError('Limite de pedidos à IA por minuto atingido. Aguarde um pouco e tente de novo.', 429, 'RateLimited'));
            if ([400, 404, 409, 503].includes(err.status) && fromServer)
              return throwError(() => new NistApiError(fromServer, err.status, reason));
            return throwError(() => new NistApiError(fallback, err.status, reason));
          }
          return throwError(() => new NistApiError(fallback, 0));
        }),
      );
  }
}

/** Contexto de trabalho a partir da seleção (nulo enquanto faltar avaliação, rodada ou escopo). */
export function ctxOf(
  s: { assessment: { id: string }; cycle: { id: string } | null; scope: { id: string } | null } | null,
): NistCtx | null {
  return s && s.cycle && s.scope ? { assessmentId: s.assessment.id, cycleId: s.cycle.id, scopeId: s.scope.id } : null;
}

/** Dispara o download de um Blob pelo navegador (object URL sempre revogado depois). */
export function saveBlob(blob: Blob, filename: string): void {
  const url = URL.createObjectURL(blob);
  try {
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    a.remove();
  } finally {
    setTimeout(() => URL.revokeObjectURL(url), 1500);
  }
}

/**
 * [AEGIS-NIST-JOURNEY-01/02] Avaliação, rodada e escopo escolhidos, por tenant — conveniência local (o menu leva às
 * funções sem parâmetros). A fonte de verdade é a URL (?avaliacao=&rodada=&escopo=); isto só lembra a última escolha.
 * Leitura e escrita protegidas: sem armazenamento disponível, a tela escolhe a avaliação e a rodada mais recentes.
 */
@Injectable({ providedIn: 'root' })
export class NistSelectionService {
  private key(tenantId: string | null): string {
    return `aegis.nist.selection.${tenantId ?? 'none'}`;
  }

  read(tenantId: string | null): { assessmentId: string; cycleId: string | null; scopeId: string } | null {
    try {
      const raw = localStorage.getItem(this.key(tenantId));
      if (!raw) return null;
      const v = JSON.parse(raw) as { assessmentId?: string; cycleId?: string | null; scopeId?: string };
      return v.assessmentId && v.scopeId ? { assessmentId: v.assessmentId, cycleId: v.cycleId ?? null, scopeId: v.scopeId } : null;
    } catch {
      return null;
    }
  }

  write(tenantId: string | null, assessmentId: string, scopeId: string, cycleId: string | null = null): void {
    try {
      localStorage.setItem(this.key(tenantId), JSON.stringify({ assessmentId, cycleId, scopeId }));
    } catch {
      /* armazenamento indisponível: a URL continua sendo a fonte de verdade */
    }
  }
}

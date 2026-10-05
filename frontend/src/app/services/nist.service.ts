import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, throwError, timeout } from 'rxjs';
import { environment } from '../../environments/environment';
import {
  CreateNistAssessmentRequest,
  LinkNistEvidenceRequest,
  NistAiSuggestion,
  NistAssessment,
  NistFunctionView,
  NistHistoryItem,
  NistProfile,
  NistScope,
  NistSubcategoryDetail,
  SaveNistEvaluationRequest,
} from '../models/nist.models';

/** Erro da API do NIST com o status preservado (409 = versão desatualizada; 403 = papel; 503 = IA indisponível). */
export class NistApiError extends Error {
  constructor(message: string, readonly status: number) {
    super(message);
  }
}

/**
 * [AEGIS-NIST-JOURNEY-01] Cliente da jornada do AEGIS NIST (/api/v1/nist/assessments). Bearer e X-Tenant vêm dos
 * interceptors. A mensagem do servidor (pt-BR, sem dado sensível) é repassada ao componente; o status fica no erro para
 * a tela distinguir conflito de versão, papel insuficiente e IA indisponível.
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

  create(request: CreateNistAssessmentRequest): Observable<NistAssessment> {
    return this.http.post<NistAssessment>(this.base, request).pipe(this.handle('Não foi possível criar a avaliação.'));
  }

  addScope(assessmentId: string, name: string, description: string | null): Observable<NistScope> {
    return this.http
      .post<NistScope>(`${this.base}/${assessmentId}/scopes`, { name, description })
      .pipe(this.handle('Não foi possível criar o escopo.'));
  }

  profile(assessmentId: string, scopeId: string): Observable<NistProfile> {
    return this.http
      .get<NistProfile>(`${this.base}/${assessmentId}/scopes/${scopeId}/profile`)
      .pipe(this.handle('Não foi possível carregar o resumo da avaliação.'));
  }

  functionView(assessmentId: string, scopeId: string, fn: string): Observable<NistFunctionView> {
    return this.http
      .get<NistFunctionView>(`${this.base}/${assessmentId}/scopes/${scopeId}/functions/${encodeURIComponent(fn)}`)
      .pipe(this.handle('Não foi possível carregar a função.'));
  }

  subcategory(assessmentId: string, scopeId: string, code: string): Observable<NistSubcategoryDetail> {
    return this.http
      .get<NistSubcategoryDetail>(this.subUrl(assessmentId, scopeId, code))
      .pipe(this.handle('Não foi possível carregar a subcategoria.'));
  }

  save(assessmentId: string, scopeId: string, code: string, request: SaveNistEvaluationRequest): Observable<NistSubcategoryDetail> {
    return this.http
      .put<NistSubcategoryDetail>(this.subUrl(assessmentId, scopeId, code), request)
      .pipe(this.handle('Não foi possível gravar a avaliação.'));
  }

  linkEvidence(assessmentId: string, scopeId: string, code: string, request: LinkNistEvidenceRequest): Observable<NistSubcategoryDetail> {
    return this.http
      .post<NistSubcategoryDetail>(`${this.subUrl(assessmentId, scopeId, code)}/evidence`, request)
      .pipe(this.handle('Não foi possível vincular a evidência.'));
  }

  removeEvidence(assessmentId: string, scopeId: string, code: string, evidenceId: string): Observable<NistSubcategoryDetail> {
    return this.http
      .delete<NistSubcategoryDetail>(`${this.subUrl(assessmentId, scopeId, code)}/evidence/${evidenceId}`)
      .pipe(this.handle('Não foi possível retirar a evidência.'));
  }

  suggest(assessmentId: string, scopeId: string, code: string): Observable<NistAiSuggestion> {
    return this.http
      .post<NistAiSuggestion>(`${this.subUrl(assessmentId, scopeId, code)}/ai-suggestion`, {})
      .pipe(this.handle('A IA não respondeu agora. A avaliação segue normalmente sem ela.', this.AI_TIMEOUT_MS));
  }

  private subUrl(assessmentId: string, scopeId: string, code: string): string {
    return `${this.base}/${assessmentId}/scopes/${scopeId}/subcategories/${encodeURIComponent(code)}`;
  }

  private handle<T>(fallback: string, ms = this.TIMEOUT_MS) {
    return (source: Observable<T>) =>
      source.pipe(
        timeout(ms),
        catchError((err: unknown) => {
          if (err instanceof HttpErrorResponse) {
            const body = err.error as { message?: string } | string | null;
            const fromServer = typeof body === 'string' ? body : body?.message;
            if (err.status === 403)
              return throwError(() => new NistApiError('Seu papel permite consultar, mas não registrar (requer Manager ou TenantAdmin).', 403));
            if ([400, 404, 409, 503].includes(err.status) && fromServer)
              return throwError(() => new NistApiError(fromServer, err.status));
            return throwError(() => new NistApiError(fallback, err.status));
          }
          return throwError(() => new NistApiError(fallback, 0));
        }),
      );
  }
}

/**
 * [AEGIS-NIST-JOURNEY-01] Avaliação e escopo escolhidos, por tenant — conveniência local (o menu leva às funções sem
 * parâmetros). A fonte de verdade é a URL (?avaliacao=&escopo=); isto só lembra a última escolha. Leitura e escrita
 * protegidas: sem armazenamento disponível, a tela escolhe a avaliação mais recente.
 */
@Injectable({ providedIn: 'root' })
export class NistSelectionService {
  private key(tenantId: string | null): string {
    return `aegis.nist.selection.${tenantId ?? 'none'}`;
  }

  read(tenantId: string | null): { assessmentId: string; scopeId: string } | null {
    try {
      const raw = localStorage.getItem(this.key(tenantId));
      if (!raw) return null;
      const v = JSON.parse(raw) as { assessmentId?: string; scopeId?: string };
      return v.assessmentId && v.scopeId ? { assessmentId: v.assessmentId, scopeId: v.scopeId } : null;
    } catch {
      return null;
    }
  }

  write(tenantId: string | null, assessmentId: string, scopeId: string): void {
    try {
      localStorage.setItem(this.key(tenantId), JSON.stringify({ assessmentId, scopeId }));
    } catch {
      /* armazenamento indisponível: a URL continua sendo a fonte de verdade */
    }
  }
}

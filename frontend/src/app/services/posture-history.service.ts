import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, throwError, timeout } from 'rxjs';
import { environment } from '../../environments/environment';
import { KNIGHT_SOURCES, KnightSourceType } from '../models/knight.models';
import {
  FrozenPostureHistory,
  HistoryWindowRequest,
  KnightConsolidatedSourceSelection,
  PostureComparisonResult,
  PostureExportFormat,
  PostureMonthlyHistory,
  PostureSnapshotDetail,
  PostureSnapshotSummary,
  PostureSnapshotType,
  PublishConsolidatedKnightSnapshotRequest,
  PublishPostureSnapshotRequest,
  fallbackExportFilename,
  parseContentDispositionFilename,
} from '../models/posture-history.models';

/** Mesmos apelidos curtos aceitos pelo servidor — nunca o nome completo do enum (catálogo único de fontes). */
const SOURCE_SLUG: Partial<Record<KnightSourceType, string>> = Object.fromEntries(
  KNIGHT_SOURCES.filter((d) => d.microsoftConnector).map((d) => [d.source, d.slug]),
);

/**
 * [AEGIS-ASSESSMENT-VISUALS-01] 409 porque a série do histórico mudou desde a prévia (outra publicação entrou, o mês virou).
 * Nada foi publicado: a tela relê a prévia e mostra a série nova antes de nova confirmação.
 */
export class HistoryChangedError extends Error {
  constructor(message: string) {
    super(message || 'O histórico mudou desde a prévia. Revise a prévia novamente antes de publicar.');
    this.name = 'HistoryChangedError';
  }
}

function isHistoryChange(err: HttpErrorResponse): boolean {
  return typeof err.error === 'string' && err.error.includes('histórico mudou');
}

/** Arquivo exportado, pronto para download como Blob (nunca carregado como string). */
export interface PostureExportFile {
  blob: Blob;
  filename: string;
}

/**
 * Cliente do HISTÓRICO auditável de postura (/api/v1/posture/snapshots). O X-Tenant e o Bearer são injetados
 * pelo authInterceptor. Todas as chamadas têm TIMEOUT explícito; o erro é normalizado num Error limpo para o
 * componente renderizar estado + retry. A publicação exige papel Manager/TenantAdmin (403 → mensagem clara);
 * publicar KNIGHT sem assessment retorna 409 (mensagem específica). A comparação incompatível NÃO é um erro —
 * volta 200 com `compatible=false` e é tratada como estado, não exceção.
 */
@Injectable({ providedIn: 'root' })
export class PostureHistoryService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiBase}/api/v1/posture/snapshots`;

  private readonly READ_TIMEOUT_MS = 20_000;
  private readonly PUBLISH_TIMEOUT_MS = 30_000;
  private readonly EXPORT_TIMEOUT_MS = 45_000;

  /** Lista as fotografias do tenant (mais recentes primeiro), opcionalmente por tipo. */
  list(type?: PostureSnapshotType): Observable<PostureSnapshotSummary[]> {
    let params = new HttpParams();
    if (type) params = params.set('type', type);
    return this.http.get<PostureSnapshotSummary[]>(this.base, { params }).pipe(
      timeout(this.READ_TIMEOUT_MS),
      catchError(this.normalize('Não foi possível carregar o histórico de fotografias.')),
    );
  }

  /**
   * [AEGIS-NIST-JOURNEY-01] Evolução mensal por instrumento e fonte (KNIGHT e NIST separados): último publicado do mês;
   * mês sem publicação fica sem ponto; versões incompatíveis não são ligadas (regra do servidor).
   */
  /** [AEGIS-ASSESSMENT-VISUALS-01] `until` ("aaaa-mm") escolhe o último mês do período; nulo = mês corrente. */
  monthly(months = 12, until: string | null = null): Observable<PostureMonthlyHistory> {
    let params = new HttpParams().set('months', months);
    if (until) params = params.set('until', until);
    return this.http.get<PostureMonthlyHistory>(`${this.base}/monthly`, { params }).pipe(
      timeout(this.READ_TIMEOUT_MS),
      catchError(this.normalize('Não foi possível carregar a evolução mensal.')),
    );
  }

  /** Detalhe de uma fotografia. */
  get(id: string): Observable<PostureSnapshotDetail> {
    return this.http.get<PostureSnapshotDetail>(`${this.base}/${id}`).pipe(
      timeout(this.READ_TIMEOUT_MS),
      catchError(this.normalize('Não foi possível carregar a fotografia.')),
    );
  }

  /** Publica uma fotografia da postura atual (controlada por papel no servidor). */
  publish(request: PublishPostureSnapshotRequest): Observable<PostureSnapshotDetail> {
    return this.http.post<PostureSnapshotDetail>(this.base, request).pipe(
      timeout(this.PUBLISH_TIMEOUT_MS),
      catchError((err: unknown) => {
        if (err instanceof HttpErrorResponse) {
          if (err.status === 403)
            return throwError(() => new Error('Seu papel não permite publicar fotografias (requer Manager ou TenantAdmin).'));
          if (err.status === 409 && isHistoryChange(err)) return throwError(() => new HistoryChangedError(err.error as string));
          if (err.status === 409)
            return throwError(() => new Error('Não há postura a registrar. Execute uma avaliação antes de publicar.'));
        }
        return this.normalize('Não foi possível publicar a fotografia.')(err);
      }),
    );
  }

  /**
   * [AEGIS-KNIGHT-CONSOLIDATED-02] Publica o relatório KNIGHT consolidado (fontes elegíveis do catálogo)
   * PINANDO a execução exata de cada fonte incluída — a composição EXIBIDA no instante da publicação, nunca "a
   * mais recente" recalculada pelo servidor. Mesmo controle de papel e os mesmos 403/409 da publicação por
   * fonte; o 409 aqui também cobre uma execução pinada que deixou de estar disponível/concluída/deste tenant.
   */
  publishConsolidated(request: PublishConsolidatedKnightSnapshotRequest): Observable<PostureSnapshotDetail> {
    const body = {
      selection: request.selection.map((s) => ({ source: SOURCE_SLUG[s.source] ?? s.source, runId: s.runId })),
      historyUntil: request.historyUntil ?? null,
      historyMonths: request.historyMonths ?? null,
      expectedHistoryFingerprint: request.expectedHistoryFingerprint ?? null,
    };
    return this.http.post<PostureSnapshotDetail>(`${this.base}/consolidated`, body).pipe(
      timeout(this.PUBLISH_TIMEOUT_MS),
      catchError((err: unknown) => {
        if (err instanceof HttpErrorResponse) {
          if (err.status === 403)
            return throwError(() => new Error('Seu papel não permite publicar fotografias (requer Manager ou TenantAdmin).'));
          if (err.status === 409 && isHistoryChange(err)) return throwError(() => new HistoryChangedError(err.error as string));
          if (err.status === 409)
            return throwError(() => new Error(
              'Não foi possível publicar: nenhuma fonte foi selecionada, ou alguma avaliação exibida deixou de ' +
                'estar disponível. Atualize a tela e tente novamente.',
            ));
        }
        return this.normalize('Não foi possível publicar o relatório consolidado.')(err);
      }),
    );
  }

  /**
   * [AEGIS-ASSESSMENT-VISUALS-01] Prévia do histórico que a publicação KNIGHT congelaria para a avaliação EXATA aberta, no período
   * escolhido. A impressão digital volta na publicação: se a série mudar no meio, o servidor recusa (409) em vez de gravar outra.
   */
  historyPreview(runId: string, window: HistoryWindowRequest): Observable<FrozenPostureHistory> {
    let params = new HttpParams().set('runId', runId);
    if (window.until) params = params.set('until', window.until);
    if (window.months) params = params.set('months', window.months);
    return this.http.get<FrozenPostureHistory>(`${this.base}/history-preview`, { params }).pipe(
      timeout(this.READ_TIMEOUT_MS),
      catchError(this.normalize('Não foi possível montar a prévia do histórico.')),
    );
  }

  /** [AEGIS-ASSESSMENT-VISUALS-01] Idem, para a composição consolidada PINADA (a mesma seleção da publicação). */
  consolidatedHistoryPreview(selection: KnightConsolidatedSourceSelection[], window: HistoryWindowRequest): Observable<FrozenPostureHistory> {
    const body = {
      selection: selection.map((s) => ({ source: SOURCE_SLUG[s.source] ?? s.source, runId: s.runId })),
      historyUntil: window.until,
      historyMonths: window.months,
    };
    return this.http.post<FrozenPostureHistory>(`${this.base}/consolidated/history-preview`, body).pipe(
      timeout(this.READ_TIMEOUT_MS),
      catchError(this.normalize('Não foi possível montar a prévia do histórico.')),
    );
  }

  /** Compara duas fotografias — resultado compatível (com delta) ou incompatível (com motivos). */
  compare(baseId: string, targetId: string): Observable<PostureComparisonResult> {
    const params = new HttpParams().set('baseId', baseId).set('targetId', targetId);
    return this.http.get<PostureComparisonResult>(`${this.base}/compare`, { params }).pipe(
      timeout(this.READ_TIMEOUT_MS),
      catchError(this.normalize('Não foi possível comparar as fotografias.')),
    );
  }

  /**
   * Baixa o relatório executivo (PDF) ou os dados completos (CSV) de uma fotografia como Blob — NUNCA como string.
   * O Bearer e o X-Tenant são injetados pelos interceptors existentes. Usa o filename do Content-Disposition quando
   * disponível (o interceptor de CORS expõe o header) e cai para um nome sanitizado. Erros são normalizados por
   * status (404/409/400) num Error limpo para o componente exibir e permitir nova tentativa.
   */
  exportSnapshot(id: string, format: PostureExportFormat): Observable<PostureExportFile> {
    const params = new HttpParams().set('format', format);
    return this.http
      .get(`${this.base}/${id}/export`, { params, responseType: 'blob', observe: 'response' })
      .pipe(
        timeout(this.EXPORT_TIMEOUT_MS),
        map((res) => ({
          blob: res.body ?? new Blob(),
          filename:
            parseContentDispositionFilename(res.headers.get('Content-Disposition')) ??
            fallbackExportFilename(id, format),
        })),
        catchError((err: unknown) => {
          if (err instanceof HttpErrorResponse) {
            if (err.status === 404) return throwError(() => new Error('Fotografia não encontrada.'));
            if (err.status === 409)
              return throwError(() => new Error('A integridade da fotografia não confere; a exportação foi bloqueada.'));
            if (err.status === 400) return throwError(() => new Error('Formato de exportação inválido.'));
          }
          return this.normalize(`Não foi possível baixar o ${format.toUpperCase()}.`)(err);
        }),
      );
  }

  private normalize(message: string) {
    return (err: unknown) => {
      console.error(`HISTÓRICO: ${message}`, err);
      return throwError(() => new Error(message));
    };
  }
}

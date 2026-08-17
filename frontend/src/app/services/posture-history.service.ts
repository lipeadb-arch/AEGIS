import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, throwError, timeout } from 'rxjs';
import { environment } from '../../environments/environment';
import {
  PostureComparisonResult,
  PostureExportFormat,
  PostureSnapshotDetail,
  PostureSnapshotSummary,
  PostureSnapshotType,
  PublishPostureSnapshotRequest,
  fallbackExportFilename,
  parseContentDispositionFilename,
} from '../models/posture-history.models';

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
          if (err.status === 409)
            return throwError(() => new Error('Não há postura a registrar. Execute uma avaliação antes de publicar.'));
        }
        return this.normalize('Não foi possível publicar a fotografia.')(err);
      }),
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

import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, throwError, timeout } from 'rxjs';
import { environment } from '../../environments/environment';
import {
  PostureComparisonResult,
  PostureSnapshotDetail,
  PostureSnapshotSummary,
  PostureSnapshotType,
  PublishPostureSnapshotRequest,
} from '../models/posture-history.models';

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
            return throwError(() => new Error('Não há postura a fotografar. Execute um assessment antes de publicar.'));
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

  private normalize(message: string) {
    return (err: unknown) => {
      console.error(`HISTÓRICO: ${message}`, err);
      return throwError(() => new Error(message));
    };
  }
}

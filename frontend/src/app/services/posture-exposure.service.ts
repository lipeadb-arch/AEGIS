import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, throwError } from 'rxjs';
import { environment } from '../../environments/environment';
import { PostureExposureList, PostureExposureQueryParams } from '../models/posture-exposure.models';

/**
 * [AEGIS-MVP-POSTURE-02] Cliente da superfície SOMENTE LEITURA de exposições de configuração
 * (`GET /api/v1/posture/exposures`).
 *
 * ⚠️ Nenhum TenantId trafega: o tenant ativo é resolvido no servidor a partir do claim `tenant_id` do JWT;
 * o authInterceptor anexa Bearer + X-Tenant. Sem fallback demonstrativo — erro vira estado de erro na tela.
 */
@Injectable({ providedIn: 'root' })
export class PostureExposureService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiBase}/api/v1/posture`;

  list(params: PostureExposureQueryParams): Observable<PostureExposureList> {
    let hp = new HttpParams();
    if (params.state) hp = hp.set('state', params.state);
    if (params.category) hp = hp.set('category', params.category);
    if (params.service) hp = hp.set('service', params.service);
    if (params.search) hp = hp.set('search', params.search);
    if (params.page) hp = hp.set('page', String(params.page));
    if (params.pageSize) hp = hp.set('pageSize', String(params.pageSize));

    return this.http
      .get<PostureExposureList>(`${this.base}/exposures`, { params: hp })
      .pipe(catchError((err) => throwError(() => this.describe(err))));
  }

  private describe(err: { status?: number; error?: unknown }): Error {
    switch (err?.status) {
      case 0:
        return new Error('API inacessível. Verifique se o servidor está no ar.');
      case 401:
        return new Error('Sessão expirada. Entre novamente.');
      case 403:
        return new Error('Sem permissão para consultar as exposições deste cliente.');
      default:
        return new Error(
          typeof err?.error === 'string' && err.error
            ? err.error
            : 'Não foi possível carregar as exposições. Tente novamente.',
        );
    }
  }
}

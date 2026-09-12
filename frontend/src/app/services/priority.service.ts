import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, throwError } from 'rxjs';
import { environment } from '../../environments/environment';
import { PriorityWorkspace } from '../models/priority.models';
import { CrossSourceFilter, CrossSourceSituationList } from '../models/cross-source.models';

/**
 * [AEGIS-MVP-PRIORITIES-01] Cliente da superfície SOMENTE LEITURA da Central de Prioridades
 * (`GET /api/v1/priorities`) — read model composto (postura + exposições + vulnerabilidades).
 *
 * ⚠️ Nenhum TenantId trafega: o tenant ativo é resolvido no servidor a partir do claim do JWT; o
 * authInterceptor anexa Bearer + X-Tenant. Sem fallback demonstrativo — erro vira estado de erro na tela.
 */
@Injectable({ providedIn: 'root' })
export class PriorityService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiBase}/api/v1/priorities`;

  get(): Observable<PriorityWorkspace> {
    return this.http
      .get<PriorityWorkspace>(this.base)
      .pipe(catchError((err) => throwError(() => this.describe(err))));
  }

  /**
   * [AEGIS-CROSS-SOURCE-01] `GET /api/v1/priorities/correlations` — situações identificadas entre fontes, SEPARADAS das
   * filas: resumo por regra × estado e lista por ativo × regra, paginada (ordem por nome do ativo, não por risco).
   */
  correlations(filter: CrossSourceFilter): Observable<CrossSourceSituationList> {
    let params = new HttpParams()
      .set('state', filter.state)
      .set('page', filter.page)
      .set('pageSize', filter.pageSize);
    if (filter.rule) params = params.set('rule', filter.rule);
    return this.http
      .get<CrossSourceSituationList>(`${this.base}/correlations`, { params })
      .pipe(catchError((err) => throwError(() => this.describe(err,
        'Não foi possível avaliar as situações entre fontes agora — nada é exibido, para que a falha não pareça ' +
        'ausência de situação. Tente novamente.'))));
  }

  private describe(
    err: { status?: number; error?: unknown },
    fallback = 'Não foi possível carregar a Central de Prioridades. Tente novamente.',
  ): Error {
    switch (err?.status) {
      case 0:
        return new Error('API inacessível. Verifique se o servidor está no ar.');
      case 401:
        return new Error('Sessão expirada. Entre novamente.');
      case 403:
        return new Error('Sem permissão para consultar as prioridades deste cliente.');
      default:
        return new Error(
          typeof err?.error === 'string' && err.error ? err.error : fallback,
        );
    }
  }
}

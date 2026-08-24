import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, throwError } from 'rxjs';
import { environment } from '../../environments/environment';
import { PriorityWorkspace } from '../models/priority.models';

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

  private describe(err: { status?: number; error?: unknown }): Error {
    switch (err?.status) {
      case 0:
        return new Error('API inacessível. Verifique se o servidor está no ar.');
      case 401:
        return new Error('Sessão expirada. Entre novamente.');
      case 403:
        return new Error('Sem permissão para consultar as prioridades deste cliente.');
      default:
        return new Error(
          typeof err?.error === 'string' && err.error
            ? err.error
            : 'Não foi possível carregar a Central de Prioridades. Tente novamente.',
        );
    }
  }
}

import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, throwError } from 'rxjs';
import { environment } from '../../environments/environment';
import {
  SoftwareInventoryList,
  SoftwareInventoryQueryParams,
  SoftwareProductAssets,
} from '../models/software-inventory.models';

/**
 * [AEGIS-MVP-MICROSOFT-COVERAGE-01] Cliente da superfície SOMENTE LEITURA de inventário/exposição de software
 * (`GET /api/v1/software-inventory`).
 *
 * ⚠️ Nenhum TenantId trafega: o tenant ativo é resolvido no servidor a partir do claim do JWT; o authInterceptor
 * anexa Bearer + X-Tenant. Sem fallback demonstrativo — erro vira estado de erro na tela.
 */
@Injectable({ providedIn: 'root' })
export class SoftwareInventoryService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiBase}/api/v1/software-inventory`;

  list(params: SoftwareInventoryQueryParams): Observable<SoftwareInventoryList> {
    return this.http
      .get<SoftwareInventoryList>(this.base, { params: this.toParams(params) })
      .pipe(catchError((err) => throwError(() => this.describe(err))));
  }

  /** Ativos relacionados a UM produto (expansão paginada sob demanda). */
  assets(productId: string, page: number, pageSize: number): Observable<SoftwareProductAssets> {
    const hp = new HttpParams().set('page', String(page)).set('pageSize', String(pageSize));
    return this.http
      .get<SoftwareProductAssets>(`${this.base}/${productId}/assets`, { params: hp })
      .pipe(catchError((err) => throwError(() => this.describe(err))));
  }

  private toParams(params: SoftwareInventoryQueryParams): HttpParams {
    let hp = new HttpParams();
    if (params.search) hp = hp.set('search', params.search);
    if (params.vendor) hp = hp.set('vendor', params.vendor);
    if (params.publicExploit) hp = hp.set('publicExploit', 'true');
    if (params.activeAlert) hp = hp.set('activeAlert', 'true');
    if (params.withWeaknesses) hp = hp.set('withWeaknesses', 'true');
    if (params.minImpact != null) hp = hp.set('minImpact', String(params.minImpact));
    if (params.maxImpact != null) hp = hp.set('maxImpact', String(params.maxImpact));
    if (params.state) hp = hp.set('state', params.state);
    if (params.assetId) hp = hp.set('assetId', params.assetId);
    if (params.page) hp = hp.set('page', String(params.page));
    if (params.pageSize) hp = hp.set('pageSize', String(params.pageSize));
    return hp;
  }

  private describe(err: { status?: number; error?: unknown }): Error {
    switch (err?.status) {
      case 0:
        return new Error('API inacessível. Verifique se o servidor está no ar.');
      case 401:
        return new Error('Sessão expirada. Entre novamente.');
      case 403:
        return new Error('Sem permissão para consultar o inventário de software deste cliente.');
      default:
        return new Error(
          typeof err?.error === 'string' && err.error
            ? err.error
            : 'Não foi possível carregar o inventário de software. Tente novamente.',
        );
    }
  }
}

import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { AssetDto, AssetQuery, AssetSources, PagedResult } from '../models/asset.models';
import { AssetCrossSource } from '../models/cross-source.models';
import { environment } from '../../environments/environment';

@Injectable({ providedIn: 'root' })
export class AssetService {
  private readonly http = inject(HttpClient);

  /** GET /api/v1/assets — inventário paginado + filtros NIST, escopado pelo header X-Tenant. */
  list(query: AssetQuery): Observable<PagedResult<AssetDto>> {
    let params = new HttpParams()
      .set('page', query.page)
      .set('pageSize', query.pageSize);

    // Categorias repetidas viram ?category=Hardware&category=Software (OR no backend).
    for (const c of query.category ?? []) params = params.append('category', c);
    if (query.riskLevel) params = params.set('riskLevel', query.riskLevel);
    if (query.criticality != null) params = params.set('criticality', query.criticality);
    if (query.isActive != null) params = params.set('isActive', query.isActive);
    if (query.search?.trim()) params = params.set('search', query.search.trim());

    return this.http.get<PagedResult<AssetDto>>(`${environment.apiBase}/api/v1/assets`, {
      params,
      headers: { Accept: 'application/json' },
    });
  }

  /**
   * [AEGIS-CROSS-SOURCE-01] GET /api/v1/assets/{id}/correlations — situações identificadas entre fontes do ativo,
   * com as evidências de cada fonte e as CVEs que as sustentam (paginadas).
   */
  correlations(assetId: string, cvePage = 1, cvePageSize = 10): Observable<AssetCrossSource> {
    const params = new HttpParams().set('cvePage', cvePage).set('cvePageSize', cvePageSize);
    return this.http.get<AssetCrossSource>(
      `${environment.apiBase}/api/v1/assets/${encodeURIComponent(assetId)}/correlations`,
      { params, headers: { Accept: 'application/json' } },
    );
  }

  /** [AEGIS-ENTITY-RESOLUTION-01] GET /api/v1/assets/{id}/sources — fontes do ativo e o vínculo entre elas. */
  sources(assetId: string): Observable<AssetSources> {
    return this.http.get<AssetSources>(
      `${environment.apiBase}/api/v1/assets/${encodeURIComponent(assetId)}/sources`,
      { headers: { Accept: 'application/json' } },
    );
  }
}

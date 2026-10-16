import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { AssetDto, AssetQuery, AssetSources, PagedResult } from '../models/asset.models';
import { DevicePriorityCriticality } from '../models/device-priority.models';
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

  /** [AEGIS-RISK-PRIORITIZATION-01] PUT /api/v1/assets/{id}/criticality — declara a criticidade (autor vem do token). */
  declareCriticality(assetId: string, criticality: number, note: string | null): Observable<DevicePriorityCriticality> {
    return this.http.put<DevicePriorityCriticality>(
      `${environment.apiBase}/api/v1/assets/${encodeURIComponent(assetId)}/criticality`,
      { criticality, note: note?.trim() || null },
      { headers: { Accept: 'application/json' } },
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

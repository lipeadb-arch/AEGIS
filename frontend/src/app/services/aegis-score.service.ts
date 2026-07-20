import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { TenantTrendDto, CurrentScoreDto } from '../models/aegis-score.models';
import { environment } from '../../environments/environment';

@Injectable({ providedIn: 'root' })
export class AegisScoreService {
  private readonly http = inject(HttpClient);

  /**
   * GET /api/v1/scoring/trend — série diária de postura do tenant (default 30 dias, ordem
   * cronológica). Escopada pelo header X-Tenant; o Bearer do JWT é anexado pelo authInterceptor.
   */
  fetchTrend(days = 30): Observable<TenantTrendDto[]> {
    const params = new HttpParams().set('days', days);
    return this.http.get<TenantTrendDto[]>(`${environment.apiBase}/api/v1/scoring/trend`, {
      params,
      headers: { Accept: 'application/json' },
    });
  }

  /**
   * GET /api/v1/scoring/pending — nº de controles NIST não-conformes do tenant (KPI do HUD).
   * Escopado pelo header X-Tenant; o Bearer do JWT é anexado pelo authInterceptor.
   */
  fetchPendingControls(): Observable<number> {
    return this.http.get<number>(`${environment.apiBase}/api/v1/scoring/pending`, {
      headers: { Accept: 'application/json' },
    });
  }

  /**
   * GET /api/v1/scoring/current — Score Atual (%) do tenant em tempo real, direto do
   * TenantControlState (sem esperar a foto diária). Escopado pelo header X-Tenant; o Bearer do JWT é
   * anexado pelo authInterceptor.
   */
  fetchCurrentScore(): Observable<CurrentScoreDto> {
    return this.http.get<CurrentScoreDto>(`${environment.apiBase}/api/v1/scoring/current`, {
      headers: { Accept: 'application/json' },
    });
  }
}

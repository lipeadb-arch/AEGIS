import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import { BlastRadiusSummary, ExecutiveDashboard } from '../models/dashboard.models';
import { DashboardOverview } from '../models/dashboard-overview.models';
import { environment } from '../../environments/environment';

@Injectable({ providedIn: 'root' })
export class DashboardService {
  private readonly http = inject(HttpClient);

  private readonly headers = { Accept: 'application/json' };

  /**
   * [AEGIS-MVP-PRODUCT-01] GET /api/v1/dashboard/overview — a leitura COMPOSTA da tela inicial, com cada
   * dimensão carregando o próprio estado e a própria origem. É UMA chamada: o backend já compõe postura,
   * ambiente observado, risco de negócio, filas prioritárias, identidade e saúde das fontes, sem acionar
   * coleta externa. A tela não faz N requisições para montar o mesmo quadro.
   */
  fetchOverview(): Observable<DashboardOverview> {
    return this.http.get<DashboardOverview>(
      `${environment.apiBase}/api/v1/dashboard/overview`,
      { headers: this.headers },
    );
  }

  /** GET /api/v1/dashboard/executive, escopado pelo tenant via header X-Tenant. */
  fetchExecutive(): Observable<ExecutiveDashboard> {
    return this.http.get<ExecutiveDashboard>(
      `${environment.apiBase}/api/v1/dashboard/executive`,
      { headers: this.headers },
    );
  }

  /**
   * GET /api/v1/dashboard/blast-radius-summary — o pior raio conhecido do tenant.
   *
   * Responde **204 No Content** quando nenhum raio foi calculado ainda. O `HttpClient` entrega isso
   * como `null`, e é exatamente a distinção que o painel precisa: "nunca medimos" ≠ "o raio é zero".
   * Por isso o retorno é `| null` em vez de um DTO zerado.
   */
  fetchBlastRadiusSummary(): Observable<BlastRadiusSummary | null> {
    return this.http
      .get<BlastRadiusSummary>(`${environment.apiBase}/api/v1/dashboard/blast-radius-summary`, {
        headers: this.headers,
      })
      .pipe(map((r) => r ?? null));
  }
}

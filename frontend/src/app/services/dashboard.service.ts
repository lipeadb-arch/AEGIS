import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ExecutiveDashboard } from '../models/dashboard.models';
import { environment } from '../../environments/environment';

@Injectable({ providedIn: 'root' })
export class DashboardService {
  private readonly http = inject(HttpClient);

  /** GET /api/v1/dashboard/executive, escopado pelo tenant via header X-Tenant. */
  fetchExecutive(): Observable<ExecutiveDashboard> {
    return this.http.get<ExecutiveDashboard>(
      `${environment.apiBase}/api/v1/dashboard/executive`,
      { headers: { 'X-Tenant': environment.tenantId, Accept: 'application/json' } },
    );
  }
}

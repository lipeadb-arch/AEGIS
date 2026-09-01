import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, throwError } from 'rxjs';
import { environment } from '../../environments/environment';
import { DetectionCoverageView } from '../models/detection-coverage.models';

/**
 * [AEGIS-MVP-GOOGLE-SECOPS-02] Cliente da superfície SOMENTE LEITURA da cobertura de detecção
 * (`GET /api/v1/detection-coverage`).
 *
 * ⚠️ Nenhum TenantId trafega: o tenant ativo é resolvido no servidor pelo claim `tenant_id` do JWT; o
 * authInterceptor anexa Bearer + X-Tenant. Sem fallback demonstrativo — erro vira estado de erro na tela.
 */
@Injectable({ providedIn: 'root' })
export class DetectionCoverageService {
  private readonly http = inject(HttpClient);
  private readonly url = `${environment.apiBase}/api/v1/detection-coverage`;

  get(): Observable<DetectionCoverageView> {
    return this.http
      .get<DetectionCoverageView>(this.url)
      .pipe(catchError((err) => throwError(() => this.describe(err))));
  }

  private describe(err: { status?: number; error?: unknown }): Error {
    switch (err?.status) {
      case 0:
        return new Error('API inacessível. Verifique se o servidor está no ar.');
      case 401:
        return new Error('Sessão expirada. Entre novamente.');
      case 403:
        return new Error('Sem permissão para consultar a cobertura de detecção deste cliente.');
      default:
        return new Error('Não foi possível carregar a cobertura de detecção. Tente novamente.');
    }
  }
}

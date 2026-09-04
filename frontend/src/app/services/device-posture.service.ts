import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, throwError } from 'rxjs';
import { environment } from '../../environments/environment';
import { DevicePostureView } from '../models/device-posture.models';

/**
 * [AEGIS-MVP-MICROSOFT-COVERAGE-02] Cliente da superfície SOMENTE LEITURA da postura de configuração e
 * conformidade de dispositivos (`GET /api/v1/device-posture`).
 *
 * ⚠️ Nenhum TenantId trafega: o tenant ativo é resolvido no servidor pelo claim `tenant_id` do JWT; o
 * authInterceptor anexa Bearer + X-Tenant. Sem fallback demonstrativo — erro vira estado de erro na tela.
 */
@Injectable({ providedIn: 'root' })
export class DevicePostureService {
  private readonly http = inject(HttpClient);
  private readonly url = `${environment.apiBase}/api/v1/device-posture`;

  get(): Observable<DevicePostureView> {
    return this.http
      .get<DevicePostureView>(this.url)
      .pipe(catchError((err) => throwError(() => this.describe(err))));
  }

  private describe(err: { status?: number; error?: unknown }): Error {
    switch (err?.status) {
      case 0:
        return new Error('API inacessível. Verifique se o servidor está no ar.');
      case 401:
        return new Error('Sessão expirada. Entre novamente.');
      case 403:
        return new Error('Sem permissão para consultar a postura de dispositivos deste cliente.');
      default:
        return new Error('Não foi possível carregar a postura de dispositivos. Tente novamente.');
    }
  }
}

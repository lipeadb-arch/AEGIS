import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, throwError, timeout } from 'rxjs';
import { environment } from '../../environments/environment';
import { IdentityEvidenceProjection } from '../models/identity-risk.models';

/**
 * [AEGIS-MVP-MICROSOFT-COVERAGE-03] Cliente SOMENTE LEITURA da Evidence Fabric de identidade
 * (`GET /api/v1/telemetry/identity/entra-id`).
 *
 * Esta é a MESMA fotografia que o AEGIS KNIGHT avalia — a tela lê o snapshot já persistido e NUNCA dispara
 * uma segunda consulta ao Microsoft Graph. A coleta continua sendo disparada por um único caminho (o botão
 * "Coletar do Entra ID" do KNIGHT, que converge para o serviço compartilhado).
 *
 * ⚠️ Nenhum TenantId trafega: o tenant vem do claim `tenant_id` do JWT; o authInterceptor anexa Bearer e
 * X-Tenant. Sem fallback demonstrativo — um erro vira estado de erro na tela, jamais números inventados.
 */
@Injectable({ providedIn: 'root' })
export class IdentityRiskService {
  private readonly http = inject(HttpClient);
  private readonly url = `${environment.apiBase}/api/v1/telemetry/identity/entra-id`;

  private readonly READ_TIMEOUT_MS = 15_000;

  get(): Observable<IdentityEvidenceProjection> {
    return this.http.get<IdentityEvidenceProjection>(this.url).pipe(
      timeout(this.READ_TIMEOUT_MS),
      catchError((err) => throwError(() => this.describe(err))),
    );
  }

  private describe(err: { status?: number }): Error {
    switch (err?.status) {
      case 0:
        return new Error('API inacessível. Verifique se o servidor está no ar.');
      case 401:
        return new Error('Sessão expirada. Entre novamente.');
      case 403:
        return new Error('Sem permissão para consultar o risco de identidade deste cliente.');
      default:
        return new Error('Não foi possível carregar o risco de identidade. Tente novamente.');
    }
  }
}

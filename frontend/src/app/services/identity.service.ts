import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, throwError } from 'rxjs';
import { environment } from '../../environments/environment';
import { EntraIdIngestionRequest, IdentityVerdict } from '../models/identity.models';

/**
 * Cliente da esteira ATIVA de telemetria de identidade (Microsoft Entra ID). Diferente do ScoringService
 * (leitura do ledger), aqui DISPARAMOS a coleta: POST /api/v1/telemetry/identity/entra-id. O X-Tenant e o
 * Bearer são injetados pelo authInterceptor — não os repetimos. Resiliente: normaliza o erro num Error
 * limpo para o Smart Component renderizar um estado elegante em vez de quebrar.
 */
@Injectable({ providedIn: 'root' })
export class IdentityService {
  private readonly http = inject(HttpClient);
  private readonly url = `${environment.apiBase}/api/v1/telemetry/identity/entra-id`;

  /**
   * Executa a análise de postura do Entra ID do tenant. O servidor coleta as métricas (provider),
   * enxerta o contexto de rede do corpo e devolve os vereditos por controle (PR.AA-01, GV.RR-01) — já
   * gravados no ledger com fonte Telemetry. Passe `hasNetworkIsolation: true` para os ativos OT/legado
   * isolados: é o que permite ao motor mitigar (em vez de reprovar) contas de serviço sem MFA.
   */
  runEntraIdAnalysis(request: EntraIdIngestionRequest): Observable<IdentityVerdict[]> {
    return this.http.post<IdentityVerdict[]>(this.url, request).pipe(
      catchError((err) => {
        console.error('Identity: falha no POST /telemetry/identity/entra-id.', err);
        return throwError(() => new Error('Não foi possível executar a análise de identidade do Entra ID.'));
      }),
    );
  }
}

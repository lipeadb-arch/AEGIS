import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, of } from 'rxjs';
import { environment } from '../../environments/environment';

/** Estado EFETIVO da IA para o tenant autenticado (retrato de configuração, não health check). */
export type AiEffectiveState =
  | 'EnterpriseConfigured'
  | 'DemoConfigured'
  | 'ExternalBlockedForTenant'
  | 'Simulated'
  | 'Unavailable';

/** Status tenant-scoped da IA. Nenhum campo carrega segredo. */
export interface AiStatus {
  mode: string;
  effectiveState: AiEffectiveState | string;
  providerConfigured: boolean;
  externalAllowedForTenant: boolean;
  freeTier: boolean;
  limitationNotice: string | null;
}

@Injectable({ providedIn: 'root' })
export class AiStatusService {
  private readonly http = inject(HttpClient);
  private readonly url = `${environment.apiBase}/api/v1/ai/status`;

  status(): Observable<AiStatus | null> {
    return this.http.get<AiStatus>(this.url).pipe(catchError(() => of(null)));
  }
}

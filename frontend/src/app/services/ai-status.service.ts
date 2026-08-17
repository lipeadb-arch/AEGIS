import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, of } from 'rxjs';
import { environment } from '../../environments/environment';

/** Estado EFETIVO da IA para o tenant autenticado (espelha AiStatusDto do backend). */
export type AiEffectiveState =
  | 'DemoActive'
  | 'ExternalBlockedForTenant'
  | 'Simulated'
  | 'Unavailable';

/**
 * Status tenant-scoped da IA. Nenhum campo carrega segredo. <c>effectiveState</c> é o rótulo do tenant;
 * <c>freeTier</c> liga o aviso de dados sintéticos; <c>limitationNotice</c> traz o texto do aviso.
 */
export interface AiStatus {
  mode: string;
  effectiveState: AiEffectiveState | string;
  providerConfigured: boolean;
  externalAllowedForTenant: boolean;
  freeTier: boolean;
  limitationNotice: string | null;
}

/**
 * Cliente do endpoint de status da IA (GET /api/v1/ai/status). Resiliente: uma falha vira `null` (a UI
 * simplesmente não mostra o chip/aviso), nunca derruba a tela. X-Tenant + Bearer via authInterceptor.
 */
@Injectable({ providedIn: 'root' })
export class AiStatusService {
  private readonly http = inject(HttpClient);
  private readonly url = `${environment.apiBase}/api/v1/ai/status`;

  status(): Observable<AiStatus | null> {
    return this.http.get<AiStatus>(this.url).pipe(catchError(() => of(null)));
  }
}

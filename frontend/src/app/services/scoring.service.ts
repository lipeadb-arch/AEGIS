import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, throwError } from 'rxjs';
import { environment } from '../../environments/environment';
import { PillarKey, TenantControlStateDto, pillarPrefix } from '../models/scoring.models';

/**
 * Camada de acesso a dados do HUD de scoring por pilar. Simples por design (sem NgRx): expõe Observables;
 * o ESTADO fica em Signals nos componentes. O header X-Tenant e o Bearer do JWT são injetados pelo
 * authInterceptor em toda chamada ao apiBase — por isso NÃO os repetimos aqui.
 *
 * Resiliência: normaliza qualquer erro de transporte num Error limpo (sem vazar o HttpErrorResponse cru),
 * para o Smart Component renderizar um estado de erro elegante no HUD em vez de quebrar.
 */
@Injectable({ providedIn: 'root' })
export class ScoringService {
  private readonly http = inject(HttpClient);
  private readonly dashboardUrl = `${environment.apiBase}/api/v1/scoring/dashboard`;

  /**
   * GET /api/v1/scoring/dashboard — matriz de conformidade de TODOS os controles avaliados do tenant
   * (lista plana; o pilar é o prefixo do código NIST). Buscado a cada navegação para refletir avaliações
   * recém-processadas (ex.: um /sync do Govern) — o payload é pequeno.
   */
  getDashboard(): Observable<TenantControlStateDto[]> {
    return this.http.get<TenantControlStateDto[]>(this.dashboardUrl).pipe(
      catchError((err) => {
        console.error('Aegis Score: falha ao carregar /scoring/dashboard.', err);
        return throwError(() => new Error('Não foi possível carregar a matriz de conformidade.'));
      }),
    );
  }

  /** Controles de UM pilar, filtrados pelo prefixo do código NIST ("PR." → Protect). */
  getPillarControls(pillar: PillarKey): Observable<TenantControlStateDto[]> {
    const prefix = pillarPrefix(pillar);
    return this.getDashboard().pipe(
      map((all) => all.filter((c) => c.subcategoryCode.startsWith(prefix))),
    );
  }
}

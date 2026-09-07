import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, throwError, timeout } from 'rxjs';
import { environment } from '../../environments/environment';
import {
  ActionPlan,
  CreateActionPlanRequest,
  RecordExecutionRequest,
  UpdateActionPlanRequest,
  ValidateActionPlanRequest,
} from '../models/remediation.models';

/**
 * [AEGIS-MVP-PRODUCT-03] Cliente da jornada de remediação (`/api/v1/remediation/action-plans`).
 *
 * ⚠️ Nenhum TenantId trafega: o tenant é resolvido no servidor pelo claim do JWT; o authInterceptor anexa
 * Bearer + X-Tenant. Sem fallback demonstrativo — erro vira estado de erro na tela.
 *
 * O 409 de DUPLICIDADE é tratado como caso de negócio, não como falha: o servidor devolve o identificador da
 * ação ativa existente e a tela abre aquela ação, em vez de criar uma segunda.
 */
export class ActionPlanConflictError extends Error {
  constructor(
    message: string,
    /** Ação ATIVA já existente para a mesma origem, quando o conflito é de duplicidade. */
    readonly existingActionPlanId: string | null,
  ) {
    super(message);
    this.name = 'ActionPlanConflictError';
  }
}

@Injectable({ providedIn: 'root' })
export class RemediationService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiBase}/api/v1/remediation/action-plans`;

  private readonly READ_TIMEOUT_MS = 20_000;
  private readonly WRITE_TIMEOUT_MS = 30_000;

  /** Ações de achado do tenant. `activeOnly` traz só as que ocupam a origem (Aberta/Em andamento/Aguardando). */
  list(indicatorId?: string, activeOnly = false): Observable<ActionPlan[]> {
    let params = new HttpParams();
    if (indicatorId) params = params.set('indicatorId', indicatorId);
    if (activeOnly) params = params.set('activeOnly', 'true');
    return this.http.get<ActionPlan[]>(this.base, { params }).pipe(
      timeout(this.READ_TIMEOUT_MS),
      catchError(this.normalize('Não foi possível carregar os planos de ação.')),
    );
  }

  get(id: string): Observable<ActionPlan> {
    return this.http.get<ActionPlan>(`${this.base}/${id}`).pipe(
      timeout(this.READ_TIMEOUT_MS),
      catchError(this.normalize('Não foi possível carregar o plano de ação.')),
    );
  }

  create(request: CreateActionPlanRequest): Observable<ActionPlan> {
    return this.http.post<ActionPlan>(this.base, request).pipe(
      timeout(this.WRITE_TIMEOUT_MS),
      catchError(this.normalize('Não foi possível criar o plano de ação.')),
    );
  }

  update(id: string, request: UpdateActionPlanRequest): Observable<ActionPlan> {
    return this.http.put<ActionPlan>(`${this.base}/${id}`, request).pipe(
      timeout(this.WRITE_TIMEOUT_MS),
      catchError(this.normalize('Não foi possível salvar o plano de ação.')),
    );
  }

  recordExecution(id: string, request: RecordExecutionRequest): Observable<ActionPlan> {
    return this.http.post<ActionPlan>(`${this.base}/${id}/execution`, request).pipe(
      timeout(this.WRITE_TIMEOUT_MS),
      catchError(this.normalize('Não foi possível registrar a execução.')),
    );
  }

  validate(id: string, request: ValidateActionPlanRequest): Observable<ActionPlan> {
    return this.http.post<ActionPlan>(`${this.base}/${id}/validations`, request).pipe(
      timeout(this.WRITE_TIMEOUT_MS),
      catchError(this.normalize('Não foi possível registrar a validação.')),
    );
  }

  private normalize(message: string) {
    return (err: unknown) => {
      if (err instanceof HttpErrorResponse) {
        if (err.status === 0) return throwError(() => new Error('API inacessível. Verifique se o servidor está no ar.'));
        if (err.status === 401) return throwError(() => new Error('Sessão expirada. Entre novamente.'));
        if (err.status === 403)
          return throwError(() => new Error('Seu papel não permite alterar planos de ação (requer Manager ou TenantAdmin).'));
        if (err.status === 404) return throwError(() => new Error('Avaliação, achado ou plano não encontrados neste cliente.'));
        if (err.status === 409) {
          const body = err.error as { message?: string; existingActionPlanId?: string } | string | null;
          if (typeof body === 'object' && body !== null) {
            return throwError(
              () => new ActionPlanConflictError(body.message ?? message, body.existingActionPlanId ?? null),
            );
          }
          return throwError(() => new ActionPlanConflictError(typeof body === 'string' ? body : message, null));
        }
        if (err.status === 400 && typeof err.error === 'string' && err.error) {
          return throwError(() => new Error(err.error as string));
        }
      }
      console.error(`REMEDIAÇÃO: ${message}`, err);
      return throwError(() => new Error(message));
    };
  }
}

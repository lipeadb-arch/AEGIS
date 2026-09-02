import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, throwError } from 'rxjs';
import { environment } from '../../environments/environment';
import { TenantAdmin } from '../models/tenant-admin.models';

/** Resposta da criação de tenant: só o id do ambiente recém-criado. */
export interface CreatedTenant {
  id: string;
}

/**
 * Criação de ambientes (tenants) — autoridade de PLATAFORMA (PlatformAdmin). O criador recebe, no servidor
 * e atomicamente, um acesso TenantAdmin no novo ambiente. NENHUM TenantId trafega. As mensagens de erro
 * cobrem tanto respostas `{title, status}` quanto respostas em string simples (o endpoint usa ambas).
 */
@Injectable({ providedIn: 'root' })
export class TenantAdminService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiBase}/api/v1`;

  /** Cria um novo ambiente. 409 se o slug já existir; 400 se nome/slug inválidos; 403 sem autoridade global. */
  createTenant(name: string, slug: string): Observable<CreatedTenant> {
    return this.http
      .post<CreatedTenant>(`${this.base}/tenants`, { name, slug })
      .pipe(catchError((err) => throwError(() => this.describe(err))));
  }

  // ---- [AEGIS-MVP-ADMIN-LIFECYCLE-01] Ciclo de vida administrativo (PlatformAdmin) ----
  // NENHUM método envia autoridade de tenant: o ator vem do JWT (policy de plataforma). O alvo é o id do
  // ambiente na rota. O slug é IMUTÁVEL — a renomeação só carrega o nome.

  /** Catálogo COMPLETO de tenants (inclui suspensos). */
  listTenants(): Observable<TenantAdmin[]> {
    return this.http
      .get<TenantAdmin[]>(`${this.base}/platform/tenants`)
      .pipe(catchError((err) => throwError(() => this.describe(err))));
  }

  /** Renomeia o nome de exibição (slug inalterado). */
  renameTenant(tenantId: string, name: string): Observable<TenantAdmin> {
    return this.http
      .put<TenantAdmin>(`${this.base}/platform/tenants/${tenantId}`, { name })
      .pipe(catchError((err) => throwError(() => this.describe(err))));
  }

  /** Suspende (preserva histórico; impede o uso operacional). 409 se deixaria o ator sem ambiente de recuperação. */
  suspendTenant(tenantId: string): Observable<TenantAdmin> {
    return this.http
      .post<TenantAdmin>(`${this.base}/platform/tenants/${tenantId}/suspend`, {})
      .pipe(catchError((err) => throwError(() => this.describe(err))));
  }

  /** Reativa um tenant suspenso (→ Ativo). Idempotente. */
  reactivateTenant(tenantId: string): Observable<TenantAdmin> {
    return this.http
      .post<TenantAdmin>(`${this.base}/platform/tenants/${tenantId}/reactivate`, {})
      .pipe(catchError((err) => throwError(() => this.describe(err))));
  }

  private describe(err: unknown): Error {
    if (err instanceof HttpErrorResponse) {
      // O endpoint responde ora {title,status} (validação nova), ora string simples (contrato antigo).
      const body = err.error;
      if (typeof body === 'string' && body) return new Error(body);
      if (typeof body === 'object' && body?.title) return new Error(String(body.title));
      switch (err.status) {
        case 0:
          return new Error('API inacessível. Verifique se o servidor está no ar.');
        case 401:
          return new Error('Sessão expirada. Entre novamente.');
        case 403:
          return new Error('Somente administradores da plataforma podem criar ambientes.');
        case 409:
          return new Error('Já existe um ambiente com este identificador (slug).');
        default:
          return new Error('Não foi possível criar o ambiente. Tente novamente.');
      }
    }
    return new Error('Não foi possível criar o ambiente. Tente novamente.');
  }
}

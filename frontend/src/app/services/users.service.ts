import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, throwError } from 'rxjs';
import { environment } from '../../environments/environment';
import {
  AdminResetPasswordRequest,
  OnboardTenantUserRequest,
  OnboardTenantUserResponse,
  TenantUser,
  UpdateMembershipRequest,
} from '../models/users.models';

/**
 * Cliente da administração de usuários e acessos do tenant ambiente. NENHUM método envia TenantId — o
 * servidor o resolve do JWT, e o authInterceptor anexa Bearer + X-Tenant. As mensagens de erro preferem o
 * `title` sanitizado do servidor (dono da política), com fallback por código HTTP.
 */
@Injectable({ providedIn: 'root' })
export class UsersService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiBase}/api/v1`;

  /** Acessos do tenant ambiente (TenantAdmin). */
  list(): Observable<TenantUser[]> {
    return this.http
      .get<TenantUser[]>(`${this.base}/users`)
      .pipe(catchError((err) => throwError(() => this.describe(err))));
  }

  /**
   * Onboarding: cria a identidade quando nova E concede acesso (PlatformAdmin + TenantAdmin). A resposta
   * indica se a pessoa já existia (senha preservada). 403 ⇒ falta autoridade global.
   */
  onboard(body: OnboardTenantUserRequest): Observable<OnboardTenantUserResponse> {
    return this.http
      .post<OnboardTenantUserResponse>(`${this.base}/platform/tenant-users`, body)
      .pipe(catchError((err) => throwError(() => this.describe(err))));
  }

  /** Edita nome e/ou papel de um acesso (PUT). */
  update(membershipId: string, body: UpdateMembershipRequest): Observable<TenantUser> {
    return this.http
      .put<TenantUser>(`${this.base}/users/${membershipId}`, body)
      .pipe(catchError((err) => throwError(() => this.describe(err))));
  }

  /** Desativa (reversível) um acesso. */
  deactivate(membershipId: string): Observable<TenantUser> {
    return this.http
      .post<TenantUser>(`${this.base}/users/${membershipId}/deactivate`, {})
      .pipe(catchError((err) => throwError(() => this.describe(err))));
  }

  /** Reativa um acesso (não restaura sessões antigas). */
  reactivate(membershipId: string): Observable<TenantUser> {
    return this.http
      .post<TenantUser>(`${this.base}/users/${membershipId}/reactivate`, {})
      .pipe(catchError((err) => throwError(() => this.describe(err))));
  }

  /**
   * Redefinição ADMINISTRATIVA de senha (PlatformAdmin). O alvo é a IDENTIDADE GLOBAL (`identityAccountId`),
   * NUNCA o membership — por isso a rota é a global de plataforma, não `/users`. Em sucesso (204) o backend já
   * substituiu o hash e revogou as sessões da pessoa em TODOS os ambientes. A senha nunca é guardada nem logada;
   * aqui ela só passa no corpo da requisição. 403 ⇒ falta autoridade de plataforma; 409 ⇒ conta federada ou a
   * própria conta; 404 ⇒ identidade inexistente; 400 ⇒ senha fora da política.
   */
  resetPassword(identityAccountId: string, newPassword: string): Observable<void> {
    const body: AdminResetPasswordRequest = { newPassword };
    return this.http
      .post<void>(`${this.base}/platform/identities/${identityAccountId}/password`, body)
      .pipe(catchError((err) => throwError(() => this.describe(err))));
  }

  /**
   * Traduz o erro HTTP numa mensagem acionável. Prefere o `title` sanitizado do servidor (a política vive
   * lá); só cai no genérico por status quando o corpo não traz título.
   */
  private describe(err: unknown): Error {
    if (err instanceof HttpErrorResponse) {
      const serverTitle = typeof err.error === 'object' && err.error?.title ? String(err.error.title) : null;
      if (serverTitle) return new Error(serverTitle);
      switch (err.status) {
        case 0:
          return new Error('API inacessível. Verifique se o servidor está no ar.');
        case 401:
          return new Error('Sessão expirada. Entre novamente.');
        case 403:
          return new Error('Você não tem autoridade para esta operação.');
        case 404:
          return new Error('Usuário não encontrado neste ambiente.');
        case 409:
          return new Error('Operação não permitida no estado atual.');
        case 429:
          return new Error('Muitas tentativas. Aguarde um minuto e tente novamente.');
        default:
          return new Error('Não foi possível concluir a operação. Tente novamente.');
      }
    }
    return new Error('Não foi possível concluir a operação. Tente novamente.');
  }
}

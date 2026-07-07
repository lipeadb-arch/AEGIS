import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, catchError, map, of, tap } from 'rxjs';
import { environment } from '../../environments/environment';

/** Corpo devolvido por POST /auth/login e /auth/refresh. O refresh token NUNCA vem aqui — só no cookie. */
export interface AuthResponse {
  accessToken: string;
  accessTokenExpiresAt: string;
}

/**
 * Dono da sessão do lado do cliente.
 *
 * Regra de ouro (anti-XSS): o access token vive ESTRITAMENTE em memória (um signal). Nunca toca em
 * localStorage/sessionStorage — assim um XSS não consegue exfiltrar o JWT de um storage persistente.
 * A durabilidade da sessão entre reloads vem do refresh token, que mora num cookie HttpOnly
 * inacessível ao JavaScript; no bootstrap tentamos um "silent refresh" para reidratar o access token.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);
  private readonly baseUrl = `${environment.apiBase}/api/v1/auth`;

  // ---- Estado do access token: apenas em memória ----
  private readonly _accessToken = signal<string | null>(null);

  /** Sinal reativo do token (para quem quiser reagir a login/logout). */
  readonly accessToken = this._accessToken.asReadonly();

  /** Há sessão ativa em memória? (guards, exibição condicional do layout). */
  readonly isAuthenticated = computed(() => this._accessToken() !== null);

  /** Leitura síncrona para o auth interceptor (não pode esperar um Observable). */
  get token(): string | null {
    return this._accessToken();
  }

  /** Autentica por credenciais; o cookie de refresh é setado pelo servidor (withCredentials). */
  login(email: string, password: string): Observable<void> {
    return this.http
      .post<AuthResponse>(`${this.baseUrl}/login`, { email, password }, { withCredentials: true })
      .pipe(
        tap((res) => this.setSession(res)),
        map(() => void 0),
      );
  }

  /**
   * Troca o refresh token do cookie por um novo par (RTR). É a chamada que o refresh interceptor
   * dispara uma única vez sob 401. Retorna o novo access token já persistido em memória.
   */
  refresh(): Observable<string> {
    return this.http
      .post<AuthResponse>(`${this.baseUrl}/refresh`, {}, { withCredentials: true })
      .pipe(
        tap((res) => this.setSession(res)),
        map((res) => res.accessToken),
      );
  }

  /**
   * Reidrata a sessão no start do app usando só o cookie (silent refresh). Nunca rejeita: se não há
   * cookie/sessão válida, limpa o estado e resolve false — o app inicia e o guard leva ao login.
   */
  restoreSession(): Observable<boolean> {
    return this.refresh().pipe(
      map(() => true),
      catchError(() => {
        this.clearSession();
        return of(false);
      }),
    );
  }

  /** Logout iniciado pelo usuário: revoga o refresh no servidor (best-effort) e limpa o cliente. */
  logout(): void {
    this.http.post(`${this.baseUrl}/logout`, {}, { withCredentials: true }).subscribe({
      next: () => {},
      error: () => {},
    });
    this.clearSession();
    this.router.navigate(['/login']);
  }

  /** Logout forçado pelo refresh interceptor quando a renovação falha (refresh inválido/breach). */
  forceLogout(): void {
    this.clearSession();
    this.router.navigate(['/login']);
  }

  private setSession(res: AuthResponse): void {
    this._accessToken.set(res.accessToken);
  }

  private clearSession(): void {
    this._accessToken.set(null);
  }
}

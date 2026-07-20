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

/** Um ambiente disponível no seletor do HUD. `role` é o papel NAQUELE cliente. */
export interface TenantOption {
  id: string;
  name: string;
  slug: string;
  role: string;
}

/**
 * Lê o payload de um JWT SEM validar assinatura. É seguro e correto aqui: o cliente não decide nada
 * com isto — quem valida é o servidor. Serve só para o HUD saber em que ambiente está sem precisar de
 * uma chamada extra, e para o interceptor derivar o X-Tenant do PRÓPRIO token (ver auth.interceptor).
 */
function readJwtClaim(token: string | null, claim: string): string | null {
  if (!token) return null;
  const payload = token.split('.')[1];
  if (!payload) return null;
  try {
    const json = atob(payload.replace(/-/g, '+').replace(/_/g, '/'));
    const value = JSON.parse(json)?.[claim];
    return typeof value === 'string' ? value : null;
  } catch {
    return null;
  }
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

  // ---- Ambientes (Tenant Switcher do HUD) ----
  private readonly _tenants = signal<TenantOption[]>([]);

  /** Ambientes a que a pessoa autenticada tem acesso. Vazio até `loadTenants()`. */
  readonly tenants = this._tenants.asReadonly();

  /**
   * Tenant ATIVO, derivado da claim `tenant_id` do próprio access token.
   *
   * ⚠️ Deliberadamente NÃO é um signal editável. Se o ambiente ativo fosse estado próprio do cliente,
   * ele poderia divergir do token — e toda chamada levaria 403 do TenantConsistencyMiddleware. Sendo
   * derivado, a troca de ambiente acontece por um único caminho possível: trocar o token.
   */
  readonly activeTenantId = computed(() => readJwtClaim(this._accessToken(), 'tenant_id'));

  /** O ambiente ativo já resolvido em nome/slug/papel (quando a lista está carregada). */
  readonly activeTenant = computed(() => {
    const id = this.activeTenantId();
    return this._tenants().find((t) => t.id === id) ?? null;
  });

  /** Papel da pessoa NO ambiente ativo, lido do token (não da lista, que pode estar velha). */
  readonly activeRole = computed(() => readJwtClaim(this._accessToken(), 'role'));

  /** Leitura síncrona para o auth interceptor (não pode esperar um Observable). */
  get token(): string | null {
    return this._accessToken();
  }

  /** Autentica por credenciais; o cookie de refresh é setado pelo servidor (withCredentials). */
  login(email: string, password: string): Observable<void> {
    return this.http
      .post<AuthResponse>(`${this.baseUrl}/login`, { email, password }, { withCredentials: true })
      .pipe(
        tap((res) => {
          this.setSession(res);
          // Ambientes carregam EM PARALELO, não em série: encadeá-los atrasaria a navegação
          // pós-login por uma chamada que só alimenta um dropdown. O seletor aparece quando chegar.
          this.getAvailableTenants().subscribe();
        }),
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
      // Reidratação por F5: sem isto o seletor sumiria a cada recarga, porque a lista de ambientes só
      // era carregada no login. O `refresh()` normal (renovação a cada ~10 min) NÃO recarrega a lista
      // de propósito — os acessos da pessoa não mudam a essa cadência.
      tap(() => this.getAvailableTenants().subscribe()),
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

  /**
   * Ambientes acessíveis pela pessoa autenticada (`GET /users/me/tenants`). A resposta é derivada da
   * claim `account_id` do token no servidor — o cliente não informa quem é.
   */
  getAvailableTenants(): Observable<TenantOption[]> {
    return this.http.get<TenantOption[]>(`${environment.apiBase}/api/v1/users/me/tenants`).pipe(
      tap((list) => this._tenants.set(list)),
      catchError(() => {
        // Falha aqui não derruba a sessão: o HUD apenas não oferece a troca.
        this._tenants.set([]);
        return of([] as TenantOption[]);
      }),
    );
  }

  /**
   * Troca o ambiente ativo. O servidor confirma o membership, REVOGA o refresh anterior e emite um par
   * novo; aqui só substituímos o access token em memória — e, como o `activeTenantId` é derivado dele,
   * o HUD inteiro (e o X-Tenant do interceptor) passa a apontar para o novo ambiente de uma vez.
   *
   * ⚠️ O token antigo é DESCARTADO no mesmo `set`: não há janela em que os dois coexistam no cliente.
   */
  switchTenant(tenantId: string): Observable<void> {
    return this.http
      .post<AuthResponse>(
        `${this.baseUrl}/switch-tenant`,
        { targetTenantId: tenantId },
        { withCredentials: true },
      )
      .pipe(
        tap((res) => this.setSession(res)),
        map(() => void 0),
      );
  }

  private setSession(res: AuthResponse): void {
    this._accessToken.set(res.accessToken);
  }

  private clearSession(): void {
    this._accessToken.set(null);
    this._tenants.set([]);
  }
}

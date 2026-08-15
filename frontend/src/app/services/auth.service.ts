import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, catchError, finalize, map, of, switchMap, tap, throwError, timeout, timer } from 'rxjs';
import { environment } from '../../environments/environment';

/** Corpo devolvido por POST /auth/refresh, /auth/select-tenant e /auth/switch-tenant. O refresh token NUNCA vem aqui — só no cookie. */
export interface AuthResponse {
  accessToken: string;
  accessTokenExpiresAt: string;
}

/**
 * [AEGIS-AUD-012] Corpo de POST /auth/login e /auth/federation/exchange, com desfecho EXPLÍCITO — o cliente
 * nunca é jogado num tenant escolhido em silêncio. `authenticated` traz o access token; `selection_required`
 * traz os ambientes e um ticket curto para concluir a escolha em /auth/select-tenant.
 */
export interface LoginResultResponse {
  status: 'authenticated' | 'selection_required';
  accessToken?: string;
  accessTokenExpiresAt?: string;
  selectionTicket?: string;
  selectionTicketExpiresAt?: string;
  tenants?: TenantOption[];
}

/** O que a tela de login recebe: ou a sessão já está pronta, ou uma escolha explícita de ambiente é exigida. */
export type LoginFlowResult =
  | { kind: 'authenticated' }
  | { kind: 'selection'; ticket: string; tenants: TenantOption[] };

/**
 * [AEGIS-AUD-007] Config PÚBLICA e sanitizada da federação (GET /auth/federation/config). Só
 * identificadores públicos — nunca segredo. Guia a UI do login e a inicialização do MSAL.
 */
export interface FederationConfig {
  enabled: boolean;
  mode: string;
  passwordLoginEnabled: boolean;
  authority: string | null;
  spaClientId: string | null;
  scope: string | null;
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
 * [AEGIS-AUD-009] Atraso do retry único do conflito benigno de rotação. Lê o Retry-After (segundos) da
 * resposta 409 e o converte em ms, com teto curto — o suficiente para a resposta vencedora atualizar o
 * cookie sucessor, sem travar a UI. Quando o Retry-After está ausente ou inacessível (ex.: bloqueado pelo
 * CORS entre portas distintas em dev), usa **1000 ms**, coerente com o `Retry-After: 1` do backend — um
 * fallback menor deixaria o retry correr antes de o vencedor gravar o cookie, reabrindo a corrida.
 */
function retryAfterMs(err: HttpErrorResponse): number {
  const seconds = Number(err.headers?.get('Retry-After'));
  const fromHeader = Number.isFinite(seconds) && seconds > 0 ? seconds * 1000 : 0;
  return Math.min(2000, Math.max(250, fromHeader || 1000));
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

  // ---- Projeções SEGURAS das claims (para a aba Geral e as guardas de UI) ----
  // Lidas do próprio access token em memória — o JWT NUNCA é persistido em storage (regra anti-XSS). A
  // visibilidade derivada daqui NÃO substitui a autorização do backend, que permanece a autoridade efetiva.

  /** E-mail global da pessoa autenticada (claim `email`). */
  readonly email = computed(() => readJwtClaim(this._accessToken(), 'email'));

  /** Nome de exibição no ambiente ativo (claim `name`). */
  readonly displayName = computed(() => readJwtClaim(this._accessToken(), 'name'));

  /** Id da PESSOA global (claim `account_id`) — estável através de ambientes. */
  readonly accountId = computed(() => readJwtClaim(this._accessToken(), 'account_id'));

  /**
   * Id do MEMBERSHIP ativo (claim `sub`) — a própria linha da pessoa NESTE tenant. Só UX: permite à lista de
   * usuários desabilitar auto-desativação/auto-rebaixamento na própria linha. O backend permanece a autoridade.
   */
  readonly activeMembershipId = computed(() => readJwtClaim(this._accessToken(), 'sub'));

  /** Autoridade GLOBAL de plataforma? (claim `platform_role=PlatformAdmin`, emitida só quando há). */
  readonly isPlatformAdmin = computed(() => readJwtClaim(this._accessToken(), 'platform_role') === 'PlatformAdmin');

  /** É administrador DESTE tenant? (claim `role`). Gate de visibilidade das abas administrativas. */
  readonly isTenantAdmin = computed(() => this.activeRole() === 'TenantAdmin');

  /** A conta tem credencial LOCAL (senha)? (claim booleana `has_local_credential`). Decide a seção "Minha conta". */
  readonly hasLocalCredential = computed(() => readJwtClaim(this._accessToken(), 'has_local_credential') === 'true');

  /** Leitura síncrona para o auth interceptor (não pode esperar um Observable). */
  get token(): string | null {
    return this._accessToken();
  }

  /**
   * [AEGIS-AUD-012] Autentica por credenciais. Envia a DICA do último tenant (revalidada no servidor) e
   * devolve o desfecho: sessão pronta, ou seleção explícita de ambiente. O cookie de refresh é setado pelo
   * servidor (withCredentials) apenas quando a sessão é emitida.
   */
  login(email: string, password: string): Observable<LoginFlowResult> {
    return this.http
      .post<LoginResultResponse>(
        `${this.baseUrl}/login`,
        { email, password, lastTenantId: this.readLastTenant() },
        { withCredentials: true },
      )
      .pipe(map((res) => this.handleLoginResult(res)));
  }

  /**
   * [AEGIS-AUD-012] Conclui a seleção inicial de ambiente com o ticket curto recebido no login. Em sucesso,
   * o servidor emite a sessão e seta o cookie de refresh; aqui só guardamos o access token em memória.
   */
  selectTenant(ticket: string, tenantId: string): Observable<void> {
    return this.http
      .post<AuthResponse>(
        `${this.baseUrl}/select-tenant`,
        { selectionTicket: ticket, targetTenantId: tenantId },
        { withCredentials: true },
      )
      .pipe(
        tap((res) => this.setSession(res.accessToken)),
        tap(() => this.getAvailableTenants().subscribe()),
        map(() => void 0),
      );
  }

  /** Aplica o desfecho do login/troca federada: emite a sessão, ou sinaliza a seleção pendente. */
  private handleLoginResult(res: LoginResultResponse): LoginFlowResult {
    if (res.status === 'selection_required' && res.selectionTicket) {
      return { kind: 'selection', ticket: res.selectionTicket, tenants: res.tenants ?? [] };
    }
    this.setSession(res.accessToken!);
    // Ambientes carregam EM PARALELO: encadeá-los atrasaria a navegação por uma chamada que só alimenta o seletor.
    this.getAvailableTenants().subscribe();
    return { kind: 'authenticated' };
  }

  /**
   * [AEGIS-AUD-007] Config pública da federação. NÃO trata falha como Local (fail-closed): um servidor
   * Federated indisponível NÃO pode virar "mostre o formulário de senha". O erro propaga e a tela de
   * login exibe um estado genérico com opção de recarregar. Em Local, o servidor responde `enabled=false`
   * e o formulário aparece normalmente.
   */
  federationConfig(): Observable<FederationConfig> {
    return this.http.get<FederationConfig>(`${this.baseUrl}/federation/config`);
  }

  /**
   * [AEGIS-AUD-007] Troca um access token do Entra JÁ obtido pelo MSAL por uma sessão local. O token
   * externo vai SÓ neste header, para o endpoint de troca — nunca é armazenado. O cookie de refresh
   * (HttpOnly) é setado pelo servidor; a partir daí o fluxo local segue idêntico ao login por senha.
   */
  exchangeFederated(externalToken: string): Observable<LoginFlowResult> {
    return this.http
      .post<LoginResultResponse>(
        `${this.baseUrl}/federation/exchange`,
        { lastTenantId: this.readLastTenant() },
        { withCredentials: true, headers: { Authorization: `Bearer ${externalToken}` } },
      )
      .pipe(map((res) => this.handleLoginResult(res)));
  }

  /**
   * Troca o refresh token do cookie por um novo par (RTR). É a chamada que o refresh interceptor
   * dispara uma única vez sob 401. Retorna o novo access token já persistido em memória.
   *
   * [AEGIS-AUD-009] Trata o conflito benigno de rotação (HTTP 409): quando outra aba/janela venceu a
   * rotação com o MESMO cookie, o servidor não pode reentregar o sucessor (só o hash é persistido) e
   * responde 409 + Retry-After. Aqui esperamos esse atraso curto e tentamos UMA única vez — o cookie já
   * terá sido atualizado pela resposta vencedora. Sem loop, sem armazenar token (o refresh continua só no
   * cookie HttpOnly). Persistindo o conflito, o erro propaga e o interceptor faz o logout normal.
   */
  refresh(): Observable<string> {
    return this.postRefresh().pipe(
      catchError((err) => {
        if (err instanceof HttpErrorResponse && err.status === 409) {
          return timer(retryAfterMs(err)).pipe(switchMap(() => this.postRefresh()));
        }
        return throwError(() => err);
      }),
    );
  }

  private postRefresh(): Observable<string> {
    return this.http
      .post<AuthResponse>(`${this.baseUrl}/refresh`, {}, { withCredentials: true })
      .pipe(
        tap((res) => this.setSession(res.accessToken)),
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

  // ---- Encerramento de sessão ----
  private readonly _loggingOut = signal(false);

  /** Verdadeiro enquanto o logout está em andamento — desabilita a ação e barra cliques repetidos. */
  readonly loggingOut = this._loggingOut.asReadonly();

  /**
   * Logout iniciado pelo usuário. Revoga o refresh no servidor e SÓ ENTÃO encerra o cliente: aguardar a
   * resposta garante que o cookie HttpOnly já foi invalidado antes de sairmos — assim um F5 logo em
   * seguida não reidrata a sessão. Em falha de rede (ou timeout curto) encerra localmente mesmo assim
   * (fail-safe: o access token em memória é descartado e deixa de ser utilizável). Barra cliques repetidos
   * com um guard em memória. O endpoint é anônimo, responde 204 e está na lista bearerless — não passa
   * pelo caminho de auto-refresh, então não há loop com o refresh interceptor.
   */
  logout(): void {
    if (this._loggingOut()) return;
    this._loggingOut.set(true);
    this.http
      .post(`${this.baseUrl}/logout`, {}, { withCredentials: true })
      .pipe(
        timeout(8000),
        // Encerra o cliente em QUALQUER desfecho (204, erro de rede ou timeout), sempre DEPOIS de a
        // revogação retornar — nunca antes. O access token em memória é descartado aqui.
        finalize(() => {
          this.clearSession();
          this._loggingOut.set(false);
          this.router.navigate(['/login']);
        }),
      )
      .subscribe({ next: () => {}, error: () => {} });
  }

  /** Logout forçado pelo refresh interceptor quando a renovação falha (refresh inválido/breach). */
  forceLogout(): void {
    this.clearSession();
    this.router.navigate(['/login']);
  }

  /**
   * [Configurações · Minha conta] Troca a PRÓPRIA senha. O endpoint é `[Authorize]` (recebe Bearer + X-Tenant
   * pelo interceptor) e pertence à família de auth (isento do cancelamento no switch). Em sucesso (204), o
   * servidor já revogou TODAS as sessões da identidade em todos os tenants e limpou o cookie de refresh —
   * então o chamador deve encerrar a sessão local e pedir novo login. Erros (senha atual incorreta, nova
   * fraca, conta sem senha local) chegam como `HttpErrorResponse` para a UI traduzir. A senha nunca é
   * guardada nem logada.
   */
  changePassword(currentPassword: string, newPassword: string): Observable<void> {
    return this.http
      .post(`${this.baseUrl}/password`, { currentPassword, newPassword }, { withCredentials: true })
      .pipe(
        map(() => void 0),
        catchError((err: HttpErrorResponse) => throwError(() => new Error(this.describePasswordError(err)))),
      );
  }

  /** Mensagem sanitizada da troca de senha: prefere o `title` do servidor; trata 429 e falha de rede. */
  private describePasswordError(err: HttpErrorResponse): string {
    const title = typeof err.error === 'object' && err.error?.title ? String(err.error.title) : null;
    if (title) return title;
    switch (err.status) {
      case 0:
        return 'API inacessível. Verifique se o servidor está no ar.';
      case 401:
        return 'Sessão expirada. Entre novamente.';
      case 429:
        return 'Muitas tentativas. Aguarde um minuto e tente novamente.';
      default:
        return 'Não foi possível trocar a senha. Tente novamente.';
    }
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
        tap((res) => this.setSession(res.accessToken)),
        map(() => void 0),
      );
  }

  private setSession(accessToken: string): void {
    this._accessToken.set(accessToken);
    // [AEGIS-AUD-012] Lembra o ambiente ativo como DICA do próximo login (revalidada no servidor). É só o
    // GUID do tenant — não um segredo; o access token continua ESTRITAMENTE em memória (regra anti-XSS).
    const tenant = readJwtClaim(accessToken, 'tenant_id');
    if (tenant) this.writeLastTenant(tenant);
  }

  private clearSession(): void {
    this._accessToken.set(null);
    this._tenants.set([]);
  }

  // ---- Dica do último tenant (AUD-012): persistida entre sessões, nunca confiada sem revalidação ----

  private static readonly LAST_TENANT_KEY = 'aegis_last_tenant';

  private readLastTenant(): string | null {
    try {
      return localStorage.getItem(AuthService.LAST_TENANT_KEY);
    } catch {
      return null; // storage indisponível (modo privado/SSR): a dica é best-effort.
    }
  }

  private writeLastTenant(tenantId: string): void {
    try {
      localStorage.setItem(AuthService.LAST_TENANT_KEY, tenantId);
    } catch {
      /* storage indisponível: seguimos sem a dica — o login apenas pedirá seleção. */
    }
  }
}

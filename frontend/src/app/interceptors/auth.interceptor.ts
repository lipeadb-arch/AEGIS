import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { takeUntil } from 'rxjs';
import { AuthService } from '../services/auth.service';
import { TenantContextService } from '../services/tenant-context.service';
import { environment } from '../../environments/environment';
import { isAuthEndpoint, isBearerlessAuthEndpoint } from './api-endpoints';

/**
 * Interceptor de saída. Para toda chamada à nossa API:
 *  - anexa o header X-Tenant derivado da claim `tenant_id` do PRÓPRIO access token (§22): token e
 *    header saem da mesma fonte, então não podem divergir — e a troca de ambiente pelo HUD passa a
 *    valer para toda a API sem nenhum estado paralelo a sincronizar;
 *  - anexa Authorization: Bearer <token> local quando há token em memória, EXCETO nos endpoints com auth
 *    própria (login/refresh/logout/select-tenant/federation) — o switch-tenant, por ser [Authorize],
 *    RECEBE o Bearer local (é a troca de uma sessão já autenticada);
 *  - liga withCredentials para o cookie HttpOnly de refresh acompanhar as chamadas de /auth;
 *  - [AEGIS-AUD-030] CANCELA a requisição se o tenant ativo trocar antes de ela completar: uma resposta
 *    iniciada no tenant anterior nunca chega ao componente para repovoar a UI (cancelamento real via
 *    `takeUntil(switched$)`). A família de auth é isenta — ela precisa completar (é ela que troca o tenant).
 *
 * É o interceptor INTERNO da cadeia: quando o refresh interceptor refaz a requisição, ela repassa
 * por aqui e recebe o Bearer já renovado, sem duplicação de lógica.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  // Não toca em requisições que não sejam para a nossa API (assets estáticos, terceiros).
  if (!req.url.startsWith(environment.apiBase)) {
    return next(req);
  }

  const auth = inject(AuthService);
  const tenantContext = inject(TenantContextService);
  const headers: Record<string, string> = {};

  // X-Tenant derivado da claim `tenant_id` do PRÓPRIO access token — não mais de environment.tenantId
  // (fixo, incompatível com a troca de ambiente pelo HUD). Como token e header saem da mesma fonte,
  // eles não têm como divergir, e o TenantConsistencyMiddleware nunca é acionado por engano. Sem
  // sessão (login/refresh) o header simplesmente não vai: o servidor resolve o tenant sozinho.
  const activeTenant = auth.activeTenantId();
  if (activeTenant) {
    headers['X-Tenant'] = activeTenant;
  }

  // switch-tenant e as rotas normais recebem o Bearer LOCAL; login/refresh/logout/select-tenant/federation
  // se autenticam por conta própria (senha, cookie, ticket ou Bearer externo do Entra) e NÃO o recebem.
  const token = auth.token;
  if (token && !isBearerlessAuthEndpoint(req.url)) {
    headers['Authorization'] = `Bearer ${token}`;
  }

  const forwarded = next(req.clone({ setHeaders: headers, withCredentials: true }));

  // Leituras tenant-scoped são abortadas na troca de ambiente; a FAMÍLIA de auth — inclusive o próprio
  // switch-tenant — precisa completar (do contrário a troca cancelaria a si mesma).
  return isAuthEndpoint(req.url) ? forwarded : forwarded.pipe(takeUntil(tenantContext.switched$));
};

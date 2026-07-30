import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { takeUntil } from 'rxjs';
import { AuthService } from '../services/auth.service';
import { TenantContextService } from '../services/tenant-context.service';
import { environment } from '../../environments/environment';
import { isAuthEndpoint } from './api-endpoints';

/**
 * Interceptor de saída. Para toda chamada à nossa API:
 *  - anexa o header X-Tenant derivado da claim `tenant_id` do PRÓPRIO access token (§22): token e
 *    header saem da mesma fonte, então não podem divergir — e a troca de ambiente pelo HUD passa a
 *    valer para toda a API sem nenhum estado paralelo a sincronizar;
 *  - anexa Authorization: Bearer <token> quando há token em memória e a rota não é de auth
 *    (login/refresh/logout usam cookie, não Bearer);
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

  const isAuth = isAuthEndpoint(req.url);
  const token = auth.token;
  if (token && !isAuth) {
    headers['Authorization'] = `Bearer ${token}`;
  }

  const forwarded = next(req.clone({ setHeaders: headers, withCredentials: true }));

  // Leituras tenant-scoped são abortadas na troca de ambiente; a família de auth precisa completar.
  return isAuth ? forwarded : forwarded.pipe(takeUntil(tenantContext.switched$));
};

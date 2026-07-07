import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { AuthService } from '../services/auth.service';
import { environment } from '../../environments/environment';
import { isAuthEndpoint } from './api-endpoints';

/**
 * Interceptor de saída. Para toda chamada à nossa API:
 *  - anexa o header X-Tenant (tripwire de segurança — fonte confiável = environment.tenantId; se o
 *    estado for adulterado para divergir do tenant do token, o TenantConsistencyMiddleware barra 403);
 *  - anexa Authorization: Bearer <token> quando há token em memória e a rota não é de auth
 *    (login/refresh/logout usam cookie, não Bearer);
 *  - liga withCredentials para o cookie HttpOnly de refresh acompanhar as chamadas de /auth.
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
  const headers: Record<string, string> = { 'X-Tenant': environment.tenantId };

  const token = auth.token;
  if (token && !isAuthEndpoint(req.url)) {
    headers['Authorization'] = `Bearer ${token}`;
  }

  return next(req.clone({ setHeaders: headers, withCredentials: true }));
};

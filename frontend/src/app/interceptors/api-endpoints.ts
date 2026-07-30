import { HttpContextToken } from '@angular/common/http';

/**
 * Pertence à FAMÍLIA de gestão de sessão (`/api/v1/auth/*`)? Usado só para a isenção de CANCELAMENTO no
 * switch: nenhuma requisição de auth — INCLUSIVE o próprio `switch-tenant` — pode ser abortada pelo sinal
 * que encerra as leituras do tenant antigo, senão a troca cancelaria a si mesma. NÃO decide Bearer/refresh
 * (isso é papel de {@link isBearerlessAuthEndpoint}, que trata cada endpoint pelo seu método de auth).
 */
export function isAuthEndpoint(url: string): boolean {
  return url.includes('/api/v1/auth/');
}

/**
 * Endpoints da família de auth que NÃO usam o access token LOCAL do AEGIS — cada um se autentica pelo seu
 * próprio método: senha (login), cookie de refresh HttpOnly (refresh/logout), ticket de seleção
 * (select-tenant), Bearer EXTERNO do Entra (federation/exchange), ou nada (federation/config). Para estes:
 *  - o authInterceptor NÃO anexa o Bearer local (não sobrescreve o Bearer externo do exchange); e
 *  - o refreshInterceptor NÃO dispara auto-refresh sob 401 (evita o loop 401→refresh→401 e não faz sentido).
 *
 * ⚠️ `switch-tenant` NÃO está aqui de propósito: ele é `[Authorize]` e exige o Bearer LOCAL atual (é a troca
 * de ambiente de uma sessão JÁ autenticada). Logo recebe o Bearer + o X-Tenant do token atual e pode seguir
 * o fluxo normal de refresh/retry sob 401, sem loop (a requisição refeita é marcada como já re-tentada).
 */
const BEARERLESS_AUTH_PATHS = [
  '/api/v1/auth/login',
  '/api/v1/auth/refresh',
  '/api/v1/auth/logout',
  '/api/v1/auth/select-tenant',
  '/api/v1/auth/federation/config',
  '/api/v1/auth/federation/exchange',
];

export function isBearerlessAuthEndpoint(url: string): boolean {
  return BEARERLESS_AUTH_PATHS.some((path) => url.includes(path));
}

/**
 * Marca uma requisição que já foi refeita depois de um refresh bem-sucedido. Se ela tomar 401 de
 * novo, o refresh interceptor a deixa passar em vez de renovar outra vez — corta o loop
 * 401 → refresh → 401.
 */
export const ALREADY_RETRIED = new HttpContextToken<boolean>(() => false);

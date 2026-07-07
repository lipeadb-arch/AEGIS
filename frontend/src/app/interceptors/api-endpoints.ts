import { HttpContextToken } from '@angular/common/http';

/**
 * Endpoints de gestão de sessão (login/refresh/logout). Não recebem Bearer (autenticam por
 * credencial/cookie) e não devem disparar o auto-refresh sob 401 — senão o próprio /refresh
 * retornando 401 entraria em loop.
 */
export function isAuthEndpoint(url: string): boolean {
  return url.includes('/api/v1/auth/');
}

/**
 * Marca uma requisição que já foi refeita depois de um refresh bem-sucedido. Se ela tomar 401 de
 * novo, o refresh interceptor a deixa passar em vez de renovar outra vez — corta o loop
 * 401 → refresh → 401.
 */
export const ALREADY_RETRIED = new HttpContextToken<boolean>(() => false);

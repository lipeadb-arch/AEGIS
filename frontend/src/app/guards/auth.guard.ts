import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from '../services/auth.service';

/**
 * Protege as rotas da aplicação: sem sessão em memória, redireciona para /login.
 * Como o silent refresh roda no APP_INITIALIZER (antes da 1ª navegação), o estado de auth já está
 * resolvido quando o guard executa.
 */
export const authGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  return auth.isAuthenticated() ? true : router.createUrlTree(['/login']);
};

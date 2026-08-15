import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from '../services/auth.service';

/**
 * Protege as rotas ADMINISTRATIVAS de Configurações (Usuários/Integrações): exige o papel TenantAdmin no
 * ambiente ativo. Encadeado APÓS o authGuard (a sessão já está resolvida pelo silent refresh no
 * APP_INITIALIZER). Acesso manual sem autoridade → redireciona de forma compreensível para /settings/general,
 * a aba disponível a qualquer autenticado.
 *
 * ⚠️ Visibilidade de UI NÃO substitui a autorização do backend, que permanece a autoridade efetiva (as rotas
 * respondem 403 a quem não é TenantAdmin).
 */
export const tenantAdminGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  return auth.isTenantAdmin() ? true : router.createUrlTree(['/settings/general']);
};

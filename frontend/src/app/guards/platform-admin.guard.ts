import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from '../services/auth.service';

/**
 * [AEGIS-MVP-ADMIN-LIFECYCLE-01] Protege a aba de ADMINISTRAÇÃO DE AMBIENTES (tenants): exige autoridade
 * GLOBAL de plataforma (claim `platform_role = PlatformAdmin`), NÃO um papel de tenant. Encadeado após o
 * authGuard. Acesso sem autoridade → redireciona para /settings/general (a aba disponível a qualquer sessão).
 *
 * ⚠️ Visibilidade de UI NÃO substitui a autorização do backend: a superfície de plataforma
 * (api/v1/platform/tenants) permanece protegida pela policy e responde 403 a quem não é PlatformAdmin.
 */
export const platformAdminGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  return auth.isPlatformAdmin() ? true : router.createUrlTree(['/settings/general']);
};

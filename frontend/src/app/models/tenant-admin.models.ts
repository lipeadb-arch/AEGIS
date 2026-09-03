/**
 * [AEGIS-MVP-ADMIN-LIFECYCLE-01] Contratos e lógica PURA da administração de tenants (PlatformAdmin).
 * Espelham os DTOs de `AegisScore.Api/Contracts/Dtos.cs`. O `status` volta como NOME do enum
 * ("Onboarding"/"Active"/"Suspended"), mesmo idioma dos demais DTOs de leitura.
 *
 * As funções abaixo são puras (sem DOM) para serem exercitadas pelos testes de lógica do frontend.
 */

/** Estado do tenant como vem nas RESPOSTAS (nome do enum). */
export type TenantStatusName = 'Onboarding' | 'Active' | 'Suspended';

/** Um tenant no catálogo administrativo da plataforma. */
export interface TenantAdmin {
  id: string;
  name: string;
  slug: string;
  status: TenantStatusName;
  createdAt: string;
  updatedAt: string | null;
}

/** Corpo da renomeação (PUT /platform/tenants/{id}). SÓ o nome — o slug é imutável e não trafega. */
export interface RenameTenantRequest {
  name: string;
}

/** Rótulo amigável do estado do tenant. */
export function tenantStatusLabel(status: string): string {
  switch (status) {
    case 'Active':
      return 'Ativo';
    case 'Onboarding':
      return 'Em implantação';
    case 'Suspended':
      return 'Suspenso';
    default:
      return status;
  }
}

/** Tom visual do estado (reusa a paleta ok/warn/idle). */
export function tenantStatusTone(status: string): 'ok' | 'warn' | 'idle' {
  switch (status) {
    case 'Active':
      return 'ok';
    case 'Onboarding':
      return 'idle';
    case 'Suspended':
      return 'warn';
    default:
      return 'idle';
  }
}

/** Suspender só faz sentido para um tenant que ainda NÃO está suspenso. */
export function canSuspend(t: TenantAdmin): boolean {
  return t.status !== 'Suspended';
}

/** Reativar só faz sentido para um tenant suspenso. */
export function canReactivate(t: TenantAdmin): boolean {
  return t.status === 'Suspended';
}

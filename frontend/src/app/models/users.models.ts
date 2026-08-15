/**
 * Contratos da aba "Usuários e acessos". Espelham os DTOs de `AegisScore.Api/Contracts/Dtos.cs`.
 *
 * ⚠️ Assimetria de enum na fronteira JSON (igual à tela de Integrações): a API NÃO tem um conversor
 * global de enum-string, então nas REQUISIÇÕES o papel viaja como NÚMERO (0/1/2). Nas RESPOSTAS ele volta
 * como NOME ("Analyst"/"Manager"/"TenantAdmin"), porque o servidor o projeta com `.ToString()`.
 */

/** Papel tenant-scoped como vem nas RESPOSTAS (nome do enum). */
export type TenantRoleName = 'Analyst' | 'Manager' | 'TenantAdmin';

/** Valor NUMÉRICO do papel para as REQUISIÇÕES — a API desserializa enum de número (0/1/2). */
export type TenantRoleValue = 0 | 1 | 2;

/** Mapa nome → valor numérico (o que o POST/PUT envia). */
export const TENANT_ROLE_VALUE: Record<TenantRoleName, TenantRoleValue> = {
  Analyst: 0,
  Manager: 1,
  TenantAdmin: 2,
};

/** Rótulos amigáveis (sem termos internos como "membership"): o que o usuário lê. */
export const ROLE_LABELS: Record<TenantRoleName, string> = {
  Analyst: 'Analista',
  Manager: 'Gestor',
  TenantAdmin: 'Administrador',
};

/** Papéis atribuíveis pela interface, na ordem de menor → maior privilégio. */
export const ASSIGNABLE_ROLES: TenantRoleName[] = ['Analyst', 'Manager', 'TenantAdmin'];

/** Um acesso na listagem (GET /api/v1/users). Sem tenantId (implícito) e sem hash. */
export interface TenantUser {
  id: string;
  email: string;
  displayName: string;
  role: TenantRoleName;
  isActive: boolean;
  hasLocalCredential: boolean;
  createdAt: string;
  lastLoginAt: string | null;
}

/** Corpo do onboarding (POST /api/v1/platform/tenant-users). `role` como NÚMERO. */
export interface OnboardTenantUserRequest {
  email: string;
  displayName: string;
  role: TenantRoleValue;
  initialPassword?: string | null;
}

/** Desfecho do onboarding. `identityExisted` deixa explícito que a pessoa já existia (senha preservada). */
export interface OnboardTenantUserResponse {
  user: TenantUser;
  outcome: 'identity_created' | 'access_granted' | 'access_updated';
  identityExisted: boolean;
}

/** Corpo da edição (PUT /api/v1/users/{id}). Campos ausentes NÃO são alterados. `role` como NÚMERO. */
export interface UpdateMembershipRequest {
  displayName?: string;
  role?: TenantRoleValue;
}

/** Tradução do papel (nome do enum) para o rótulo amigável, com fallback seguro. */
export function roleLabel(role: string): string {
  return ROLE_LABELS[role as TenantRoleName] ?? role;
}

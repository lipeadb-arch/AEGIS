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
  /**
   * Id da PESSOA global (a `IdentityAccount`), distinto do membership `id` (tenant-scoped). É a chave da rota
   * GLOBAL de redefinição administrativa de senha. Expô-lo NÃO concede autoridade — a rota continua protegida
   * pela policy de plataforma no backend.
   */
  identityAccountId: string;
  email: string;
  displayName: string;
  role: TenantRoleName;
  isActive: boolean;
  hasLocalCredential: boolean;
  createdAt: string;
  lastLoginAt: string | null;
}

/** Piso/teto de comprimento da senha — MESMA régua do backend (`PasswordPolicy`, NIST SP 800-63B). */
export const PASSWORD_MIN_LENGTH = 12;
export const PASSWORD_MAX_LENGTH = 128;

/** Corpo da redefinição administrativa (POST /platform/identities/{accountId}/password). Só a nova senha. */
export interface AdminResetPasswordRequest {
  newPassword: string;
}

/**
 * Predicado PURO da visibilidade do botão "Redefinir senha" (redefinição administrativa). Extraído aqui para
 * ser a autoridade única da regra — o template só o consome, e o build o verifica por tipos. A UI é apenas
 * conveniência: o backend permanece a autoridade efetiva (policy de plataforma + validações).
 *
 * Mostrar a ação SOMENTE quando as três condições valem juntas:
 *  - quem vê é `PlatformAdmin` (autoridade global);
 *  - o alvo tem credencial LOCAL (uma conta federated-only não tem senha a redefinir);
 *  - o alvo NÃO é a própria conta (para a própria senha existe a troca normal, que exige a senha atual).
 */
export function canResetLocalPassword(input: {
  viewerIsPlatformAdmin: boolean;
  targetHasLocalCredential: boolean;
  targetIsSelf: boolean;
}): boolean {
  return input.viewerIsPlatformAdmin && input.targetHasLocalCredential && !input.targetIsSelf;
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

/**
 * [AEGIS-MVP-ADMIN-LIFECYCLE-01] Rótulo do ESTADO DE ACESSO na linguagem operacional da tela: o usuário
 * procura "excluir", mas a operação segura é remover/desativar o ACESSO ao ambiente — a identidade global e o
 * histórico são preservados. Função pura e testável. `true` (ativo) ⇒ "Ativo"; `false` ⇒ "Acesso removido"
 * (nunca "Inativo"/"Excluído", que sugeririam apagamento).
 */
export function accessStateLabel(isActive: boolean): string {
  return isActive ? 'Ativo' : 'Acesso removido';
}

/** Tom visual do estado de acesso (reusa a paleta on/off da lista). */
export function accessStateTone(isActive: boolean): 'on' | 'off' {
  return isActive ? 'on' : 'off';
}

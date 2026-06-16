/**
 * Configuração de runtime do dashboard.
 * - apiBase:  URL base da API S.T.A.R.S.
 * - tenantId: GUID do cliente, enviado no header `X-Tenant` (obtido em POST /api/v1/tenants).
 *
 * Substitui os antigos VITE_API_BASE / VITE_TENANT_ID. Sem um tenantId válido o
 * dashboard cai automaticamente nos dados de exemplo.
 */
export const environment = {
  production: true,
  apiBase: 'http://localhost:5080',
  tenantId: '',
};

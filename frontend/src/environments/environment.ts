/**
 * Configuração de runtime do dashboard.
 * - apiBase:  URL base da API Aegis Score.
 * - tenantId: GUID do cliente, enviado no header `X-Tenant` (obtido em POST /api/v1/tenants).
 *
 * Substitui os antigos VITE_API_BASE / VITE_TENANT_ID. Sem um tenantId válido o
 * dashboard cai automaticamente nos dados de exemplo.
 */
export const environment = {
  production: true,
  apiBase: 'http://localhost:5100',
  // Tenant de demonstração criado por POST /api/v1/dev/seed-demo (ambiente Development).
  // Troque pelo GUID real (POST /api/v1/tenants) quando for usar dados de produção.
  tenantId: 'aa000000-0000-0000-0000-000000000001',
  // Ativo-raiz do raio de explosão no seed demo (AD Domain Controller) — usado quando o pedido de
  // topologia no chat não cita um UUID de ativo. Espelha DevController.DemoRootAssetId.
  blastRadiusDemoAssetId: 'bb000000-0000-0000-0000-000000000001',
};

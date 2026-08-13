/**
 * Configuração de runtime do dashboard — ambiente de DESENVOLVIMENTO (padrão do `ng serve`).
 * - apiBase: URL base da API Aegis Score. Em dev o SPA (ng serve :5173) e a API (:5100) vivem em origens
 *   distintas, então aponta para a porta local da API. Em produção (mesmo domínio) este arquivo é
 *   substituído por `environment.production.ts` (apiBase relativo) via `fileReplacements` do angular.json.
 *
 * ⚠️ `tenantId` FOI REMOVIDO (§22). O ambiente ativo deixou de ser configuração de build e passou a ser
 * derivado da claim `tenant_id` do próprio access token — é o que permite o analista alternar entre
 * clientes pelo seletor do HUD. Como token e header `X-Tenant` saem da MESMA fonte, eles não têm como
 * divergir e o TenantConsistencyMiddleware nunca é acionado por engano. Ver auth.interceptor.ts.
 */
export const environment = {
  production: false,
  apiBase: 'http://localhost:5100',
  // Ativo-raiz do raio de explosão no seed demo (AD Domain Controller) — usado quando o pedido de
  // topologia no chat não cita um UUID de ativo. Espelha DevController.DemoRootAssetId.
  blastRadiusDemoAssetId: 'bb000000-0000-0000-0000-000000000001',
};

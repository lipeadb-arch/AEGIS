/**
 * Configuração de runtime do dashboard — ambiente de PRODUÇÃO (homologação em container).
 *
 * O SPA é servido pela PRÓPRIA API, no MESMO domínio (same-origin). Por isso `apiBase` é RELATIVO
 * (string vazia): as chamadas ficam `/api/v1/...` e acompanham automaticamente o host/porta/esquema
 * públicos do proxy da hospedagem — sem `localhost` e sem URL absoluta cravada no build. O
 * `auth.interceptor.ts` continua correto: com same-origin, toda chamada à API recebe X-Tenant/Bearer.
 *
 * Ver `environment.ts` (desenvolvimento) e o `fileReplacements` do angular.json que troca um pelo outro
 * na configuração de produção.
 */
export const environment = {
  production: true,
  apiBase: '',
  // Ativo-raiz do raio de explosão no seed demo (AD Domain Controller) — usado quando o pedido de
  // topologia no chat não cita um UUID de ativo. Espelha DevController.DemoRootAssetId.
  blastRadiusDemoAssetId: 'bb000000-0000-0000-0000-000000000001',
};

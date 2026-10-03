/**
 * [AEGIS-KNIGHT-CLOSURE-01] Testes de LÓGICA PURA do fechamento do KNIGHT na interface.
 *
 * Fixam o que a tela não pode errar:
 *   (1) o recorte da cobertura separa o que aceita resultado manual (sem avaliação automatizada) do que é avaliado;
 *   (2) o formulário de resultado manual exige justificativa, responsável e evidência — retirar dispensa a evidência;
 *   (3) a limitação por capacidade traz o requisito do servidor e a orientação específica (Databricks desligado ou
 *       recusado, versão preview fora do contrato);
 *   (4) a opção do Databricks só vai no pedido com o AEGIS KNIGHT, e a matriz de permissões não repete permissão.
 */
import {
  KnightAssessment,
  KnightReferenceControlStatus,
  RecordKnightManualResultRequest,
  capabilityLabel,
  filterReferenceControls,
  limitationViews,
  manualResultBadge,
  manualResultProblems,
  referenceLabel,
} from '../src/app/models/knight.models';
import { MICROSOFT_HUB_SERVICES, buildMicrosoftHubRequest } from '../src/app/models/connector.models';

let failures = 0;
let count = 0;
function test(name: string, fn: () => void): void {
  count++;
  try {
    fn();
    console.log(`  ok - ${name}`);
  } catch (e) {
    failures++;
    console.log(`  FAIL - ${name}\n      ${(e as Error).message}`);
  }
}
function eq<T>(actual: T, expected: T, msg: string): void {
  if (actual !== expected) throw new Error(`${msg}: esperado ${String(expected)}, obtido ${String(actual)}`);
}
function ok(condition: boolean, msg: string): void {
  if (!condition) throw new Error(msg);
}

function ref(over: Partial<KnightReferenceControlStatus>): KnightReferenceControlStatus {
  return {
    key: 'CIS-M365-7.0.0:1.1.1', framework: 'CIS Microsoft 365', version: '7.0.0', section: '1.1.1', variant: null,
    service: 'EntraId', serviceLabel: 'Microsoft Entra ID', platform: 'Microsoft Entra ID', severity: 'High',
    title: 'Contas administrativas só na nuvem', disposition: 'Implemented', dispositionLabel: 'Integral',
    indicatorIds: ['AK-ENTRA-001'], note: null, previewApis: [], manualEligible: false, manualResult: null, ...over,
  };
}

const CONTROLS = [
  ref({}),
  ref({ key: 'CIS-M365-7.0.0:5.2.3.6', section: '5.2.3.6', previewApis: ['Microsoft Graph beta, /policies/authenticationMethodsPolicy'], indicatorIds: ['AK-ENTRA-072'] }),
  ref({ key: 'CIS-M365-7.0.0:5.3.1', section: '5.3.1', disposition: 'ManualOnly', dispositionLabel: 'Verificação manual', indicatorIds: [], manualEligible: true }),
  ref({
    key: 'CIS-AZ-6.0.0:2.1.12', framework: 'CIS Microsoft Azure', version: '6.0.0', section: '2.1.12', platform: 'Microsoft Azure',
    disposition: 'ApiLimitation', dispositionLabel: 'Sem método na API', indicatorIds: [], manualEligible: true,
    manualResult: {
      id: 'm1', referenceKey: 'CIS-AZ-6.0.0:2.1.12', result: 'Compliant', resultLabel: 'Conforme (verificação manual)', justification: 'x'.repeat(12),
      responsibleName: 'Responsável Demo', evidenceReference: 'CHG-1', evidenceDocumentId: null, evidenceDocumentTitle: null,
      evidenceDocumentSha256: null, validUntil: '2026-01-01', expired: true, referenceDisposition: 'ApiLimitation',
      catalogVersion: 'ak-knight-v10', recordedByName: 'Gestor Demo', recordedAt: '2025-12-01T00:00:00Z',
    },
  }),
];

test('o recorte padrão mostra só o que aceita resultado manual — nunca um controle avaliado', () => {
  const manual = filterReferenceControls(CONTROLS, { view: 'manual', platform: '', q: '' });
  eq(manual.length, 2, 'as duas referências sem avaliação automatizada');
  ok(manual.every((c) => c.manualEligible), 'só elegíveis');
  eq(filterReferenceControls(CONTROLS, { view: 'automated', platform: '', q: '' }).length, 2, 'avaliadas = integral + parcial');
  eq(filterReferenceControls(CONTROLS, { view: 'preview', platform: '', q: '' })[0].key, 'CIS-M365-7.0.0:5.2.3.6', 'só a que usa preview');
  eq(filterReferenceControls(CONTROLS, { view: 'withManual', platform: '', q: '' }).length, 1, 'com resultado manual');
  eq(filterReferenceControls(CONTROLS, { view: 'all', platform: 'Microsoft Azure', q: '' }).length, 1, 'filtro por plataforma');
  eq(filterReferenceControls(CONTROLS, { view: 'all', platform: '', q: 'ak-entra-072' }).length, 1, 'pesquisa pelo controle KNIGHT');
});

test('o rótulo cita benchmark, versão e seção; o resultado vencido é dito', () => {
  eq(referenceLabel(CONTROLS[2]), 'CIS Microsoft 365 7.0.0 · 5.3.1', 'rótulo da referência');
  eq(manualResultBadge(CONTROLS[3].manualResult), 'Conforme (verificação manual) — vencido', 'vencido nunca parece vigente');
  eq(manualResultBadge(null), null, 'sem resultado, sem selo');
});

test('o formulário exige justificativa, responsável e evidência; retirar dispensa a evidência', () => {
  const base: RecordKnightManualResultRequest = {
    referenceKey: 'k', result: 'Compliant', justification: 'Verificado no portal em 03/10.', responsibleName: 'Responsável Demo',
    evidenceReference: 'CHG-1', evidenceDocumentId: null, validUntil: null,
  };
  eq(manualResultProblems(base, '2026-10-03').length, 0, 'completo');
  eq(manualResultProblems({ ...base, justification: 'curta' }, '2026-10-03').length, 1, 'justificativa curta');
  eq(manualResultProblems({ ...base, responsibleName: ' ' }, '2026-10-03').length, 1, 'sem responsável');
  eq(manualResultProblems({ ...base, evidenceReference: null }, '2026-10-03').length, 1, 'sem evidência');
  eq(manualResultProblems({ ...base, evidenceReference: null, evidenceDocumentId: 'doc-1' }, '2026-10-03').length, 0, 'documento basta');
  eq(manualResultProblems({ ...base, validUntil: '2026-10-02' }, '2026-10-03').length, 1, 'validade no passado');
  eq(manualResultProblems({ ...base, result: 'Withdrawn', evidenceReference: null }, '2026-10-03').length, 0, 'retirar sem evidência');
});

test('a limitação traz o requisito do servidor e a orientação específica', () => {
  const a = {
    indicators: [
      { indicatorId: 'AK-AZ-DBR-008', status: 'NotEvaluated', presentation: { requiredCapabilities: ['AzureDatabricksWorkspaceApi'] } },
      { indicatorId: 'AK-AZ-MDC-021', status: 'NotEvaluated', presentation: { requiredCapabilities: ['AzureSecurityContacts'] } },
    ],
    capabilities: [
      { capability: 'AzureDatabricksWorkspaceApi', outcome: 'NotAttempted', detail: 'desligada', requirement: 'Aplicação adicionada ao workspace', previewApi: null },
      { capability: 'AzureSecurityContacts', outcome: 'Error', detail: 'fora do contrato', requirement: 'Papel Leitor', previewApi: 'api-version 2023-12-01-preview' },
      { capability: 'AzureStorage', outcome: 'Collected', detail: null },
    ],
  } as unknown as KnightAssessment;
  const views = limitationViews(a);
  eq(views.length, 2, 'só as não coletadas');
  const dbx = views.find((v) => v.capability === 'AzureDatabricksWorkspaceApi')!;
  ok(dbx.guidance.includes('Integrações'), 'Databricks desligado: habilitar em Integrações');
  eq(dbx.requirement, 'Aplicação adicionada ao workspace', 'requisito do servidor');
  eq(dbx.affectedControls.join(','), 'AK-AZ-DBR-008', 'controle prejudicado');
  const contacts = views.find((v) => v.capability === 'AzureSecurityContacts')!;
  ok(contacts.guidance.includes('versão preview'), 'preview fora do contrato: nada a conceder');
  eq(contacts.previewApi, 'api-version 2023-12-01-preview', 'versão preview identificada');
  ok(!capabilityLabel('PerUserMfaStates').startsWith('PerUser'), 'rótulo legível da capacidade nova');
});

test('a leitura do Databricks só acompanha o AEGIS KNIGHT, e a matriz não repete permissão', () => {
  const creds = { tenantId: 't', clientId: 'c', clientSecret: 's' };
  const com = buildMicrosoftHubRequest(creds, [{ key: 'IdentityPosture', syncIntervalMinutes: 360 }], { databricksWorkspaceApi: true });
  eq(com.databricksWorkspaceApi, true, 'habilitada vai no pedido');
  const desliga = buildMicrosoftHubRequest(creds, [{ key: 'IdentityPosture', syncIntervalMinutes: 360 }], { databricksWorkspaceApi: false });
  eq(desliga.databricksWorkspaceApi, false, 'desligar também é explícito');
  const mantem = buildMicrosoftHubRequest(creds, [{ key: 'IdentityPosture', syncIntervalMinutes: 360 }], { databricksWorkspaceApi: null });
  eq(mantem.databricksWorkspaceApi, undefined, 'nulo = mantém o guardado');
  const sem = buildMicrosoftHubRequest(creds, [{ key: 'SecureScore', syncIntervalMinutes: 360 }], { databricksWorkspaceApi: true });
  eq(sem.databricksWorkspaceApi, undefined, 'sem o KNIGHT, nada do Databricks');

  const knight = MICROSOFT_HUB_SERVICES.find((s) => s.key === 'IdentityPosture')!;
  eq(new Set(knight.appPermissions).size, knight.appPermissions.length, 'permissões sem repetição');
  ok(knight.appPermissions.includes('OrgSettings-Forms.Read.All') && knight.appPermissions.includes('OrgSettings-AppsAndServices.Read.All'),
    'as duas permissões novas de leitura aparecem');
  eq(new Set((knight.capabilities ?? []).map((c) => c.name)).size, (knight.capabilities ?? []).length, 'nomes de capacidade únicos (chave da lista)');
});

console.log(`${count - failures}/${count} ok`);
if (failures > 0) process.exit(1);

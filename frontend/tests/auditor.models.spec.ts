/**
 * [AEGIS-AUDITOR-CONTEXT-01] Regras PURAS do Auditor Virtual e da correlação KNIGHT × NIST na tela:
 *   (1) o foco vem da URL (página e seleções) e nunca muda a identidade; identificadores fora do formato não viajam;
 *   (2) fontes só abrem rotas internas (nada de link externo ou protocolo relativo);
 *   (3) resposta de outra sessão (outro ambiente, outra conta, conversa encerrada) é descartada;
 *   (4) erros do chat em pt-BR, sem dado sensível;
 *   (5) registro de origem das evidências (KNIGHT, biblioteca, inventário) preserva a seleção NIST;
 *   (6) correlação: o que pede revisão primeiro; documento vinculado nesta rodada.
 *
 * Compilado por `tsc` (CommonJS) e executado por `node`, como os demais specs de lógica.
 */
import '@angular/compiler';
import {
  AuditorSource,
  auditorFocusFromUrl,
  chatErrorMessage,
  chatRequest,
  focusHint,
  sameSession,
  sourceRouterLink,
} from '../src/app/models/auditor.models';
import { NistCorrelationRow, correlationAttention, documentLinkFor, evidenceOriginLink } from '../src/app/models/nist.models';

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
function ok(cond: boolean, msg: string): void {
  if (!cond) throw new Error(msg);
}

const A = 'aaaaaaaa-0000-4000-8000-000000000001';
const C = 'cccccccc-0000-4000-8000-000000000001';
const S = 'eeeeeeee-0000-4000-8000-000000000001';
const RUN = 'bbbbbbbb-0000-4000-8000-000000000009';
const q = `?avaliacao=${A}&rodada=${C}&escopo=${S}`;

test('(1) foco: página, seleção NIST completa, avaliação e controle do KNIGHT; desconhecido vira visão geral', () => {
  const k = auditorFocusFromUrl(`/knight?run=${RUN}&finding=AK-ENTRA-002&tab=controls`);
  eq(k.page, 'knight', 'página do KNIGHT');
  eq(k.knight!.runId, RUN, 'avaliação do KNIGHT');
  eq(k.knight!.indicatorId, 'AK-ENTRA-002', 'controle');
  eq(auditorFocusFromUrl('/knight?run=nao-e-guid&finding=AK-1').knight, null, 'avaliação fora do formato não viaja');
  eq(auditorFocusFromUrl(`/knight?run=${RUN}&finding=<script>`).knight!.indicatorId, null, 'controle fora do formato não viaja');
  const dash = auditorFocusFromUrl(`/dashboard${q}`);
  ok(dash.page === 'dashboard' && dash.nist?.assessmentId === A && dash.nist.code === null, 'seleção do Dashboards');
  eq(auditorFocusFromUrl(`/nist/id/ativos${q}`).page, 'assets', 'inventário');
  eq(auditorFocusFromUrl('/qualquer/coisa').page, 'general', 'página desconhecida');
  eq(auditorFocusFromUrl('/nist?avaliacao=a&rodada=b&escopo=c').nist, null, 'identificadores fora do formato não viajam');
  eq(focusHint(auditorFocusFromUrl(`/nist/pr/PR.AA-01${q}`)), 'AEGIS NIST · PR.AA-01', 'rótulo do foco');
  const req = chatRequest('Pergunta', k, 'conv-1');
  ok(req.page === 'knight' && req.knight?.runId === RUN && req.conversationId === 'conv-1' && !('history' in req), 'o histórico não é enviado');
});

function source(link: AuditorSource['link']): AuditorSource {
  return { key: 'K1', module: 'KNIGHT', nature: 'ObservedConfiguration', natureLabel: 'x', title: 't', detail: null, date: null, isDemo: false, limitation: null, link };
}

test('(2) fontes só abrem rotas internas', () => {
  const l = sourceRouterLink(source({ route: '/nist/gv/GV.PO-01', query: { avaliacao: A }, fragment: 'ev-1' }))!;
  ok(l.commands[0] === '/nist/gv/GV.PO-01' && l.queryParams!['avaliacao'] === A && l.fragment === 'ev-1', 'rota interna');
  eq(sourceRouterLink(source({ route: 'https://evil.example.com', query: null, fragment: null })), null, 'externo recusado');
  eq(sourceRouterLink(source({ route: '//evil.example.com', query: null, fragment: null })), null, 'protocolo relativo recusado');
  eq(sourceRouterLink(source(null)), null, 'sem link');
});

test('(3) resposta de outra sessão é descartada; troca de página no mesmo ambiente não', () => {
  const asked = { tenantId: 't1', accountId: 'p1', epoch: 3 };
  ok(sameSession(asked, { tenantId: 't1', accountId: 'p1', epoch: 3 }), 'mesma sessão');
  ok(!sameSession(asked, { tenantId: 't2', accountId: 'p1', epoch: 3 }), 'outro ambiente');
  ok(!sameSession(asked, { tenantId: 't1', accountId: 'p2', epoch: 3 }), 'outra conta');
  ok(!sameSession(asked, { tenantId: 't1', accountId: 'p1', epoch: 4 }), 'nova conversa ou saída');
});

test('(4) erros do chat', () => {
  eq(chatErrorMessage(404, 'Conversa não encontrada.'), 'Conversa não encontrada.', 'título do servidor');
  ok(chatErrorMessage(404, null).includes('não existe neste ambiente'), '404 sem título');
  ok(chatErrorMessage(429, null).includes('Limite'), '429');
  ok(chatErrorMessage(500, 'stack trace').includes('indisponível'), 'erro genérico não repassa detalhe do servidor');
});

test('(5) origem das evidências preserva a seleção NIST', () => {
  const params = { avaliacao: A, rodada: C, escopo: S };
  const k = evidenceOriginLink({ originKind: 'KnightIndicator', originRef: `${RUN}/AK-ENTRA-002` }, params)!;
  ok(k.commands[0] === '/knight' && k.queryParams['run'] === RUN && k.queryParams['finding'] === 'AK-ENTRA-002', 'execução exata e controle');
  const d = evidenceOriginLink({ originKind: 'GovernanceDocument', documentId: 'doc-1' }, params)!;
  ok(d.commands.join('/') === '/nist/gv/documentos' && d.queryParams['documento'] === 'doc-1' && d.queryParams['avaliacao'] === A, 'biblioteca com a seleção');
  const i = evidenceOriginLink({ originKind: 'AssetInventory' }, params)!;
  ok(i.commands.join('/') === '/nist/id/ativos' && i.queryParams['escopo'] === S, 'inventário com a seleção');
  eq(evidenceOriginLink({ originKind: 'Manual' }, params), null, 'registro do analista não tem origem navegável');
});

function row(code: string, over: Partial<NistCorrelationRow> = {}): NistCorrelationRow {
  return {
    code, title: code, functionCode: code.slice(0, 2), state: 'NotEvaluated', currentLevel: null, targetLevel: null, humanConfirmed: false,
    linkedTechnical: [], candidates: [], technicalNotEvaluated: 0, linkedEvidence: 0, evaluatedWithoutEvidence: false, openFindings: 0, plans: [],
    ...over,
  };
}

test('(6) correlação: revisão primeiro; documento vinculado nesta rodada', () => {
  const exposed = { evidenceId: null, knightRunId: RUN, knightIndicatorId: 'AK-1', title: 'x', status: 'Exposed', statusLabel: 'Reprovado', severity: 'Alta',
    sourceLabel: 's', collectedAt: null, isDemo: false, partialCollection: false, newerStatus: null, newerRunId: null, limitation: '' };
  const ordered = correlationAttention([row('GV.OC-01'), row('PR.AA-01', { candidates: [exposed] }), row('ID.AM-01', { evaluatedWithoutEvidence: true })]);
  eq(ordered.map((r) => r.code).join(','), 'PR.AA-01,ID.AM-01,GV.OC-01', 'reprovado disponível, depois nível sem evidência');
  const links = [{ evidenceId: 'e1', code: 'GV.PO-01', documentId: 'doc-1', title: 'Política', linkedAt: '', recordedByName: null }];
  eq(documentLinkFor(links, 'doc-1', 'GV.PO-01')?.evidenceId, 'e1', 'vinculado');
  eq(documentLinkFor(links, 'doc-1', 'GV.PO-02'), null, 'mesmo documento em outra subcategoria não está vinculado');
});

console.log(`\n${count - failures}/${count} ok`);
if (failures > 0) process.exit(1);

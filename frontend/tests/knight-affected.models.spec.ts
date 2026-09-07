/**
 * [AEGIS-MVP-PRODUCT-02] Testes de LÓGICA PURA da apresentação dos objetos AFETADOS por um achado KNIGHT.
 *
 * O risco desta entrega não é layout — é a tela afirmar mais do que a coleta provou. Estes testes fixam:
 *   (1) nome ausente permanece ausente (identificador, nunca rótulo inventado);
 *   (2) tipo do objeto é explícito — aplicação e grupo não viram "usuário";
 *   (3) os quatro estados de detalhe dizem coisas DIFERENTES, e "sem detalhe preservado" nunca é confundido
 *       com "nenhum objeto afetado";
 *   (4) a leitura honesta de cada achado separa o que ele significa do que ele NÃO significa.
 *
 * Não há runner Angular (karma/jest) neste projeto — apenas `ng build`. Compilado por `tsc` (CommonJS) e
 * executado por `node`, no mesmo padrão de dashboard-overview.models.spec.ts.
 */
import {
  KnightAffectedObject,
  KnightAffectedObjects,
  affectedKindLabel,
  affectedLabel,
  affectedNotice,
  affectedRequestKey,
  findingReading,
  findingSituation,
  findingTitle,
  isCurrentAffectedResponse,
  hasAffectedTable,
  isUnnamed,
  totalPages,
} from '../src/app/models/knight.models';

// ---- micro-harness (sem dependências externas) -------------------------------------------------
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

function obj(over: Partial<KnightAffectedObject> = {}): KnightAffectedObject {
  return {
    externalId: 'demo-user-01',
    kind: 'User',
    displayName: 'Ana Prado',
    userPrincipalName: 'ana.01@demo.example.com',
    roles: ['Administrador Global'],
    detail: 'Papel(is): Administrador Global.',
    ...over,
  };
}

function page(over: Partial<KnightAffectedObjects> = {}): KnightAffectedObjects {
  return {
    runId: 'run-1',
    indicatorId: 'AK-ENTRA-002',
    state: 'Available',
    affectedObjectCount: 12,
    totalPreserved: 12,
    matchCount: 12,
    page: 1,
    pageSize: 5,
    items: [obj()],
    limitation: null,
    collectedAt: '2026-09-06T12:00:00.000Z',
    ...over,
  };
}

console.log('knight-affected.models');

// ---- 1) Nome ausente não se inventa ------------------------------------------------------------
test('objeto sem nome é identificado pelo ID, nunca por um rótulo inventado', () => {
  const semNome = obj({ displayName: null, userPrincipalName: null });
  eq(isUnnamed(semNome), true, 'a tela precisa saber que não há nome');
  eq(affectedLabel(semNome), 'demo-user-01', 'o identificador da fonte é o rótulo possível');
});

test('sem nome de exibição, o UPN é usado antes do identificador', () => {
  const soUpn = obj({ displayName: null });
  eq(isUnnamed(soUpn), false, 'há um identificador humano disponível');
  eq(affectedLabel(soUpn), 'ana.01@demo.example.com', 'o UPN identifica a conta');
});

// ---- 2) Tipo explícito: aplicação não é pessoa -------------------------------------------------
test('cada natureza de objeto tem rótulo próprio — aplicação não vira "usuário"', () => {
  eq(affectedKindLabel('ServicePrincipal'), 'Aplicação', 'exigir MFA de uma aplicação não faz sentido');
  eq(affectedKindLabel('Group'), 'Grupo', 'o acesso do grupo é herdado pelos membros');
  eq(affectedKindLabel('Guest'), 'Convidado', '');
  eq(affectedKindLabel('Unknown'), 'Tipo não identificado', 'tipo desconhecido é declarado, não presumido');
  const rotulos = ['User', 'Guest', 'ServicePrincipal', 'Group', 'Device', 'Unknown']
    .map((k) => affectedKindLabel(k as KnightAffectedObject['kind']));
  eq(new Set(rotulos).size, rotulos.length, 'nenhum tipo compartilha rótulo com outro');
});

// ---- 3) Os quatro estados dizem coisas diferentes ----------------------------------------------
test('avaliação anterior à preservação declara a ausência e não mostra tabela', () => {
  const p = page({ state: 'NotPreserved', totalPreserved: 0, matchCount: 0, items: [] });
  eq(hasAffectedTable(p), false, 'não há lista daquela coleta para exibir');
  const aviso = affectedNotice(p) ?? '';
  ok(/anterior/i.test(aviso), `o aviso precisa dizer que a avaliação é anterior: "${aviso}"`);
  ok(/nova avaliação/i.test(aviso), 'e dizer o que fazer para obter o detalhe');
});

test('achado fora do escopo de detalhe não promete lista', () => {
  const p = page({ state: 'OutOfScope', indicatorId: 'AK-ENTRA-005', totalPreserved: 0, matchCount: 0, items: [] });
  eq(hasAffectedTable(p), false, '');
  ok(/próxima entrega/i.test(affectedNotice(p) ?? ''), 'a tela diz honestamente que o detalhe ainda não existe');
});

test('lista parcial mostra a tabela E o alerta de que ela não é o conjunto inteiro', () => {
  const p = page({ state: 'Partial', totalPreserved: 2, matchCount: 2, limitation: 'Falha na segunda página.' });
  eq(hasAffectedTable(p), true, 'a lista parcial continua útil');
  eq(affectedNotice(p), 'Falha na segunda página.', 'a limitação da coleta é preservada literalmente');
});

test('conjunto VAZIO não é o mesmo que ausência de detalhe', () => {
  const vazio = page({ affectedObjectCount: 0, totalPreserved: 0, matchCount: 0, items: [] });
  eq(hasAffectedTable(vazio), true, 'zero objeto sinalizado É a resposta, não uma lacuna');
  eq(affectedNotice(vazio), null, 'e nada precisa ser ressalvado');
});

test('limitação de uma lista completa continua visível (ex.: objeto sem nome)', () => {
  const p = page({ limitation: '1 objeto sem nome de exibição devolvido pela fonte.' });
  eq(hasAffectedTable(p), true, '');
  ok((affectedNotice(p) ?? '').includes('sem nome'), 'a limitação acompanha a tabela completa');
});

// ---- 4) Paginação: o total vem do servidor, não do tamanho do array ----------------------------
test('o número de páginas deriva da contagem do servidor, não dos itens carregados', () => {
  eq(totalPages(page({ matchCount: 12, pageSize: 5 })), 3, '12 objetos em páginas de 5');
  eq(totalPages(page({ matchCount: 0, pageSize: 25, items: [] })), 1, 'busca sem resultado ainda é uma página');
  eq(totalPages(page({ matchCount: 25, pageSize: 25 })), 1, '');
});

// ---- 5) Leitura honesta: o que o achado NÃO afirma ---------------------------------------------
test('a quantidade de privilegiados não é acusação de acesso desnecessário', () => {
  const r = findingReading('AK-ENTRA-002');
  ok(r !== null, 'o achado tem leitura própria');
  ok(/revisão/i.test(r!.means), 'o conjunto é material de revisão');
  ok(/não sabe quem precisa|desnecessári/i.test(r!.doesNotMean),
    `a tela precisa negar a leitura acusatória: "${r!.doesNotMean}"`);
});

test('o teto de contas privilegiadas é parâmetro do AEGIS, não exigência do NIST', () => {
  const r = findingReading('AK-ENTRA-002')!;
  ok(/parâmetro do AEGIS/i.test(r.criterion), `o critério precisa dizer de quem é o teto: "${r.criterion}"`);
  ok(/não fixa um número/i.test(r.criterion), 'e que o NIST não fixa número');
});

test('registro de MFA não comprova imposição efetiva', () => {
  const r = findingReading('AK-ENTRA-001')!;
  ok(/não comprovam a imposição|imposição efetiva/i.test(r.doesNotMean),
    `registro ≠ imposição: "${r.doesNotMean}"`);
});

test('atividade desconhecida não comprova inatividade', () => {
  const r = findingReading('AK-ENTRA-004')!;
  ok(/desconhecida não é inatividade comprovada/i.test(r.doesNotMean),
    `ausência de sinal não é prova de desuso: "${r.doesNotMean}"`);
});

test('achado sem leitura específica não recebe interpretação inventada', () => {
  eq(findingReading('AK-ENTRA-003'), null, 'sem leitura própria, a tela mostra só a evidência do backend');
});

// ================================================================================================
// [AEGIS-MVP-PRODUCT-02 · correção dirigida] Linguagem PRINCIPAL compartilhada entre KNIGHT e Prioridades.
// A ressalva escondida no detalhe não conserta um título que afirma mais forte do que a coleta prova.
// ================================================================================================

const achado = (indicatorId: string, status: string, n: number, evidence: string) =>
  ({ indicatorId, title: 'titulo do catalogo', status, affectedObjectCount: n, evidence }) as never;

test('o título principal não afirma "MFA efetivo" — a fonte observa registro/capacidade', () => {
  const f = achado('AK-ENTRA-001', 'Exposed', 3, '3 de 12 conta(s) privilegiada(s) sem MFA efetivo.');
  ok(!/MFA efetivo/i.test(findingTitle(f)), `título ainda afirma imposição: "${findingTitle(f)}"`);
  ok(/registrado/i.test(findingTitle(f)), 'o título precisa falar de REGISTRO de método');
  ok(!/MFA efetivo/i.test(findingSituation(f)), 'a situação também não pode afirmar imposição');
  ok(/não comprova imposição/i.test(findingSituation(f)), 'a ressalva vem junto da afirmação, não escondida');
});

test('privilegiados são conjunto sujeito a revisão, não acusação de acesso desnecessário', () => {
  const f = achado('AK-ENTRA-002', 'Exposed', 78, '78 contas privilegiadas excedem o teto...');
  ok(/revisão/i.test(findingTitle(f)), `título deve falar em revisão: "${findingTitle(f)}"`);
  ok(/não uma lista de acessos desnecess/i.test(findingSituation(f)),
    `a única menção a acesso desnecessário é para NEGAR a leitura: "${findingSituation(f)}"`);
  ok(/AEGIS/i.test(findingSituation(f)), 'o teto precisa aparecer como parâmetro do AEGIS');
});

test('o teto de menor privilégio não é apresentado como exigência do NIST', () => {
  const f = achado('AK-ENTRA-002', 'Passed', 0, 'dentro do teto');
  ok(/parâmetro do AEGIS|parametrizado no\s+AEGIS/i.test(findingSituation(f)),
    `o parâmetro é do AEGIS: "${findingSituation(f)}"`);
});

test('convidados são sinalizados por atividade desconhecida, não por inatividade comprovada', () => {
  const f = achado('AK-ENTRA-004', 'Exposed', 5, '5 convidado(s) inativo(s) há mais de 90 dias...');
  ok(/desconhecida/i.test(findingTitle(f)), `título: "${findingTitle(f)}"`);
  ok(/não\s+comprova desuso/i.test(findingSituation(f)), `situação: "${findingSituation(f)}"`);
});

test('achado sem redação revisada mantém título e evidência do backend, sem invenção', () => {
  const f = achado('AK-ENTRA-003', 'Exposed', 2, 'texto literal gravado na avaliação');
  eq(findingTitle(f), 'titulo do catalogo', 'sem redação própria, o título do catálogo é preservado');
  eq(findingSituation(f), 'texto literal gravado na avaliação', 'a evidência gravada é usada literalmente');
});

// ================================================================================================
// [AEGIS-MVP-PRODUCT-02 · correção dirigida] Guarda de contexto das leituras de afetados: uma resposta
// atrasada de A não pode preencher B.
// ================================================================================================

const A = 'run-a';
const B = 'run-b';

test('resposta atrasada da avaliação A não pertence ao contexto da avaliação B', () => {
  const pedidoA = affectedRequestKey(A, 'AK-ENTRA-002', 1, null);
  const pedidoB = affectedRequestKey(B, 'AK-ENTRA-002', 1, null);
  ok(!isCurrentAffectedResponse(pedidoB, pedidoA),
    'a lista de A jamais pode escrever no detalhe aberto em B');
  ok(isCurrentAffectedResponse(pedidoB, pedidoB), 'a resposta do próprio pedido é aceita');
});

test('trocar de indicador na mesma avaliação também invalida a resposta anterior', () => {
  const antes = affectedRequestKey(A, 'AK-ENTRA-002', 1, null);
  const agora = affectedRequestKey(A, 'AK-ENTRA-004', 1, null);
  ok(!isCurrentAffectedResponse(agora, antes), 'objetos de um achado não sustentam o veredito de outro');
});

test('resposta da busca anterior não é resposta da busca nova', () => {
  const antes = affectedRequestKey(A, 'AK-ENTRA-002', 1, 'ana');
  const agora = affectedRequestKey(A, 'AK-ENTRA-002', 1, 'bruno');
  ok(!isCurrentAffectedResponse(agora, antes), 'o resultado de "ana" não pode aparecer como resposta de "bruno"');
});

test('trocar de página invalida a resposta da página anterior', () => {
  const p1 = affectedRequestKey(A, 'AK-ENTRA-002', 1, null);
  const p2 = affectedRequestKey(A, 'AK-ENTRA-002', 2, null);
  ok(!isCurrentAffectedResponse(p2, p1), 'página 1 não é resposta do pedido da página 2');
});

test('busca ausente e busca em branco são o MESMO pedido', () => {
  eq(affectedRequestKey(A, 'AK-ENTRA-002', 1, null), affectedRequestKey(A, 'AK-ENTRA-002', 1, '   '),
    'espaço em branco não cria um contexto diferente');
});

console.log(`\n${count - failures}/${count} testes passaram (knight-affected.models).`);
if (failures > 0) throw new Error(`${failures} teste(s) dos afetados do KNIGHT falharam`);

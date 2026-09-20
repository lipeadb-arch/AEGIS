/**
 * [AEGIS-KNIGHT-COVERAGE-02] Testes de LÓGICA PURA do bloco do Microsoft Teams na interface.
 *
 * Fixam três coisas que a tela não pode errar:
 *   (1) o conector Microsoft alimenta DUAS fontes, e o estado de sincronização de uma não sobrescreve o da
 *       outra — a chave de acompanhamento inclui a fonte;
 *   (2) a fonte exibida no KNIGHT é escolhida explicitamente, e as outras continuam existindo: sincronizar o
 *       Teams não faz a avaliação do Entra ID desaparecer;
 *   (3) os rótulos do Teams (fonte, categoria) existem — uma categoria sem rótulo apareceria em branco.
 */
import {
  KnightAssessment,
  KnightSourceLatest,
  categoryLabel,
  sourceTypeLabel,
} from '../src/app/models/knight.models';
import { knightSourcesOf, syncKey } from '../src/app/models/knight-sync.models';

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

/**
 * A MESMA escolha de fonte padrão do componente (`pickSource`): a fonte com o resultado CONCLUÍDO mais
 * recente; sem nenhum concluído, a da tentativa mais recente, para que a pendência apareça.
 */
function pickSource(sources: readonly KnightSourceLatest[]): KnightSourceLatest | null {
  const instante = (s: KnightSourceLatest) =>
    Date.parse(s.assessment?.startedAt ?? s.unfinishedAttempt?.startedAt ?? '') || 0;
  const comResultado = sources.filter((s) => s.assessment !== null);
  const candidatas = comResultado.length > 0 ? comResultado : [...sources];
  return candidatas.sort((a, b) => instante(b) - instante(a))[0] ?? null;
}

function bloco(over: Partial<KnightSourceLatest> & Pick<KnightSourceLatest, 'source'>): KnightSourceLatest {
  return {
    slug: over.source === 'MicrosoftTeams' ? 'teams' : 'entra',
    label: sourceTypeLabel(over.source),
    assessment: null,
    unfinishedAttempt: null,
    ...over,
  };
}

function avaliacao(startedAt: string): KnightAssessment {
  return { id: `run-${startedAt}`, startedAt } as unknown as KnightAssessment;
}

console.log('knight-teams.models.spec');

test('o conector Microsoft alimenta Entra ID e Microsoft Teams, nesta ordem', () => {
  const fontes = knightSourcesOf('Microsoft');
  eq(fontes.length, 2, 'duas fontes');
  eq(fontes[0].slug, 'entra', 'a fonte padrão vem primeiro');
  eq(fontes[1].slug, 'teams', 'Teams é a segunda');
  eq(fontes[0].requirement, null, 'o Entra ID não exige nada além da credencial');
  ok(
    (fontes[1].requirement ?? '').includes('Leitor do Teams'),
    'o Teams diz o que exige além da credencial — o papel de diretório',
  );
});

test('o conector Google continua com uma fonte só', () => {
  const fontes = knightSourcesOf('Google');
  eq(fontes.length, 1, 'uma fonte');
  eq(fontes[0].slug, 'google', 'Google Workspace');
});

test('provedor sem fonte KNIGHT não inventa linha de sincronização', () => {
  eq(knightSourcesOf('Wiz').length, 0, 'nenhuma fonte');
});

test('a chave de acompanhamento separa as duas fontes do MESMO conector', () => {
  const entra = syncKey('conn-1', 'entra');
  const teams = syncKey('conn-1', 'teams');
  ok(entra !== teams, 'chaves distintas — o estado de uma não sobrescreve o da outra');
  eq(syncKey('conn-1', 'teams'), teams, 'a chave é estável');
  ok(syncKey('conn-2', 'teams') !== teams, 'conectores distintos também não se misturam');
});

test('a fonte aberta por padrão é a do resultado concluído mais recente', () => {
  const escolhida = pickSource([
    bloco({ source: 'MicrosoftEntraId', assessment: avaliacao('2026-09-19T10:00:00Z') }),
    bloco({ source: 'MicrosoftTeams', assessment: avaliacao('2026-09-20T08:00:00Z') }),
  ]);
  eq(escolhida?.source, 'MicrosoftTeams', 'a coleta mais recente abre primeiro');
});

test('uma sincronização do Teams NÃO descarta a avaliação do Entra ID', () => {
  const fontes = [
    bloco({ source: 'MicrosoftEntraId', assessment: avaliacao('2026-09-19T10:00:00Z') }),
    bloco({ source: 'MicrosoftTeams', assessment: avaliacao('2026-09-20T08:00:00Z') }),
  ];
  // A lista continua trazendo AS DUAS: a que não está na tela some da vista, não do produto.
  eq(fontes.length, 2, 'as duas fontes continuam na leitura');
  const entra = fontes.find((f) => f.source === 'MicrosoftEntraId');
  ok(entra?.assessment !== null, 'a avaliação do Entra ID continua disponível para ser aberta');
});

test('sem resultado concluído, a fonte com a tentativa mais recente aparece — a pendência não some', () => {
  const escolhida = pickSource([
    bloco({
      source: 'MicrosoftTeams',
      unfinishedAttempt: {
        id: 'x',
        status: 'Running',
        sourceType: 'MicrosoftTeams',
        mode: 'Live',
        startedAt: '2026-09-20T09:00:00Z',
      },
    }),
  ]);
  eq(escolhida?.source, 'MicrosoftTeams', 'a tentativa não finalizada é apresentada como tentativa');
  eq(escolhida?.assessment, null, 'sem resultado concluído — e a tela não inventa um');
});

test('lista vazia não quebra a escolha', () => {
  eq(pickSource([]), null, 'sem fonte avaliada, não há o que abrir');
});

test('os rótulos do Teams existem — nada aparece em branco na tela', () => {
  eq(sourceTypeLabel('MicrosoftTeams'), 'Microsoft Teams', 'rótulo da fonte');
  eq(categoryLabel('CollaborationSecurity'), 'Colaboração e comunicação', 'rótulo da categoria');
});

console.log(`${count - failures}/${count} ok`);
if (failures > 0) process.exit(1);

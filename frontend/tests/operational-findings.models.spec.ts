/**
 * [AEGIS-MVP-LANGUAGE-02] Testes de LÓGICA PURA da linguagem clara de vulnerabilidades e exposições (frontend).
 *
 * Após a rodada de correções, a NARRATIVA de vulnerabilidade (título/exploit/porquê/1ª ação/severidade do grupo)
 * é AUTORIDADE ÚNICA do backend (VulnerabilityNarrative) e chega PRONTA em `VulnerabilityGroup` — o frontend NÃO
 * recompõe. Portanto, aqui cobrimos: (a) o CONTRATO do grupo carrega os campos claros do backend e o frontend os
 * consome verbatim; (b) os helpers de APRESENTAÇÃO que sobrevivem (enum de severidade do resumo; vocabulário de
 * EXPOSIÇÃO: categoria/tier/impacto/tipo de ação; "alcance não informado"). Compiladas por `tsc`, executadas por `node`.
 */
import { VulnerabilityGroup, severityPt } from '../src/app/models/vulnerability.models';
import {
  EXPOSURE_REACH_UNKNOWN,
  actionTypePt,
  categoryPt,
  impactPt,
  tierPt,
} from '../src/app/models/posture-exposure.models';

// ---- micro-harness ---------------------------------------------------------------------------
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
function assert(cond: boolean, msg: string): void {
  if (!cond) throw new Error(msg);
}
function eq<T>(actual: T, expected: T, msg: string): void {
  if (actual !== expected) throw new Error(`${msg}: esperado ${String(expected)}, obtido ${String(actual)}`);
}

// Fábrica de grupo com TODOS os campos de narrativa já preenchidos pelo backend (autoridade única). O tsc falha
// se algum campo do contrato sumir/for renomeado — é a trava de que o frontend espelha o DTO do backend.
function group(over: Partial<VulnerabilityGroup>): VulnerabilityGroup {
  return {
    cveId: 'CVE-2024-0001',
    displayTitle: 'Vulnerabilidade alta em Apache Log4j',
    severityLabel: 'Alta',
    exploitLabel: 'Sem exploit informado pela fonte',
    whyItMatters: 'Afeta 1 ativo ainda aberto.',
    firstAction: 'Valide a atualização ou mitigação disponível e priorize os ativos mais críticos.',
    severity: 'High',
    cvssScore: 8.1,
    cvssVector: 'v',
    epss: 0.3,
    publicExploit: false,
    exploitVerified: false,
    publishedOn: null,
    sourceTitle: null,
    productLabel: 'Apache Log4j',
    affectedAssetCount: 1,
    openAssetCount: 1,
    resolvedAssetCount: 0,
    maxAssetCriticality: 1,
    assetPreview: [],
    assetPreviewTruncated: false,
    providers: ['Microsoft'],
    firstSeenAt: '2026-08-01T00:00:00Z',
    lastSeenAt: '2026-08-10T00:00:00Z',
    effectiveLifecycle: 'Open',
    ...over,
  };
}

// ---- 1) tradução de severidade (enum cru → rótulo dos chips do RESUMO) -------------------------
test('severityPt traduz os níveis e cai em Desconhecida', () => {
  eq(severityPt('Critical'), 'Crítica', 'critical');
  eq(severityPt('high'), 'Alta', 'high');
  eq(severityPt('MEDIUM'), 'Média', 'medium');
  eq(severityPt('Low'), 'Baixa', 'low');
  eq(severityPt(null), 'Desconhecida', 'null');
  eq(severityPt('weird'), 'Desconhecida', 'desconhecido');
});

// ---- 2) CONTRATO: o grupo carrega a narrativa do backend e o frontend a consome verbatim -------
test('VulnerabilityGroup expõe a narrativa CLARA do backend (frontend não recompõe)', () => {
  const g = group({
    displayTitle: 'Vulnerabilidade crítica em ativos do ambiente',
    severityLabel: 'Crítica',
    exploitLabel: 'Exploit confirmado disponível',
    whyItMatters: 'Afeta 3 ativos ainda abertos · CVSS 9.8.',
    firstAction: 'Aplicar a correção KB5000001 nos ativos afetados, começando pelos mais críticos.',
    openAssetCount: 3,
    resolvedAssetCount: 2,
    affectedAssetCount: 5,
  });
  // A tela usa estes campos DIRETO (sem funções locais de narrativa): título nunca é só o CVE, e os rótulos são
  // exatamente o que o backend mandou.
  assert(!/^CVE-/.test(g.displayTitle), 'o título principal nunca é apenas o CVE');
  eq(g.severityLabel, 'Crítica', 'rótulo de severidade verbatim');
  eq(g.exploitLabel, 'Exploit confirmado disponível', 'rótulo de exploit verbatim');
  assert(g.exploitLabel.toLowerCase().includes('disponível'), 'exploit = DISPONIBILIDADE');
  assert(!g.exploitLabel.toLowerCase().includes('exploração ativa'), 'jamais "exploração ativa"');
  assert(!g.whyItMatters.includes('<'), 'porquê nunca traz HTML cru');
  // Contagens por estado presentes e coerentes (abertas + resolvidas cabem no total de ativos afetados).
  assert(g.openAssetCount + g.resolvedAssetCount <= g.affectedAssetCount, 'abertas+resolvidas ≤ afetados');
});

// ---- 3) vocabulário visível de EXPOSIÇÃO (helpers de apresentação que sobrevivem) --------------
test('categoryPt/tierPt/impactPt/actionTypePt traduzem e deixam o desconhecido passar', () => {
  eq(categoryPt('Device'), 'Dispositivos', 'device');
  eq(categoryPt('Apps'), 'Aplicativos', 'apps');
  eq(categoryPt('Identity'), 'Identidades', 'identity');
  eq(categoryPt('Data'), 'Dados', 'data');
  eq(categoryPt('Nuvem'), 'Nuvem', 'desconhecido passa direto (nunca inventa)');
  eq(tierPt('Core'), 'Essencial', 'core');
  eq(tierPt('Defense in Depth'), 'Defesa em profundidade', 'defense in depth');
  eq(tierPt('Advanced'), 'Avançado', 'advanced');
  eq(impactPt('Low'), 'Baixo', 'low');
  eq(impactPt('Moderate'), 'Moderado', 'moderate');
  eq(impactPt('High'), 'Alto', 'high');
  eq(actionTypePt('Config'), 'Configuração', 'config');
  eq(actionTypePt('Review'), 'Revisão', 'review');
  eq(actionTypePt('Behavior'), 'Comportamento', 'behavior');
  eq(categoryPt(null), null, 'null preserva null');
});

// ---- 4) alcance por ativo não informado (exposição) -------------------------------------------
test('exposição declara honestamente que o alcance por ativo não é informado', () => {
  eq(EXPOSURE_REACH_UNKNOWN, 'Alcance por ativo não informado pela fonte', 'texto honesto de alcance');
});

console.log(`\n${count - failures}/${count} testes de lógica do frontend (operational-findings) aprovados.`);
if (failures > 0) throw new Error(`${failures} teste(s) de lógica do frontend (operational-findings) falharam`);

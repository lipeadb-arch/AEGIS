/**
 * [AEGIS-MVP-LANGUAGE-02] Testes de LÓGICA PURA da linguagem clara de vulnerabilidades e exposições (frontend).
 *
 * Cobrem, sem DOM: tradução de severidade/categoria/tier/impacto/tipo de ação; escolha do título principal
 * (com produto × "ativos do ambiente") com o CVE como referência SECUNDÁRIA; rótulos de exploit semanticamente
 * corretos (NUNCA "exploração ativa"); "por que importa" só com fatos; "alcance não informado" para exposição;
 * grupo com vários ativos; e ausência de HTML cru na saída. Compiladas por `tsc` e executadas por `node`.
 */
import {
  VulnerabilityGroup,
  exploitLabel,
  severityPt,
  vulnerabilityTitle,
  vulnerabilityWhyItMatters,
} from '../src/app/models/vulnerability.models';
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

function group(over: Partial<VulnerabilityGroup>): VulnerabilityGroup {
  return {
    cveId: 'CVE-2024-0001',
    severity: 'High',
    cvssScore: 8.1,
    cvssVector: 'v',
    epss: 0.3,
    publicExploit: false,
    exploitVerified: false,
    publishedOn: null,
    sourceTitle: null,
    productLabel: null,
    affectedAssetCount: 1,
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

// ---- 1) tradução de severidade ----------------------------------------------------------------
test('severityPt traduz os níveis e cai em Desconhecida', () => {
  eq(severityPt('Critical'), 'Crítica', 'critical');
  eq(severityPt('high'), 'Alta', 'high');
  eq(severityPt('MEDIUM'), 'Média', 'medium');
  eq(severityPt('Low'), 'Baixa', 'low');
  eq(severityPt(null), 'Desconhecida', 'null');
  eq(severityPt('weird'), 'Desconhecida', 'desconhecido');
});

// ---- 2) título principal: produto × "ativos do ambiente"; CVE é secundário --------------------
test('vulnerabilityTitle usa o produto quando confiável, senão "ativos do ambiente"', () => {
  eq(
    vulnerabilityTitle({ severity: 'Critical', productLabel: 'Apache Log4j' }),
    'Vulnerabilidade crítica em Apache Log4j',
    'com produto',
  );
  eq(
    vulnerabilityTitle({ severity: 'High', productLabel: 'Windows 11' }),
    'Vulnerabilidade alta em Windows 11',
    'produto Windows',
  );
  eq(
    vulnerabilityTitle({ severity: 'Critical', productLabel: null }),
    'Vulnerabilidade crítica em ativos do ambiente',
    'sem produto',
  );
  const t = vulnerabilityTitle({ severity: 'Low', productLabel: null });
  assert(!/^CVE-/.test(t), 'o título principal nunca é apenas o CVE');
});

// ---- 3) rótulos de exploit — NUNCA "exploração ativa" -----------------------------------------
test('exploitLabel usa os três rótulos e nunca afirma exploração ativa', () => {
  eq(exploitLabel({ exploitVerified: true, publicExploit: true }), 'Exploit confirmado disponível', 'verificado');
  eq(exploitLabel({ exploitVerified: false, publicExploit: true }), 'Exploit público disponível', 'público');
  eq(exploitLabel({ exploitVerified: false, publicExploit: false }), 'Sem exploit informado pela fonte', 'nenhum');
  for (const g of [
    { exploitVerified: true, publicExploit: true },
    { exploitVerified: false, publicExploit: true },
    { exploitVerified: false, publicExploit: false },
  ]) {
    assert(!exploitLabel(g).toLowerCase().includes('exploração ativa'), 'jamais "exploração ativa"');
    assert(!exploitLabel(g).toLowerCase().includes('atacado'), 'jamais afirma que o tenant foi atacado');
  }
});

// ---- 4) "por que importa" só com fatos; grupo com vários ativos; sem HTML cru -----------------
test('vulnerabilityWhyItMatters usa fatos (alcance, criticidade, CVSS, EPSS, exploit)', () => {
  const w = vulnerabilityWhyItMatters(
    group({ affectedAssetCount: 12, maxAssetCriticality: 4, cvssScore: 9.8, epss: 0.42, exploitVerified: true }),
  );
  assert(w.includes('12 ativos'), 'alcance');
  assert(w.includes('alta criticidade'), 'criticidade máxima alta');
  assert(w.includes('CVSS 9.8'), 'CVSS');
  assert(w.includes('EPSS 42%'), 'EPSS');
  assert(w.includes('exploit confirmado disponível'), 'exploit');
  assert(!w.includes('<'), 'nunca HTML cru');

  const one = vulnerabilityWhyItMatters(group({ affectedAssetCount: 1, maxAssetCriticality: 1, cvssScore: null, epss: null }));
  assert(one.includes('1 ativo do ambiente'), 'singular');
  assert(!one.includes('alta criticidade'), 'sem criticidade alta quando baixa');
});

// ---- 5) vocabulário visível de exposição ------------------------------------------------------
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

// ---- 6) alcance por ativo não informado (exposição) -------------------------------------------
test('exposição declara honestamente que o alcance por ativo não é informado', () => {
  eq(EXPOSURE_REACH_UNKNOWN, 'Alcance por ativo não informado pela fonte', 'texto honesto de alcance');
});

console.log(`\n${count - failures}/${count} testes de lógica do frontend (operational-findings) aprovados.`);
if (failures > 0) throw new Error(`${failures} teste(s) de lógica do frontend (operational-findings) falharam`);

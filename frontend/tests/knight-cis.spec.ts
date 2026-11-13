/**
 * [AEGIS-KNIGHT-CIS-01] Página "CIS Benchmarks" do KNIGHT: lógica de leitura da projeção do servidor (dados SINTÉTICOS).
 *
 * O que este spec trava:
 *   (1) filtros por benchmark, serviço, resultado e perfil, e busca por seção ou título (sem acento nem caixa);
 *   (2) contagens do recorte com os denominadores da projeção ("Fora do escopo" fora das consideradas) e barra segmentada
 *       com rótulos que somam o total do benchmark;
 *   (3) perfil: opções por benchmark, "perfil não registrado" e troca de benchmark limpando o perfil;
 *   (4) o componente real: carregamento, erro com nova tentativa, legenda que filtra a lista e abertura do detalhe;
 *   (5) a ordem da página e do detalhe: origem → resumo por benchmark → reconciliação → lista; "O que foi verificado",
 *       "O que encontramos", "Por que importa", "Como corrigir";
 *   (6) limitações por fonte agrupadas por causa (visão Geral).
 *
 * Compilado por `tsc` (CommonJS, com decorators) e executado por `node`, como os demais specs de lógica.
 */
import '@angular/compiler';
import * as fs from 'fs';
import * as path from 'path';
import { Injector, runInInjectionContext } from '@angular/core';
import { Observable, Subject } from 'rxjs';
import { KnightCisComponent } from '../src/app/pages/knight-cis.component';
import { KnightService } from '../src/app/services/knight.service';
import {
  CisCounts,
  CisRecommendation,
  EMPTY_CIS_FILTERS,
  KnightCisView,
  cisBarLabel,
  cisCounts,
  cisCountsText,
  cisSegments,
  describeCisFilters,
  filterCis,
  groupSourceLimitations,
  profileOptions,
  ratio,
  ruleRows,
} from '../src/app/models/knight-cis.models';

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

// ---- dados sintéticos -------------------------------------------------------------------------
function counts(c: number, nc: number, ne: number, na: number, oos: number): CisCounts {
  return { compliant: c, nonCompliant: nc, notEvaluated: ne, notApplicable: na, outOfScope: oos, considered: c + nc + ne + na, evaluated: c + nc, total: c + nc + ne + na + oos };
}
function rec(id: string, status: CisRecommendation['status'], extra: Partial<CisRecommendation> = {}): CisRecommendation {
  const [bench, section] = id.split(':');
  return {
    id, benchmark: bench, section, title: `Recomendação sintética ${section}`, service: 'EntraId', serviceLabel: 'Microsoft Entra ID',
    platform: 'Microsoft Entra ID', severity: 'High', severityLabel: 'Alto', profileState: 'NotRecorded', profileStateLabel: 'Perfil não registrado',
    profiles: [], profileLabels: [], status, statusLabel: status, origin: 'Automated', originLabel: 'Avaliação automatizada', mitigated: false,
    reason: null, whatChecked: 'verificado', found: 'encontrado', whyItMatters: null, howToFix: null, links: [], limitations: [],
    variants: [{ key: id, variant: null, title: 't', status, statusLabel: status, reason: null, disposition: 'Implemented', dispositionLabel: 'Implementado',
      dispositionNote: null, traceRule: 'regra-sintetica', rules: [{ indicatorId: 'AK-ENTRA-900', match: 'Exact', matchLabel: 'Integral', note: null, proof: 'ProvesViolation', proofLabel: 'Comprova a violação' }] }],
    ruleIds: ['AK-ENTRA-900'], manual: null, ...extra,
  };
}

function view(): KnightCisView {
  const recs: CisRecommendation[] = [
    rec('CIS-M365-7.0.0:5.2.2.1', 'NonCompliant', { title: 'Proteção de acesso dos administradores', profiles: ['E3 Level 1', 'E5 Level 1'], profileLabels: ['E3 · Nível 1', 'E5 · Nível 1'], profileState: 'Recorded' }),
    rec('CIS-M365-7.0.0:5.2.2.10', 'Compliant', { title: 'Método de autenticação forte' }),
    rec('CIS-M365-7.0.0:8.1.1', 'OutOfScope', { service: 'Fabric', serviceLabel: 'Microsoft Fabric (Power BI)', title: 'Compartilhamento externo' }),
    rec('CIS-AZ-6.0.0:7.3', 'NotEvaluated', { service: 'AzureNetworking', serviceLabel: 'Rede do Azure', title: 'Acesso UDP a partir da Internet', profiles: ['Level 1'], profileLabels: ['Nível 1'], profileState: 'Recorded' }),
    rec('CIS-AZ-6.0.0:9.1', 'NotApplicable', { service: 'AzureDatabases', serviceLabel: 'Bancos de dados do Azure', title: 'Sessão do banco' }),
  ];
  return {
    schemaVersion: 'knight-cis-v1',
    mapping: { catalogVersion: 'ak-knight-v13', referenceRepository: '', referenceCommit: '7cfa390', traceRepository: '', traceCommit: '7cfa390', reliableCatalogVersions: ['ak-knight-v13'], note: 'Vínculos do catálogo ak-knight-v13.' },
    origin: { label: 'Consolidado — sintético', dataRecency: '2026-11-11T20:40:00Z', sources: [] },
    benchmarks: [
      { key: 'CIS-M365-7.0.0', framework: 'CIS Microsoft 365 Foundations', version: '7.0.0', label: 'Microsoft 365 Foundations 7.0.0', url: 'https://www.cisecurity.org/benchmark/microsoft_365',
        ruleset: 'cis_m365_7.0.0.json', recommendations: 3, catalogEntries: 3, counts: counts(1, 1, 0, 0, 1), manualCounts: counts(0, 0, 0, 0, 0), mitigated: 0,
        profiles: [{ key: 'E3 Level 1', label: 'E3 · Nível 1', recommendations: 1, counts: counts(0, 1, 0, 0, 0) }, { key: 'E5 Level 1', label: 'E5 · Nível 1', recommendations: 1, counts: counts(0, 1, 0, 0, 0) }],
        profileNotRecorded: 2, profileDivergent: 0 },
      { key: 'CIS-AZ-6.0.0', framework: 'CIS Microsoft Azure Foundations', version: '6.0.0', label: 'Azure Foundations 6.0.0', url: 'https://www.cisecurity.org/benchmark/azure',
        ruleset: 'cis_azure_6.0.json', recommendations: 2, catalogEntries: 6, counts: counts(0, 0, 1, 1, 0), manualCounts: counts(0, 0, 0, 0, 0), mitigated: 0,
        profiles: [{ key: 'Level 1', label: 'Nível 1', recommendations: 1, counts: counts(0, 0, 1, 0, 0) }], profileNotRecorded: 1, profileDivergent: 0 },
    ],
    recommendations: recs,
    rules: [{ indicatorId: 'AK-ENTRA-900', title: 'Regra sintética', source: 'MicrosoftEntraId', sourceLabel: 'Microsoft Entra ID', runId: 'r1', catalogVersion: 'ak-knight-v13',
      status: 'Exposed', statusLabel: 'Reprovado', evidence: '3 contas fora do critério (sintético).', notEvaluatedReason: null, affectedCount: 3,
      affectedComposition: '3 contas de usuário', evidenceCount: 0, detailLimitation: null, expected: 'MFA exigida', criterion: null, recommendation: null, links: [], recommendations: [] }],
    ownChecks: [],
    reconciliation: { knightControls: 362, knightApplicable: 243, knightEvaluated: 201, knightNotApplicable: 119, rulesInComposition: 362, rulesLinked: 349, ownChecks: 13,
      recommendations: 5, considered: 4, evaluated: 2, outOfScope: 1, withoutAutomatedRule: 0, explanation: ['O KNIGHT conta regras; a visão CIS conta recomendações.'] },
    limitations: [],
    disclaimer: 'Recorte técnico; não é certificação CIS.',
  };
}

// ---- (1) filtros e busca ------------------------------------------------------------------------
test('filtros por benchmark, serviço, resultado e busca por seção ou título', () => {
  const v = view();
  eq(filterCis(v, EMPTY_CIS_FILTERS).length, 5, 'sem filtro, todas');
  eq(filterCis(v, { ...EMPTY_CIS_FILTERS, benchmark: 'CIS-AZ-6.0.0' }).length, 2, 'benchmark');
  eq(filterCis(v, { ...EMPTY_CIS_FILTERS, service: 'AzureNetworking' }).length, 1, 'serviço');
  eq(filterCis(v, { ...EMPTY_CIS_FILTERS, status: 'NonCompliant' })[0].id, 'CIS-M365-7.0.0:5.2.2.1', 'resultado');
  eq(filterCis(v, { ...EMPTY_CIS_FILTERS, q: '5.2.2' }).length, 2, 'seção como prefixo pega 5.2.2.1 e 5.2.2.10');
  eq(filterCis(v, { ...EMPTY_CIS_FILTERS, q: 'protecao ADMIN' }).length, 1, 'título sem acento e sem caixa');
  eq(filterCis(v, { ...EMPTY_CIS_FILTERS, q: 'udp internet' })[0].section, '7.3', 'várias palavras');
});

// ---- (2) contagens e barra ----------------------------------------------------------------------
test('contagens do recorte: Fora do escopo fora das consideradas; denominadores sempre ditos', () => {
  const c = cisCounts(view().recommendations);
  eq(c.considered, 4, 'consideradas');
  eq(c.evaluated, 2, 'com veredito');
  eq(c.total, 5, 'total');
  eq(ratio(1, 2, 'com veredito'), '1 de 2 com veredito (50%)', 'razão com denominador');
  eq(ratio(0, 0, 'consideradas'), 'nenhuma consideradas', 'sem denominador não inventa percentual');
  eq(cisCountsText(c), '2 com veredito de 4 consideradas (1 conformes, 1 não conformes, 1 não avaliadas, 1 não aplicáveis, 1 fora do escopo)', 'texto do recorte');
});

test('barra segmentada: só segmentos não vazios, na ordem de leitura, somando 100% do total, com rótulo acessível', () => {
  const b = view().benchmarks[0];
  const segs = cisSegments(b.counts);
  eq(segs.map((s) => s.status).join(','), 'NonCompliant,Compliant,OutOfScope', 'ordem e só os não vazios');
  eq(Math.round(segs.reduce((n, s) => n + s.percent, 0)), 100, 'soma 100% do total do benchmark');
  const label = cisBarLabel(b.label, b.counts);
  ok(label.includes('Não conforme 1') && label.includes('Fora do escopo 1') && label.includes('Não avaliado 0'), `rótulo com todos os estados: ${label}`);
});

// ---- (3) perfil ---------------------------------------------------------------------------------
test('perfil: opções por benchmark, perfil não registrado e significado E3/E5 × nível preservado', () => {
  const v = view();
  eq(profileOptions(v, '').map((p) => p.label).join(','), 'E3 · Nível 1,E5 · Nível 1,Nível 1', 'todos os benchmarks');
  eq(profileOptions(v, 'CIS-AZ-6.0.0').map((p) => p.key).join(','), 'Level 1', 'só do benchmark escolhido');
  eq(filterCis(v, { ...EMPTY_CIS_FILTERS, profile: 'E5 Level 1' }).length, 1, 'perfil E5 nível 1');
  eq(filterCis(v, { ...EMPTY_CIS_FILTERS, profile: '-' }).length, 3, 'perfil não registrado');
  eq(describeCisFilters(v, { ...EMPTY_CIS_FILTERS, benchmark: 'CIS-M365-7.0.0', profile: 'E3 Level 1', q: '5.2' }),
    'benchmark: Microsoft 365 Foundations 7.0.0 · perfil: E3 · Nível 1 · pesquisa: “5.2”', 'recorte em palavras');
});

test('detalhe: linhas regra × variante trazem o resultado da regra e o que ela provou', () => {
  const v = view();
  const rows = ruleRows(v, v.recommendations[0]);
  eq(rows.length, 1, 'uma regra');
  eq(rows[0].rule?.affectedComposition, '3 contas de usuário', 'contas e recursos da regra');
  eq(rows[0].ref.proofLabel, 'Comprova a violação', 'o que prova');
});

// ---- (4) componente real --------------------------------------------------------------------------
function component(): { c: KnightCisComponent; calls: Subject<KnightCisView>[] } {
  const calls: Subject<KnightCisView>[] = [];
  const knight = { getCis: (): Observable<KnightCisView> => { const s = new Subject<KnightCisView>(); calls.push(s); return s; } };
  const injector = Injector.create({ providers: [{ provide: KnightService, useValue: knight }] });
  const c = runInInjectionContext(injector, () => new KnightCisComponent());
  return { c, calls };
}

test('componente: carrega, filtra pela legenda, troca de benchmark limpa o perfil e abre o detalhe', () => {
  const { c, calls } = component();
  c.ngOnInit();
  eq(c.loading(), true, 'carregando');
  calls[0].next(view());
  calls[0].complete();
  eq(c.loading(), false, 'carregou');
  eq(c.visible().length, 5, 'todas visíveis');
  c.set('profile', 'E3 Level 1');
  c.setBenchmark('CIS-AZ-6.0.0');
  eq(c.filters().profile, '', 'o perfil é de cada benchmark');
  c.filterBy('CIS-M365-7.0.0', 'NonCompliant');
  eq(c.visible().length, 1, 'legenda filtra benchmark + resultado');
  eq(c.visibleCounts().nonCompliant, 1, 'contagem do recorte');
  c.toggle('CIS-M365-7.0.0:5.2.2.1');
  eq(c.open(), 'CIS-M365-7.0.0:5.2.2.1', 'abre o detalhe');
  c.toggle('CIS-M365-7.0.0:5.2.2.1');
  eq(c.open(), null, 'fecha o detalhe');
  c.clear();
  eq(c.visible().length, 5, 'limpar filtros');
  eq(c.slug('MicrosoftEntraId'), 'entra', 'regra abre o achado na aba da fonte');
});

test('componente: erro com nova tentativa', () => {
  const { c, calls } = component();
  c.load();
  calls[0].error(new Error('Não foi possível carregar a visão CIS Benchmarks.'));
  eq(c.error(), 'Não foi possível carregar a visão CIS Benchmarks.', 'mensagem');
  eq(c.loading(), false, 'parou de carregar');
  c.load();
  eq(c.error(), null, 'nova tentativa limpa o erro');
  calls[1].next(view());
  eq(c.view()?.recommendations.length, 5, 'carregou na segunda');
});

// ---- (5) ordem da página e do detalhe ---------------------------------------------------------------
test('ordem: origem → resumo por benchmark → reconciliação → lista; detalhe nas quatro perguntas', () => {
  const src = fs.readFileSync(path.join(__dirname, '..', '..', '..', 'src', 'app', 'pages', 'knight-cis.component.ts'), 'utf8');
  const at = (s: string) => { const i = src.indexOf(s); ok(i >= 0, `"${s}" ausente do template`); return i; };
  ok(at('Avaliação de origem') < at('Resumo por benchmark'), 'origem antes do resumo');
  ok(at('Resumo por benchmark') < at('Por que os totais CIS diferem'), 'resumo antes da reconciliação');
  ok(at('Por que os totais CIS diferem') < at('id="cis-list"'), 'reconciliação antes da lista');
  ok(at('<h4>O que foi verificado</h4>') < at('<h4>O que encontramos</h4>') && at('<h4>O que encontramos</h4>') < at('<h4>Por que importa</h4>')
    && at('<h4>Por que importa</h4>') < at('<h4>Como corrigir</h4>'), 'quatro perguntas na ordem');
  ok(src.includes('aria-expanded') && src.includes('role="search"') && src.includes('aria-live="polite"'), 'acessibilidade: expansível, busca e recorte anunciado');
});

// ---- (6) limitações agrupadas ------------------------------------------------------------------------
test('limitações da fonte agrupadas por causa, detalhe recolhido', () => {
  const g = groupSourceLimitations([
    'Políticas de reunião do Teams: Autorização recusada — leitura recusada (sintético)',
    'Federação e acesso externo do Teams: Autorização recusada — leitura recusada (sintético)',
    'Configuração do cliente do Teams: Erro de coleta — HTTP 500 em https://example.com/x (sintético)',
  ]);
  eq(g.length, 2, 'duas causas');
  eq(g[0].cause, 'Autorização recusada', 'causa repetida agrupada');
  eq(g[0].items.length, 2, 'duas leituras');
  eq(g[1].items[0].detail, 'HTTP 500 em https://example.com/x (sintético)', 'endpoint fica no detalhe');
});

console.log(`\n${count - failures}/${count} verificações aprovadas`);
if (failures > 0) process.exit(1);

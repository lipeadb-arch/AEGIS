import { NIST_FUNCTION_DESCRIPTIONS, categoryName } from './nist-glossary';
import { BlindSpotRow, GapBalance } from './dashboard.models';

// Espelha AegisScore.Application.Queries.TenantControlStateDto — o contrato de /api/v1/scoring/dashboard.
// Enums viram string na fronteira (nunca o valor numérico do enum C#, que muda ao reordenar o domínio).

/** Status de conformidade de um controle NIST. */
export type ControlStatus = 'Compliant' | 'MitigatedByThirdParty' | 'NonCompliant';

/** Procedência do veredito vigente: Telemetry (autoritativa, até 100%) ou Documentary (teto 50%). */
export type VerdictSource = 'Telemetry' | 'Documentary';

/**
 * Gravidade de um achado — a régua ÚNICA de severidade do produto (espelha o enum SeverityLevel do
 * backend). Mora aqui, no modelo de scoring, porque TODO controle NIST tem severidade; a tela de
 * identidade (identity.models) a reimporta daqui em vez de manter uma segunda escala.
 */
export type SeverityLevel = 'Critical' | 'High' | 'Medium' | 'Low' | 'Informational';

/** Item do checklist técnico que justifica o veredito (espelha ComplianceCheck do backend, camelCase). */
export interface ComplianceCheck {
  name: string;
  passed: boolean;
  details: string;
}

/**
 * Rastro CRU da ferramenta que gerou a não-conformidade (espelha TelemetryEvidence). Não confundir com
 * `aiEvidence` (a prosa interpretada do motor): aqui é o que o EntraID/SentinelOne literalmente emitiu.
 */
export interface TelemetryEvidence {
  sourceTool: string; // "EntraID", "SentinelOne"
  rawTrace: string; // trecho cru do log/JSON (texto multilinha)
  collectedAt: string | null; // ISO 8601; nulo quando a ferramenta não informa
}

/**
 * Natureza da prova que falta para um controle (espelha ComplianceRequirementType do backend, que chega
 * como NOME — nunca como índice de enum). É o eixo que decide o ícone no HUD: rede × pasta.
 *
 * ⚠️ Não confundir com `VerdictSource`: aquele diz o que PRODUZIU o veredito vigente; este diz o que
 * FALTA para prová-lo. Um controle avaliado por telemetria pode estar devendo documentação.
 */
export type ComplianceRequirementType = 'Telemetry' | 'Documentation' | 'Both';

/**
 * Uma lacuna de evidência por trás de uma não-conformidade (espelha MissingRequirementDto). Distingue
 * "o SOC não emite o log" de "a política nunca foi escrita" — ações com donos e prazos diferentes.
 */
export interface MissingRequirement {
  type: ComplianceRequirementType;
  /** Fonte que deveria supri-la: "Entra ID", "Microsoft Sentinel", "MANUAL_AUDIT_REQUIRED". */
  sourceIdentifier: string;
  /** O que falta, em uma frase, redigido pelo motor de avaliação. */
  description: string;
}

/** Ponto da série de conformidade do controle — matéria-prima da sparkline de 30 dias. */
export interface ComplianceHistoryPoint {
  date: string; // "2026-07-16" (DateOnly do backend)
  compliancePercent: number; // 0..100 da célula no dia
}

/**
 * Estado atual de UM controle NIST do tenant, exatamente como /scoring/dashboard entrega (JSON camelCase).
 * Contrato de leitura: o frontend jamais recebe a entidade de domínio crua.
 */
export interface TenantControlStateDto {
  subcategoryId: string;
  subcategoryCode: string; // "PR.AA-01"
  scorePoints: number; // numerador (pontos obtidos)
  maxScorePoints: number; // denominador (peso da subcategoria no catálogo)
  controlStatus: ControlStatus;
  aiEvidence: string | null;
  lastEvaluatedAt: string; // ISO 8601
  lastVerdictSource: VerdictSource;
  checks: ComplianceCheck[]; // checklist técnico que justifica o status (vazio se o motor não decompôs)

  // ---- Enriquecimento para o HUD (o motor de IA preenche; hoje trafega vazio/nulo) ----
  severity: SeverityLevel; // do motor, ou o proxy derivado do status — nunca ausente
  historicalCompliance: ComplianceHistoryPoint[]; // sparkline 30d — VAZIA até existir snapshot por controle
  telemetryEvidence: TelemetryEvidence | null; // rastro cru da ferramenta
  remediationPlan: string | null; // plano inline do LLM (o passo a passo completo é o AdvisoryDto)
  aiConfidenceScore: number | null; // 0..100; nulo em veredito determinístico
  threatLandscape: string[]; // vetores de ataque abertos pela falha
  mttdMinutes: number | null; // tempo médio de detecção (DE/RS/RC)
  mttrMinutes: number | null; // tempo médio de resposta (DE/RS/RC)
  missingRequirements: MissingRequirement[]; // lacunas de prova (vazia em controle conforme ou falha de mérito)
}

// ---- Recomendações de Remediação (Advisories) — espelha /api/v1/scoring/advisories ----

/** Corpo do POST de criação (espelha CreateAdvisoryRequest): só o código NIST-alvo trafega ao servidor. */
export interface GenerateAdvisoryCommand {
  subcategoryCode: string; // "PR.DS-01"
}

/**
 * Advisory devolvido pelo backend (espelha RemediationAdvisoryDto, camelCase). O texto é REDIGIDO pelo
 * motor de IA no servidor — o cliente só escolhe o controle; nunca injeta prosa.
 */
export interface AdvisoryDto {
  id: string;
  subcategoryCode: string;
  title: string;
  documentedRisk: string; // "RiscoDocumentado" — o porquê, em linguagem de risco
  technicalSteps: string; // "PassoAPassoTecnico" — o como fazer, exportável (texto multilinha)
  createdAt: string; // ISO 8601
}

/**
 * Funções NIST que expõem o painel de conformidade por controle. Govern (GV) reusa o MESMO painel embutido
 * na sua Central de Documentos; Identify tem a tela própria de inventário de ativos.
 */
export type PillarKey = 'PR' | 'DE' | 'RS' | 'RC' | 'GV';

/** Metadados estáticos de um pilar — a config que torna os 4 painéis UM só componente (DRY). */
export interface PillarMeta {
  key: PillarKey;
  code: string; // "PR"
  label: string; // "Protect"
  blurb: string; // subtítulo curto (categorias do pilar)
  description: string; // subtítulo tático: o que a Função significa + o que o Aegis mede nela
  /**
   * O HUD deste pilar exibe MTTD/MTTR. Só faz sentido onde há linha do tempo de incidente —
   * Detect/Respond/Recover; não existe "tempo de detecção" de uma política de governança (GV) nem de um
   * inventário (ID). É config, não `if` espalhado: mantém os painéis como UM componente.
   */
  showsResponseMetrics: boolean;
}

// Metadados dos pilares. O `blurb` (categorias) é curto e específico do painel; a `description` (subtítulo
// tático) NÃO é duplicada aqui — deriva do dicionário único NIST_FUNCTION_DESCRIPTIONS (nist-glossary.ts),
// de modo que a MESMA redação alimenta os painéis de pilar, o Govern e a tela de inventário (Identify).
export const PILLARS: Record<PillarKey, PillarMeta> = {
  PR: {
    key: 'PR',
    code: 'PR',
    label: 'Protect',
    blurb: 'Identidade e Acesso · Proteção de Dados · Segurança de Plataforma · Rede e Infraestrutura',
    description: NIST_FUNCTION_DESCRIPTIONS.PR,
    showsResponseMetrics: false,
  },
  DE: {
    key: 'DE',
    code: 'DE',
    label: 'Detect',
    blurb: 'Análise de Eventos · Monitoramento Contínuo',
    description: NIST_FUNCTION_DESCRIPTIONS.DE,
    showsResponseMetrics: true,
  },
  RS: {
    key: 'RS',
    code: 'RS',
    label: 'Respond',
    blurb: 'Gestão e Mitigação de Incidentes',
    description: NIST_FUNCTION_DESCRIPTIONS.RS,
    showsResponseMetrics: true,
  },
  RC: {
    key: 'RC',
    code: 'RC',
    label: 'Recover',
    blurb: 'Plano de Recuperação',
    description: NIST_FUNCTION_DESCRIPTIONS.RC,
    showsResponseMetrics: true,
  },
  GV: {
    key: 'GV',
    code: 'GV',
    label: 'Govern',
    blurb: 'Cadeia de Suprimentos · Papéis e Responsabilidades · Políticas',
    description: NIST_FUNCTION_DESCRIPTIONS.GV,
    showsResponseMetrics: false,
  },
};

/**
 * Gap Analysis de um pilar: a mesma matriz de controles, particionada pela pergunta que o operador faz
 * ("o que o Aegis consegue enxergar?" × "onde ele está cego?").
 *
 * ⚠️ DERIVADA no cliente, sem endpoint novo. O `/scoring/dashboard` já traz status + `missingRequirements`
 * por controle — que é exatamente a matéria-prima. Um endpoint dedicado duplicaria a consulta e criaria
 * uma segunda definição de "coberto" capaz de divergir desta.
 */
export interface PillarGapAnalysis {
  meta: PillarMeta;
  /** Controles com evidência: o motor conseguiu avaliá-los (conformes ou não). */
  covered: ControlView[];
  /** Pontos cegos: controles cuja reprovação é por FALTA DE PROVA, não por falha de prática. */
  blindSpots: ControlView[];
  /** Total de lacunas de telemetria entre os pontos cegos (inclui as de dupla evidência). */
  telemetryGaps: number;
  /** Total de lacunas documentais entre os pontos cegos (inclui as de dupla evidência). */
  documentationGaps: number;
}

/**
 * Particiona os controles de um pilar entre COBERTO e PONTO CEGO. O critério é a existência de
 * `missingRequirements` — e não o status: um controle `NonCompliant` cuja telemetria CHEGOU e reprovou
 * está coberto (o Aegis o enxerga, ele é que está mal). Cego é o controle que o Aegis não consegue
 * avaliar por falta de fonte. Confundir os dois mandaria o operador ligar conector já funcionando.
 */
export function buildPillarGapAnalysis(meta: PillarMeta, dtos: TenantControlStateDto[]): PillarGapAnalysis {
  const all = buildPillarView(meta, dtos).controls;
  const isBlind = (c: ControlView) => c.missingGroups.length > 0;

  const blindSpots = all.filter(isBlind);
  const countOf = (types: ComplianceRequirementType[]) =>
    blindSpots.reduce(
      (n, c) => n + c.missingGroups.filter((g) => types.includes(g.type)).reduce((m, g) => m + g.items.length, 0),
      0,
    );

  return {
    meta,
    covered: all.filter((c) => !isBlind(c)),
    blindSpots,
    // 'Both' conta nos DOIS lados: é uma pendência que só fecha com as duas provas.
    telemetryGaps: countOf(['Telemetry', 'Both']),
    documentationGaps: countOf(['Documentation', 'Both']),
  };
}

/**
 * Token do catálogo NIST SP 800-53 que marca "telemetria não prova este controle sozinha" (espelha
 * `RuleEvaluator.ManualAuditToken`). Chega como `sourceIdentifier` e NUNCA deve ser exibido cru.
 */
export const MANUAL_AUDIT_TOKEN = 'MANUAL_AUDIT_REQUIRED';

/**
 * Rótulo da fonte para exibição. Vive AQUI, e não em cada componente, porque já vazou uma vez: o
 * `MissingRequirementsComponent` traduzia com uma constante local e o painel de balanço, escrito
 * depois, mostrou `MANUAL_AUDIT_REQUIRED` cru na vitrine executiva. Vocabulário de máquina na tela do
 * board é ruído — e a segunda cópia da regra é como ele chega lá.
 */
export function sourceLabelOf(sourceIdentifier: string): string {
  return sourceIdentifier === MANUAL_AUDIT_TOKEN ? 'Auditoria Manual' : sourceIdentifier;
}

/**
 * Agrega TODAS as lacunas de evidência do tenant no balanço CAPEX × OPEX, respondendo à pergunta
 * orçamentária da diretoria: *"o que falta se compra ou se escreve?"*.
 *
 * Lacuna de TELEMETRIA fecha com ferramenta/conector (capex, projeto de engenharia); lacuna de
 * DOCUMENTAÇÃO fecha com processo e gente (opex, trabalho de governança). Saber a proporção decide
 * para onde vai o orçamento — e é informação que o backend já entrega, mas ninguém consolidava.
 *
 * ⚠️ `Both` conta nos DOIS lados: é uma pendência que só fecha com as duas provas, então onera os dois
 * orçamentos. Por isso `telemetryCount + documentationCount` pode passar de `total` — o denominador do
 * percentual é a soma dos lados, não o total de controles.
 *
 * O Top N ordena por PONTOS NIST em jogo (peso do controle), não por contagem: fechar um controle de
 * peso 20 vale mais que dois de peso 5. Função PURA — testável e sem Angular.
 */
export function buildGapBalance(dtos: TenantControlStateDto[], topN = 3): GapBalance {
  const blind = dtos.filter((d) => (d.missingRequirements ?? []).length > 0);

  let telemetryCount = 0;
  let documentationCount = 0;

  for (const d of blind) {
    for (const m of d.missingRequirements ?? []) {
      if (m.type === 'Telemetry' || m.type === 'Both') telemetryCount++;
      if (m.type === 'Documentation' || m.type === 'Both') documentationCount++;
    }
  }

  const sides = telemetryCount + documentationCount;
  const pct = (n: number) => (sides === 0 ? 0 : Math.round((100 * n) / sides));

  const topBlindSpots: BlindSpotRow[] = blind
    .map((d) => {
      // A natureza DOMINANTE do controle: 'Both' manda, senão a do primeiro item.
      const gaps = d.missingRequirements ?? [];
      const both = gaps.find((g) => g.type === 'Both');
      const chosen = both ?? gaps[0];
      return {
        code: d.subcategoryCode,
        label: categoryName(d.subcategoryCode),
        nature: chosen.type,
        sourceIdentifier: sourceLabelOf(chosen.sourceIdentifier),
        // O que se perde mantendo o controle cego: o peso ainda NÃO conquistado.
        pointsAtStake: Math.max(0, d.maxScorePoints - d.scorePoints),
      };
    })
    .sort((a, b) => b.pointsAtStake - a.pointsAtStake || a.code.localeCompare(b.code))
    .slice(0, topN);

  return {
    telemetryCount,
    documentationCount,
    total: blind.length,
    telemetryPct: pct(telemetryCount),
    documentationPct: pct(documentationCount),
    topBlindSpots,
  };
}

/**
 * Projeta a série do Aegis Score (`/scoring/trend`) no formato que o `SparklineComponent` consome.
 * O componente foi escrito para a série POR CONTROLE (`ComplianceHistoryPoint`); a do tenant tem os
 * mesmos dois eixos com outros nomes, então a adaptação é um `map` — não um segundo componente.
 */
export function trendToSparkline(
  trend: { snapshotDate: string; percentage: number }[],
): ComplianceHistoryPoint[] {
  return trend.map((t) => ({ date: t.snapshotDate, compliancePercent: Math.round(t.percentage) }));
}

/** Prefixo do código NIST de um pilar: 'PR' → "PR." (casa "PR.AA-01" mas não "PRX"). */
export function pillarPrefix(key: PillarKey): string {
  return `${key}.`;
}

/**
 * Controle já projetado para a UI (o que os Dumb Components consomem — nunca o DTO cru): status tipado,
 * percentual da célula e categoria derivada do código ("PR.AA-01" → "PR.AA").
 */
export interface ControlView {
  code: string;
  category: string; // "PR.AA"
  status: ControlStatus;
  scorePoints: number;
  maxScorePoints: number;
  pct: number; // 0..100 da célula
  evidence: string | null;
  source: VerdictSource;
  evaluatedAt: string;
  checks: ComplianceCheck[]; // decomposição técnica do veredito, exibida no accordion do card
  severity: SeverityLevel; // tinge o badge do card
  history: ComplianceHistoryPoint[]; // sparkline 30d (vazia ⇒ o card a omite)
  telemetryEvidence: TelemetryEvidence | null;
  remediationPlan: string | null;
  aiConfidence: number | null; // 0..100
  threatLandscape: string[];
  mttdMinutes: number | null;
  mttrMinutes: number | null;
  /** Lacunas de PROVA, já agrupadas por natureza — o card renderiza um bloco por grupo. */
  missingGroups: MissingRequirementGroup[];
}

/**
 * Lacunas de uma mesma natureza, com a apresentação já resolvida. Agrupar no MODELO (função pura) e não
 * no template mantém o card burro: ele itera grupos e lê rótulo/ícone/tom prontos, sem `if` por tipo
 * espalhado pelo HTML.
 */
export interface MissingRequirementGroup {
  type: ComplianceRequirementType;
  /** Rótulo da seção: "Telemetria Ausente", "Documentação Ausente". */
  label: string;
  /** Chave do ícone SVG desenhado no componente ('network' | 'folder' | 'both'). */
  icon: 'network' | 'folder' | 'both';
  /** Tom HUD: 'critical' pulsa em vermelho (sensor cego), 'warn' é âmbar (dívida de governança). */
  tone: 'critical' | 'warn';
  items: MissingRequirement[];
}

/** Postura consolidada de um pilar — o que o Smart Component monta e distribui aos Dumb Components. */
export interface PillarView {
  meta: PillarMeta;
  compliancePct: number; // SUM(scorePoints)/SUM(maxScorePoints)*100 (0 se vazio)
  total: number;
  compliant: number;
  partial: number;
  nonCompliant: number;
  controls: ControlView[]; // ordenados: NonCompliant primeiro (o que precisa saltar aos olhos)
  mttdMinutes: number | null; // média dos controles que reportam (null = ninguém reportou)
  mttrMinutes: number | null;
}

/**
 * Projeta o DTO de transporte no modelo de view (deriva categoria + percentual da célula). Os campos de
 * enriquecimento usam `??` de propósito: o backend pode ser mais antigo que este frontend (ou o motor não
 * ter emitido o bloco) e o card precisa degradar seção a seção, nunca quebrar.
 */
export function toControlView(d: TenantControlStateDto): ControlView {
  const lastDash = d.subcategoryCode.lastIndexOf('-');
  return {
    code: d.subcategoryCode,
    category: lastDash > 0 ? d.subcategoryCode.slice(0, lastDash) : d.subcategoryCode,
    status: d.controlStatus,
    scorePoints: d.scorePoints,
    maxScorePoints: d.maxScorePoints,
    pct: d.maxScorePoints === 0 ? 0 : Math.round((100 * d.scorePoints) / d.maxScorePoints),
    evidence: d.aiEvidence,
    source: d.lastVerdictSource,
    evaluatedAt: d.lastEvaluatedAt,
    checks: d.checks ?? [],
    severity: d.severity ?? severityForStatus(d.controlStatus),
    history: d.historicalCompliance ?? [],
    telemetryEvidence: d.telemetryEvidence ?? null,
    remediationPlan: d.remediationPlan ?? null,
    aiConfidence: d.aiConfidenceScore ?? null,
    threatLandscape: d.threatLandscape ?? [],
    mttdMinutes: d.mttdMinutes ?? null,
    mttrMinutes: d.mttrMinutes ?? null,
    missingGroups: groupMissingRequirements(d.missingRequirements ?? []),
  };
}

/**
 * Metadados de apresentação por natureza de lacuna — o dicionário ÚNICO que decide ícone, rótulo e tom.
 *
 * A escolha de tom é semântica, não decorativa: telemetria ausente é um SENSOR CEGO (não sabemos o
 * estado do controle — vermelho pulsante, urgência de SOC), documentação ausente é DÍVIDA DE PROCESSO
 * (o controle pode até estar implementado — âmbar, alerta de governança). Pintar as duas de vermelho
 * apagaria justamente a distinção que este recurso existe para criar.
 */
const MISSING_PRESENTATION: Record<
  ComplianceRequirementType,
  Omit<MissingRequirementGroup, 'type' | 'items'>
> = {
  Telemetry: { label: 'Telemetria Ausente', icon: 'network', tone: 'critical' },
  Documentation: { label: 'Documentação Ausente', icon: 'folder', tone: 'warn' },
  Both: { label: 'Telemetria e Documentação Ausentes', icon: 'both', tone: 'critical' },
};

/** Ordem de exibição: o sensor cego primeiro — é o que impede o Aegis de avaliar o controle. */
const MISSING_ORDER: ComplianceRequirementType[] = ['Telemetry', 'Both', 'Documentation'];

/**
 * Agrupa as lacunas por natureza, na ordem de urgência, descartando grupos vazios. Função PURA — o
 * componente só a consome; testável sem Angular.
 */
export function groupMissingRequirements(items: MissingRequirement[]): MissingRequirementGroup[] {
  return MISSING_ORDER.map((type) => ({
    type,
    ...MISSING_PRESENTATION[type],
    items: items.filter((m) => m.type === type),
  })).filter((g) => g.items.length > 0);
}

/**
 * Severidade PROXY derivada do status — espelha SeverityLevels.FromStatus do backend. É a rede de
 * segurança do cliente: o backend já resolve a severidade, mas se o campo faltar o badge não some.
 */
export function severityForStatus(status: ControlStatus): SeverityLevel {
  switch (status) {
    case 'NonCompliant':
      return 'Critical';
    case 'MitigatedByThirdParty':
      return 'Medium';
    case 'Compliant':
      return 'Low';
  }
}

/** NonCompliant primeiro (risco salta aos olhos), depois parcial, depois conforme; empate por código. */
const STATUS_RANK: Record<ControlStatus, number> = { NonCompliant: 0, MitigatedByThirdParty: 1, Compliant: 2 };

/**
 * Agrega os controles de um pilar num PillarView: percentual de conformidade (modelo Aegis Score =
 * SUM(pontos)/SUM(peso)), contagens por status e a lista ordenada por risco. Função PURA — testável e
 * livre de Angular; o Smart Component só a chama dentro de um `computed`.
 */
export function buildPillarView(meta: PillarMeta, dtos: TenantControlStateDto[]): PillarView {
  const controls = dtos
    .map(toControlView)
    .sort((a, b) => STATUS_RANK[a.status] - STATUS_RANK[b.status] || a.code.localeCompare(b.code));

  const sumScore = dtos.reduce((s, d) => s + d.scorePoints, 0);
  const sumMax = dtos.reduce((s, d) => s + d.maxScorePoints, 0);

  return {
    meta,
    compliancePct: sumMax === 0 ? 0 : Math.round((100 * sumScore) / sumMax),
    total: controls.length,
    compliant: controls.filter((c) => c.status === 'Compliant').length,
    partial: controls.filter((c) => c.status === 'MitigatedByThirdParty').length,
    nonCompliant: controls.filter((c) => c.status === 'NonCompliant').length,
    controls,
    mttdMinutes: averageOf(controls.map((c) => c.mttdMinutes)),
    mttrMinutes: averageOf(controls.map((c) => c.mttrMinutes)),
  };
}

/**
 * Média dos valores REPORTADOS, ignorando os nulos — e `null` quando ninguém reportou. Tratar nulo como
 * zero afundaria a média e faria o HUD anunciar uma detecção instantânea que não existe: em métrica de
 * SOC, "não medido" e "zero minutos" são coisas opostas.
 */
function averageOf(values: (number | null)[]): number | null {
  const known = values.filter((v): v is number => v !== null);
  if (known.length === 0) return null;
  return Math.round(known.reduce((s, v) => s + v, 0) / known.length);
}

/**
 * Formata minutos no idioma do SOC ("18 min", "2h 30m", "1d 4h") — e "—" quando não há medição. Função
 * PURA de apresentação, ao lado do modelo que a alimenta (mesmo padrão do glossário NIST).
 */
export function formatDuration(minutes: number | null): string {
  if (minutes === null) return '—';
  if (minutes < 60) return `${minutes} min`;

  const hours = Math.floor(minutes / 60);
  const mins = minutes % 60;
  if (hours < 24) return mins === 0 ? `${hours}h` : `${hours}h ${mins}m`;

  const days = Math.floor(hours / 24);
  const restHours = hours % 24;
  return restHours === 0 ? `${days}d` : `${days}d ${restHours}h`;
}

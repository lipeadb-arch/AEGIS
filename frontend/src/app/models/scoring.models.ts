// Espelha AegisScore.Application.Queries.TenantControlStateDto — o contrato de /api/v1/scoring/dashboard.
// Enums viram string na fronteira (nunca o valor numérico do enum C#, que muda ao reordenar o domínio).

/** Status de conformidade de um controle NIST. */
export type ControlStatus = 'Compliant' | 'MitigatedByThirdParty' | 'NonCompliant';

/** Procedência do veredito vigente: Telemetry (autoritativa, até 100%) ou Documentary (teto 50%). */
export type VerdictSource = 'Telemetry' | 'Documentary';

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
}

export const PILLARS: Record<PillarKey, PillarMeta> = {
  PR: { key: 'PR', code: 'PR', label: 'Protect', blurb: 'Identity · Data · Platform · Network' },
  DE: { key: 'DE', code: 'DE', label: 'Detect', blurb: 'Anomalies · Monitoring · Detection Engineering' },
  RS: { key: 'RS', code: 'RS', label: 'Respond', blurb: 'Incident Analysis · Mitigation' },
  RC: { key: 'RC', code: 'RC', label: 'Recover', blurb: 'Recovery Plan Execution' },
  GV: { key: 'GV', code: 'GV', label: 'Govern', blurb: 'Supply Chain · Roles & Responsibilities · Policy' },
};

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
}

/** Projeta o DTO de transporte no modelo de view (deriva categoria + percentual da célula). */
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
  };
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
  };
}

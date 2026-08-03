// Modelos da tela AEGIS KNIGHT — espelham o contrato de /api/v1/knight/assessments (camelCase). Enums viajam
// como NOME (nunca ordinal). Funções PURAS de apresentação (testáveis, sem Angular). O score aqui é o score
// KNIGHT (fórmula própria), ANULÁVEL e DISTINTO do AEGIS Score geral.

import { SeverityLevel } from './scoring.models';

export type { SeverityLevel };

export type KnightMode = 'Demo' | 'Live';
export type KnightRunStatus = 'Pending' | 'Running' | 'Completed' | 'Failed';

/** Veredito determinístico de um indicador. Falta de dado é NotEvaluated (reduz cobertura), nunca aprovação. */
export type KnightIndicatorStatus =
  | 'Passed'
  | 'Exposed'
  | 'Mitigated'
  | 'NotEvaluated'
  | 'Error'
  | 'NotApplicable';

export type KnightCategory =
  | 'PrivilegedAccess'
  | 'IdentityGovernance'
  | 'AccountHygiene'
  | 'GuestAccess'
  | 'ServiceAccounts';

/** Estado de conexão exibido no badge: separação inequívoca entre Demo, Não configurado e Conectado. */
export type KnightConnectionState = 'Demo' | 'NotConfigured' | 'Connected';

export interface KnightIndicator {
  indicatorId: string; // "AK-ENTRA-001"
  title: string;
  category: KnightCategory;
  severity: SeverityLevel;
  status: KnightIndicatorStatus;
  evidence: string;
  affectedObjectCount: number;
  nistCodes: string[];
  mitreTechniques: string[];
  recommendation: string;
  collectedAt: string; // ISO 8601
}

export interface KnightCounts {
  passed: number;
  exposed: number;
  mitigated: number;
  notEvaluated: number;
  error: number;
  notApplicable: number;
}

export interface KnightPriorityRisk {
  title: string;
  rationale: string;
  indicatorIds: string[];
}

export interface KnightRecommendedAction {
  order: number;
  action: string;
  indicatorIds: string[];
}

export interface KnightCorrelation {
  description: string;
  indicatorIds: string[];
}

/** Interpretação/priorização ASSISTIDA POR IA (ou fallback determinístico). Nunca contém veredito/score. */
export interface KnightAdvisory {
  executiveSummary: string;
  priorityRisks: KnightPriorityRisk[];
  recommendedActions: KnightRecommendedAction[];
  correlations: KnightCorrelation[];
  collectionGaps: string[];
}

export interface KnightAssessment {
  id: string;
  mode: KnightMode;
  isDemo: boolean;
  source: string;
  status: KnightRunStatus;
  catalogVersion: string; // "ak-knight-v1"
  scoreFormulaVersion: string; // "knight-score-v1"
  startedAt: string; // ISO 8601
  completedAt: string | null;
  score: number | null; // score KNIGHT — null = sem avaliação (nunca 0 por ausência)
  coverage: number; // 0..100
  counts: KnightCounts;
  indicators: KnightIndicator[];
  advisory: KnightAdvisory | null;
  advisoryFromAi: boolean; // true = IA; false = fallback determinístico
}

/** Badge: sem assessment → NÃO CONFIGURADO; demo → DEMONSTRAÇÃO; real → CONECTADO. */
export function connectionStateOf(a: KnightAssessment | null): KnightConnectionState {
  if (!a) return 'NotConfigured';
  return a.isDemo ? 'Demo' : 'Connected';
}

export function connectionBadgeLabel(state: KnightConnectionState): string {
  switch (state) {
    case 'Demo':
      return 'DEMONSTRAÇÃO';
    case 'Connected':
      return 'CONECTADO';
    case 'NotConfigured':
      return 'NÃO CONFIGURADO';
  }
}

const STATUS_LABEL: Record<KnightIndicatorStatus, string> = {
  Passed: 'Conforme',
  Exposed: 'Exposto',
  Mitigated: 'Mitigado',
  NotEvaluated: 'Não avaliado',
  Error: 'Erro',
  NotApplicable: 'Não aplicável',
};

export function statusLabel(status: KnightIndicatorStatus): string {
  return STATUS_LABEL[status];
}

const SEVERITY_LABEL: Record<SeverityLevel, string> = {
  Critical: 'Crítica',
  High: 'Alta',
  Medium: 'Média',
  Low: 'Baixa',
  Informational: 'Informativa',
};

export function severityLabel(severity: SeverityLevel): string {
  return SEVERITY_LABEL[severity];
}

const CATEGORY_LABEL: Record<KnightCategory, string> = {
  PrivilegedAccess: 'Acesso privilegiado',
  IdentityGovernance: 'Governança de identidade',
  AccountHygiene: 'Higiene de contas',
  GuestAccess: 'Acesso de convidados',
  ServiceAccounts: 'Contas de serviço',
};

export function categoryLabel(category: KnightCategory): string {
  return CATEGORY_LABEL[category];
}

/** Ordem de exibição: exposto primeiro (salta aos olhos), depois mitigado, conforme e o restante; empate por ID. */
const STATUS_RANK: Record<KnightIndicatorStatus, number> = {
  Exposed: 0,
  Mitigated: 1,
  Passed: 2,
  NotEvaluated: 3,
  Error: 4,
  NotApplicable: 5,
};

export function sortIndicatorsByRisk(indicators: KnightIndicator[]): KnightIndicator[] {
  return [...indicators].sort(
    (a, b) => STATUS_RANK[a.status] - STATUS_RANK[b.status] || a.indicatorId.localeCompare(b.indicatorId),
  );
}

/** Score para exibição: "—" quando null (sem avaliação), nunca "0". */
export function scoreDisplay(score: number | null): string {
  return score === null ? '—' : String(Math.round(score));
}

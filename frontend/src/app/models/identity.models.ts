// Modelos da tela de Postura de Identidade (Entra ID). Espelham o contrato de
// POST /api/v1/telemetry/identity/entra-id (lista de TelemetryVerdictDto, camelCase) e projetam-no na
// "tabela tática de exposição" que os Dumb Components consomem. Funções PURAS (testáveis, sem Angular).

import { ControlStatus } from './scoring.models';

/** Nível de severidade (escala Purple Knight). O SeverityComponent o renderiza com cor HUD. */
export type SeverityLevel = 'Critical' | 'High' | 'Medium' | 'Low' | 'Informational';

/** Plataforma de identidade de origem do achado — decide o ícone da coluna Platform. */
export type IdentityPlatform = 'Entra' | 'AD' | 'Okta';

/**
 * Corpo do POST de análise: as MÉTRICAS vêm do provider (Graph/stub) no servidor; daqui só sobe o
 * CONTEXTO que o Entra não conhece — o isolamento de rede dos ativos OT/legado (controle compensatório).
 */
export interface EntraIdIngestionRequest {
  tenantDomain?: string;
  hasNetworkIsolation: boolean;
  compensatingControls?: string[];
}

/** Veredito de UM controle de identidade devolvido pela análise (espelha TelemetryVerdictDto do backend). */
export interface IdentityVerdict {
  subcategoryCode: string; // "PR.AA-01" | "GV.RR-01"
  status: ControlStatus;
  awardedScore: number;
  maxScorePoints: number;
  percentage: number;
  aiEvidence: string;
}

/** Uma linha da tabela tática de "Identity Exposure" (o que o Dumb Component recebe, nunca o DTO cru). */
export interface IdentityFinding {
  code: string; // controle NIST de origem ("PR.AA-01")
  name: string; // rótulo legível estilo-indicador (Purple Knight)
  platform: IdentityPlatform;
  severity: SeverityLevel;
  status: ControlStatus;
  evidence: string; // aiEvidence — traz os números e o motivo da mitigação
  compensated: boolean; // true quando MitigatedByThirdParty (controle compensatório de rede)
}

/** Postura de identidade consolidada — o que o Smart Component monta e distribui aos Dumb Components. */
export interface IdentityPostureView {
  posturePct: number; // SUM(pontos)/SUM(peso)*100 dos controles de identidade (modelo Aegis Score)
  findings: IdentityFinding[]; // ordenados por risco (NonCompliant primeiro)
  total: number;
  compliant: number;
  compensated: number;
  nonCompliant: number;
}

/**
 * Metadados de apresentação por controle de identidade: o nome legível (estilo indicador do Purple Knight)
 * e a plataforma. Extensível — quando o backend enriquecer a análise com mais controles (higiene de
 * convidados, etc.), basta adicionar aqui; a tabela renderiza N linhas sem mudança.
 */
const CONTROL_META: Record<string, { name: string; platform: IdentityPlatform }> = {
  'PR.AA-01': { name: 'Contas Privilegiadas · Cobertura de MFA', platform: 'Entra' },
  'GV.RR-01': { name: 'Governança de Identidade · Menor Privilégio', platform: 'Entra' },
};

/** Severidade derivada do veredito (proxy: não-conforme = crítico; mitigado = médio; conforme = baixo). */
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

/** NonCompliant primeiro (salta aos olhos), depois compensado, depois conforme; empate por código. */
const STATUS_RANK: Record<ControlStatus, number> = { NonCompliant: 0, MitigatedByThirdParty: 1, Compliant: 2 };

/** Projeta um veredito de controle numa linha de achado da tabela tática. */
export function toIdentityFinding(v: IdentityVerdict): IdentityFinding {
  const meta = CONTROL_META[v.subcategoryCode] ?? { name: v.subcategoryCode, platform: 'Entra' as IdentityPlatform };
  return {
    code: v.subcategoryCode,
    name: meta.name,
    platform: meta.platform,
    severity: severityForStatus(v.status),
    status: v.status,
    evidence: v.aiEvidence,
    compensated: v.status === 'MitigatedByThirdParty',
  };
}

/**
 * Agrega os vereditos numa postura de identidade consolidada (modelo Aegis Score = SUM(pontos)/SUM(peso)).
 * Função PURA — o Smart Component só a chama dentro de um `computed`.
 */
export function buildIdentityPostureView(verdicts: IdentityVerdict[]): IdentityPostureView {
  const findings = verdicts
    .map(toIdentityFinding)
    .sort((a, b) => STATUS_RANK[a.status] - STATUS_RANK[b.status] || a.code.localeCompare(b.code));

  const sumScore = verdicts.reduce((s, v) => s + v.awardedScore, 0);
  const sumMax = verdicts.reduce((s, v) => s + v.maxScorePoints, 0);

  return {
    posturePct: sumMax === 0 ? 0 : Math.round((100 * sumScore) / sumMax),
    findings,
    total: findings.length,
    compliant: findings.filter((f) => f.status === 'Compliant').length,
    compensated: findings.filter((f) => f.status === 'MitigatedByThirdParty').length,
    nonCompliant: findings.filter((f) => f.status === 'NonCompliant').length,
  };
}

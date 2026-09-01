/**
 * [AEGIS-MVP-GOOGLE-SECOPS-02] Modelos de leitura da COBERTURA DE DETECÇÃO (regras do SIEM × MITRE ATT&CK).
 * Espelham o contrato `GET /api/v1/detection-coverage` — só agregados seguros; nunca nome/texto de regra,
 * credencial ou payload. É CONSULTIVO: configuração de regras não altera o AEGIS Score.
 */

/** Estado geral da visão — o componente escolhe a tela por ele. */
export type DetectionCoverageState =
  | 'NotConfigured'
  | 'NeverSynced'
  | 'Available'
  | 'Partial'
  | 'Unavailable';

export interface DetectionCoverageTactic {
  id: string;
  name: string;
}

export interface DetectionCoverageTechnique {
  techniqueId: string;
  name: string;
  isSubtechnique: boolean;
  parentTechniqueId: string | null;
  tactics: DetectionCoverageTactic[];
  ruleCount: number;
  liveRuleCount: number;
  alertingRuleCount: number;
  statusLabel: string;
  needsAttention: boolean;
}

export interface DetectionCoverageSummary {
  activeRules: number;
  rulesWithMitre: number;
  rulesWithoutMitre: number;
  rulesInLiveMode: number;
  rulesWithAlerting: number;
  techniquesObserved: number;
  techniquesNeedingAttention: number;
}

export interface DetectionCoverageView {
  state: DetectionCoverageState;
  source: string | null;
  attackVersion: string;
  attackLabel: string;
  storedCollectionState: string | null;
  lastAttemptState: string;
  lastCollectionAt: string | null;
  lastAttemptAt: string | null;
  summary: DetectionCoverageSummary;
  techniques: DetectionCoverageTechnique[];
  affectsScore: boolean;
  scoreDisclaimer: string;
}

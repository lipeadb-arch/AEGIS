// Espelha os DTOs de Govern do backend (Document Hub + Auditor Virtual / GRC).
// Fonte: Contracts/Dtos.cs (seções `// ---- Govern ----`) e Domain/Common.cs (enums).
// Os enums são serializados pela API como o NOME do valor (ex.: "Politica", "Analyzed").

// ---- Enums (string unions espelhando Domain/Common.cs) ----

/** Tipo documental de compliance (GovernanceDocumentType). */
export type GovernanceDocumentType =
  | 'Politica'
  | 'Norma'
  | 'Diretriz'
  | 'Procedimento'
  | 'Contrato'
  | 'Outro';

/** Origem do documento no hub (DocumentSource). */
export type DocumentSource = 'UploadManual' | 'Integracao';

/** Estágio do pipeline de leitura pela IA (AiAnalysisStatus) — o "Status de Leitura da IA" da UI. */
export type AiAnalysisStatus = 'Pending' | 'Queued' | 'Processing' | 'Analyzed' | 'Failed';

/** Validade documental / lifecycle, independente da leitura da IA (GovernanceStatus). */
export type GovernanceStatus = 'Rascunho' | 'Vigente' | 'EmRevisao' | 'Expirado' | 'Descontinuado';

/** Estado de cobertura derivado por subcategoria (CoverageStatus). */
export type CoverageStatus = 'NaoCoberto' | 'Parcial' | 'Coberto';

/** Fonte da evidência que sustenta a cobertura — governança híbrida (CoverageEvidenceSource). */
export type EvidenceSource = 'None' | 'Document' | 'Interview' | 'Both';

/** Situação de uma sessão do Auditor Virtual (GrcInterviewStatus). */
export type InterviewStatus = 'Active' | 'Completed' | 'Abandoned';

/** Autor de uma mensagem na entrevista GRC (GrcMessageRole). */
export type MessageRole = 'System' | 'Assistant' | 'User';

// ---- Document Hub ----
export interface DocumentMapping {
  subcategoryCode: string;
  confidence: number;
  /** Trecho LITERAL validado (citação verbatim que sustenta o mapeamento). Nulo = herança não probatória. */
  evidenceQuote: string | null;
  /** Racional da análise (separado do trecho literal). */
  evidence: string | null;
  analystConfirmed: boolean;
}

export interface GovernanceDocument {
  id: string;
  title: string;
  type: GovernanceDocumentType;
  source: DocumentSource;
  sourceReference: string | null;
  fileName: string | null;
  contentType: string | null;
  fileSizeBytes: number | null;
  sha256: string | null;
  documentDate: string | null;
  status: GovernanceStatus;
  analysisStatus: AiAnalysisStatus;
  analysisSummary: string | null;
  analysisError: string | null;
  analyzedAt: string | null;
  mappings: DocumentMapping[];
}

/** Resposta 202 do upload / reanalyze (DocumentAcceptedDto). */
export interface DocumentAccepted {
  id: string;
  analysisStatus: AiAnalysisStatus;
}

/** Resposta 202 do gatilho manual de sincronização de políticas corporativas (PolicySyncAcceptedDto). */
export interface PolicySyncAccepted {
  tenantId: string;
  status: string;
  message: string;
}

/** Registra um documento vindo de integração — SharePoint/Confluence (ConnectDocumentRequest). */
export interface ConnectDocumentRequest {
  title: string;
  type: GovernanceDocumentType;
  sourceReference: string;
}

/** Human-in-the-loop: confirma/ajusta um mapeamento sugerido pela IA (ConfirmMappingRequest). */
export interface ConfirmMappingRequest {
  confirmed: boolean;
  confidence?: number | null;
}

// ---- Cobertura híbrida (documentos + entrevistas) ----
export interface CoverageCell {
  code: string;
  description: string;
  status: CoverageStatus;
  evidenceSource: EvidenceSource;
}
export interface GovernCategoryCoverage {
  code: string;
  name: string;
  subcategories: CoverageCell[];
}
export interface GovernCoverage {
  coveredPct: number;
  partialPct: number;
  categories: GovernCategoryCoverage[];
}
export interface Gap {
  code: string;
  description: string;
  status: CoverageStatus;
}

// ---- Auditor Virtual (GRC) ----
export interface InterviewMessage {
  id: string;
  role: MessageRole;
  content: string;
  sequence: number;
  targetSubcategoryCode: string | null;
  sentAt: string;
}
export interface InterviewSession {
  id: string;
  title: string;
  status: InterviewStatus;
  targetSubcategoryCodes: string[];
  startedAt: string;
  messages: InterviewMessage[];
}
export interface CoverageChange {
  subcategoryCode: string;
  status: CoverageStatus;
  evidenceSource: EvidenceSource;
}
export interface InterviewTurn {
  sessionId: string;
  question: InterviewMessage | null;
  isComplete: boolean;
  coverageChange: CoverageChange | null;
  identifiedRiskId: string | null;
}

/** Abre uma sessão do Auditor Virtual (StartInterviewRequest). */
export interface StartInterviewRequest {
  title?: string | null;
  assessmentId?: string | null;
  subcategoryCodes?: string[] | null;
}

/** Resposta do usuário a uma pergunta da entrevista (PostAnswerRequest). */
export interface PostAnswerRequest {
  content: string;
}

/** Risco identificado ao confirmar uma lacuna na prática — Descrição/Causa/Consequência (IdentifiedRiskDto). */
export interface IdentifiedRisk {
  id: string;
  title: string;
  description: string;
  cause: string | null;
  consequence: string | null;
  subcategoryCode: string;
  assessmentId: string | null;
  promotedToRisk: boolean;
  identifiedAt: string;
}

// ---- Rótulos PT para a UI ----

/** Tipos de documento para o dropdown de upload/ingestão (valor da API → rótulo PT). */
export const DOCUMENT_TYPES: ReadonlyArray<{ value: GovernanceDocumentType; label: string }> = [
  { value: 'Politica', label: 'Política' },
  { value: 'Norma', label: 'Norma' },
  { value: 'Diretriz', label: 'Diretriz' },
  { value: 'Procedimento', label: 'Procedimento' },
  { value: 'Contrato', label: 'Contrato' },
  { value: 'Outro', label: 'Outro' },
];

/** Status da análise para o filtro + a pill de status (valor da API → rótulo PT). */
export const ANALYSIS_STATUSES: ReadonlyArray<{ value: AiAnalysisStatus; label: string }> = [
  { value: 'Pending', label: 'Aguardando processamento' },
  { value: 'Queued', label: 'Na fila' },
  { value: 'Processing', label: 'Analisando' },
  { value: 'Analyzed', label: 'Analisado' },
  { value: 'Failed', label: 'Falha na análise' },
];

/** Estados NÃO terminais da análise — enquanto houver algum, a UI faz polling. */
export const ACTIVE_ANALYSIS_STATUSES: ReadonlyArray<AiAnalysisStatus> = ['Pending', 'Queued', 'Processing'];

/** True se o status é ativo (a análise ainda vai mudar) — controla polling e spinner. */
export function isActiveAnalysisStatus(status: AiAnalysisStatus): boolean {
  return ACTIVE_ANALYSIS_STATUSES.includes(status);
}

/** True se o documento tem ao menos um mapeamento com TRECHO PROBATÓRIO literal (evidenceQuote). */
export function hasProbativeEvidence(doc: GovernanceDocument): boolean {
  return doc.mappings.some((m) => !!m.evidenceQuote && m.evidenceQuote.trim().length > 0);
}

/**
 * Estado de exibição REFINADO do documento: separa "Analisado com evidência" de "Analisado sem evidência"
 * (documento sem valor probatório) — os demais estados espelham o status de leitura da IA.
 */
export type DocumentDisplayState =
  | 'Pending'
  | 'Queued'
  | 'Processing'
  | 'AnalyzedWithEvidence'
  | 'AnalyzedWithoutEvidence'
  | 'Failed';

export function documentDisplayState(doc: GovernanceDocument): DocumentDisplayState {
  switch (doc.analysisStatus) {
    case 'Analyzed':
      return hasProbativeEvidence(doc) ? 'AnalyzedWithEvidence' : 'AnalyzedWithoutEvidence';
    case 'Failed':
      return 'Failed';
    case 'Processing':
      return 'Processing';
    case 'Queued':
      return 'Queued';
    default:
      return 'Pending';
  }
}

const DISPLAY_STATE_LABELS = new Map<DocumentDisplayState, string>([
  ['Pending', 'Aguardando processamento'],
  ['Queued', 'Na fila'],
  ['Processing', 'Analisando'],
  ['AnalyzedWithEvidence', 'Analisado com evidência'],
  ['AnalyzedWithoutEvidence', 'Analisado sem evidência'],
  ['Failed', 'Falha na análise'],
]);

/** Rótulo PT do estado refinado de exibição (fallback: o próprio valor). */
export function documentDisplayStateLabel(state: DocumentDisplayState): string {
  return DISPLAY_STATE_LABELS.get(state) ?? state;
}

const DOCUMENT_TYPE_LABELS = new Map<string, string>(DOCUMENT_TYPES.map((t) => [t.value, t.label]));
const ANALYSIS_STATUS_LABELS = new Map<string, string>(ANALYSIS_STATUSES.map((s) => [s.value, s.label]));
const COVERAGE_STATUS_LABELS = new Map<string, string>([
  ['NaoCoberto', 'Não coberto'],
  ['Parcial', 'Parcial'],
  ['Coberto', 'Coberto'],
]);
const EVIDENCE_SOURCE_LABELS = new Map<string, string>([
  ['None', 'Nenhuma'],
  ['Document', 'Documento'],
  ['Interview', 'Entrevista'],
  ['Both', 'Ambos'],
]);

/** Rótulo PT de um tipo de documento (fallback: o próprio valor). */
export function documentTypeLabel(value: string): string {
  return DOCUMENT_TYPE_LABELS.get(value) ?? value;
}

/** Rótulo PT de um status da análise (fallback: o próprio valor). */
export function analysisStatusLabel(value: string): string {
  return ANALYSIS_STATUS_LABELS.get(value) ?? value;
}

/** Rótulo PT da origem do documento (valor da API → texto legível). */
const DOCUMENT_SOURCE_LABELS = new Map<string, string>([
  ['UploadManual', 'Upload manual'],
  ['Integracao', 'Integração'],
]);

/** Rótulo PT de uma origem de documento (fallback: o próprio valor). */
export function documentSourceLabel(value: string): string {
  return DOCUMENT_SOURCE_LABELS.get(value) ?? value;
}

/** Rótulo PT de um status de cobertura (fallback: o próprio valor). */
export function coverageStatusLabel(value: string): string {
  return COVERAGE_STATUS_LABELS.get(value) ?? value;
}

/** Rótulo PT de uma fonte de evidência (fallback: o próprio valor). */
export function evidenceSourceLabel(value: string): string {
  return EVIDENCE_SOURCE_LABELS.get(value) ?? value;
}

/** Categoria de erro da análise (nome sanitizado do tipo de exceção) → mensagem amigável em PT. */
const ANALYSIS_ERROR_LABELS = new Map<string, string>([
  [
    'AiQuotaExhaustedException',
    'Cota gratuita da IA temporariamente esgotada. Reanalise após a renovação da cota.',
  ],
  ['AiUnavailableException', 'Serviço de IA temporariamente indisponível. Reanalise mais tarde.'],
]);

/**
 * Traduz a CATEGORIA de erro da análise (nome sanitizado do tipo de exceção .NET, ex.:
 * `AiQuotaExhaustedException`) para uma mensagem amigável — NUNCA exibe o nome da exceção ao usuário.
 * Categoria desconhecida ou ausente cai numa mensagem genérica.
 */
export function analysisErrorLabel(category: string | null): string {
  const generic = 'Falha ao analisar o documento. Reanalise mais tarde.';
  return category ? (ANALYSIS_ERROR_LABELS.get(category) ?? generic) : generic;
}

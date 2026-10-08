/**
 * [AEGIS-AUDITOR-CONTEXT-01] Auditor Virtual — UMA identidade em toda a aplicação. A página aberta dá o FOCO (página, avaliação · rodada ·
 * escopo · subcategoria do NIST, avaliação e controle do KNIGHT); o servidor confere tudo pelo tenant do token e monta o contexto. A
 * conversa pertence ao tenant e à conta: trocar de ambiente ou sair encerra a conversa da tela e descarta respostas atrasadas.
 */

/** Páginas conhecidas pelo servidor (o foco não é fronteira de segurança: desconhecida vira "general"). */
export type AuditorPage =
  | 'dashboard'
  | 'knight'
  | 'nist'
  | 'documents'
  | 'assets'
  | 'vulnerabilities'
  | 'priorities'
  | 'recommendations'
  | 'history'
  | 'settings'
  | 'general';

export interface AuditorNistSelection {
  assessmentId: string;
  cycleId: string;
  scopeId: string;
  code: string | null;
}

export interface AuditorKnightFocus {
  runId: string;
  indicatorId: string | null;
}

export interface AuditorFocus {
  page: AuditorPage;
  nist: AuditorNistSelection | null;
  knight: AuditorKnightFocus | null;
}

export const PAGE_LABEL: Record<AuditorPage, string> = {
  dashboard: 'Dashboards',
  knight: 'AEGIS KNIGHT',
  nist: 'AEGIS NIST',
  documents: 'AEGIS NIST · Biblioteca de documentos',
  assets: 'AEGIS NIST · Inventário de ativos',
  vulnerabilities: 'AEGIS NIST · Vulnerabilidades',
  priorities: 'AEGIS NIST · Prioridades de tratamento',
  recommendations: 'AEGIS NIST · Recomendações de postura',
  history: 'Histórico de postura',
  settings: 'Configurações',
  general: 'Visão geral',
};

const NIST_SUPPORT_PAGES: Record<string, AuditorPage> = {
  'gv/documentos': 'documents',
  'id/ativos': 'assets',
  'id/vulnerabilidades': 'vulnerabilities',
  'id/prioridades': 'priorities',
  'pr/recomendacoes': 'recommendations',
};

const CODE = /^[A-Za-z]{2}\.[A-Za-z]{2}-\d{2}$/;
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const INDICATOR = /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/;

/**
 * Lê o foco da URL. A seleção NIST só vale completa (avaliação, rodada e escopo, como GUID); o código só numa subcategoria; a
 * avaliação do KNIGHT só na página do KNIGHT. Valores fora do formato são ignorados aqui — o servidor recusaria (400).
 */
export function auditorFocusFromUrl(url: string): AuditorFocus {
  const [path, rest = ''] = url.split('?');
  const segs = path.split('#')[0].split('/').filter(Boolean);
  const q = new URLSearchParams(rest.split('#')[0]);
  const a = q.get('avaliacao');
  const c = q.get('rodada');
  const s = q.get('escopo');
  const selection = a && c && s && GUID.test(a) && GUID.test(c) && GUID.test(s) ? { assessmentId: a, cycleId: c, scopeId: s } : null;
  const first = segs[0] ?? '';

  if (first === 'nist') {
    const support = NIST_SUPPORT_PAGES[`${segs[1] ?? ''}/${segs[2] ?? ''}`.toLowerCase()];
    const code = !support && segs.length >= 3 && CODE.test(segs[2]) ? segs[2].toUpperCase() : null;
    return { page: support ?? 'nist', nist: selection ? { ...selection, code } : null, knight: null };
  }
  if (first === 'knight') {
    const run = q.get('run');
    const finding = q.get('finding');
    const knight = run && GUID.test(run) ? { runId: run, indicatorId: finding && INDICATOR.test(finding) ? finding : null } : null;
    return { page: 'knight', nist: null, knight };
  }
  if (first === 'dashboard') return { page: 'dashboard', nist: selection ? { ...selection, code: null } : null, knight: null };
  if (first === 'history') return { page: 'history', nist: null, knight: null };
  if (first === 'settings') return { page: 'settings', nist: null, knight: null };
  return { page: 'general', nist: null, knight: null };
}

/** Rótulo do foco antes da resposta do servidor (que devolve o rótulo completo, com os nomes conferidos). */
export function focusHint(f: AuditorFocus): string {
  const base = PAGE_LABEL[f.page];
  if (f.nist?.code) return `${base} · ${f.nist.code}`;
  if (f.knight?.indicatorId) return `${base} · controle ${f.knight.indicatorId}`;
  return base;
}

export interface AuditorSourceLink {
  route: string;
  query: Record<string, string> | null;
  fragment: string | null;
}

export interface AuditorSource {
  key: string;
  module: string;
  nature: string;
  natureLabel: string;
  title: string;
  detail: string | null;
  date: string | null;
  isDemo: boolean;
  limitation: string | null;
  link: AuditorSourceLink | null;
}

/** Resposta do servidor ao turno do Auditor. */
export interface AuditorChatReply {
  reply: string;
  scope: string;
  intent: 'COPILOT';
  metadata: null;
  conversationId: string;
  mode: 'Real' | 'Simulated';
  focusLabel: string;
  sources: AuditorSource[];
  limitations: string[];
  notes: string[];
}

export interface AuditorChatRequest {
  message: string;
  page: AuditorPage;
  nist: AuditorNistSelection | null;
  knight: AuditorKnightFocus | null;
  conversationId: string | null;
}

export function chatRequest(message: string, focus: AuditorFocus, conversationId: string | null): AuditorChatRequest {
  return { message, page: focus.page, nist: focus.nist, knight: focus.knight, conversationId };
}

/** Destino interno da fonte para o routerLink (rota relativa ao app; nunca link externo). */
export function sourceRouterLink(s: AuditorSource): { commands: string[]; queryParams: Record<string, string> | null; fragment: string | undefined } | null {
  const l = s.link;
  if (!l || !l.route.startsWith('/') || l.route.startsWith('//')) return null;
  return { commands: [l.route], queryParams: l.query ?? null, fragment: l.fragment ?? undefined };
}

/** Classe visual por natureza (o que a fonte demonstra). */
export function natureTone(nature: string): string {
  switch (nature) {
    case 'ObservedConfiguration':
    case 'VerificationResult':
    case 'InventoryRecord':
      return 'info';
    case 'Documentation':
    case 'AssessorStatement':
    case 'RecordedFinding':
      return 'violet';
    case 'Unconfirmed':
    case 'NotExamined':
      return 'warn';
    default:
      return 'neutral';
  }
}

/**
 * Sessão da conversa na tela: a resposta só entra se foi pedida NESTA sessão (mesmo tenant, mesma conta, mesma conversa — "Nova
 * conversa", troca de ambiente e saída abrem outra sessão). A troca de página no mesmo ambiente NÃO descarta: a resposta é da pergunta
 * feita naquele foco, e ela é mostrada com o foco em que foi respondida.
 */
export interface AuditorSessionKey {
  tenantId: string | null;
  accountId: string | null;
  epoch: number;
}

export function sameSession(a: AuditorSessionKey, b: AuditorSessionKey): boolean {
  return a.tenantId === b.tenantId && a.accountId === b.accountId && a.epoch === b.epoch;
}

/** Mensagem do servidor para erros conhecidos do chat (o servidor já devolve título em pt-BR, sem dado sensível). */
export function chatErrorMessage(status: number | undefined, serverTitle: string | null): string {
  if (status === 404) return serverTitle || 'A seleção ou a conversa não existe neste ambiente. Inicie uma nova conversa.';
  if (status === 409) return serverTitle || 'Outra resposta foi concluída nesta conversa. Envie de novo.';
  if (status === 400) return serverTitle || 'Não foi possível enviar: confira a mensagem e a seleção da tela.';
  if (status === 429) return 'Limite de perguntas por minuto atingido. Aguarde um instante.';
  if (status === 503 && serverTitle) return serverTitle;
  return 'O Auditor está indisponível no momento. Tente novamente.';
}

// [AEGIS-KNIGHT-MULTICLOUD-01] Sincronização do AEGIS KNIGHT iniciada em Configurações → Integrações.
//
// O pedido é DURÁVEL e IDENTIFICADO desde o primeiro instante (202 com `id`): a tela acompanha AQUELE pedido até
// o desfecho, sem depender do tempo limite do navegador e sem adivinhar qual execução é a dela. Funções PURAS
// (testadas em tests/knight-sync.models.spec.ts).

export type KnightSyncStatus = 'Pending' | 'Running' | 'Completed' | 'Failed';

export interface KnightSyncRequest {
  id: string;
  connectorId: string;
  source: string;
  status: KnightSyncStatus;
  requestedAt: string;
  startedAt: string | null;
  completedAt: string | null;
  attempts: number;
  runId: string | null;
  resultSourceState: string | null;
  failureCategory: string | null;
  message: string | null;
  /** A última avaliação CONCLUÍDA da fonte — o que continua valendo se este pedido falhar. */
  lastCompletedRunId: string | null;
  lastCompletedAt: string | null;
  alreadyActive: boolean;
}

/** Intervalo entre consultas e limite do ACOMPANHAMENTO (não da coleta — ela segue no servidor). */
export const SYNC_POLL_INTERVAL_MS = 3000;
export const SYNC_WATCH_LIMIT_MS = 15 * 60 * 1000;

export function isActiveSync(r: KnightSyncRequest | null | undefined): boolean {
  return !!r && (r.status === 'Pending' || r.status === 'Running');
}

/**
 * Uma resposta só atualiza a tela se for do MESMO tenant ativo, do MESMO conector e do MESMO pedido que a tela
 * está acompanhando — uma resposta atrasada de outro tenant (troca de ambiente) ou de outra execução nunca
 * escreve no estado.
 */
export function isCurrentSyncResponse(
  watching: { tenantId: string | null; connectorId: string; requestId: string } | null,
  currentTenantId: string | null,
  response: KnightSyncRequest,
): boolean {
  return (
    !!watching &&
    watching.tenantId !== null &&
    watching.tenantId === currentTenantId &&
    watching.connectorId === response.connectorId &&
    watching.requestId === response.id
  );
}

export type SyncTone = 'busy' | 'ok' | 'warn' | 'err';

export interface SyncView {
  tone: SyncTone;
  title: string;
  detail: string | null;
  /** Avaliação produzida por ESTE pedido (link direto) — só quando concluído. */
  runId: string | null;
  /** Avaliação anterior que continua valendo — nunca apresentada como resultado deste pedido. */
  previousRunId: string | null;
}

const SOURCE_STATE_TEXT: Record<string, string> = {
  Completed: 'coleta completa',
  PartialCollection: 'coleta parcial — parte das capacidades não pôde ser lida',
  InsufficientPermission: 'sem permissão para as capacidades da fonte',
  AuthenticationFailure: 'falha de autenticação junto à fonte',
  Throttled: 'limite de taxa da fonte',
  Unavailable: 'fonte indisponível',
  Error: 'erro durante a coleta',
};

function fmt(iso: string | null): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (isNaN(d.getTime())) return '';
  const p = (n: number) => String(n).padStart(2, '0');
  return `${p(d.getDate())}/${p(d.getMonth() + 1)}/${d.getFullYear()} ${p(d.getHours())}:${p(d.getMinutes())}`;
}

/** O que a tela diz sobre um pedido — sem prometer nada que o estado não comprove. */
export function syncView(r: KnightSyncRequest): SyncView {
  switch (r.status) {
    case 'Pending':
      return {
        tone: 'busy',
        title: r.alreadyActive ? 'Já havia uma sincronização em andamento — acompanhando a mesma.' : 'Sincronização registrada — aguardando início.',
        detail: `Pedido de ${fmt(r.requestedAt)}. Nenhuma coleta duplicada é disparada.`,
        runId: null,
        previousRunId: null,
      };
    case 'Running':
      return {
        tone: 'busy',
        title: 'Coletando da fonte e avaliando os controles…',
        detail: `Iniciada em ${fmt(r.startedAt ?? r.requestedAt)}${r.attempts > 1 ? ` · tentativa ${r.attempts}` : ''}.`,
        runId: null,
        previousRunId: null,
      };
    case 'Completed': {
      const state = r.resultSourceState ? SOURCE_STATE_TEXT[r.resultSourceState] ?? r.resultSourceState : null;
      const partial = r.resultSourceState && r.resultSourceState !== 'Completed';
      return {
        tone: partial ? 'warn' : 'ok',
        title: `Avaliação concluída em ${fmt(r.completedAt)}${state ? ` · ${state}` : ''}.`,
        detail: partial ? 'Os controles que dependiam das capacidades não lidas ficam “Não avaliado” — nunca aprovados.' : null,
        runId: r.runId,
        previousRunId: null,
      };
    }
    case 'Failed':
    default: {
      const previous = r.lastCompletedRunId && r.lastCompletedRunId !== r.runId ? r.lastCompletedRunId : null;
      return {
        tone: 'err',
        title: r.message ?? 'A sincronização não foi concluída.',
        detail: previous
          ? `Nenhuma avaliação nova foi registrada. A avaliação anterior (${fmt(r.lastCompletedAt)}) continua disponível.`
          : 'Nenhuma avaliação foi registrada.',
        runId: null,
        previousRunId: previous,
      };
    }
  }
}

import { Injectable, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router } from '@angular/router';
import { CoverageChange, IdentifiedRisk } from '../models/governance.models';

/** As seis Funções do NIST CSF 2.0. */
export type NistFunction = 'Govern' | 'Identify' | 'Protect' | 'Detect' | 'Respond' | 'Recover';

/**
 * Contexto ativo do Agente Global: uma das Funções NIST OU 'General' — a visão neutra das rotas
 * sem Função dedicada (dashboard, deep-links, rotas futuras). Substitui o antigo fallback que
 * "prendia" o agente em Govern em qualquer aba fora da Governança.
 */
export type AgentContext = NistFunction | 'General';

/** Escopo de contexto para o backend do Copiloto GRC — o código da tela ativa (Visão Geral → 'GLOBAL'). */
export type AuditorScope = 'GLOBAL' | 'GV' | 'ID' | 'PR' | 'DE' | 'RS' | 'RC';

/** Metadados de apresentação de cada contexto (código oficial + rótulo PT-BR + se há agente real). */
export interface NistContext {
  readonly fn: AgentContext;
  readonly code: string; // GV, ID, PR, DE, RS, RC — ou '—' para a visão geral
  readonly label: string; // rótulo PT-BR
  readonly ready: boolean; // há um agente dedicado por trás? (só GOVERN hoje)
}

const CONTEXTS: Record<AgentContext, NistContext> = {
  Govern: { fn: 'Govern', code: 'GV', label: 'Governar', ready: true },
  Identify: { fn: 'Identify', code: 'ID', label: 'Identificar', ready: false },
  Protect: { fn: 'Protect', code: 'PR', label: 'Proteger', ready: false },
  Detect: { fn: 'Detect', code: 'DE', label: 'Detectar', ready: false },
  Respond: { fn: 'Respond', code: 'RS', label: 'Responder', ready: false },
  Recover: { fn: 'Recover', code: 'RC', label: 'Recuperar', ready: false },
  General: { fn: 'General', code: '—', label: 'Visão Geral', ready: false },
};

/**
 * 1º segmento da rota → contexto do Agente. Só as rotas com Função dedicada são mapeadas;
 * o dashboard e qualquer rota desconhecida caem em 'General' (fallback neutro).
 * Obs.: a rota real do pilar Identify é `/assets`.
 */
const ROUTE_TO_CONTEXT: Record<string, AgentContext> = {
  governance: 'Govern',
  assets: 'Identify',
  protect: 'Protect',
  detect: 'Detect',
  respond: 'Respond',
  recover: 'Recover',
};

/**
 * AgentStateService — estado global do Auditor Virtual (o "Agente Global").
 *
 * Elevado do pilar de Governança para o layout raiz: um único drawer/chat vive no App e
 * este serviço decide (a) se está aberto e (b) qual o contexto NIST ativo, derivado da URL.
 * Também atua como barramento reverso: como o chat deixou de ser filho do Document Hub, as
 * mudanças de cobertura/risco produzidas pela entrevista são publicadas aqui, e as telas que
 * exibem cobertura (ex.: o strip de GOVERN) reagem a esses sinais.
 *
 * Padrão da casa: signals puros, standalone, zero-dependência de terceiros.
 */
@Injectable({ providedIn: 'root' })
export class AgentStateService {
  private readonly router = inject(Router);

  // ---- Contexto ativo (derivado da rota) ----------------------------------

  /**
   * Contexto ativo. Semeado com a URL corrente (cobre o primeiro paint e deep-links) e
   * reprojetado a cada NavigationEnd — determinístico, sem depender do timing de subscribe.
   */
  private readonly _activeFunction = signal<AgentContext>(this.contextForUrl(this.router.url));

  /** Função NIST (ou 'General') correspondente à aba atual. */
  readonly activeFunction = this._activeFunction.asReadonly();

  /** Metadados do contexto ativo (código/rótulo/disponibilidade). */
  readonly context = computed<NistContext>(() => CONTEXTS[this._activeFunction()]);

  /**
   * Escopo enviado ao backend do Copiloto (POST /auditor/chat): o código da tela ativa, com a Visão Geral
   * projetada em 'GLOBAL'. É o que ajusta dinamicamente o System Prompt da IA por Função NIST.
   */
  readonly contextScope = computed<AuditorScope>(() => {
    const c = this.context();
    return c.fn === 'General' ? 'GLOBAL' : (c.code as AuditorScope);
  });

  constructor() {
    // Escuta explicitamente o fim de cada navegação e reprojeta o contexto na aba corrente.
    // takeUntilDestroyed encerra a assinatura junto com o serviço (limpo em testes/HMR);
    // em runtime o serviço é singleton root e vive o tempo todo da app.
    this.router.events.pipe(takeUntilDestroyed()).subscribe((e) => {
      if (e instanceof NavigationEnd) {
        this._activeFunction.set(this.contextForUrl(e.urlAfterRedirects));
      }
    });
  }

  /** Deriva o contexto do 1º segmento da URL; rotas sem Função dedicada → 'General'. */
  private contextForUrl(url: string): AgentContext {
    const seg = url.split(/[?#]/)[0].split('/').filter(Boolean)[0] ?? '';
    return ROUTE_TO_CONTEXT[seg] ?? 'General';
  }

  // ---- Drawer -------------------------------------------------------------

  /** Visibilidade do painel lateral. */
  readonly open = signal(false);

  /** Título do drawer — agora REAGE ao contexto: "Auditor Virtual — Proteger (PR)" fora da visão geral. */
  readonly drawerTitle = computed(() => {
    const c = this.context();
    return c.fn === 'General' ? 'Auditor Virtual' : `Auditor Virtual — ${c.label} (${c.code})`;
  });

  /** Eyebrow do drawer: contextualiza o agente na Função NIST ativa. */
  readonly drawerSubtitle = computed(() => {
    const c = this.context();
    return `Agente GRC · NIST CSF · ${c.label} (${c.code})`;
  });

  openAgent(): void {
    this.open.set(true);
  }

  close(): void {
    this.open.set(false);
  }

  toggle(): void {
    this.open.update((v) => !v);
  }

  // ---- Barramento reverso (entrevista → telas) ----------------------------

  private readonly _coverageVersion = signal(0);
  /** Incrementa a cada mudança de cobertura — telas de cobertura observam para recarregar. */
  readonly coverageVersion = this._coverageVersion.asReadonly();

  /** Última mudança de cobertura publicada pela entrevista. */
  readonly lastCoverageChange = signal<CoverageChange | null>(null);
  /** Último risco identificado — gancho para o futuro módulo de Riscos. */
  readonly lastRiskIdentified = signal<IdentifiedRisk | null>(null);

  /** Chamado pelo Auditor quando uma resposta altera a cobertura de alguma subcategoria. */
  notifyCoverageChanged(change: CoverageChange): void {
    this.lastCoverageChange.set(change);
    this._coverageVersion.update((v) => v + 1);
  }

  /** Chamado pelo Auditor quando uma lacuna é materializada em Risco Identificado. */
  notifyRiskIdentified(risk: IdentifiedRisk): void {
    this.lastRiskIdentified.set(risk);
  }
}

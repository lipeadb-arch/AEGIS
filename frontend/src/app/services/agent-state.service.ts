import { Injectable, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router } from '@angular/router';
import { filter, map } from 'rxjs';
import { CoverageChange, IdentifiedRisk } from '../models/governance.models';

/** As seis Funções do NIST CSF 2.0 — o "contexto ativo" do Agente Global. */
export type NistFunction = 'Govern' | 'Identify' | 'Protect' | 'Detect' | 'Respond' | 'Recover';

/** Metadados de apresentação de cada Função (código oficial + rótulo PT-BR + se há agente real). */
export interface NistContext {
  readonly fn: NistFunction;
  readonly code: string; // GV, ID, PR, DE, RS, RC
  readonly label: string; // rótulo PT-BR
  readonly ready: boolean; // há um agente dedicado por trás? (só GOVERN hoje)
}

const CONTEXTS: Record<NistFunction, NistContext> = {
  Govern: { fn: 'Govern', code: 'GV', label: 'Governar', ready: true },
  Identify: { fn: 'Identify', code: 'ID', label: 'Identificar', ready: false },
  Protect: { fn: 'Protect', code: 'PR', label: 'Proteger', ready: false },
  Detect: { fn: 'Detect', code: 'DE', label: 'Detectar', ready: false },
  Respond: { fn: 'Respond', code: 'RS', label: 'Responder', ready: false },
  Recover: { fn: 'Recover', code: 'RC', label: 'Recuperar', ready: false },
};

/** 1º segmento da rota → Função NIST. Rotas não mapeadas caem em GOVERN (fallback). */
const ROUTE_TO_FUNCTION: Record<string, NistFunction> = {
  governance: 'Govern',
  assets: 'Identify',
  dashboard: 'Govern',
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
 * Padrão da casa: signals puros, zero-dependência.
 */
@Injectable({ providedIn: 'root' })
export class AgentStateService {
  private readonly router = inject(Router);

  // ---- Contexto ativo (derivado da rota) ----------------------------------

  /** URL corrente — atualiza a cada navegação concluída. */
  private readonly url = toSignal(
    this.router.events.pipe(
      filter((e): e is NavigationEnd => e instanceof NavigationEnd),
      map((e) => e.urlAfterRedirects),
    ),
    { initialValue: this.router.url },
  );

  /** Função NIST correspondente à aba atual. */
  readonly activeFunction = computed<NistFunction>(() => {
    const seg = this.url().split(/[?#]/)[0].split('/').filter(Boolean)[0] ?? '';
    return ROUTE_TO_FUNCTION[seg] ?? 'Govern';
  });

  /** Metadados do contexto ativo (código/rótulo/disponibilidade). */
  readonly context = computed<NistContext>(() => CONTEXTS[this.activeFunction()]);

  // ---- Drawer -------------------------------------------------------------

  /** Visibilidade do painel lateral. */
  readonly open = signal(false);

  /** Título fixo do drawer — o subtítulo é que carrega o contexto. */
  readonly drawerTitle = computed(() => 'Auditor Virtual');

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

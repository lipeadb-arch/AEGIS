import { DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { Subscription } from 'rxjs';
import { NistEvidenceOverview, resolveSelection, selectionParams } from '../../models/nist.models';
import { AuthService } from '../../services/auth.service';
import { NistCtx, NistSelectionService, NistService } from '../../services/nist.service';

/**
 * [AEGIS-AUDITOR-CONTEXT-01] Contexto da jornada NIST para as páginas de apoio (documentos na Governança, ativos na Identificação).
 *
 *   • a avaliação · rodada · escopo vêm da URL; sem eles, a lembrada e depois a mais recente — e a URL é reescrita (compartilhável, e a
 *     trilha volta para a jornada com a mesma seleção);
 *   • a visão de evidências da rodada (documentos e inventário vinculados, nome da rodada e se está aberta) é lida do servidor;
 *   • gravar exige papel de escrita E rodada aberta; o servidor confere de novo (403/409);
 *   • troca de seleção ou de tenant descarta respostas atrasadas da anterior.
 *
 * Construído no contexto de injeção do componente.
 */
export class NistPageContext {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly nist = inject(NistService);
  private readonly memory = inject(NistSelectionService);
  private readonly auth = inject(AuthService);

  readonly ctx = signal<NistCtx | null>(null);
  readonly overview = signal<NistEvidenceOverview | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  /** Não há avaliação NIST no tenant (a biblioteca e o inventário continuam utilizáveis). */
  readonly noAssessment = signal(false);

  readonly params = computed((): Record<string, string> => {
    const c = this.ctx();
    return c ? { avaliacao: c.assessmentId, rodada: c.cycleId, escopo: c.scopeId } : {};
  });
  readonly canWrite = computed(() => ['Manager', 'TenantAdmin'].includes(this.auth.activeRole() ?? ''));
  readonly cycleOpen = computed(() => this.overview()?.cycleStatus !== 'Closed');
  /** Pode vincular agora: papel de escrita, seleção carregada e rodada aberta. */
  readonly canLink = computed(() => this.canWrite() && !!this.overview() && this.cycleOpen());

  private gen = 0;
  private sub: Subscription | null = null;
  private key: string | null = null;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.leave());
    this.route.queryParamMap.pipe(takeUntilDestroyed()).subscribe((q) => {
      const tenant = this.auth.activeTenantId();
      const a = q.get('avaliacao');
      const c = q.get('rodada');
      const s = q.get('escopo');
      const key = [tenant, a, c, s].join('|');
      if (key === this.key) return;
      this.leave();
      this.key = key;
      this.overview.set(null);
      this.error.set(null);
      if (a && c && s) {
        this.ctx.set({ assessmentId: a, cycleId: c, scopeId: s });
        this.reload();
      } else {
        this.ctx.set(null);
        this.resolve(tenant, a, c, s);
      }
    });
  }

  /** Relê a visão de evidências da seleção atual (depois de vincular, por exemplo). */
  reload(): void {
    const c = this.ctx();
    if (!c) return;
    const gen = ++this.gen;
    const tenant = this.auth.activeTenantId();
    this.sub?.unsubscribe();
    this.loading.set(true);
    this.sub = this.nist.evidenceOverview(c).subscribe({
      next: (o) => {
        if (!this.current(gen, tenant) || o.assessmentId !== c.assessmentId || o.cycleId !== c.cycleId || o.scopeId !== c.scopeId) return;
        this.overview.set(o);
        this.loading.set(false);
        this.memory.write(tenant, c.assessmentId, c.scopeId, c.cycleId);
      },
      error: (e: Error) => {
        if (!this.current(gen, tenant)) return;
        this.loading.set(false);
        this.error.set(e.message);
      },
    });
  }

  /** A resposta pedida na geração `gen` ainda pertence à tela (mesma seleção e tenant)? */
  current(gen: number, tenant: string | null): boolean {
    return gen === this.gen && tenant === this.auth.activeTenantId();
  }

  /** Tenant ativo agora (do token). */
  tenantId(): string | null {
    return this.auth.activeTenantId();
  }

  /** Geração atual (para quem grava a partir desta seleção e precisa descartar a resposta se ela mudar). */
  generation(): number {
    return this.gen;
  }

  private resolve(tenant: string | null, a: string | null, c: string | null, s: string | null): void {
    const gen = ++this.gen;
    this.loading.set(true);
    this.sub = this.nist.list().subscribe({
      next: (list) => {
        if (!this.current(gen, tenant)) return;
        this.loading.set(false);
        const sel = resolveSelection(list, { assessmentId: a, cycleId: c, scopeId: s }, this.memory.read(tenant));
        if (!sel?.cycle || !sel.scope) {
          this.noAssessment.set(list.length === 0);
          return;
        }
        void this.router.navigate([], { relativeTo: this.route, queryParams: selectionParams(sel), queryParamsHandling: 'merge', replaceUrl: true });
      },
      error: (e: Error) => {
        if (!this.current(gen, tenant)) return;
        this.loading.set(false);
        this.error.set(e.message);
      },
    });
  }

  private leave(): void {
    this.gen++;
    this.sub?.unsubscribe();
    this.sub = null;
    this.loading.set(false);
    this.noAssessment.set(false);
  }
}

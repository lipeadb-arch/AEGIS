import { Injectable, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Subject } from 'rxjs';
import { AuthService } from './auth.service';

/**
 * [AEGIS-AUD-030] Autoridade CENTRAL da troca de tenant ativo. Concentra, num único lugar, o protocolo de
 * switch que antes vivia solto no seletor. A troca segue esta ordem lógica:
 *  1. valida no backend (revoga o refresh anterior e emite o novo par para o alvo, via AuthService);
 *  2. troca token/contexto (o `activeTenantId` do AuthService passa a apontar para o novo tenant);
 *  3. incrementa a GERAÇÃO (epoch) e emite `switched$` — o auth interceptor faz `takeUntil(switched$)`, então
 *     CANCELA de verdade toda requisição tenant-scoped em voo: uma resposta iniciada no tenant ANTERIOR não
 *     pode mais repovoar a UI depois da troca;
 *  4. limpa o estado tenant-scoped (caches, signals, stores, paginações) e recarrega os dados do novo tenant.
 *
 * A geração é um identificador MONOTÔNICO: além do cancelamento no interceptor, qualquer consumidor pode
 * capturá-la ao iniciar uma leitura e descartar o resultado se ela tiver mudado (defesa em profundidade).
 */
@Injectable({ providedIn: 'root' })
export class TenantContextService {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  /** Geração monotônica do tenant ativo — incrementa a cada troca CONCLUÍDA. */
  private readonly _generation = signal(0);
  readonly generation = this._generation.asReadonly();

  /** Emite a cada troca CONCLUÍDA. O interceptor faz `takeUntil(switched$)` para abortar leituras antigas. */
  readonly switched$ = new Subject<void>();

  /** Troca em andamento? Desabilita o seletor e barra trocas concorrentes. */
  private readonly _switching = signal(false);
  readonly switching = this._switching.asReadonly();

  /**
   * Troca o tenant ativo. Idempotente contra cliques repetidos e contra selecionar o ambiente já ativo.
   */
  switch(tenantId: string): void {
    if (this._switching() || tenantId === this.auth.activeTenantId()) return;

    this._switching.set(true);
    this.auth.switchTenant(tenantId).subscribe({
      next: () => {
        // O token JÁ é do novo tenant. Invalida a geração e ABORTA toda leitura tenant-scoped em voo: uma
        // resposta do tenant anterior não pode mais escrever em nenhum signal/store depois deste ponto.
        this._generation.update((g) => g + 1);
        this.switched$.next();
        this._switching.set(false);
        this.reloadForNewTenant();
      },
      error: () => {
        this._switching.set(false);
        // 403: o acesso deixou de existir entre carregar a lista e clicar. Recarrega a lista para que o
        // ambiente sumir do seletor seja a explicação visível.
        this.auth.getAvailableTenants().subscribe();
      },
    });
  }

  /**
   * Limpeza determinística do estado tenant-scoped + recarga dos dados do novo tenant. As telas guardam o
   * estado em signals locais e paginações próprias; a recarga dura garante que NENHUM valor do tenant
   * anterior sobreviva, sem depender de cada página lembrar de se limpar. As requisições do tenant antigo já
   * foram abortadas em `switch()`, então não há resposta atrasada capaz de repovoar a UI durante a recarga.
   */
  private reloadForNewTenant(): void {
    this.router.navigateByUrl('/').then(() => window.location.reload());
  }
}

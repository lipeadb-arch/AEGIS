import { Injectable, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Subject } from 'rxjs';
import { AuthService } from './auth.service';

/**
 * [AEGIS-AUD-030] Autoridade CENTRAL da troca de tenant ativo. Concentra, num único lugar, o protocolo de
 * switch que antes vivia solto no seletor. A ordem é DELIBERADA — o cancelamento vem ANTES da chamada de troca:
 *  1. bloqueia trocas concorrentes;
 *  2. incrementa a GERAÇÃO (epoch) e emite `switched$` ANTES de chamar a troca — o auth interceptor faz
 *     `takeUntil(switched$)`, então CANCELA de verdade as leituras tenant-scoped já em andamento; assim
 *     nenhuma resposta do tenant anterior atualiza a UI durante o round-trip da troca (a requisição de
 *     switch é ISENTA do sinal, por ser da família de auth — não cancela a si mesma);
 *  3. chama `/auth/switch-tenant` com o Bearer LOCAL + o X-Tenant do token atual;
 *  4. em SUCESSO, o AuthService instala o novo par; então limpa/recarrega a app no novo tenant;
 *  5. em FALHA, mantém o token/tenant anterior, libera o `switching` e recarrega o estado atual (as leituras
 *     já foram canceladas, então a app não pode ficar travada num carregamento do tenant que não trocou).
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

    // 1) Bloqueia trocas concorrentes.
    this._switching.set(true);

    // 2) ANTES de chamar a troca: invalida a geração e emite o sinal, ABORTANDO as leituras tenant-scoped já
    //    em andamento. Nenhuma resposta do tenant atual pode atualizar a UI durante o round-trip do switch.
    //    A requisição de switch é isenta do sinal (família de auth), então não cancela a si mesma.
    this._generation.update((g) => g + 1);
    this.switched$.next();

    // 3) Chama a troca — o interceptor anexa o Bearer local + o X-Tenant do token atual.
    this.auth.switchTenant(tenantId).subscribe({
      next: () => {
        // 4) O AuthService já instalou o novo par (o token agora é do novo tenant). Limpa e recarrega no novo.
        this._switching.set(false);
        this.router.navigateByUrl('/').then(() => window.location.reload());
      },
      error: () => {
        // 5) Falha (403 sem acesso, ou 401 após refresh esgotado): o token/tenant ANTERIOR permanece intacto.
        //    Libera o switching e recarrega o ESTADO ATUAL — como as leituras foram canceladas no passo 2, uma
        //    recarga in-place devolve uma visão consistente do tenant atual (e a lista volta no bootstrap, sem
        //    o ambiente que porventura perdeu acesso), sem trocar de ambiente nem deixar a UI travada.
        this._switching.set(false);
        window.location.reload();
      },
    });
  }
}

import { Component, computed, inject, signal } from '@angular/core';
import { AuthService, TenantOption } from '../services/auth.service';
import { TenantContextService } from '../services/tenant-context.service';

/**
 * Seletor de ambiente do HUD (Tenant Switcher) — a experiência de SOC terceirizado: o analista entra
 * com e-mail e senha e alterna entre os clientes a que tem acesso, sem jamais digitar slug.
 *
 * Dumb-ish por design: todo o estado de sessão vive no AuthService (signals). O componente não guarda
 * "qual é o tenant ativo" — lê `auth.activeTenant()`, que é DERIVADO do token. Manter uma cópia local
 * abriria a porta para a tela mostrar um ambiente e as chamadas irem para outro.
 *
 * ⚠️ Após a troca, navega para a raiz e força recarga dos dados: as telas trazem dados do ambiente
 * ANTERIOR e não podem simplesmente continuar exibindo-os sob o novo rótulo.
 */
@Component({
  selector: 'app-tenant-switcher',
  standalone: true,
  template: `
    @if (visible()) {
      <div class="switcher" [class.open]="open()">
        <button
          type="button"
          class="trigger"
          [disabled]="switching()"
          (click)="toggle()"
          [attr.aria-expanded]="open()"
          aria-haspopup="listbox"
        >
          <span class="glyph" aria-hidden="true">◆</span>
          <span class="label">
            <span class="name">{{ activeName() }}</span>
            <span class="role">{{ auth.activeRole() ?? '—' }}</span>
          </span>
          <span class="caret" aria-hidden="true">{{ switching() ? '⋯' : '▾' }}</span>
        </button>

        @if (open()) {
          <ul class="menu" role="listbox">
            @for (t of auth.tenants(); track t.id) {
              <li>
                <button
                  type="button"
                  role="option"
                  [attr.aria-selected]="t.id === auth.activeTenantId()"
                  [class.current]="t.id === auth.activeTenantId()"
                  [disabled]="switching()"
                  (click)="select(t)"
                >
                  <span class="t-name">{{ t.name }}</span>
                  <span class="t-role">{{ t.role }}</span>
                </button>
              </li>
            }
          </ul>
        }
      </div>
    }
  `,
  styles: [
    `
      .switcher {
        position: relative;
        min-width: 0;
      }
      .trigger {
        display: flex;
        align-items: center;
        gap: 10px;
        width: 100%;
        min-width: 13rem;
        max-width: 18rem;
        min-height: var(--control-h);
        padding: 3px 10px 3px 12px;
        border: 1px solid rgba(38, 224, 255, 0.3);
        border-radius: var(--radius-sm);
        background: var(--tint-cyan);
        color: var(--text);
        cursor: pointer;
        transition: border-color var(--ease), background var(--ease);
      }
      .trigger:hover:not(:disabled) {
        border-color: rgba(38, 224, 255, 0.6);
      }
      .trigger:focus-visible {
        outline: none;
        box-shadow: var(--focus);
      }
      .trigger:disabled {
        opacity: 0.6;
        cursor: progress;
      }
      .glyph {
        color: var(--cyan);
        font-size: var(--fs-meta);
      }
      .label {
        display: flex;
        flex-direction: column;
        align-items: flex-start;
        flex: 1;
        min-width: 0;
        line-height: 1.2;
      }
      .name {
        max-width: 100%;
        font-size: var(--fs-sm);
        font-weight: 600;
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
      }
      .role {
        font-size: var(--fs-caps);
        color: var(--text-2);
      }
      .caret {
        color: var(--muted);
      }
      .menu {
        position: absolute;
        top: calc(100% + 6px);
        right: 0;
        z-index: 70;
        min-width: 100%;
        width: max-content;
        max-width: min(22rem, calc(100vw - 32px));
        max-height: 18rem;
        margin: 0;
        padding: 4px;
        overflow-y: auto;
        list-style: none;
        border: 1px solid var(--line-strong);
        border-radius: var(--radius);
        background: var(--panel-2);
        box-shadow: 0 18px 40px -16px rgba(0, 0, 0, 0.9);
      }
      .menu button {
        display: flex;
        justify-content: space-between;
        align-items: center;
        gap: 12px;
        width: 100%;
        min-height: 36px;
        padding: 6px 10px;
        border: 0;
        border-radius: 6px;
        background: transparent;
        color: var(--text);
        text-align: left;
        cursor: pointer;
      }
      .menu button:hover:not(:disabled),
      .menu button:focus-visible {
        outline: none;
        background: var(--hover);
      }
      .menu button.current {
        color: var(--cyan);
        background: var(--tint-cyan);
      }
      .t-name {
        font-size: var(--fs-sm);
      }
      .t-role {
        font-size: var(--fs-caps);
        color: var(--text-2);
      }
      @media (max-width: 720px) {
        .trigger {
          min-width: 0;
          max-width: 44vw;
        }
        /* Ancorada no botão, a lista saía pela borda esquerda: no celular ela ocupa a largura útil da tela. */
        .menu {
          position: fixed;
          top: calc(var(--header-h) - 4px);
          left: var(--sp-4);
          right: var(--sp-4);
          width: auto;
          min-width: 0;
          max-width: none;
        }
      }
    `,
  ],
})
export class TenantSwitcherComponent {
  readonly auth = inject(AuthService);
  private readonly tenantContext = inject(TenantContextService);

  readonly open = signal(false);
  /** [AEGIS-AUD-030] O estado de troca agora é do serviço central — autoridade única do switch. */
  readonly switching = this.tenantContext.switching;

  /** Some quando há 0 ou 1 ambiente: um seletor de uma opção só é ruído no HUD. */
  readonly visible = computed(() => this.auth.isAuthenticated() && this.auth.tenants().length > 1);

  /** Nome do ambiente ativo; cai no id curto enquanto a lista não chegou. */
  readonly activeName = computed(
    () => this.auth.activeTenant()?.name ?? this.auth.activeTenantId()?.slice(0, 8) ?? '—',
  );

  toggle(): void {
    this.open.update((v) => !v);
  }

  select(tenant: TenantOption): void {
    this.open.set(false);
    if (tenant.id === this.auth.activeTenantId() || this.switching()) return;

    // [AEGIS-AUD-030] Delega à autoridade central: ela valida no backend, cancela as leituras do tenant
    // antigo, limpa o estado e recarrega os dados do novo — nesta ordem. O seletor não orquestra mais nada.
    this.tenantContext.switch(tenant.id);
  }
}

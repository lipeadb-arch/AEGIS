import { Component, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { AuthService, TenantOption } from '../services/auth.service';

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
        font-family: inherit;
      }
      .trigger {
        display: flex;
        align-items: center;
        gap: 0.6rem;
        padding: 0.4rem 0.75rem;
        background: color-mix(in srgb, var(--hud-cyan, #22d3ee) 8%, transparent);
        border: 1px solid color-mix(in srgb, var(--hud-cyan, #22d3ee) 35%, transparent);
        border-radius: 6px;
        color: var(--hud-text, #e2e8f0);
        cursor: pointer;
        min-width: 13rem;
      }
      .trigger:disabled {
        opacity: 0.6;
        cursor: progress;
      }
      .glyph {
        color: var(--hud-cyan, #22d3ee);
      }
      .label {
        display: flex;
        flex-direction: column;
        align-items: flex-start;
        line-height: 1.15;
        flex: 1;
        min-width: 0;
      }
      .name {
        font-size: 0.85rem;
        font-weight: 600;
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
        max-width: 100%;
      }
      .role {
        font-size: 0.65rem;
        letter-spacing: 0.08em;
        text-transform: uppercase;
        opacity: 0.7;
      }
      .caret {
        opacity: 0.7;
      }
      .menu {
        position: absolute;
        top: calc(100% + 0.35rem);
        right: 0;
        left: 0;
        z-index: 50;
        margin: 0;
        padding: 0.25rem;
        list-style: none;
        background: var(--hud-panel, #0f172a);
        border: 1px solid color-mix(in srgb, var(--hud-cyan, #22d3ee) 30%, transparent);
        border-radius: 6px;
        max-height: 16rem;
        overflow-y: auto;
      }
      .menu button {
        display: flex;
        justify-content: space-between;
        gap: 0.75rem;
        width: 100%;
        padding: 0.45rem 0.6rem;
        background: transparent;
        border: 0;
        border-radius: 4px;
        color: var(--hud-text, #e2e8f0);
        cursor: pointer;
        text-align: left;
      }
      .menu button:hover:not(:disabled) {
        background: color-mix(in srgb, var(--hud-cyan, #22d3ee) 14%, transparent);
      }
      .menu button.current {
        color: var(--hud-cyan, #22d3ee);
      }
      .t-name {
        font-size: 0.82rem;
      }
      .t-role {
        font-size: 0.65rem;
        opacity: 0.65;
        text-transform: uppercase;
      }
    `,
  ],
})
export class TenantSwitcherComponent {
  readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly open = signal(false);
  readonly switching = signal(false);

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

    this.switching.set(true);
    this.auth.switchTenant(tenant.id).subscribe({
      next: () => {
        this.switching.set(false);
        // Recarga dura: as telas já montadas seguram dados do ambiente anterior. Um simples
        // navigate() reusaria os componentes e exibiria números do cliente errado sob o novo nome.
        this.router.navigateByUrl('/').then(() => window.location.reload());
      },
      error: () => {
        this.switching.set(false);
        // 403 = o acesso deixou de existir entre o carregamento da lista e o clique. Recarrega a
        // lista para que o ambiente sumir do seletor seja a explicação visível.
        this.auth.getAvailableTenants().subscribe();
      },
    });
  }
}

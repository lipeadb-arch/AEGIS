import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from '../../services/auth.service';

/**
 * Shell de Configurações: cabeçalho + navegação por abas + <router-outlet> das seções filhas
 * (Geral, Usuários e acessos, Integrações). Só a casca vive aqui — cada aba é um componente próprio, para
 * não inchar o template principal. As abas administrativas só aparecem para TenantAdmin (a autorização
 * efetiva permanece no backend e nas guardas de rota; a visibilidade é apenas UX).
 */
@Component({
  selector: 'app-settings',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <section class="settings">
      <header class="head">
        <h1>Configurações</h1>
        <p class="sub">Ambiente ativo, sua conta, usuários e integrações do AEGIS.</p>
      </header>

      <nav class="tabs" role="tablist" aria-label="Seções de configurações">
        <a
          class="tab"
          routerLink="general"
          routerLinkActive="active"
          ariaCurrentWhenActive="page"
          role="tab"
        >Geral</a>
        @if (isTenantAdmin()) {
          <a
            class="tab"
            routerLink="users"
            routerLinkActive="active"
            ariaCurrentWhenActive="page"
            role="tab"
          >Usuários e acessos</a>
          <a
            class="tab"
            routerLink="integrations"
            routerLinkActive="active"
            ariaCurrentWhenActive="page"
            role="tab"
          >Integrações</a>
        }
        @if (isPlatformAdmin()) {
          <a
            class="tab"
            routerLink="tenants"
            routerLinkActive="active"
            ariaCurrentWhenActive="page"
            role="tab"
          >Ambientes</a>
        }
      </nav>

      <div class="tab-panel">
        <router-outlet />
      </div>
    </section>
  `,
  styles: [
    `
      .settings {
        max-width: 1080px;
        margin: 0 auto;
        padding: 1.5rem 1.25rem 3rem;
      }
      .head h1 {
        margin: 0;
        font-family: var(--display, var(--sans));
        font-size: 1.6rem;
        color: var(--text);
      }
      .head .sub {
        margin: 0.35rem 0 0;
        font-size: 0.85rem;
        color: var(--muted);
      }
      /* Abas: rolagem horizontal em telas estreitas, sem quebrar o layout. */
      .tabs {
        display: flex;
        gap: 0.25rem;
        margin: 1.25rem 0 1.5rem;
        border-bottom: 1px solid var(--line);
        overflow-x: auto;
      }
      .tab {
        flex: none;
        padding: 0.6rem 0.95rem;
        font-family: var(--mono);
        font-size: 0.82rem;
        letter-spacing: 0.02em;
        color: var(--muted);
        text-decoration: none;
        border: 1px solid transparent;
        border-bottom: 2px solid transparent;
        border-radius: 8px 8px 0 0;
        white-space: nowrap;
        transition: 0.15s;
      }
      .tab:hover {
        color: var(--text);
        background: rgba(122, 145, 190, 0.06);
      }
      .tab.active {
        color: var(--text);
        border-bottom-color: var(--cyan);
        background: linear-gradient(180deg, rgba(38, 224, 255, 0.1), transparent);
      }
      .tab:focus-visible {
        outline: 2px solid var(--cyan);
        outline-offset: 2px;
      }
      /* O painel não adiciona padding: cada aba traz o próprio espaçamento (evita margens duplicadas). */
      .tab-panel {
        min-height: 40vh;
      }
      @media (max-width: 640px) {
        .settings {
          padding: 1rem 0.85rem 2.5rem;
        }
      }
    `,
  ],
})
export class SettingsComponent {
  private readonly auth = inject(AuthService);
  protected readonly isTenantAdmin = this.auth.isTenantAdmin;
  protected readonly isPlatformAdmin = this.auth.isPlatformAdmin;
}

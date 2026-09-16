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
    <section class="page settings">
      <header class="page-head">
        <div>
          <p class="page-eyebrow">Configurações</p>
          <h1>Configurações</h1>
          <p class="page-desc">Ambiente ativo, sua conta, usuários e integrações do AEGIS.</p>
        </div>
      </header>

      <nav class="tabbar" role="tablist" aria-label="Seções de configurações">
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
      /* Página, cabeçalho e abas: sistema visual global (styles.css). Formulários não precisam da largura total. */
      .settings {
        max-width: 1280px;
      }
      /* O painel não adiciona padding: cada aba traz o próprio espaçamento (evita margens duplicadas). */
      .tab-panel {
        min-height: 40vh;
      }
    `,
  ],
})
export class SettingsComponent {
  private readonly auth = inject(AuthService);
  protected readonly isTenantAdmin = this.auth.isTenantAdmin;
  protected readonly isPlatformAdmin = this.auth.isPlatformAdmin;
}

import { Component, ElementRef, computed, inject, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { DrawerComponent } from './components/drawer.component';
import { TenantSwitcherComponent } from './components/tenant-switcher.component';
import { AuditorChatComponent } from './components/auditor-chat.component';
import { IconComponent, IconName } from './components/icon.component';
import { AgentStateService } from './services/agent-state.service';
import { AuthService } from './services/auth.service';

interface NavLink {
  path: string;
  label: string;
  icon: IconName;
}
interface NavGroup {
  /** Sem rótulo = grupo de entrada (Visão geral e Prioridades). */
  label: string | null;
  items: NavLink[];
}

/**
 * [AEGIS-MVP-PRODUCT-01] Navegação organizada por INTENÇÃO, não por framework: Visão geral · Prioridades · Ambiente ·
 * Assessment (AEGIS KNIGHT) · Governança e controles · Relatórios · Configurações. As seis Funções NIST vivem DENTRO de "Controles NIST"
 * e nenhuma rota foi removida — todas seguem acessíveis por link direto.
 */
const NAV: NavGroup[] = [
  {
    label: null,
    items: [
      { path: '/dashboard', label: 'Visão geral', icon: 'overview' },
      { path: '/priorities', label: 'Prioridades', icon: 'priorities' },
    ],
  },
  {
    label: 'Ambiente',
    items: [
      { path: '/assets', label: 'Ativos', icon: 'assets' },
      { path: '/vulnerabilities', label: 'Vulnerabilidades', icon: 'vulnerabilities' },
      // [AEGIS-LANGUAGE-STATES-01] Recomendações do Microsoft Secure Score: diferença de pontos da fonte não comprova,
      // sozinha, configuração exposta. A rota /exposures é mantida (links antigos seguem válidos).
      { path: '/exposures', label: 'Recomendações de postura', icon: 'recommendations' },
    ],
  },
  // [AEGIS-KNIGHT-MULTICLOUD-01] O KNIGHT é o assessment de postura (hoje: identidade — Entra ID e Google Workspace);
  // a rota /identity é mantida para os links já emitidos.
  { label: 'Assessment', items: [{ path: '/identity', label: 'AEGIS KNIGHT', icon: 'identity' }] },
  {
    label: 'Governança e controles',
    items: [
      // Uma entrada para as SEIS Funções NIST: a navegação por Função é interna a esta tela.
      { path: '/controls', label: 'Controles NIST', icon: 'controls' },
      { path: '/governance', label: 'Evidências e documentos', icon: 'documents' },
    ],
  },
  {
    label: 'Relatórios',
    items: [
      // Fotografias PUBLICADAS (comparação e exportação PDF/CSV) — distintas da leitura atual das telas.
      { path: '/history', label: 'Histórico e publicação', icon: 'history' },
      { path: '/aegis-score', label: 'Tendência de postura', icon: 'trend' },
    ],
  },
  { label: 'Configurações', items: [{ path: '/settings', label: 'Configurações', icon: 'settings' }] },
];

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, DrawerComponent, AuditorChatComponent, TenantSwitcherComponent, IconComponent],
  host: { '(document:keydown.escape)': 'closeNav()' },
  template: `
    @if (showShell()) {
    <button type="button" class="skip-link" [attr.inert]="behind() ? '' : null" (click)="focusMain()">Pular para o conteúdo</button>

    <!-- Cabeçalho global: marca à esquerda (sobre a coluna do menu), ambiente e saída à direita.
         Com o menu sobreposto ou o Auditor aberto, o que fica ATRÁS vira inerte: não recebe foco nem clique. -->
    <header class="shell-header">
      <button
        #navToggle
        type="button"
        class="nav-toggle"
        [attr.inert]="agent.open() ? '' : null"
        (keydown)="onToggleKey($event)"
        (click)="toggleNav()"
        [attr.aria-expanded]="navOpen()"
        aria-controls="app-sidebar"
        [attr.aria-label]="navOpen() ? 'Fechar navegação' : 'Abrir navegação'"
      >
        <app-icon [name]="navOpen() ? 'close' : 'menu'" />
      </button>

      <!-- Logo: 'AEGIS' dentro de um escudo (SVG) com a borda dual-neon. -->
      <a class="brand" routerLink="/dashboard" aria-label="AEGIS — Visão geral" [attr.inert]="behind() ? '' : null">
        <svg class="shield" viewBox="0 0 120 138" fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
          <defs>
            <linearGradient id="shieldStroke" x1="8" y1="6" x2="112" y2="132" gradientUnits="userSpaceOnUse">
              <stop stop-color="#26e0ff" />
              <stop offset="0.55" stop-color="#8b5cff" />
              <stop offset="1" stop-color="#ff3d9a" />
            </linearGradient>
            <linearGradient id="shieldFill" x1="60" y1="5" x2="60" y2="132" gradientUnits="userSpaceOnUse">
              <stop stop-color="#26e0ff" stop-opacity="0.14" />
              <stop offset="1" stop-color="#0b0f1a" stop-opacity="0.25" />
            </linearGradient>
            <radialGradient id="shieldGlow" cx="50%" cy="24%" r="62%">
              <stop stop-color="#26e0ff" stop-opacity="0.4" />
              <stop offset="1" stop-color="#26e0ff" stop-opacity="0" />
            </radialGradient>
          </defs>
          <path
            d="M60 5 L108 22 V64 C108 96 88 120 60 132 C32 120 12 96 12 64 V22 Z"
            fill="url(#shieldFill)"
            stroke="url(#shieldStroke)"
            stroke-width="5"
            stroke-linejoin="round"
          />
          <path d="M60 5 L108 22 V64 C108 96 88 120 60 132 C32 120 12 96 12 64 V22 Z" fill="url(#shieldGlow)" />
          <text
            x="60"
            y="73"
            text-anchor="middle"
            font-family="Orbitron, JetBrains Mono, monospace"
            font-weight="800"
            font-size="22"
            letter-spacing="1"
            fill="#eaf1ff"
          >
            AEGIS
          </text>
        </svg>
        <span class="brand-text">
          <span class="brand-name">AEGIS</span>
          <span class="brand-tag">Análise da postura de segurança multicloud.</span>
        </span>
      </a>

      <div class="header-actions" [attr.inert]="behind() ? '' : null">
        <!-- Agente Global (Auditor Virtual): gatilho no cabeçalho — sempre a um clique e sem cobrir conteúdo da página.
             Égide: escudo circular com a máscara da Medusa reduzida ao essencial (anel, serpentes, rosto e olhar). -->
        <button
          type="button"
          class="auditor-trigger"
          [class.on]="agent.open()"
          [attr.aria-expanded]="agent.open()"
          aria-haspopup="dialog"
          title="Auditor Virtual"
          (click)="agent.toggle()"
        >
          <span class="auditor-ic" aria-hidden="true">
            <svg viewBox="0 0 32 32" width="20" height="20" focusable="false">
              <g stroke="currentColor" stroke-width="1.6" stroke-linecap="round" opacity="0.9">
                <path d="M16 1.5v3" /><path d="M23.9 3.6l-1.5 2.6" /><path d="M28.4 8.1l-2.6 1.5" />
                <path d="M30.5 16h-3" /><path d="M8.1 3.6l1.5 2.6" /><path d="M3.6 8.1l2.6 1.5" />
                <path d="M1.5 16h3" /><path d="M16 30.5v-3" />
              </g>
              <circle cx="16" cy="16" r="11.5" fill="none" stroke="currentColor" stroke-width="2.2" />
              <circle cx="16" cy="16" r="8.6" fill="none" stroke="currentColor" stroke-width="1" opacity="0.55" />
              <path d="M11.4 12.6h9.2c0 4.4-2.2 7.4-4.6 8.9-2.4-1.5-4.6-4.5-4.6-8.9z" fill="currentColor" opacity="0.92" />
              <circle cx="13.9" cy="15.4" r="1.05" fill="#26e0ff" />
              <circle cx="18.1" cy="15.4" r="1.05" fill="#26e0ff" />
            </svg>
          </span>
          <span class="lb">Auditor Virtual</span>
        </button>
        <!-- Seletor de ambiente: some sozinho quando a pessoa só tem acesso a um cliente. -->
        <app-tenant-switcher />
        <!-- Encerrar sessão: SEMPRE visível para o usuário autenticado (independe da contagem de ambientes).
             Desabilita enquanto o logout corre, barrando cliques repetidos. -->
        <button
          type="button"
          class="hud-logout"
          [disabled]="auth.loggingOut()"
          [attr.aria-busy]="auth.loggingOut()"
          (click)="auth.logout()"
          title="Encerrar sessão"
        >
          <app-icon name="logout" />
          <span class="lb">{{ auth.loggingOut() ? 'Saindo…' : 'Sair' }}</span>
        </button>
      </div>
    </header>

    <!-- Menu lateral: fixo no desktop; no estreito vira painel sobreposto aberto pelo botão do cabeçalho. -->
    <aside
      #sidebar
      id="app-sidebar"
      class="sidebar"
      [class.open]="navOpen()"
      [attr.inert]="agent.open() || (narrow() && !navOpen()) ? '' : null"
      aria-label="Navegação principal"
      (keydown)="onSidebarKey($event)"
    >
      <nav class="side-nav">
        @for (g of nav; track g.label) {
          <div class="nav-section">
            @if (g.label) {
              <p class="nav-group">{{ g.label }}</p>
            }
            @for (i of g.items; track i.path) {
              <a class="nav-item" [routerLink]="i.path" routerLinkActive="active" #rla="routerLinkActive"
                [attr.aria-current]="rla.isActive ? 'page' : null">
                <app-icon [name]="i.icon" />
                <span class="lb">{{ i.label }}</span>
              </a>
            }
          </div>
        }
      </nav>

      <!-- Referência externa: separada porque NÃO é uma tela do produto — é a fonte normativa. Sem routerLink de
           propósito (href externo); rel="noopener noreferrer" é obrigatório com target="_blank" (anti tab-nabbing). -->
      <div class="sidebar-foot">
        <a class="nav-item external" href="https://www.nist.gov/cyberframework" target="_blank" rel="noopener noreferrer">
          <app-icon name="external" />
          <span class="lb">Sobre o NIST CSF 2.0</span>
          <span class="sr-only">(abre em nova aba)</span>
        </a>
      </div>
    </aside>
    @if (navOpen()) {
      <div class="nav-backdrop" (click)="closeNav()" aria-hidden="true"></div>
    }

    <main class="app-shell" id="conteudo" tabindex="-1" [attr.inert]="behind() ? '' : null">
      <router-outlet />
    </main>

    <!-- Agente Global (Auditor Virtual) — a Função NIST é derivada da rota pelo AgentStateService. -->
    <app-drawer
      [open]="agent.open()"
      [title]="agent.drawerTitle()"
      [subtitle]="agent.drawerSubtitle()"
      (closed)="agent.close()"
    >
      <app-auditor-chat />
    </app-drawer>

    } @else {
    <router-outlet />
    }
  `,
  styles: [
    `
      /* ---- Cabeçalho global ---- */
      .shell-header {
        position: fixed;
        inset: 0 0 auto;
        z-index: 45;
        display: flex;
        align-items: center;
        gap: var(--sp-3);
        height: var(--header-h);
        padding: 0 var(--sp-6) 0 var(--sp-5);
        background: rgba(7, 10, 20, 0.88);
        backdrop-filter: blur(10px);
        border-bottom: 1px solid var(--line);
      }
      /* Aresta neon inferior: o mesmo filete dual-neon que separa o menu do conteúdo. */
      .shell-header::after {
        content: '';
        position: absolute;
        inset: auto 0 -1px;
        height: 1px;
        background: var(--neon-h);
        opacity: 0.35;
        pointer-events: none;
      }
      .brand {
        display: flex;
        align-items: center;
        gap: 10px;
        min-width: 0;
        margin-right: auto;
        padding: 4px 6px 4px 0;
        border-radius: var(--radius-sm);
        text-decoration: none;
      }
      .brand .shield {
        flex: none;
        width: 36px;
        height: auto;
        filter: drop-shadow(0 4px 12px rgba(38, 224, 255, 0.3));
      }
      .brand-text {
        display: flex;
        flex-direction: column;
        line-height: 1.15;
        min-width: 0;
      }
      .brand-name {
        font-family: var(--brand);
        font-weight: 800;
        font-size: 17px;
        letter-spacing: 0.12em;
        background: var(--neon-h);
        -webkit-background-clip: text;
        background-clip: text;
        color: transparent;
      }
      .brand-tag {
        font-size: var(--fs-caps);
        color: var(--muted);
        white-space: nowrap;
      }
      .header-actions {
        display: flex;
        align-items: center;
        gap: var(--sp-2);
        min-width: 0;
      }
      .hud-logout,
      .nav-toggle {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        gap: var(--sp-2);
        min-height: var(--control-h);
        padding: 0 12px;
        border: 1px solid var(--line-strong);
        border-radius: var(--radius-sm);
        background: transparent;
        color: var(--text-2);
        font-size: var(--fs-sm);
        font-weight: 500;
        cursor: pointer;
        transition: color var(--ease), background var(--ease), border-color var(--ease);
      }
      .hud-logout:hover:not(:disabled),
      .nav-toggle:hover {
        color: var(--text);
        background: var(--tint-cyan);
        border-color: rgba(38, 224, 255, 0.45);
      }
      .hud-logout:disabled {
        opacity: 0.55;
        cursor: progress;
      }
      .hud-logout:focus-visible,
      .nav-toggle:focus-visible,
      .brand:focus-visible {
        outline: none;
        box-shadow: var(--focus);
      }
      .nav-toggle {
        display: none;
        width: var(--control-h);
        padding: 0;
      }
      .skip-link {
        position: fixed;
        top: 8px;
        left: 8px;
        z-index: 100;
        padding: 8px 14px;
        border: 1px solid var(--cyan);
        border-radius: var(--radius-sm);
        background: var(--panel-2);
        color: var(--text);
        font-size: var(--fs-sm);
        cursor: pointer;
        transform: translateY(-160%);
      }
      .skip-link:focus-visible {
        transform: none;
        outline: none;
        box-shadow: var(--focus);
      }

      /* ---- Menu lateral ---- */
      .sidebar {
        position: fixed;
        top: var(--header-h);
        bottom: 0;
        left: 0;
        z-index: 40;
        display: flex;
        flex-direction: column;
        width: var(--sidebar-w);
        padding: var(--sp-4) var(--sp-3);
        background: linear-gradient(180deg, rgba(11, 15, 26, 0.94), rgba(7, 10, 20, 0.97));
        border-right: 1px solid var(--line);
        overflow-y: auto;
        overflow-x: hidden;
        overscroll-behavior: contain;
      }
      /* Aresta neon: ancorada em right:0 (fora do box seria recortada e criaria transbordo horizontal). */
      .sidebar::after {
        content: '';
        position: absolute;
        top: 0;
        right: 0;
        bottom: 0;
        width: 1px;
        background: var(--neon-v);
        opacity: 0.4;
        pointer-events: none;
      }
      .side-nav {
        display: flex;
        flex-direction: column;
        gap: var(--sp-4);
      }
      .nav-section {
        display: flex;
        flex-direction: column;
        gap: 2px;
      }
      .nav-group {
        margin: 0 0 6px;
        padding: 0 var(--sp-3);
        font-size: var(--fs-caps);
        font-weight: 600;
        letter-spacing: var(--tracking-caps);
        text-transform: uppercase;
        color: var(--muted);
      }
      .nav-item {
        position: relative;
        display: flex;
        align-items: center;
        gap: var(--sp-3);
        min-height: var(--nav-h);
        padding: 8px var(--sp-3);
        border-radius: var(--radius-sm);
        color: var(--text-2);
        font-size: var(--fs-body);
        font-weight: 500;
        line-height: 1.3;
        text-decoration: none;
        transition: color var(--ease), background var(--ease);
      }
      .nav-item .icon {
        color: var(--muted);
        transition: color var(--ease);
      }
      .nav-item .lb {
        flex: 1;
        min-width: 0;
      }
      .nav-item:hover {
        color: var(--text);
        background: var(--hover);
      }
      .nav-item:hover .icon {
        color: var(--text-2);
      }
      .nav-item:focus-visible {
        outline: none;
        box-shadow: inset 0 0 0 2px var(--cyan);
      }
      /* Ativo: fundo tonalizado + barra neon à esquerda + ícone aceso. */
      .nav-item.active {
        color: var(--text);
        background: linear-gradient(90deg, rgba(38, 224, 255, 0.14), rgba(139, 92, 255, 0.06));
        box-shadow: inset 0 0 0 1px rgba(38, 224, 255, 0.16);
      }
      .nav-item.active::before {
        content: '';
        position: absolute;
        left: 0;
        top: 8px;
        bottom: 8px;
        width: 3px;
        border-radius: 0 3px 3px 0;
        background: var(--neon-v);
        box-shadow: 0 0 10px rgba(38, 224, 255, 0.7);
      }
      .nav-item.active .icon {
        color: var(--cyan);
      }
      .sidebar-foot {
        margin-top: auto;
        padding-top: var(--sp-4);
        border-top: 1px solid var(--line-2);
      }
      .nav-item.external {
        font-size: var(--fs-sm);
      }
      .nav-backdrop {
        display: none;
      }

      /* ---- Área de conteúdo: a ÚNICA rolagem principal é a da página ---- */
      .app-shell {
        display: block;
        min-height: 100vh;
        padding-top: var(--header-h);
        padding-left: var(--sidebar-w);
      }
      .app-shell:focus {
        outline: none;
      }

      /* ---- Gatilho do Auditor Virtual (Agente Global) no cabeçalho ---- */
      .auditor-trigger {
        display: inline-flex;
        align-items: center;
        gap: var(--sp-2);
        min-height: var(--control-h);
        padding: 0 14px 0 4px;
        border: 1px solid rgba(139, 92, 255, 0.45);
        border-radius: var(--radius-pill);
        background: linear-gradient(90deg, rgba(38, 224, 255, 0.1), rgba(255, 61, 154, 0.08));
        color: var(--text);
        font-size: var(--fs-sm);
        font-weight: 500;
        white-space: nowrap;
        cursor: pointer;
        transition: border-color var(--ease), box-shadow var(--ease);
      }
      .auditor-trigger:hover,
      .auditor-trigger.on {
        border-color: rgba(38, 224, 255, 0.6);
        box-shadow: 0 0 18px -6px rgba(38, 224, 255, 0.7);
      }
      .auditor-trigger:focus-visible {
        outline: none;
        box-shadow: var(--focus);
      }
      /* Égide em neon: o contraste do ícone vem do fundo dual-neon (currentColor escuro). */
      .auditor-ic {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        width: 28px;
        height: 28px;
        border-radius: 50%;
        background: var(--neon-h);
        color: #05070f;
        box-shadow: 0 0 12px -2px rgba(38, 224, 255, 0.6);
      }
      .auditor-ic svg {
        display: block;
      }

      /* ---- Estreito: o menu vira painel sobreposto, aberto pelo botão do cabeçalho ---- */
      @media (max-width: 1023px) {
        .shell-header {
          padding: 0 var(--sp-4);
        }
        .nav-toggle {
          display: inline-flex;
        }
        .sidebar {
          width: min(300px, 86vw);
          border-right: 1px solid var(--line-strong);
          box-shadow: 24px 0 48px -24px rgba(0, 0, 0, 0.95);
          transform: translateX(-100%);
          visibility: hidden;
          transition: transform 0.24s cubic-bezier(0.22, 1, 0.36, 1), visibility 0s linear 0.24s;
        }
        .sidebar.open {
          transform: none;
          visibility: visible;
          transition: transform 0.24s cubic-bezier(0.22, 1, 0.36, 1);
        }
        .nav-backdrop {
          display: block;
          position: fixed;
          inset: var(--header-h) 0 0;
          z-index: 39;
          background: rgba(3, 5, 12, 0.6);
          backdrop-filter: blur(2px);
        }
        .app-shell {
          padding-left: 0;
        }
      }
      @media (max-width: 720px) {
        .shell-header {
          gap: var(--sp-2);
          padding: 0 var(--sp-3);
        }
        .brand-tag {
          display: none;
        }
        .hud-logout {
          width: var(--control-h);
          padding: 0;
        }
        .auditor-trigger {
          padding: 0 4px;
        }
        .hud-logout .lb,
        .auditor-trigger .lb {
          position: absolute;
          width: 1px;
          height: 1px;
          overflow: hidden;
          clip: rect(0, 0, 0, 0);
        }
      }
      @media (max-width: 420px) {
        .brand-text {
          display: none;
        }
      }

      @media (prefers-reduced-motion: reduce) {
        .sidebar,
        .sidebar.open,
        .auditor-trigger {
          transition: none;
        }
      }
    `,
  ],
})
export class App {
  private readonly router = inject(Router);
  protected readonly agent = inject(AgentStateService);
  protected readonly auth = inject(AuthService);

  protected readonly nav = NAV;

  /** Menu sobreposto (telas estreitas). No desktop o menu é fixo e este estado não tem efeito visual. */
  protected readonly navOpen = signal(false);

  /** URL corrente, reativa — semeada no boot e reprojetada a cada NavigationEnd. */
  private readonly currentUrl = signal(this.router.url);

  /**
   * A CASCA da aplicação (menu + drawer do Auditor) só é renderizada autenticado E FORA da tela de login. A checagem
   * de rota é DEFESA EM PROFUNDIDADE: mesmo que isAuthenticated() ainda esteja true (ex.: token em memória não limpo
   * ao expirar a sessão), a rota /login nunca herda o menu.
   */
  protected readonly showShell = computed(() =>
    this.auth.isAuthenticated() && !this.currentUrl().startsWith('/login'));

  /** Largura em que o menu é painel sobreposto (mesmo corte do CSS: < 1024 px). */
  private readonly narrowQuery = window.matchMedia('(max-width: 1023px)');
  protected readonly narrow = signal(this.narrowQuery.matches);

  /** Há uma camada sobre a página (menu sobreposto ou Auditor): o que está atrás fica inerte. */
  protected readonly behind = computed(() => (this.navOpen() && this.narrow()) || this.agent.open());

  private readonly navToggle = viewChild<ElementRef<HTMLButtonElement>>('navToggle');
  private readonly sidebar = viewChild<ElementRef<HTMLElement>>('sidebar');

  constructor() {
    this.narrowQuery.addEventListener('change', (e) => this.narrow.set(e.matches));
    // takeUntilDestroyed encerra a assinatura com o componente (limpo em testes/HMR).
    this.router.events.pipe(takeUntilDestroyed()).subscribe((e) => {
      if (e instanceof NavigationEnd) {
        this.currentUrl.set(e.urlAfterRedirects);
        // Escolher um destino fecha o menu sobreposto; o foco segue para o conteúdo da nova página.
        if (this.navOpen()) {
          this.navOpen.set(false);
          if (this.narrow()) setTimeout(() => this.focusMain());
        }
      }
    });
  }

  protected toggleNav(): void {
    if (this.navOpen()) {
      this.closeNav();
      return;
    }
    this.navOpen.set(true);
    // Foco ao abrir: o item da página atual (ou o primeiro destino).
    setTimeout(() => {
      const root = this.sidebar()?.nativeElement;
      (root?.querySelector<HTMLElement>('.nav-item.active') ?? root?.querySelector<HTMLElement>('.nav-item'))?.focus();
    });
  }

  /** Fecha o menu sobreposto (botão, Escape ou fundo) e devolve o foco ao botão que o abriu. */
  protected closeNav(): void {
    if (!this.navOpen()) return;
    this.navOpen.set(false);
    if (this.narrow()) setTimeout(() => this.navToggle()?.nativeElement.focus());
  }

  /** Tab no último destino volta ao botão do menu: o foco circula só entre o botão e o menu enquanto aberto. */
  protected onSidebarKey(e: KeyboardEvent): void {
    if (e.key !== 'Tab' || e.shiftKey || !this.navOpen() || !this.narrow()) return;
    const items = this.sidebar()?.nativeElement.querySelectorAll<HTMLElement>('a[href]');
    if (items?.length && document.activeElement === items[items.length - 1]) {
      e.preventDefault();
      this.navToggle()?.nativeElement.focus();
    }
  }

  /** Shift+Tab no botão do menu aberto vai ao último destino do menu (fecha o ciclo). */
  protected onToggleKey(e: KeyboardEvent): void {
    if (e.key !== 'Tab' || !e.shiftKey || !this.navOpen() || !this.narrow()) return;
    const items = this.sidebar()?.nativeElement.querySelectorAll<HTMLElement>('a[href]');
    if (items?.length) {
      e.preventDefault();
      items[items.length - 1].focus();
    }
  }

  protected focusMain(): void {
    document.getElementById('conteudo')?.focus();
  }
}

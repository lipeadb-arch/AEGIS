import { Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { DrawerComponent } from './components/drawer.component';
import { AuditorChatComponent } from './components/auditor-chat.component';
import { AgentStateService } from './services/agent-state.service';
import { AuthService } from './services/auth.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, DrawerComponent, AuditorChatComponent],
  template: `
    @if (showShell()) {
    <aside class="sidebar">
      <!-- Logo: 'AEGIS' dentro de um escudo (SVG) com a borda dual-neon. -->
      <a class="brand" routerLink="/dashboard" aria-label="AEGIS — Dashboard">
        <svg
          class="shield"
          viewBox="0 0 120 138"
          fill="none"
          xmlns="http://www.w3.org/2000/svg"
          role="img"
          aria-label="AEGIS"
        >
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
            stroke-width="3"
            stroke-linejoin="round"
          />
          <path d="M60 5 L108 22 V64 C108 96 88 120 60 132 C32 120 12 96 12 64 V22 Z" fill="url(#shieldGlow)" />
          <text
            x="60"
            y="70"
            text-anchor="middle"
            font-family="Orbitron, JetBrains Mono, monospace"
            font-weight="800"
            font-size="20"
            letter-spacing="1.5"
            fill="#eaf1ff"
            style="filter: drop-shadow(0 0 8px rgba(38, 224, 255, 0.55))"
          >
            AEGIS
          </text>
        </svg>
        <span class="brand-sub">Synapse OS</span>
      </a>

      <nav class="side-nav">
        <a class="nav-item home" routerLink="/dashboard" routerLinkActive="active">
          <span class="ic" aria-hidden="true">▦</span>
          <span class="lb">Dashboard</span>
        </a>

        <a class="nav-item" routerLink="/aegis-score" routerLinkActive="active">
          <span class="ic" aria-hidden="true">◈</span>
          <span class="lb">Aegis Score HUD</span>
        </a>

        <p class="nav-group">Funções · NIST CSF 2.0</p>

        <!-- Todas as 6 Funções NIST CSF 2.0 têm tela. -->
        <a class="nav-item" routerLink="/governance" routerLinkActive="active">
          <span class="dot" aria-hidden="true"></span><span class="lb">Govern</span><span class="code">GV</span>
        </a>
        <a class="nav-item" routerLink="/assets" routerLinkActive="active">
          <span class="dot" aria-hidden="true"></span><span class="lb">Identify</span><span class="code">ID</span>
        </a>
        <a class="nav-item" routerLink="/protect" routerLinkActive="active">
          <span class="dot" aria-hidden="true"></span><span class="lb">Protect</span><span class="code">PR</span>
        </a>
        <a class="nav-item" routerLink="/detect" routerLinkActive="active">
          <span class="dot" aria-hidden="true"></span><span class="lb">Detect</span><span class="code">DE</span>
        </a>
        <a class="nav-item" routerLink="/respond" routerLinkActive="active">
          <span class="dot" aria-hidden="true"></span><span class="lb">Respond</span><span class="code">RS</span>
        </a>
        <a class="nav-item" routerLink="/recover" routerLinkActive="active">
          <span class="dot" aria-hidden="true"></span><span class="lb">Recover</span><span class="code">RC</span>
        </a>
      </nav>

      <!-- Gatilho global do Agente: segue no layout raiz, agora no rodapé do sidebar. -->
      <button type="button" class="agent-trigger" [class.on]="agent.open()" (click)="agent.toggle()">
        <span class="pulse"></span> Auditor Virtual
      </button>
    </aside>

    <div class="app-shell">
      <router-outlet />
    </div>

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
      /* ---- Sidebar fixa ---- */
      .sidebar {
        position: fixed;
        left: 0;
        top: 0;
        bottom: 0;
        width: 236px;
        z-index: 40;
        display: flex;
        flex-direction: column;
        padding: 22px 16px 18px;
        background: linear-gradient(180deg, rgba(11, 15, 26, 0.94), rgba(7, 10, 20, 0.96));
        border-right: 1px solid var(--line);
        backdrop-filter: blur(6px);
        overflow-y: auto;
      }
      .sidebar::after {
        content: '';
        position: absolute;
        top: 0;
        right: -1px;
        bottom: 0;
        width: 1px;
        background: var(--neon);
        opacity: 0.55;
        pointer-events: none;
      }

      /* ---- Marca / escudo ---- */
      .brand {
        display: flex;
        flex-direction: column;
        align-items: center;
        text-decoration: none;
        padding: 4px 0 8px;
      }
      .brand .shield {
        width: 96px;
        height: auto;
        filter: drop-shadow(0 8px 20px rgba(38, 224, 255, 0.25));
      }
      .brand-sub {
        margin-top: 6px;
        font-family: var(--mono);
        font-size: 9.5px;
        letter-spacing: 0.28em;
        text-transform: uppercase;
        color: var(--muted);
      }

      /* ---- Navegação ---- */
      .side-nav {
        display: flex;
        flex-direction: column;
        gap: 3px;
        margin-top: 14px;
      }
      .nav-group {
        margin: 16px 10px 7px;
        font-family: var(--mono);
        font-size: 9px;
        letter-spacing: 0.2em;
        text-transform: uppercase;
        color: var(--muted);
        opacity: 0.8;
      }
      .nav-item {
        display: flex;
        align-items: center;
        gap: 11px;
        padding: 10px 12px;
        border-radius: 10px;
        font-family: var(--mono);
        font-size: 12.5px;
        letter-spacing: 0.04em;
        color: var(--muted);
        text-decoration: none;
        border: 1px solid transparent;
        transition: 0.15s;
      }
      .nav-item .lb {
        flex: 1;
      }
      .nav-item .code {
        font-size: 10px;
        letter-spacing: 0.12em;
        color: var(--muted);
        opacity: 0.7;
      }
      .nav-item .dot {
        width: 6px;
        height: 6px;
        border-radius: 50%;
        background: var(--muted);
        opacity: 0.6;
        flex: none;
        transition: 0.15s;
      }
      .nav-item .ic {
        font-size: 14px;
        line-height: 1;
        color: var(--cyan);
      }
      .nav-item:hover {
        color: var(--text);
        background: rgba(122, 145, 190, 0.06);
      }
      .nav-item:hover .dot {
        background: var(--cyan);
        opacity: 1;
        box-shadow: 0 0 8px 1px rgba(38, 224, 255, 0.7);
      }
      .nav-item.active {
        color: var(--text);
        background: linear-gradient(90deg, rgba(38, 224, 255, 0.16), rgba(139, 92, 255, 0.05));
        border-color: var(--line);
        box-shadow: inset 2px 0 0 var(--cyan), 0 0 18px -10px rgba(38, 224, 255, 0.8);
      }
      .nav-item.active .dot {
        background: var(--cyan);
        opacity: 1;
        box-shadow: 0 0 8px 1px rgba(38, 224, 255, 0.7);
      }
      .nav-item.active .code {
        color: var(--cyan);
        opacity: 1;
      }
      .nav-item.home {
        font-family: var(--sans);
        font-weight: 600;
        font-size: 13px;
        letter-spacing: 0.01em;
        margin-bottom: 2px;
      }
      .nav-item.soon {
        cursor: default;
        opacity: 0.5;
      }
      .nav-item.soon:hover {
        background: transparent;
        color: var(--muted);
      }
      .nav-item.soon:hover .dot {
        background: var(--muted);
        opacity: 0.6;
        box-shadow: none;
      }

      /* ---- Gatilho do Agente (rodapé do sidebar) ---- */
      .agent-trigger {
        margin-top: auto;
        display: inline-flex;
        align-items: center;
        justify-content: center;
        gap: 8px;
        width: 100%;
        cursor: pointer;
        font-family: var(--mono);
        font-size: 11.5px;
        letter-spacing: 0.04em;
        color: #05070f;
        font-weight: 600;
        background: var(--neon-h);
        border: 1px solid transparent;
        border-radius: 12px;
        padding: 11px 14px;
        box-shadow: 0 0 16px -4px rgba(38, 224, 255, 0.6);
        transition: 0.15s;
      }
      .agent-trigger:hover {
        box-shadow: 0 0 22px -3px rgba(38, 224, 255, 0.85);
      }
      .agent-trigger.on {
        filter: saturate(1.15);
      }
      .agent-trigger .pulse {
        width: 8px;
        height: 8px;
        border-radius: 50%;
        background: #05070f;
        animation: agent-pulse 1.8s infinite;
      }
      @keyframes agent-pulse {
        0% {
          box-shadow: 0 0 0 0 rgba(5, 7, 15, 0.5);
        }
        70% {
          box-shadow: 0 0 0 6px rgba(5, 7, 15, 0);
        }
        100% {
          box-shadow: 0 0 0 0 rgba(5, 7, 15, 0);
        }
      }

      /* ---- Área de conteúdo ---- */
      .app-shell {
        padding-left: 236px;
        min-height: 100%;
      }

      /* ---- Responsivo: o sidebar vira faixa superior ---- */
      @media (max-width: 960px) {
        .sidebar {
          position: static;
          width: auto;
          bottom: auto;
          flex-direction: row;
          flex-wrap: wrap;
          align-items: center;
          gap: 6px 10px;
          border-right: none;
          border-bottom: 1px solid var(--line);
          padding: 12px 16px;
        }
        .sidebar::after {
          top: auto;
          bottom: -1px;
          right: 0;
          left: 0;
          width: auto;
          height: 1px;
        }
        .brand {
          flex-direction: row;
          gap: 10px;
          padding: 0;
        }
        .brand .shield {
          width: 40px;
        }
        .brand-sub {
          display: none;
        }
        .side-nav {
          flex-direction: row;
          flex-wrap: wrap;
          gap: 4px;
          margin-top: 0;
          margin-left: 6px;
        }
        .nav-group {
          display: none;
        }
        .nav-item {
          padding: 8px 11px;
        }
        .nav-item .code {
          display: none;
        }
        .agent-trigger {
          width: auto;
          margin-left: auto;
        }
        .app-shell {
          padding-left: 0;
        }
      }
    `,
  ],
})
export class App {
  private readonly router = inject(Router);
  protected readonly agent = inject(AgentStateService);
  protected readonly auth = inject(AuthService);

  /** URL corrente, reativa — semeada no boot e reprojetada a cada NavigationEnd. */
  private readonly currentUrl = signal(this.router.url);

  /**
   * A CASCA da aplicação (sidebar + drawer do Auditor) só é renderizada autenticado E FORA da tela de
   * login. A checagem de rota é DEFESA EM PROFUNDIDADE: mesmo que isAuthenticated() ainda esteja true
   * (ex.: token em memória não limpo ao expirar a sessão), a rota /login nunca herda o sidebar.
   */
  protected readonly showShell = computed(() =>
    this.auth.isAuthenticated() && !this.currentUrl().startsWith('/login'));

  constructor() {
    // takeUntilDestroyed encerra a assinatura com o componente (limpo em testes/HMR).
    this.router.events.pipe(takeUntilDestroyed()).subscribe((e) => {
      if (e instanceof NavigationEnd) this.currentUrl.set(e.urlAfterRedirects);
    });
  }
}

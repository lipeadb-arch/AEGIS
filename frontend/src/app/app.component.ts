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

        <p class="nav-group">Telemetria Ativa</p>
        <a class="nav-item" routerLink="/identity" routerLinkActive="active">
          <span class="dot" aria-hidden="true"></span><span class="lb">Identidade · Entra</span><span class="code">IAM</span>
        </a>

        <!-- Referência externa: separada das Funções NIST porque NÃO é uma tela do produto — é a fonte
             normativa. Sem routerLink/routerLinkActive de propósito (é um href externo, não uma rota);
             rel="noopener noreferrer" é obrigatório com target="_blank" (anti tab-nabbing). -->
        <p class="nav-group">Referência</p>
        <a
          class="nav-item external"
          href="https://www.nist.gov/cyberframework"
          target="_blank"
          rel="noopener noreferrer"
        >
          <span class="dot" aria-hidden="true"></span>
          <span class="lb">Sobre o NIST CSF 2.0</span>
          <span class="ext" aria-hidden="true">↗</span>
        </a>
      </nav>
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

    <!-- Gatilho global do Agente: FAB fixo, FORA do fluxo do sidebar — flutua sobre todo o conteúdo.
         Some enquanto o drawer está aberto: com z-index 9999 ele passaria POR CIMA do painel (z-index 60),
         que abre exatamente neste canto; e ali o botão é redundante (o drawer tem o próprio ✕). -->
    <button
      type="button"
      class="agent-fab"
      [class.is-hidden]="agent.open()"
      [attr.aria-expanded]="agent.open()"
      [attr.aria-hidden]="agent.open()"
      [attr.tabindex]="agent.open() ? -1 : null"
      aria-label="Auditor Virtual"
      title="Auditor Virtual"
      (click)="agent.toggle()"
    >
      <!-- Égide: escudo circular com a máscara da Medusa reduzida ao essencial — anel externo, serpentes
           irradiando (traços radiais), rosto em triângulo invertido e o olhar. Geométrico de propósito:
           num alvo de 26px qualquer detalhe figurativo vira sujeira. currentColor herda o #05070f do FAB
           (o contraste vem do fundo neon); o gradiente entra no anel para amarrar ao tema dual-neon. -->
      <svg class="ic" viewBox="0 0 32 32" width="26" height="26" aria-hidden="true" focusable="false">
        <defs>
          <linearGradient id="aegisNeon" x1="4" y1="3" x2="28" y2="29" gradientUnits="userSpaceOnUse">
            <stop stop-color="#26e0ff" />
            <stop offset="0.55" stop-color="#8b5cff" />
            <stop offset="1" stop-color="#ff3d9a" />
          </linearGradient>
        </defs>

        <!-- Serpentes: 8 traços radiais que rompem o anel, a silhueta da cabeleitura da Medusa. -->
        <g stroke="currentColor" stroke-width="1.6" stroke-linecap="round" opacity="0.9">
          <path d="M16 1.5v3" /><path d="M23.9 3.6l-1.5 2.6" /><path d="M28.4 8.1l-2.6 1.5" />
          <path d="M30.5 16h-3" /><path d="M8.1 3.6l1.5 2.6" /><path d="M3.6 8.1l2.6 1.5" />
          <path d="M1.5 16h3" /><path d="M16 30.5v-3" />
        </g>

        <!-- Anel do escudo (a égide propriamente dita). -->
        <circle cx="16" cy="16" r="11.5" fill="none" stroke="url(#aegisNeon)" stroke-width="2.2" />
        <circle cx="16" cy="16" r="8.6" fill="none" stroke="currentColor" stroke-width="1" opacity="0.55" />

        <!-- Rosto: triângulo invertido (têmporas → queixo) + olhar. -->
        <path
          d="M11.4 12.6h9.2c0 4.4-2.2 7.4-4.6 8.9-2.4-1.5-4.6-4.5-4.6-8.9z"
          fill="currentColor"
          opacity="0.92"
        />
        <circle cx="13.9" cy="15.4" r="1.05" fill="#26e0ff" />
        <circle cx="18.1" cy="15.4" r="1.05" fill="#26e0ff" />
      </svg>
      <span class="pulse" aria-hidden="true"></span>
    </button>
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
        /* Mata a barra horizontal: nada aqui deve rolar lateralmente — um item largo (ex.: o link de
           referência) deve truncar, não empurrar o menu. */
        overflow-x: hidden;
        /* Firefox/padrão: equivalente às regras ::-webkit-scrollbar abaixo (que só o WebKit/Blink lê). */
        scrollbar-width: thin;
        scrollbar-color: rgba(255, 255, 255, 0.1) transparent;
      }
      /* Barra vertical no idioma Synapse/Dark: fina, trilho invisível, polegar discreto que só "acende"
         no hover — some no repouso e não compete com o conteúdo do HUD. */
      .sidebar::-webkit-scrollbar {
        width: 6px;
      }
      .sidebar::-webkit-scrollbar-track {
        background: transparent;
      }
      .sidebar::-webkit-scrollbar-thumb {
        background: rgba(255, 255, 255, 0.1);
        border-radius: 3px;
      }
      .sidebar::-webkit-scrollbar-thumb:hover {
        background: rgba(255, 255, 255, 0.22);
      }
      /* Aresta neon: ancorada em right:0 (não -1px). Fora do box ela seria RECORTADA pelo
         overflow-x:hidden acima — e era ela, sozinha, que criava 1px de transbordo e fazia nascer a
         barra de rolagem horizontal que este ajuste veio matar. */
      .sidebar::after {
        content: '';
        position: absolute;
        top: 0;
        right: 0;
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

      /* Link externo: nunca fica "active" (não é rota). A seta ↗ e o rótulo truncável sinalizam que
         a navegação SAI do produto — com overflow-x:hidden no sidebar, truncar é obrigatório. */
      .nav-item.external .lb {
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
      .nav-item.external .ext {
        font-size: 11px;
        color: var(--muted);
        opacity: 0.7;
        flex: none;
      }
      .nav-item.external:hover .ext {
        color: var(--cyan);
        opacity: 1;
      }

      /* ---- Gatilho do Agente: FAB flutuante (fora do fluxo do sidebar) ---- */
      .agent-fab {
        position: fixed;
        bottom: 30px;
        right: 30px;
        z-index: 9999;
        width: 58px;
        height: 58px;
        border-radius: 50%;
        display: inline-flex;
        align-items: center;
        justify-content: center;
        cursor: pointer;
        color: #05070f;
        background: var(--neon-h);
        border: 1px solid transparent;
        /* Halo neon + sombra de elevação: o brilho o cola no tema; a sombra escura o descola do fundo
           do HUD, que também é escuro (só o neon não daria separação). */
        box-shadow: 0 0 18px -2px rgba(38, 224, 255, 0.55), 0 8px 24px -10px rgba(0, 0, 0, 0.9);
        transition: transform 0.18s ease, box-shadow 0.18s ease, opacity 0.18s ease;
      }
      .agent-fab:hover {
        transform: translateY(-2px);
        box-shadow: 0 0 28px 0 rgba(38, 224, 255, 0.85), 0 10px 26px -10px rgba(0, 0, 0, 0.9);
      }
      .agent-fab:focus-visible {
        outline: 2px solid var(--cyan);
        outline-offset: 3px;
      }
      /* SVG, não glifo: display:block evita o espaço fantasma de linha-base do inline. */
      .agent-fab .ic {
        display: block;
        width: 26px;
        height: 26px;
      }
      /* Indicador de atividade: orbita a borda em vez de ocupar o centro (o ícone é o conteúdo). */
      .agent-fab .pulse {
        position: absolute;
        top: 9px;
        right: 9px;
        width: 8px;
        height: 8px;
        border-radius: 50%;
        background: #05070f;
        animation: agent-pulse 1.8s infinite;
      }
      /* Drawer aberto: o FAB sai de cena (ver comentário no template). */
      .agent-fab.is-hidden {
        opacity: 0;
        pointer-events: none;
        transform: scale(0.8);
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
          bottom: 0;
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
        .app-shell {
          padding-left: 0;
        }
        /* O FAB encolhe e se aproxima da borda: em telas pequenas 58px+30px come área de leitura. */
        .agent-fab {
          width: 50px;
          height: 50px;
          bottom: 20px;
          right: 20px;
        }
        .agent-fab .ic {
          width: 23px;
          height: 23px;
        }
      }

      @media (prefers-reduced-motion: reduce) {
        .agent-fab,
        .agent-fab .pulse {
          transition: none;
          animation: none;
        }
        .agent-fab:hover {
          transform: none;
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

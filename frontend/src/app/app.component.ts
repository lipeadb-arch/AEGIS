import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { DrawerComponent } from './components/drawer.component';
import { AuditorChatComponent } from './components/auditor-chat.component';
import { AgentStateService } from './services/agent-state.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, DrawerComponent, AuditorChatComponent],
  template: `
    <nav class="app-nav">
      <a class="nav-brand" routerLink="/dashboard">AEGIS</a>
      <a routerLink="/dashboard" routerLinkActive="active">Dashboard Executivo</a>
      <a routerLink="/assets" routerLinkActive="active">Inventário de Ativos</a>
      <a routerLink="/governance" routerLinkActive="active">Governança</a>

      <!-- Gatilho global do Agente: abre o Auditor em qualquer aba (o contexto segue a rota). -->
      <button type="button" class="agent-trigger" [class.on]="agent.open()" (click)="agent.toggle()">
        <span class="pulse"></span> Auditor Virtual
      </button>
    </nav>
    <router-outlet />

    <!-- Agente Global (Auditor Virtual) — elevado do pilar de Governança ao layout raiz.
         A Função NIST (Govern/Identify/…) é derivada da rota pelo AgentStateService. -->
    <app-drawer
      [open]="agent.open()"
      [title]="agent.drawerTitle()"
      [subtitle]="agent.drawerSubtitle()"
      (closed)="agent.close()"
    >
      <app-auditor-chat />
    </app-drawer>
  `,
  styles: [
    `
      .app-nav {
        display: flex;
        align-items: center;
        gap: 6px;
        max-width: 1280px;
        margin: 0 auto;
        padding: 14px 24px 0;
      }
      .app-nav a {
        font-family: var(--mono);
        font-size: 12px;
        letter-spacing: 0.06em;
        color: var(--muted);
        text-decoration: none;
        padding: 8px 14px;
        border-radius: 9px;
        border: 1px solid transparent;
        transition: 0.15s;
      }
      .app-nav a:hover {
        color: var(--text);
      }
      .app-nav a.active {
        color: var(--text);
        background: var(--panel);
        border-color: var(--line);
      }
      .app-nav .nav-brand {
        font-family: var(--display);
        font-weight: 800;
        letter-spacing: 0.14em;
        background: var(--neon-h);
        -webkit-background-clip: text;
        background-clip: text;
        color: transparent;
        margin-right: 10px;
        padding-left: 0;
      }

      /* ---- Gatilho global do Agente ---- */
      .agent-trigger {
        margin-left: auto;
        display: inline-flex;
        align-items: center;
        gap: 8px;
        cursor: pointer;
        font-family: var(--mono);
        font-size: 11.5px;
        letter-spacing: 0.04em;
        color: #05070f;
        font-weight: 600;
        background: var(--neon-h);
        border: 1px solid transparent;
        border-radius: 999px;
        padding: 7px 15px;
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
    `,
  ],
})
export class App {
  protected readonly agent = inject(AgentStateService);
}

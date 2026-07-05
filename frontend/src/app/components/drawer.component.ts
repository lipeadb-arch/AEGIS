import { Component, OnDestroy, effect, input, output } from '@angular/core';

/**
 * Drawer — painel lateral deslizante genérico (shell, não acoplado ao conteúdo).
 * Projeta o corpo via <ng-content>, então serve para qualquer overlay lateral do app
 * (aqui hospeda o Auditor Virtual). Fecha por ✕, clique no backdrop e tecla ESC.
 *
 * Animação em CSS puro (transform + opacity) — segue o padrão "zero-dependência" da casa,
 * sem @angular/animations. O elemento permanece no DOM quando fechado (classe .open),
 * preservando o estado do conteúdo projetado (ex.: a sessão de chat) ao reabrir.
 */
@Component({
  selector: 'app-drawer',
  standalone: true,
  host: { '(document:keydown.escape)': 'onEsc()' },
  template: `
    <div class="drawer-root" [class.open]="open()" [attr.inert]="open() ? null : ''">
      <div class="backdrop" (click)="requestClose()"></div>
      <aside class="panel" role="dialog" aria-modal="true" [attr.aria-label]="title()">
        <header class="dr-head">
          <div class="dr-titles">
            <span class="dr-title">{{ title() }}</span>
            @if (subtitle()) {
              <span class="dr-sub">{{ subtitle() }}</span>
            }
          </div>
          <button type="button" class="dr-close" (click)="requestClose()" aria-label="Fechar">✕</button>
        </header>
        <div class="dr-body">
          <ng-content />
        </div>
      </aside>
    </div>
  `,
  styles: [
    `
      .drawer-root { position: fixed; inset: 0; z-index: 60; pointer-events: none; }
      .drawer-root.open { pointer-events: auto; }

      .backdrop {
        position: absolute; inset: 0;
        background: rgba(3, 5, 12, 0.62);
        backdrop-filter: blur(3px);
        opacity: 0; transition: opacity 0.28s ease;
      }
      .drawer-root.open .backdrop { opacity: 1; }

      .panel {
        position: absolute; top: 0; right: 0; bottom: 0;
        width: min(468px, 100%);
        display: flex; flex-direction: column;
        background: linear-gradient(180deg, rgba(255, 255, 255, 0.02), rgba(0, 0, 0, 0)) padding-box, var(--panel);
        box-shadow: -24px 0 60px -30px rgba(0, 0, 0, 0.9), 0 0 46px -22px rgba(38, 224, 255, 0.32);
        transform: translateX(100%);
        transition: transform 0.32s cubic-bezier(0.22, 1, 0.36, 1);
      }
      .drawer-root.open .panel { transform: translateX(0); }
      .panel::before {
        content: ""; position: absolute; left: 0; top: 0; bottom: 0; width: 2px;
        background: var(--neon); box-shadow: 0 0 16px rgba(38, 224, 255, 0.5);
      }

      .dr-head {
        display: flex; align-items: flex-start; justify-content: space-between; gap: 12px;
        padding: 18px 20px; border-bottom: 1px solid var(--line);
      }
      .dr-titles { display: flex; flex-direction: column; gap: 4px; }
      .dr-title {
        font-family: var(--display); font-weight: 700; font-size: 15px; letter-spacing: 0.08em;
        text-transform: uppercase; color: var(--text);
      }
      .dr-sub {
        font-family: var(--mono); font-size: 10px; color: var(--muted);
        letter-spacing: 0.14em; text-transform: uppercase;
      }
      .dr-close {
        flex: none; width: 32px; height: 32px; border-radius: 9px; cursor: pointer;
        font-size: 14px; color: var(--muted);
        background: var(--panel-2); border: 1px solid var(--line); transition: 0.15s;
      }
      .dr-close:hover { color: var(--text); border-color: rgba(255, 61, 154, 0.5); }

      .dr-body { flex: 1; min-height: 0; display: flex; flex-direction: column; overflow: hidden; }
    `,
  ],
})
export class DrawerComponent implements OnDestroy {
  /** Controla a visibilidade/animação do painel. */
  open = input(false);
  /** Título exibido no cabeçalho do drawer. */
  title = input('');
  /** Subtítulo opcional (eyebrow em mono). */
  subtitle = input('');
  /** Pedido de fechamento (backdrop, ✕ ou ESC) — o pai decide o estado. */
  closed = output<void>();

  constructor() {
    // Trava o scroll do fundo enquanto o drawer está aberto.
    effect(() => {
      document.body.style.overflow = this.open() ? 'hidden' : '';
    });
  }

  requestClose(): void {
    this.closed.emit();
  }

  onEsc(): void {
    if (this.open()) this.closed.emit();
  }

  ngOnDestroy(): void {
    document.body.style.overflow = '';
  }
}

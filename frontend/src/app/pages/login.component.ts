import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { AuthService, FederationConfig, LoginFlowResult, TenantOption } from '../services/auth.service';
import { FederatedLoginService } from '../services/federated-login.service';

/**
 * Tela de login mínima e funcional — fecha o ciclo de autenticação (o AuthService/interceptors são
 * o foco desta etapa). Sem @angular/forms de propósito (o projeto ainda não usa forms): lê os
 * valores por template refs no submit nativo. Ao migrar para formulários ricos, adotar
 * ReactiveFormsModule. Estilo alinhado ao tema dark-neon; refina-se depois com o resto da UI.
 *
 * [AEGIS-AUD-007] Passa a respeitar a config PÚBLICA de federação: o botão "Entrar com conta
 * corporativa" só aparece quando a federação está ligada, e o formulário de senha some em modo
 * Federated (em Local/Hybrid ele permanece). A senha continua igual; o login corporativo usa MSAL.
 */
@Component({
  selector: 'app-login',
  standalone: true,
  template: `
    <div class="login-wrap">
      <div class="login-card">
        <!-- Marca: o mesmo escudo dual-neon do cabeçalho; Orbitron só no nome. -->
        <div class="brand">
          <svg class="shield" viewBox="0 0 120 138" fill="none" aria-hidden="true">
            <defs>
              <linearGradient id="loginShieldStroke" x1="8" y1="6" x2="112" y2="132" gradientUnits="userSpaceOnUse">
                <stop stop-color="#26e0ff" />
                <stop offset="0.55" stop-color="#8b5cff" />
                <stop offset="1" stop-color="#ff3d9a" />
              </linearGradient>
              <linearGradient id="loginShieldFill" x1="60" y1="5" x2="60" y2="132" gradientUnits="userSpaceOnUse">
                <stop stop-color="#26e0ff" stop-opacity="0.16" />
                <stop offset="1" stop-color="#0b0f1a" stop-opacity="0.25" />
              </linearGradient>
            </defs>
            <path
              d="M60 5 L108 22 V64 C108 96 88 120 60 132 C32 120 12 96 12 64 V22 Z"
              fill="url(#loginShieldFill)"
              stroke="url(#loginShieldStroke)"
              stroke-width="5"
              stroke-linejoin="round"
            />
          </svg>
          <h1 class="title">AEGIS</h1>
        </div>
        <p class="sub">Acesso ao painel de postura e evidências de segurança</p>

        @if (selection(); as sel) {
          <!-- [AEGIS-AUD-012] Seleção explícita de ambiente: vários acessos sem último tenant válido. -->
          <p class="sub">Selecione o ambiente para continuar</p>
          <ul class="tenant-list" role="listbox">
            @for (t of sel.tenants; track t.id) {
              <li>
                <button
                  type="button"
                  role="option"
                  class="tenant"
                  [disabled]="loading()"
                  (click)="chooseTenant(t.id)"
                >
                  <span class="t-name">{{ t.name }}</span>
                  <span class="t-role">{{ t.role }}</span>
                </button>
              </li>
            }
          </ul>
          @if (error()) {
            <p class="error" role="alert">{{ error() }}</p>
          }
        } @else if (configLoading()) {
          <p class="loading">Carregando…</p>
        } @else if (configError()) {
          <!-- Fail-closed: enquanto a config não carrega, NÃO mostramos formulário nem botão corporativo. -->
          <p class="error" role="alert">Não foi possível carregar a configuração de autenticação.</p>
          <button type="button" class="corp" (click)="loadConfig()">Tentar novamente</button>
        } @else {
          @if (passwordLoginEnabled()) {
            <form (submit)="submit($event, emailEl.value, pwEl.value)">
              <label class="field">
                <span>E-mail</span>
                <input #emailEl type="email" name="email" autocomplete="username" required />
              </label>

              <label class="field">
                <span>Senha</span>
                <input #pwEl type="password" name="password" autocomplete="current-password" required />
              </label>

              <button type="submit" class="submit" [disabled]="loading()">
                {{ loading() ? 'Entrando…' : 'Entrar' }}
              </button>
            </form>
          }

          @if (federationEnabled() && passwordLoginEnabled()) {
            <div class="divider"><span>ou</span></div>
          }

          @if (federationEnabled()) {
            <button type="button" class="corp" [disabled]="loading()" (click)="loginCorporate()">
              {{ loading() ? 'Conectando…' : 'Entrar com conta corporativa' }}
            </button>
          }

          @if (error()) {
            <p class="error" role="alert">{{ error() }}</p>
          }
        }
      </div>
    </div>
  `,
  styles: [
    `
      .login-wrap {
        min-height: 100vh;
        display: grid;
        place-items: center;
        padding: var(--sp-6) var(--sp-4);
        background: radial-gradient(70% 55% at 50% 0%, rgba(38, 224, 255, 0.08), transparent 70%), var(--void);
      }
      .login-card {
        position: relative;
        width: 100%;
        max-width: 400px;
        display: flex;
        flex-direction: column;
        gap: var(--sp-4);
        padding: var(--sp-8) var(--sp-6) var(--sp-6);
        border: 1px solid var(--line);
        border-radius: var(--radius-lg);
        background: linear-gradient(180deg, rgba(255, 255, 255, 0.02), transparent 120px), var(--panel);
        box-shadow: var(--shadow-panel), 0 0 48px -18px rgba(38, 224, 255, 0.45);
        overflow: hidden;
      }
      .login-card::before {
        content: '';
        position: absolute;
        inset: 0 0 auto;
        height: 2px;
        background: var(--neon-h);
      }
      .brand {
        display: flex;
        flex-direction: column;
        align-items: center;
        gap: var(--sp-3);
      }
      .shield {
        width: 56px;
        height: auto;
        filter: drop-shadow(0 6px 16px rgba(38, 224, 255, 0.35));
      }
      .title {
        margin: 0;
        font-family: var(--brand);
        font-size: 24px;
        font-weight: 800;
        letter-spacing: 0.14em;
        background: var(--neon-h);
        -webkit-background-clip: text;
        background-clip: text;
        color: transparent;
      }
      .sub {
        margin: 0;
        text-align: center;
        font-size: var(--fs-sm);
        line-height: var(--lh);
        color: var(--text-2);
      }
      .field {
        display: flex;
        flex-direction: column;
        gap: 6px;
        font-size: var(--fs-sm);
        font-weight: 500;
        color: var(--text-2);
      }
      .field input {
        min-height: 44px;
        padding: 0 var(--sp-3);
        border-radius: var(--radius-sm);
        border: 1px solid var(--line-strong);
        background: var(--void-2);
        color: var(--text);
        font-size: var(--fs-body);
        outline: none;
      }
      .field input:focus-visible {
        border-color: var(--cyan);
        box-shadow: var(--focus);
      }
      .loading {
        margin: 0;
        text-align: center;
        font-size: var(--fs-sm);
        color: var(--text-2);
      }
      .error {
        margin: 0;
        font-size: var(--fs-sm);
        color: var(--red-text);
      }
      .submit {
        min-height: 44px;
        margin-top: var(--sp-1);
        border: none;
        border-radius: var(--radius-sm);
        cursor: pointer;
        font-size: var(--fs-body);
        font-weight: 600;
        color: var(--void);
        background: var(--neon-h);
        transition: filter 0.15s;
      }
      .submit:focus-visible,
      .corp:focus-visible,
      .tenant:focus-visible {
        outline: none;
        box-shadow: var(--focus);
      }
      .submit:disabled {
        opacity: 0.7;
        cursor: default;
      }
      .submit:not(:disabled):hover {
        filter: saturate(1.15) brightness(1.05);
      }
      form {
        display: flex;
        flex-direction: column;
        gap: 14px;
      }
      .divider {
        display: flex;
        align-items: center;
        gap: 10px;
        color: var(--muted, #7a91be);
        font-size: var(--fs-meta);
        text-transform: uppercase;
        letter-spacing: var(--tracking-caps);
      }
      .divider::before,
      .divider::after {
        content: '';
        flex: 1;
        height: 1px;
        background: var(--line, #1b2438);
      }
      .corp {
        padding: 12px;
        border-radius: 10px;
        cursor: pointer;
        font-weight: 600;
        color: var(--text, #eaf1ff);
        background: rgba(5, 7, 15, 0.6);
        border: 1px solid var(--cyan, #26e0ff);
        transition:
          filter 0.15s,
          background 0.15s;
      }
      .corp:disabled {
        opacity: 0.7;
        cursor: default;
      }
      .corp:not(:disabled):hover {
        background: rgba(38, 224, 255, 0.08);
      }
      /* [AEGIS-AUD-012] Seletor de ambiente no login — mesmo idioma visual do card e do tenant switcher. */
      .tenant-list {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 8px;
      }
      .tenant {
        display: flex;
        justify-content: space-between;
        align-items: center;
        gap: 12px;
        width: 100%;
        padding: 12px 14px;
        border-radius: 10px;
        cursor: pointer;
        text-align: left;
        color: var(--text, #eaf1ff);
        background: rgba(5, 7, 15, 0.6);
        border: 1px solid var(--line, #1b2438);
        transition:
          border-color 0.15s,
          background 0.15s;
      }
      .tenant:not(:disabled):hover {
        border-color: var(--cyan, #26e0ff);
        background: rgba(38, 224, 255, 0.08);
      }
      .tenant:disabled {
        opacity: 0.7;
        cursor: default;
      }
      .tenant .t-name {
        font-size: 14px;
        font-weight: 600;
      }
      .tenant .t-role {
        font-size: var(--fs-caps);
        letter-spacing: var(--tracking-caps);
        text-transform: uppercase;
        opacity: 0.7;
      }
    `,
  ],
})
export class LoginComponent implements OnInit {
  private readonly auth = inject(AuthService);
  private readonly federated = inject(FederatedLoginService);
  private readonly router = inject(Router);

  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  private readonly config = signal<FederationConfig | null>(null);
  readonly configLoading = signal(true);
  readonly configError = signal(false);

  /**
   * [AEGIS-AUD-012] Seleção de ambiente pendente: quando o login (local OU corporativo) resolve em vários
   * acessos sem último tenant válido, o servidor devolve os ambientes + um ticket curto. Enquanto isto está
   * setado, a tela mostra o seletor no lugar do formulário. Some ao concluir ou ao recomeçar o login.
   */
  readonly selection = signal<{ ticket: string; tenants: TenantOption[] } | null>(null);

  // FAIL-CLOSED: nada de formulário nem botão até a config carregar. Só quando o servidor RESPONDE é que
  // decidimos — em Local aparece o formulário; em Federated some; o botão corporativo só com federação ligada.
  readonly passwordLoginEnabled = computed(() => this.config()?.passwordLoginEnabled === true);
  readonly federationEnabled = computed(() => this.config()?.enabled === true);

  ngOnInit(): void {
    this.loadConfig();
  }

  /** Carrega a config de autenticação. Fail-closed: erro NÃO vira Local — mostra estado genérico + retry. */
  loadConfig(): void {
    this.configLoading.set(true);
    this.configError.set(false);
    this.error.set(null);
    this.auth.federationConfig().subscribe({
      next: (cfg) => {
        this.config.set(cfg);
        this.configLoading.set(false);
      },
      error: () => {
        this.config.set(null);
        this.configError.set(true);
        this.configLoading.set(false);
      },
    });
  }

  submit(event: Event, email: string, password: string): void {
    event.preventDefault();
    if (this.loading()) return;

    this.loading.set(true);
    this.error.set(null);

    this.auth.login(email, password).subscribe({
      next: (result) => this.afterLogin(result),
      error: () => {
        this.error.set('Credenciais inválidas.');
        this.loading.set(false);
      },
    });
  }

  /**
   * [AEGIS-AUD-012] Desfecho do login/troca federada: sessão pronta → dashboard; seleção exigida → mostra a
   * lista de ambientes. O MESMO fluxo serve ao login local e ao corporativo.
   */
  private afterLogin(result: LoginFlowResult): void {
    if (result.kind === 'selection') {
      this.selection.set({ ticket: result.ticket, tenants: result.tenants });
      this.loading.set(false);
      return;
    }
    this.router.navigateByUrl('/dashboard');
  }

  /** [AEGIS-AUD-012] Conclui a seleção com o ambiente escolhido. Falha (ticket expirado/alvo inválido) recomeça o login. */
  chooseTenant(tenantId: string): void {
    const sel = this.selection();
    if (!sel || this.loading()) return;

    this.loading.set(true);
    this.error.set(null);
    this.auth.selectTenant(sel.ticket, tenantId).subscribe({
      next: () => this.router.navigateByUrl('/dashboard'),
      error: () => {
        this.error.set('Não foi possível concluir a seleção. Faça login novamente.');
        this.selection.set(null);
        this.loading.set(false);
      },
    });
  }

  /**
   * [AEGIS-AUD-007] Login corporativo: abre o MSAL (popup + PKCE), obtém o access token do Entra e o
   * troca por uma sessão local. Erro sempre genérico — nunca expõe claims ou detalhes do Entra.
   */
  async loginCorporate(): Promise<void> {
    const cfg = this.config();
    if (this.loading() || !cfg?.enabled || !cfg.authority || !cfg.spaClientId || !cfg.scope) return;

    this.loading.set(true);
    this.error.set(null);
    try {
      const token = await this.federated.acquireApiToken(cfg.authority, cfg.spaClientId, cfg.scope);
      this.auth.exchangeFederated(token).subscribe({
        next: (result) => this.afterLogin(result),
        error: () => {
          this.error.set('Não foi possível autenticar a conta corporativa.');
          this.loading.set(false);
        },
      });
    } catch {
      this.error.set('Não foi possível autenticar a conta corporativa.');
      this.loading.set(false);
    }
  }
}

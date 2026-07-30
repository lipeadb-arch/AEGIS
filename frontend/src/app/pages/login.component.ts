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
        <h1 class="title">AEGIS</h1>
        <p class="sub">Acesso ao painel de maturidade cibernética</p>

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
        padding: 24px;
      }
      .login-card {
        width: 100%;
        max-width: 360px;
        display: flex;
        flex-direction: column;
        gap: 14px;
        padding: 32px 28px;
        border: 1px solid var(--line, #1b2438);
        border-radius: 16px;
        background: linear-gradient(180deg, rgba(11, 15, 26, 0.9), rgba(7, 10, 20, 0.95));
        box-shadow: 0 0 40px -12px rgba(38, 224, 255, 0.4);
      }
      .title {
        margin: 0;
        text-align: center;
        font-family: var(--mono, monospace);
        letter-spacing: 0.3em;
        color: var(--text, #eaf1ff);
        text-shadow: 0 0 12px rgba(38, 224, 255, 0.5);
      }
      .sub {
        margin: 0 0 6px;
        text-align: center;
        font-size: 12px;
        color: var(--muted, #7a91be);
      }
      .field {
        display: flex;
        flex-direction: column;
        gap: 6px;
        font-size: 12px;
        color: var(--muted, #7a91be);
      }
      .field input {
        padding: 10px 12px;
        border-radius: 10px;
        border: 1px solid var(--line, #1b2438);
        background: rgba(5, 7, 15, 0.6);
        color: var(--text, #eaf1ff);
        font-size: 14px;
        outline: none;
      }
      .field input:focus {
        border-color: var(--cyan, #26e0ff);
        box-shadow: 0 0 0 2px rgba(38, 224, 255, 0.2);
      }
      .loading {
        margin: 0;
        text-align: center;
        font-size: 12px;
        color: var(--muted, #7a91be);
      }
      .error {
        margin: 0;
        font-size: 12px;
        color: #ff6b8b;
      }
      .submit {
        margin-top: 6px;
        padding: 12px;
        border: none;
        border-radius: 10px;
        cursor: pointer;
        font-weight: 600;
        color: #05070f;
        background: var(--neon-h, linear-gradient(90deg, #26e0ff, #8b5cff));
        transition: filter 0.15s;
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
        font-size: 11px;
        text-transform: uppercase;
        letter-spacing: 0.15em;
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
        font-size: 10px;
        letter-spacing: 0.08em;
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

import { Component, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { AuthService } from '../services/auth.service';

/**
 * Tela de login mínima e funcional — fecha o ciclo de autenticação (o AuthService/interceptors são
 * o foco desta etapa). Sem @angular/forms de propósito (o projeto ainda não usa forms): lê os
 * valores por template refs no submit nativo. Ao migrar para formulários ricos, adotar
 * ReactiveFormsModule. Estilo alinhado ao tema dark-neon; refina-se depois com o resto da UI.
 */
@Component({
  selector: 'app-login',
  standalone: true,
  template: `
    <div class="login-wrap">
      <form class="login-card" (submit)="submit($event, emailEl.value, pwEl.value)">
        <h1 class="title">AEGIS</h1>
        <p class="sub">Acesso ao painel de maturidade cibernética</p>

        <label class="field">
          <span>E-mail</span>
          <input #emailEl type="email" name="email" autocomplete="username" required />
        </label>

        <label class="field">
          <span>Senha</span>
          <input #pwEl type="password" name="password" autocomplete="current-password" required />
        </label>

        @if (error()) {
          <p class="error" role="alert">{{ error() }}</p>
        }

        <button type="submit" class="submit" [disabled]="loading()">
          {{ loading() ? 'Entrando…' : 'Entrar' }}
        </button>
      </form>
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
    `,
  ],
})
export class LoginComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  submit(event: Event, email: string, password: string): void {
    event.preventDefault();
    if (this.loading()) return;

    this.loading.set(true);
    this.error.set(null);

    this.auth.login(email, password).subscribe({
      next: () => this.router.navigateByUrl('/dashboard'),
      error: () => {
        this.error.set('Credenciais inválidas.');
        this.loading.set(false);
      },
    });
  }
}

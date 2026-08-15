import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { AuthService } from '../../services/auth.service';
import { TenantContextService } from '../../services/tenant-context.service';
import { TenantAdminService } from '../../services/tenant-admin.service';

/** Slug amigável derivado do nome: minúsculas, sem acento, não-alfanumérico → hífen, aparado, até 64. */
function slugify(name: string): string {
  return (name ?? '')
    .toLowerCase()
    .normalize('NFD')
    .replace(/[̀-ͯ]/g, '')
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 64);
}

/**
 * Aba GERAL: identidade/ambiente ativos, criação de tenant (só PlatformAdmin) e "Minha conta" (troca de
 * senha para contas com credencial local). Projeções de claims vêm do AuthService (nunca do storage).
 */
@Component({
  selector: 'app-settings-general',
  standalone: true,
  imports: [ReactiveFormsModule],
  template: `
    <!-- ================= Ambiente e identidade ================= -->
    <div class="panel">
      <h2>Ambiente ativo</h2>
      <dl class="kv">
        <div><dt>Ambiente</dt><dd>{{ tenant()?.name ?? '—' }}</dd></div>
        <div><dt>Identificador (slug)</dt><dd class="mono">{{ tenant()?.slug ?? '—' }}</dd></div>
        <div><dt>Seu papel aqui</dt><dd>{{ roleLabel() }}</dd></div>
        <div><dt>Autoridade global</dt><dd>{{ auth.isPlatformAdmin() ? 'Administrador da plataforma' : '—' }}</dd></div>
        <div><dt>Modo de autenticação</dt><dd>{{ authMode() }}</dd></div>
      </dl>
    </div>

    <div class="panel">
      <h2>Você</h2>
      <dl class="kv">
        <div><dt>Nome</dt><dd>{{ auth.displayName() ?? '—' }}</dd></div>
        <div><dt>E-mail</dt><dd>{{ auth.email() ?? '—' }}</dd></div>
      </dl>
      <p class="note">
        O primeiro administrador é criado no provisionamento seguro da instalação (bootstrap), não por esta
        tela. Novos usuários são adicionados na aba <strong>Usuários e acessos</strong>.
      </p>
    </div>

    <!-- ================= Criar ambiente (PlatformAdmin) ================= -->
    @if (auth.isPlatformAdmin()) {
      <div class="panel">
        <h2>Criar novo ambiente</h2>
        <p class="note">
          Cria um ambiente (tenant) e concede a você acesso de <strong>Administrador</strong> nele.
        </p>

        @if (createdTenant(); as created) {
          <div class="msg ok" role="status">
            Ambiente “{{ created.name }}” criado. Ele já aparece no seletor de ambientes.
            <button type="button" class="btn" (click)="switchTo(created.id)" [disabled]="switching()">
              {{ switching() ? 'Alternando…' : 'Alternar para este ambiente' }}
            </button>
          </div>
        } @else {
          <form class="form" [formGroup]="tenantForm" (ngSubmit)="createTenant()">
            <label class="field">
              <span>Nome</span>
              <input type="text" formControlName="name" autocomplete="off" (input)="onNameInput()" />
              @if (invalid(tenantForm, 'name')) {
                <small class="err">Nome obrigatório.</small>
              }
            </label>
            <label class="field">
              <span>Identificador (slug)</span>
              <input type="text" formControlName="slug" autocomplete="off" placeholder="ex.: acme-corp" />
              @if (invalid(tenantForm, 'slug')) {
                <small class="err">2–64 caracteres: minúsculas, dígitos e hífens internos.</small>
              }
            </label>
            @if (createError()) {
              <p class="msg err" role="alert">{{ createError() }}</p>
            }
            <div class="actions">
              <button type="submit" class="btn primary" [disabled]="creating() || tenantForm.invalid">
                {{ creating() ? 'Criando…' : 'Criar ambiente' }}
              </button>
            </div>
          </form>
        }
      </div>
    }

    <!-- ================= Minha conta (senha) ================= -->
    <div class="panel">
      <h2>Minha conta</h2>
      @if (auth.hasLocalCredential()) {
        <p class="note">
          Trocar a senha encerra todas as suas sessões (em todos os ambientes); você precisará entrar de novo.
        </p>
        <form class="form" [formGroup]="pwForm" (ngSubmit)="changePassword()">
          <label class="field">
            <span>Senha atual</span>
            <input type="password" formControlName="current" autocomplete="current-password" />
          </label>
          <label class="field">
            <span>Nova senha (12 a 128 caracteres)</span>
            <input type="password" formControlName="next" autocomplete="new-password" />
            @if (invalid(pwForm, 'next')) {
              <small class="err">A nova senha deve ter entre 12 e 128 caracteres.</small>
            }
          </label>
          <label class="field">
            <span>Confirmar nova senha</span>
            <input type="password" formControlName="confirm" autocomplete="new-password" />
            @if (pwForm.hasError('mismatch') && pwForm.get('confirm')?.touched) {
              <small class="err">A confirmação não confere.</small>
            }
          </label>
          @if (pwError()) {
            <p class="msg err" role="alert">{{ pwError() }}</p>
          }
          <div class="actions">
            <button type="submit" class="btn primary" [disabled]="changingPw() || pwForm.invalid">
              {{ changingPw() ? 'Trocando…' : 'Trocar senha' }}
            </button>
          </div>
        </form>
      } @else {
        <p class="note">
          Sua credencial é administrada pelo provedor corporativo. Não há senha local para trocar aqui.
        </p>
      }
    </div>
  `,
  styles: [
    `
      .panel {
        background: var(--panel, rgba(11, 15, 26, 0.6));
        border: 1px solid var(--line);
        border-radius: 12px;
        padding: 1.1rem 1.25rem;
        margin-bottom: 1.1rem;
      }
      h2 {
        margin: 0 0 0.9rem;
        font-size: 1rem;
        color: var(--text);
      }
      .kv {
        margin: 0;
        display: grid;
        grid-template-columns: 1fr;
        gap: 0.55rem;
      }
      .kv > div {
        display: flex;
        justify-content: space-between;
        gap: 1rem;
        border-bottom: 1px dashed var(--line);
        padding-bottom: 0.5rem;
      }
      .kv dt {
        color: var(--muted);
        font-size: 0.82rem;
      }
      .kv dd {
        margin: 0;
        color: var(--text);
        font-size: 0.88rem;
        text-align: right;
      }
      .mono {
        font-family: var(--mono);
      }
      .note {
        margin: 0.9rem 0 0;
        font-size: 0.8rem;
        color: var(--muted);
        line-height: 1.5;
      }
      .form {
        display: flex;
        flex-direction: column;
        gap: 0.85rem;
        margin-top: 0.9rem;
        max-width: 420px;
      }
      .field {
        display: flex;
        flex-direction: column;
        gap: 0.35rem;
        font-size: 0.8rem;
        color: var(--muted);
      }
      .field input {
        padding: 0.6rem 0.7rem;
        border-radius: 8px;
        border: 1px solid var(--line);
        background: var(--void, rgba(5, 7, 15, 0.6));
        color: var(--text);
        font-size: 0.9rem;
        outline: none;
      }
      .field input:focus {
        border-color: var(--cyan);
        box-shadow: 0 0 0 2px rgba(38, 224, 255, 0.2);
      }
      .err {
        color: var(--red, #ff6b8b);
        font-size: 0.75rem;
      }
      .actions {
        display: flex;
        gap: 0.6rem;
      }
      .btn {
        padding: 0.55rem 0.9rem;
        border-radius: 8px;
        border: 1px solid var(--line);
        background: rgba(122, 145, 190, 0.08);
        color: var(--text);
        font-family: var(--mono);
        font-size: 0.82rem;
        cursor: pointer;
      }
      .btn:disabled {
        opacity: 0.6;
        cursor: default;
      }
      .btn.primary {
        border: none;
        color: #05070f;
        background: var(--neon-h, linear-gradient(90deg, #26e0ff, #8b5cff));
        font-weight: 600;
      }
      .msg {
        margin: 0.4rem 0 0;
        font-size: 0.82rem;
        border-radius: 8px;
        padding: 0.6rem 0.75rem;
      }
      .msg.err {
        color: var(--red, #ff6b8b);
        background: rgba(255, 107, 139, 0.08);
      }
      .msg.ok {
        color: var(--text);
        background: rgba(38, 224, 255, 0.08);
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 0.75rem;
      }
    `,
  ],
})
export class SettingsGeneralComponent implements OnInit {
  protected readonly auth = inject(AuthService);
  private readonly tenantContext = inject(TenantContextService);
  private readonly tenants = inject(TenantAdminService);
  private readonly fb = inject(FormBuilder);

  protected readonly tenant = this.auth.activeTenant;
  protected readonly switching = this.tenantContext.switching;

  private readonly _authMode = signal<string>('—');
  protected readonly authMode = this._authMode.asReadonly();

  protected readonly creating = signal(false);
  protected readonly createError = signal<string | null>(null);
  protected readonly createdTenant = signal<{ id: string; name: string } | null>(null);

  protected readonly changingPw = signal(false);
  protected readonly pwError = signal<string | null>(null);

  private slugEdited = false;

  protected readonly tenantForm = this.fb.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(200)]],
    slug: ['', [Validators.required, Validators.pattern(/^[a-z0-9][a-z0-9-]{0,62}[a-z0-9]$/)]],
  });

  protected readonly pwForm = this.fb.nonNullable.group(
    {
      current: ['', [Validators.required]],
      next: ['', [Validators.required, Validators.minLength(12), Validators.maxLength(128)]],
      confirm: ['', [Validators.required]],
    },
    { validators: (group) => (group.get('next')?.value === group.get('confirm')?.value ? null : { mismatch: true }) },
  );

  protected readonly roleLabel = computed(() => {
    switch (this.auth.activeRole()) {
      case 'TenantAdmin':
        return 'Administrador';
      case 'Manager':
        return 'Gestor';
      case 'Analyst':
        return 'Analista';
      default:
        return '—';
    }
  });

  ngOnInit(): void {
    // Modo Local/Federated/Hybrid vem da config pública da federação (não é segredo).
    this.auth.federationConfig().subscribe({
      next: (cfg) => this._authMode.set(cfg.mode || 'Local'),
      error: () => this._authMode.set('—'),
    });
  }

  /** Sugere o slug a partir do nome enquanto o usuário não o editar manualmente. */
  onNameInput(): void {
    const slugCtrl = this.tenantForm.controls.slug;
    if (this.slugEdited && slugCtrl.value) return;
    slugCtrl.setValue(slugify(this.tenantForm.controls.name.value));
    // Se o usuário depois editar o slug, respeitamos a edição.
    slugCtrl.markAsPristine();
  }

  invalid(form: unknown, control: string): boolean {
    const c = (form as typeof this.tenantForm).get(control);
    return !!c && c.invalid && (c.touched || c.dirty);
  }

  createTenant(): void {
    if (this.creating() || this.tenantForm.invalid) return;
    this.slugEdited = true;
    this.creating.set(true);
    this.createError.set(null);
    const { name, slug } = this.tenantForm.getRawValue();
    this.tenants.createTenant(name.trim(), slug.trim()).subscribe({
      next: (res) => {
        this.creating.set(false);
        this.createdTenant.set({ id: res.id, name: name.trim() });
        this.tenantForm.reset();
        // Atualiza a lista de ambientes do seletor (o novo já vem com acesso admin).
        this.auth.getAvailableTenants().subscribe();
      },
      error: (e: Error) => {
        this.creating.set(false);
        this.createError.set(e.message);
      },
    });
  }

  /** Alternar para o ambiente recém-criado — usa o fluxo central de troca (cancela leituras + recarrega). */
  switchTo(tenantId: string): void {
    this.tenantContext.switch(tenantId);
  }

  changePassword(): void {
    if (this.changingPw() || this.pwForm.invalid) return;
    this.changingPw.set(true);
    this.pwError.set(null);
    const { current, next } = this.pwForm.getRawValue();
    this.auth.changePassword(current, next).subscribe({
      next: () => {
        // Sucesso: NUNCA deixa a senha no DOM. Limpa e encerra a sessão (o backend já revogou tudo).
        this.pwForm.reset();
        this.changingPw.set(false);
        this.auth.forceLogout();
      },
      error: (e: Error) => {
        // Erro definitivo: limpa a senha do DOM e mostra a mensagem.
        this.pwForm.reset();
        this.changingPw.set(false);
        this.pwError.set(e.message);
      },
    });
  }
}

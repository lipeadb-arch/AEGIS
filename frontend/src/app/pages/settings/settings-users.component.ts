import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { AuthService } from '../../services/auth.service';
import { UsersService } from '../../services/users.service';
import {
  ASSIGNABLE_ROLES,
  ROLE_LABELS,
  TENANT_ROLE_VALUE,
  TenantRoleName,
  TenantUser,
  roleLabel,
} from '../../models/users.models';

/**
 * Aba USUÁRIOS E ACESSOS: listagem tenant-scoped com busca/filtros/estados, edição de nome/papel,
 * desativação (com confirmação)/reativação, e onboarding (só para quem tem PlatformAdmin + TenantAdmin).
 * A visibilidade e as desabilitações são UX; o backend permanece a autoridade (409/403 sanitizados).
 */
@Component({
  selector: 'app-settings-users',
  standalone: true,
  imports: [ReactiveFormsModule, DatePipe],
  template: `
    <!-- ============ Onboarding (só PlatformAdmin + TenantAdmin) ============ -->
    @if (canCreate()) {
      <div class="panel">
        <h2>Adicionar usuário</h2>
        <form class="form" [formGroup]="onboardForm" (ngSubmit)="onboard()">
          <div class="grid">
            <label class="field">
              <span>E-mail</span>
              <input type="email" formControlName="email" autocomplete="off" />
            </label>
            <label class="field">
              <span>Nome de exibição</span>
              <input type="text" formControlName="displayName" autocomplete="off" />
            </label>
            <label class="field">
              <span>Papel</span>
              <select formControlName="role">
                @for (r of assignableRoles; track r) {
                  <option [value]="r">{{ labels[r] }}</option>
                }
              </select>
            </label>
            @if (authMode() !== 'Federated') {
              <label class="field">
                <span>Senha inicial {{ authMode() === 'Local' ? '(nova pessoa)' : '(opcional)' }}</span>
                <input type="password" formControlName="initialPassword" autocomplete="new-password" />
              </label>
            }
          </div>
          <p class="note">
            Se o e-mail já existir globalmente, apenas o acesso a este ambiente é concedido — a credencial,
            o papel global e o vínculo corporativo são preservados. Uma senha informada nunca redefine uma
            conta existente.
          </p>
          @if (onboardError()) {
            <p class="msg err" role="alert">{{ onboardError() }}</p>
          }
          @if (onboardOk()) {
            <p class="msg ok" role="status">{{ onboardOk() }}</p>
          }
          <div class="actions">
            <button type="submit" class="btn primary" [disabled]="onboarding() || onboardForm.invalid">
              {{ onboarding() ? 'Adicionando…' : 'Adicionar usuário' }}
            </button>
          </div>
        </form>
      </div>
    } @else {
      <p class="note standalone">
        Você administra os usuários já vinculados a este ambiente. Criar novas identidades exige um
        <strong>administrador da plataforma</strong>.
      </p>
    }

    <!-- ============ Busca + filtros ============ -->
    <div class="toolbar">
      <input
        class="search"
        type="search"
        placeholder="Buscar por nome ou e-mail…"
        [value]="search()"
        (input)="search.set($any($event.target).value)"
        aria-label="Buscar usuários"
      />
      <select [value]="roleFilter()" (change)="roleFilter.set($any($event.target).value)" aria-label="Filtrar por papel">
        <option value="">Todos os papéis</option>
        @for (r of assignableRoles; track r) {
          <option [value]="r">{{ labels[r] }}</option>
        }
      </select>
      <select [value]="statusFilter()" (change)="statusFilter.set($any($event.target).value)" aria-label="Filtrar por status">
        <option value="">Todos os status</option>
        <option value="active">Ativos</option>
        <option value="inactive">Inativos</option>
      </select>
      <button type="button" class="btn" (click)="reload()" [disabled]="loading()">
        {{ loading() ? 'Carregando…' : 'Atualizar' }}
      </button>
    </div>

    <!-- ============ Estados da lista ============ -->
    @if (loading()) {
      <p class="state">Carregando usuários…</p>
    } @else if (loadError()) {
      <div class="state">
        <p class="msg err">{{ loadError() }}</p>
        <button type="button" class="btn" (click)="reload()">Tentar novamente</button>
      </div>
    } @else if (users().length === 0) {
      <p class="state">Nenhum usuário neste ambiente ainda.</p>
    } @else if (filtered().length === 0) {
      <p class="state">Nenhum usuário corresponde à busca/filtros.</p>
    } @else {
      <ul class="users">
        @for (u of filtered(); track u.id) {
          <li class="user" [class.inactive]="!u.isActive">
            <div class="main">
              <div class="who">
                <strong>{{ u.displayName }}</strong>
                <span class="email">{{ u.email }}</span>
              </div>
              <div class="meta">
                <span class="badge role">{{ label(u.role) }}</span>
                <span class="badge" [class.on]="u.isActive" [class.off]="!u.isActive">
                  {{ u.isActive ? 'Ativo' : 'Inativo' }}
                </span>
                @if (isSelf(u)) { <span class="badge self">Você</span> }
                @if (!u.hasLocalCredential) { <span class="badge prov">Provedor corporativo</span> }
              </div>
              <div class="dates">
                <span>Criado: {{ u.createdAt | date: 'dd/MM/yy' }}</span>
                <span>Último acesso: {{ u.lastLoginAt ? (u.lastLoginAt | date: 'dd/MM/yy HH:mm') : 'Nunca' }}</span>
              </div>
            </div>

            <div class="row-actions">
              @if (u.isActive) {
                <button type="button" class="btn sm" (click)="startEdit(u)" [disabled]="busyId() === u.id">Editar</button>
                <button
                  type="button"
                  class="btn sm danger"
                  (click)="confirmingId.set(u.id)"
                  [disabled]="busyId() === u.id || isSelf(u)"
                  [title]="isSelf(u) ? 'Você não pode desativar o próprio acesso' : 'Desativar acesso'"
                >Desativar</button>
              } @else {
                <button type="button" class="btn sm" (click)="reactivate(u)" [disabled]="busyId() === u.id">Reativar</button>
              }
            </div>

            <!-- Edição inline -->
            @if (editingId() === u.id) {
              <form class="edit" [formGroup]="editForm" (ngSubmit)="saveEdit(u)">
                <label class="field">
                  <span>Nome</span>
                  <input type="text" formControlName="displayName" />
                </label>
                <label class="field">
                  <span>Papel</span>
                  <select formControlName="role">
                    @for (r of assignableRoles; track r) {
                      <option [value]="r">{{ labels[r] }}</option>
                    }
                  </select>
                  @if (isSelf(u)) { <small class="note">Você não pode alterar o próprio papel aqui.</small> }
                </label>
                <div class="actions">
                  <button type="submit" class="btn sm primary" [disabled]="busyId() === u.id">Salvar</button>
                  <button type="button" class="btn sm" (click)="cancelEdit()">Cancelar</button>
                </div>
              </form>
            }

            <!-- Confirmação de desativação -->
            @if (confirmingId() === u.id) {
              <div class="confirm" role="alertdialog">
                <span>Desativar o acesso de <strong>{{ u.displayName }}</strong>? As sessões dele serão encerradas.</span>
                <div class="actions">
                  <button type="button" class="btn sm danger" (click)="deactivate(u)" [disabled]="busyId() === u.id">
                    Confirmar
                  </button>
                  <button type="button" class="btn sm" (click)="confirmingId.set(null)">Cancelar</button>
                </div>
              </div>
            }

            @if (rowError() && rowErrorId() === u.id) {
              <p class="msg err row-msg" role="alert">{{ rowError() }}</p>
            }
          </li>
        }
      </ul>
    }
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
        margin: 0 0 0.75rem;
        font-size: 1rem;
        color: var(--text);
      }
      .grid {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
        gap: 0.75rem;
      }
      .form {
        display: flex;
        flex-direction: column;
        gap: 0.75rem;
      }
      .field {
        display: flex;
        flex-direction: column;
        gap: 0.3rem;
        font-size: 0.78rem;
        color: var(--muted);
      }
      .field input,
      .field select {
        padding: 0.5rem 0.6rem;
        border-radius: 8px;
        border: 1px solid var(--line);
        background: var(--void, rgba(5, 7, 15, 0.6));
        color: var(--text);
        font-size: 0.88rem;
        outline: none;
      }
      .field input:focus,
      .field select:focus {
        border-color: var(--cyan);
        box-shadow: 0 0 0 2px rgba(38, 224, 255, 0.2);
      }
      .note {
        font-size: 0.78rem;
        color: var(--muted);
        line-height: 1.5;
        margin: 0;
      }
      .note.standalone {
        margin: 0 0 1.1rem;
      }
      .toolbar {
        display: flex;
        flex-wrap: wrap;
        gap: 0.5rem;
        margin-bottom: 1rem;
      }
      .toolbar .search {
        flex: 1 1 220px;
      }
      .toolbar input,
      .toolbar select {
        padding: 0.5rem 0.6rem;
        border-radius: 8px;
        border: 1px solid var(--line);
        background: var(--void, rgba(5, 7, 15, 0.6));
        color: var(--text);
        font-size: 0.85rem;
        outline: none;
      }
      .state {
        color: var(--muted);
        font-size: 0.86rem;
        padding: 1rem 0;
      }
      .users {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 0.6rem;
      }
      .user {
        border: 1px solid var(--line);
        border-radius: 10px;
        padding: 0.8rem 0.95rem;
        background: var(--panel, rgba(11, 15, 26, 0.4));
        display: grid;
        grid-template-columns: 1fr auto;
        gap: 0.5rem 1rem;
        align-items: center;
      }
      .user.inactive {
        opacity: 0.7;
      }
      .who {
        display: flex;
        flex-direction: column;
        gap: 0.15rem;
      }
      .who strong {
        color: var(--text);
        font-size: 0.92rem;
      }
      .who .email {
        color: var(--muted);
        font-size: 0.8rem;
      }
      .meta {
        display: flex;
        flex-wrap: wrap;
        gap: 0.35rem;
        margin-top: 0.35rem;
      }
      .badge {
        font-family: var(--mono);
        font-size: 0.68rem;
        letter-spacing: 0.03em;
        padding: 0.12rem 0.45rem;
        border-radius: 999px;
        border: 1px solid var(--line);
        color: var(--muted);
      }
      .badge.role {
        color: var(--cyan);
        border-color: color-mix(in srgb, var(--cyan) 40%, var(--line));
      }
      .badge.on {
        color: var(--teal, #2ee6b6);
        border-color: color-mix(in srgb, var(--teal, #2ee6b6) 40%, var(--line));
      }
      .badge.off {
        color: var(--amber, #ffb020);
      }
      .badge.self {
        color: var(--violet, #8b5cff);
      }
      .dates {
        grid-column: 1 / -1;
        display: flex;
        flex-wrap: wrap;
        gap: 0.25rem 1rem;
        font-size: 0.72rem;
        color: var(--muted);
        margin-top: 0.35rem;
      }
      .row-actions {
        display: flex;
        gap: 0.4rem;
        align-self: start;
      }
      .edit,
      .confirm {
        grid-column: 1 / -1;
        border-top: 1px dashed var(--line);
        padding-top: 0.7rem;
        margin-top: 0.4rem;
        display: flex;
        flex-wrap: wrap;
        gap: 0.75rem;
        align-items: flex-end;
      }
      .confirm {
        align-items: center;
        font-size: 0.83rem;
        color: var(--text);
      }
      .actions {
        display: flex;
        gap: 0.5rem;
      }
      .btn {
        padding: 0.5rem 0.8rem;
        border-radius: 8px;
        border: 1px solid var(--line);
        background: rgba(122, 145, 190, 0.08);
        color: var(--text);
        font-family: var(--mono);
        font-size: 0.8rem;
        cursor: pointer;
      }
      .btn.sm {
        padding: 0.35rem 0.6rem;
        font-size: 0.75rem;
      }
      .btn:disabled {
        opacity: 0.5;
        cursor: default;
      }
      .btn.primary {
        border: none;
        color: #05070f;
        background: var(--neon-h, linear-gradient(90deg, #26e0ff, #8b5cff));
        font-weight: 600;
      }
      .btn.danger {
        color: var(--red, #ff6b8b);
        border-color: color-mix(in srgb, var(--red, #ff6b8b) 40%, var(--line));
      }
      .msg {
        font-size: 0.8rem;
        border-radius: 8px;
        padding: 0.55rem 0.7rem;
        margin: 0;
      }
      .msg.err {
        color: var(--red, #ff6b8b);
        background: rgba(255, 107, 139, 0.08);
      }
      .msg.ok {
        color: var(--text);
        background: rgba(38, 224, 255, 0.08);
      }
      .row-msg {
        grid-column: 1 / -1;
        margin-top: 0.4rem;
      }
    `,
  ],
})
export class SettingsUsersComponent implements OnInit {
  private readonly auth = inject(AuthService);
  private readonly api = inject(UsersService);
  private readonly fb = inject(FormBuilder);

  protected readonly assignableRoles = ASSIGNABLE_ROLES;
  protected readonly labels = ROLE_LABELS;
  protected readonly label = roleLabel;

  protected readonly canCreate = computed(() => this.auth.isPlatformAdmin() && this.auth.isTenantAdmin());
  protected readonly isTenantAdmin = this.auth.isTenantAdmin;

  private readonly _users = signal<TenantUser[]>([]);
  protected readonly users = this._users.asReadonly();
  protected readonly loading = signal(false);
  protected readonly loadError = signal<string | null>(null);

  protected readonly search = signal('');
  protected readonly roleFilter = signal('');
  protected readonly statusFilter = signal('');

  protected readonly editingId = signal<string | null>(null);
  protected readonly confirmingId = signal<string | null>(null);
  protected readonly busyId = signal<string | null>(null);
  protected readonly rowError = signal<string | null>(null);
  protected readonly rowErrorId = signal<string | null>(null);

  protected readonly onboarding = signal(false);
  protected readonly onboardError = signal<string | null>(null);
  protected readonly onboardOk = signal<string | null>(null);
  private readonly _authMode = signal<string>('Local');
  protected readonly authMode = this._authMode.asReadonly();

  protected readonly onboardForm = this.fb.nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    displayName: ['', [Validators.required, Validators.maxLength(200)]],
    role: ['Analyst' as TenantRoleName, [Validators.required]],
    initialPassword: [''],
  });

  protected readonly editForm = this.fb.nonNullable.group({
    displayName: ['', [Validators.required, Validators.maxLength(200)]],
    role: ['Analyst' as TenantRoleName, [Validators.required]],
  });

  protected readonly filtered = computed(() => {
    const term = this.search().trim().toLowerCase();
    const role = this.roleFilter();
    const status = this.statusFilter();
    return this._users().filter((u) => {
      if (term && !u.displayName.toLowerCase().includes(term) && !u.email.toLowerCase().includes(term)) return false;
      if (role && u.role !== role) return false;
      if (status === 'active' && !u.isActive) return false;
      if (status === 'inactive' && u.isActive) return false;
      return true;
    });
  });

  ngOnInit(): void {
    this.auth.federationConfig().subscribe({ next: (c) => this._authMode.set(c.mode || 'Local'), error: () => {} });
    this.reload();
  }

  isSelf(u: TenantUser): boolean {
    return u.id === this.auth.activeMembershipId();
  }

  reload(): void {
    this.loading.set(true);
    this.loadError.set(null);
    this.api.list().subscribe({
      next: (list) => {
        this._users.set(list);
        this.loading.set(false);
      },
      error: (e: Error) => {
        this._users.set([]);
        this.loadError.set(e.message);
        this.loading.set(false);
      },
    });
  }

  // ---- Onboarding ----
  onboard(): void {
    if (this.onboarding() || this.onboardForm.invalid) return;
    this.onboarding.set(true);
    this.onboardError.set(null);
    this.onboardOk.set(null);
    const v = this.onboardForm.getRawValue();
    const pwd = v.initialPassword?.trim() ? v.initialPassword : null;
    this.api
      .onboard({
        email: v.email.trim(),
        displayName: v.displayName.trim(),
        role: TENANT_ROLE_VALUE[v.role],
        initialPassword: this.authMode() === 'Federated' ? null : pwd,
      })
      .subscribe({
        next: (res) => {
          this.onboarding.set(false);
          // NUNCA deixa a senha no DOM: reseta o form.
          this.onboardForm.reset({ role: 'Analyst', email: '', displayName: '', initialPassword: '' });
          this.onboardOk.set(
            res.identityExisted
              ? `${res.user.displayName} já existia — acesso concedido a este ambiente (senha preservada).`
              : `${res.user.displayName} cadastrado e com acesso a este ambiente.`,
          );
          this.reload();
        },
        error: (e: Error) => {
          this.onboarding.set(false);
          // Erro definitivo: limpa a senha do DOM, mantém os demais campos para correção.
          this.onboardForm.patchValue({ initialPassword: '' });
          this.onboardError.set(e.message);
        },
      });
  }

  // ---- Edição ----
  startEdit(u: TenantUser): void {
    this.clearRowMsg();
    this.confirmingId.set(null);
    this.editingId.set(u.id);
    this.editForm.reset({ displayName: u.displayName, role: u.role });
    // Auto-rebaixamento é barrado no backend; na UI, travamos o papel na própria linha (via API reativa,
    // não [attr.disabled], para não emitir aviso de console). O nome segue editável.
    if (this.isSelf(u)) this.editForm.controls.role.disable();
    else this.editForm.controls.role.enable();
  }

  cancelEdit(): void {
    this.editingId.set(null);
  }

  saveEdit(u: TenantUser): void {
    if (this.editForm.invalid) return;
    this.busyId.set(u.id);
    this.clearRowMsg();
    const v = this.editForm.getRawValue();
    // Auto-rebaixamento é bloqueado no backend; para o próprio usuário o papel fica travado na UI, então
    // só enviamos o papel quando NÃO é a própria linha (evita um 409 previsível).
    const body = this.isSelf(u)
      ? { displayName: v.displayName.trim() }
      : { displayName: v.displayName.trim(), role: TENANT_ROLE_VALUE[v.role] };
    this.api.update(u.id, body).subscribe({
      next: (updated) => {
        this.replaceRow(updated);
        this.editingId.set(null);
        this.busyId.set(null);
      },
      error: (e: Error) => this.rowFail(u.id, e),
    });
  }

  // ---- Desativar / reativar ----
  deactivate(u: TenantUser): void {
    this.busyId.set(u.id);
    this.clearRowMsg();
    this.api.deactivate(u.id).subscribe({
      next: (updated) => {
        this.replaceRow(updated);
        this.confirmingId.set(null);
        this.busyId.set(null);
      },
      error: (e: Error) => this.rowFail(u.id, e),
    });
  }

  reactivate(u: TenantUser): void {
    this.busyId.set(u.id);
    this.clearRowMsg();
    this.api.reactivate(u.id).subscribe({
      next: (updated) => {
        this.replaceRow(updated);
        this.busyId.set(null);
      },
      error: (e: Error) => this.rowFail(u.id, e),
    });
  }

  // ---- Helpers ----
  private replaceRow(updated: TenantUser): void {
    this._users.update((list) => list.map((x) => (x.id === updated.id ? updated : x)));
  }

  private rowFail(id: string, e: Error): void {
    this.busyId.set(null);
    this.rowErrorId.set(id);
    this.rowError.set(e.message);   // mensagem de conflito/erro vinda do backend (409/403/…)
  }

  private clearRowMsg(): void {
    this.rowError.set(null);
    this.rowErrorId.set(null);
  }
}

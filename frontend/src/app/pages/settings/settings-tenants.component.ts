import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { AuthService } from '../../services/auth.service';
import { TenantAdminService } from '../../services/tenant-admin.service';
import {
  TenantAdmin,
  canReactivate,
  canSuspend,
  tenantStatusLabel,
  tenantStatusTone,
} from '../../models/tenant-admin.models';

/**
 * [AEGIS-MVP-ADMIN-LIFECYCLE-01] Aba AMBIENTES (PlatformAdmin): catálogo de tenants com id/nome/slug/estado/
 * datas, renomeação do nome de exibição (slug imutável), suspensão e reativação. A visibilidade e as
 * desabilitações são UX; o backend (policy de plataforma + regra) permanece a autoridade efetiva.
 */
@Component({
  selector: 'app-settings-tenants',
  standalone: true,
  imports: [ReactiveFormsModule, DatePipe],
  template: `
    <div class="panel intro">
      <h2>Ambientes (tenants)</h2>
      <p class="note">
        Administração dos ambientes da plataforma. <strong>Suspender</strong> preserva histórico e configurações,
        mas impede o uso operacional do ambiente. O <strong>identificador (slug)</strong> não pode ser alterado —
        ele participa da identidade operacional e de regras de autorização.
      </p>
    </div>

    <div class="toolbar">
      <input
        class="search"
        type="search"
        placeholder="Buscar por nome ou identificador…"
        [value]="search()"
        (input)="search.set($any($event.target).value)"
        aria-label="Buscar ambientes"
      />
      <select [value]="statusFilter()" (change)="statusFilter.set($any($event.target).value)" aria-label="Filtrar por estado">
        <option value="">Todos os estados</option>
        <option value="Active">Ativos</option>
        <option value="Onboarding">Em implantação</option>
        <option value="Suspended">Suspensos</option>
      </select>
      <button type="button" class="btn" (click)="reload()" [disabled]="loading()">
        {{ loading() ? 'Carregando…' : 'Atualizar' }}
      </button>
    </div>

    @if (loading()) {
      <p class="state">Carregando ambientes…</p>
    } @else if (loadError()) {
      <div class="state">
        <p class="msg err">{{ loadError() }}</p>
        <button type="button" class="btn" (click)="reload()">Tentar novamente</button>
      </div>
    } @else if (tenants().length === 0) {
      <p class="state">Nenhum ambiente cadastrado.</p>
    } @else if (filtered().length === 0) {
      <p class="state">Nenhum ambiente corresponde à busca/filtros.</p>
    } @else {
      <ul class="tenants">
        @for (t of filtered(); track t.id) {
          <li class="tenant" [class.suspended]="t.status === 'Suspended'">
            <div class="main">
              <div class="who">
                <strong>{{ t.name }}</strong>
                <span class="slug mono">{{ t.slug }}</span>
              </div>
              <div class="meta">
                <span class="badge" [class]="'tone-' + tone(t.status)">{{ statusLabel(t.status) }}</span>
                @if (isActiveTenant(t)) { <span class="badge self">Ambiente atual</span> }
              </div>
              <div class="dates">
                <span>Criado: {{ t.createdAt | date: 'dd/MM/yy' }}</span>
                <span>Atualizado: {{ t.updatedAt ? (t.updatedAt | date: 'dd/MM/yy HH:mm') : '—' }}</span>
              </div>
            </div>

            <div class="row-actions">
              <button type="button" class="btn sm" (click)="startEdit(t)" [disabled]="busyId() === t.id">Renomear</button>
              @if (canSuspend(t)) {
                <button type="button" class="btn sm danger" (click)="confirmingId.set(t.id)" [disabled]="busyId() === t.id">
                  Suspender
                </button>
              } @else if (canReactivate(t)) {
                <button type="button" class="btn sm" (click)="reactivate(t)" [disabled]="busyId() === t.id">Reativar</button>
              }
            </div>

            @if (editingId() === t.id) {
              <form class="edit" [formGroup]="editForm" (ngSubmit)="saveEdit(t)">
                <label class="field">
                  <span>Nome de exibição</span>
                  <input type="text" formControlName="name" />
                </label>
                <label class="field">
                  <span>Identificador (slug)</span>
                  <input type="text" [value]="t.slug" disabled />
                  <small class="note">O slug é imutável.</small>
                </label>
                <div class="actions">
                  <button type="submit" class="btn sm primary" [disabled]="busyId() === t.id || editForm.invalid">Salvar</button>
                  <button type="button" class="btn sm" (click)="cancelEdit()">Cancelar</button>
                </div>
              </form>
            }

            @if (confirmingId() === t.id) {
              <div class="confirm" role="alertdialog">
                <span>
                  Suspender <strong>{{ t.name }}</strong>? Preserva histórico e configurações, mas impede o uso
                  operacional; as sessões ativas do ambiente serão encerradas.
                </span>
                <div class="actions">
                  <button type="button" class="btn sm danger" (click)="suspend(t)" [disabled]="busyId() === t.id">
                    Confirmar suspensão
                  </button>
                  <button type="button" class="btn sm" (click)="confirmingId.set(null)">Cancelar</button>
                </div>
              </div>
            }

            @if (rowError() && rowErrorId() === t.id) {
              <p class="msg err row-msg" role="alert">{{ rowError() }}</p>
            }
            @if (rowOk() && rowOkId() === t.id) {
              <p class="msg ok row-msg" role="status">{{ rowOk() }}</p>
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
      h2 { margin: 0 0 0.6rem; font-size: 1rem; color: var(--text); }
      .note { margin: 0; font-size: 0.8rem; color: var(--muted); line-height: 1.5; }
      .toolbar { display: flex; flex-wrap: wrap; gap: 0.5rem; margin-bottom: 1rem; }
      .toolbar .search { flex: 1 1 240px; }
      .toolbar input, .toolbar select {
        padding: 0.5rem 0.6rem; border-radius: 8px; border: 1px solid var(--line);
        background: var(--void, rgba(5, 7, 15, 0.6)); color: var(--text); font-size: 0.85rem; outline: none;
      }
      .state { color: var(--muted); font-size: 0.86rem; padding: 1rem 0; }
      .tenants { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 0.6rem; }
      .tenant {
        border: 1px solid var(--line); border-radius: 10px; padding: 0.8rem 0.95rem;
        background: var(--panel, rgba(11, 15, 26, 0.4));
        display: grid; grid-template-columns: 1fr auto; gap: 0.5rem 1rem; align-items: center;
      }
      .tenant.suspended { opacity: 0.75; border-style: dashed; }
      .who { display: flex; flex-direction: column; gap: 0.15rem; }
      .who strong { color: var(--text); font-size: 0.92rem; }
      .who .slug { color: var(--muted); font-size: 0.8rem; }
      .mono { font-family: var(--mono); }
      .meta { display: flex; flex-wrap: wrap; gap: 0.35rem; margin-top: 0.35rem; }
      .badge {
        font-family: var(--mono); font-size: 0.68rem; letter-spacing: 0.03em; padding: 0.12rem 0.45rem;
        border-radius: 999px; border: 1px solid var(--line); color: var(--muted);
      }
      .badge.tone-ok { color: var(--teal, #2ee6b6); border-color: color-mix(in srgb, var(--teal, #2ee6b6) 40%, var(--line)); }
      .badge.tone-warn { color: var(--amber, #ffb020); border-color: color-mix(in srgb, var(--amber, #ffb020) 40%, var(--line)); }
      .badge.tone-idle { color: var(--muted); }
      .badge.self { color: var(--violet, #8b5cff); }
      .dates {
        grid-column: 1 / -1; display: flex; flex-wrap: wrap; gap: 0.25rem 1rem;
        font-size: 0.72rem; color: var(--muted); margin-top: 0.35rem;
      }
      .row-actions { display: flex; gap: 0.4rem; align-self: start; }
      .edit, .confirm {
        grid-column: 1 / -1; border-top: 1px dashed var(--line); padding-top: 0.7rem; margin-top: 0.4rem;
        display: flex; flex-wrap: wrap; gap: 0.75rem; align-items: flex-end;
      }
      .confirm { align-items: center; font-size: 0.83rem; color: var(--text); line-height: 1.4; }
      .field { display: flex; flex-direction: column; gap: 0.3rem; font-size: 0.78rem; color: var(--muted); }
      .field input {
        padding: 0.5rem 0.6rem; border-radius: 8px; border: 1px solid var(--line);
        background: var(--void, rgba(5, 7, 15, 0.6)); color: var(--text); font-size: 0.88rem; outline: none;
      }
      .field input:disabled { opacity: 0.6; }
      .actions { display: flex; gap: 0.5rem; }
      .btn {
        padding: 0.5rem 0.8rem; border-radius: 8px; border: 1px solid var(--line);
        background: rgba(122, 145, 190, 0.08); color: var(--text); font-family: var(--mono);
        font-size: 0.8rem; cursor: pointer;
      }
      .btn.sm { padding: 0.35rem 0.6rem; font-size: 0.75rem; }
      .btn:disabled { opacity: 0.5; cursor: default; }
      .btn.primary { border: none; color: #05070f; background: var(--neon-h, linear-gradient(90deg, #26e0ff, #8b5cff)); font-weight: 600; }
      .btn.danger { color: var(--red, #ff6b8b); border-color: color-mix(in srgb, var(--red, #ff6b8b) 40%, var(--line)); }
      .msg { font-size: 0.8rem; border-radius: 8px; padding: 0.55rem 0.7rem; margin: 0; }
      .msg.err { color: var(--red, #ff6b8b); background: rgba(255, 107, 139, 0.08); }
      .msg.ok { color: var(--text); background: rgba(38, 224, 255, 0.08); }
      .row-msg { grid-column: 1 / -1; margin-top: 0.4rem; }
    `,
  ],
})
export class SettingsTenantsComponent implements OnInit {
  private readonly api = inject(TenantAdminService);
  private readonly auth = inject(AuthService);
  private readonly fb = inject(FormBuilder);

  protected readonly statusLabel = tenantStatusLabel;
  protected readonly tone = tenantStatusTone;
  protected readonly canSuspend = canSuspend;
  protected readonly canReactivate = canReactivate;

  private readonly _tenants = signal<TenantAdmin[]>([]);
  protected readonly tenants = this._tenants.asReadonly();
  protected readonly loading = signal(false);
  protected readonly loadError = signal<string | null>(null);

  protected readonly search = signal('');
  protected readonly statusFilter = signal('');

  protected readonly editingId = signal<string | null>(null);
  protected readonly confirmingId = signal<string | null>(null);
  protected readonly busyId = signal<string | null>(null);
  protected readonly rowError = signal<string | null>(null);
  protected readonly rowErrorId = signal<string | null>(null);
  protected readonly rowOk = signal<string | null>(null);
  protected readonly rowOkId = signal<string | null>(null);

  protected readonly editForm = this.fb.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(200)]],
  });

  protected readonly filtered = computed(() => {
    const term = this.search().trim().toLowerCase();
    const status = this.statusFilter();
    return this._tenants().filter((t) => {
      if (term && !t.name.toLowerCase().includes(term) && !t.slug.toLowerCase().includes(term)) return false;
      if (status && t.status !== status) return false;
      return true;
    });
  });

  ngOnInit(): void {
    this.reload();
  }

  isActiveTenant(t: TenantAdmin): boolean {
    return t.id === this.auth.activeTenantId();
  }

  reload(): void {
    this.loading.set(true);
    this.loadError.set(null);
    this.api.listTenants().subscribe({
      next: (list) => {
        this._tenants.set(list);
        this.loading.set(false);
      },
      error: (e: Error) => {
        this._tenants.set([]);
        this.loadError.set(e.message);
        this.loading.set(false);
      },
    });
  }

  startEdit(t: TenantAdmin): void {
    this.clearRowMsg();
    this.confirmingId.set(null);
    this.editingId.set(t.id);
    this.editForm.reset({ name: t.name });
  }

  cancelEdit(): void {
    this.editingId.set(null);
  }

  saveEdit(t: TenantAdmin): void {
    if (this.editForm.invalid || this.busyId() === t.id) return;
    this.busyId.set(t.id);
    this.clearRowMsg();
    this.api.renameTenant(t.id, this.editForm.getRawValue().name.trim()).subscribe({
      next: (updated) => {
        this.replaceRow(updated);
        this.editingId.set(null);
        this.busyId.set(null);
        this.rowOkId.set(t.id);
        this.rowOk.set('Nome atualizado (slug inalterado).');
      },
      error: (e: Error) => this.rowFail(t.id, e),
    });
  }

  suspend(t: TenantAdmin): void {
    if (this.busyId() === t.id) return;
    this.busyId.set(t.id);
    this.clearRowMsg();
    this.api.suspendTenant(t.id).subscribe({
      next: (updated) => {
        this.replaceRow(updated);
        this.confirmingId.set(null);
        this.busyId.set(null);
        this.rowOkId.set(t.id);
        this.rowOk.set('Ambiente suspenso. As sessões ativas foram encerradas.');
      },
      error: (e: Error) => {
        this.confirmingId.set(null);
        this.rowFail(t.id, e);
      },
    });
  }

  reactivate(t: TenantAdmin): void {
    if (this.busyId() === t.id) return;
    this.busyId.set(t.id);
    this.clearRowMsg();
    this.api.reactivateTenant(t.id).subscribe({
      next: (updated) => {
        this.replaceRow(updated);
        this.busyId.set(null);
        this.rowOkId.set(t.id);
        this.rowOk.set('Ambiente reativado.');
      },
      error: (e: Error) => this.rowFail(t.id, e),
    });
  }

  private replaceRow(updated: TenantAdmin): void {
    this._tenants.update((list) => list.map((x) => (x.id === updated.id ? updated : x)));
  }

  private rowFail(id: string, e: Error): void {
    this.busyId.set(null);
    this.rowErrorId.set(id);
    this.rowError.set(e.message);
  }

  private clearRowMsg(): void {
    this.rowError.set(null);
    this.rowErrorId.set(null);
    this.rowOk.set(null);
    this.rowOkId.set(null);
  }
}

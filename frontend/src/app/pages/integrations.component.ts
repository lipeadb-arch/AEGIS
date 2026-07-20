import { Component, computed, effect, inject, signal } from '@angular/core';
import { FormBuilder, FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { ConnectorService } from '../services/connector.service';
import {
  ConnectorConfig,
  PROVIDERS,
  ProviderSpec,
  providerByKey,
  statusLabel,
  statusTone,
} from '../models/connector.models';

type SaveState = 'idle' | 'saving' | 'done' | 'error';

/**
 * Central de Integrações — onde o analista conecta o Aegis aos ambientes reais do cliente.
 *
 * Formulário REATIVO com credenciais DINÂMICAS: o catálogo (`PROVIDERS`) diz quais campos cada
 * provedor exige, e o grupo `credentials` é reconstruído a cada troca. Um textarea genérico de JSON
 * seria mais simples de codificar e péssimo de operar — quem configura um Sentinel às 3h da manhã
 * precisa de rótulos, não de sintaxe.
 *
 * ⚠️ Nenhum TenantId trafega: o backend o resolve do JWT (§20/§22). E o segredo é escrita-apenas —
 * a listagem devolve só `hasCredentials`, então um secret configurado NUNCA volta para a tela.
 */
@Component({
  selector: 'app-integrations',
  standalone: true,
  imports: [ReactiveFormsModule],
  template: `
    <section class="page">
      <header class="page-head">
        <div>
          <h1>Integrações</h1>
          <p class="sub">
            Conecte o Aegis aos ambientes do cliente. As credenciais são cifradas no servidor e nunca
            retornam para esta tela.
          </p>
        </div>
        <button type="button" class="ghost" (click)="reload()" [disabled]="loading()">
          {{ loading() ? 'Carregando…' : 'Atualizar' }}
        </button>
      </header>

      <!-- ---------- Conectores já configurados ---------- -->
      <div class="panel">
        <h2 class="panel-title">Conectores configurados</h2>

        @if (loading()) {
          <p class="muted">Carregando integrações…</p>
        } @else if (loadError()) {
          <p class="err">{{ loadError() }}</p>
        } @else if (connectors().length === 0) {
          <p class="muted">
            Nenhuma integração configurada ainda. Use o formulário abaixo para conectar o primeiro
            ambiente.
          </p>
        } @else {
          <ul class="conn-list">
            @for (c of connectors(); track c.id) {
              <li class="conn">
                <span class="tone" [class]="'tone-' + tone(c.lastStatus)" aria-hidden="true"></span>
                <div class="conn-main">
                  <strong>{{ c.displayName }}</strong>
                  <span class="meta">{{ c.provider }} · {{ c.capability }} · {{ c.authType }}</span>
                </div>
                <div class="conn-state">
                  <span class="badge" [class]="'tone-' + tone(c.lastStatus)">{{ label(c.lastStatus) }}</span>
                  @if (!c.hasCredentials) {
                    <span class="badge warn">Sem credencial</span>
                  }
                  <span class="meta">a cada {{ c.syncIntervalMinutes }} min</span>
                </div>
                <div class="conn-actions">
                  <button type="button" class="ghost sm" (click)="test(c)" [disabled]="busyId() === c.id">
                    {{ busyId() === c.id ? '…' : 'Testar' }}
                  </button>
                  <button type="button" class="ghost sm" (click)="sync(c)" [disabled]="busyId() === c.id">
                    Coletar
                  </button>
                </div>
                @if (actionMsg()[c.id]; as msg) {
                  <p class="conn-msg" [class.err]="msg.startsWith('⚠')">{{ msg }}</p>
                }
              </li>
            }
          </ul>
        }
      </div>

      <!-- ---------- Formulário ---------- -->
      <form class="panel" [formGroup]="form" (ngSubmit)="submit()">
        <h2 class="panel-title">Nova integração</h2>
        <p class="muted small">
          Configurar o mesmo provedor duas vezes <strong>reconfigura</strong> a integração existente —
          não cria duplicata.
        </p>

        <div class="grid">
          <label class="field">
            <span>Provedor</span>
            <select formControlName="providerKey">
              <option value="">Selecione…</option>
              @for (p of providers; track p.key) {
                <option [value]="p.key">{{ p.label }}</option>
              }
            </select>
          </label>

          <label class="field">
            <span>Nome de exibição</span>
            <input type="text" formControlName="displayName" placeholder="Sentinel — Produção" />
            @if (showError('displayName')) {
              <em class="err">Informe um nome (2 a 200 caracteres).</em>
            }
          </label>

          <label class="field">
            <span>Intervalo de coleta (min)</span>
            <input type="number" formControlName="syncIntervalMinutes" min="5" max="10080" />
            @if (showError('syncIntervalMinutes')) {
              <em class="err">O servidor aplica um piso de 5 minutos.</em>
            }
          </label>
        </div>

        @if (spec(); as s) {
          <fieldset class="creds">
            <legend>Credenciais · {{ s.label }}</legend>
            <p class="muted small">
              Autenticação: <code>{{ s.authType }}</code> · Capacidade: <code>{{ s.capability }}</code>
            </p>

            <div class="grid" formGroupName="credentials">
              @for (f of s.fields; track f.key) {
                <label class="field">
                  <span>{{ f.label }}</span>
                  @if (f.secret) {
                    <input
                      [type]="revealed()[f.key] ? 'text' : 'password'"
                      [formControlName]="f.key"
                      autocomplete="new-password"
                      spellcheck="false"
                    />
                    <button type="button" class="reveal" (click)="toggleReveal(f.key)">
                      {{ revealed()[f.key] ? 'Ocultar' : 'Mostrar' }}
                    </button>
                  } @else {
                    <input type="text" [formControlName]="f.key" [placeholder]="f.placeholder ?? ''" />
                  }
                  @if (showCredError(f.key)) {
                    <em class="err">Campo obrigatório.</em>
                  }
                </label>
              }
            </div>
          </fieldset>
        } @else {
          <p class="muted">Selecione um provedor para ver os campos de credencial.</p>
        }

        <footer class="form-foot">
          <button type="submit" class="primary" [disabled]="saveState() === 'saving' || !spec()">
            {{ saveState() === 'saving' ? 'Salvando…' : 'Salvar integração' }}
          </button>

          @if (saveState() === 'done') {
            <span class="ok">✓ Integração salva. As credenciais foram cifradas no servidor.</span>
          }
          @if (saveState() === 'error') {
            <span class="err">⚠ {{ saveError() }}</span>
          }
        </footer>
      </form>
    </section>
  `,
  styles: [
    `
      .page {
        padding: 1.25rem 1.5rem 3rem;
        display: flex;
        flex-direction: column;
        gap: 1.25rem;
      }
      .page-head {
        display: flex;
        justify-content: space-between;
        align-items: flex-start;
        gap: 1rem;
      }
      h1 {
        margin: 0;
        font-size: 1.35rem;
        letter-spacing: 0.02em;
      }
      .sub {
        margin: 0.35rem 0 0;
        max-width: 62ch;
        opacity: 0.7;
        font-size: 0.85rem;
      }
      .panel {
        background: color-mix(in srgb, var(--hud-cyan, #26e0ff) 4%, transparent);
        border: 1px solid color-mix(in srgb, var(--hud-cyan, #26e0ff) 22%, transparent);
        border-radius: 8px;
        padding: 1rem 1.15rem;
      }
      .panel-title {
        margin: 0 0 0.75rem;
        font-size: 0.75rem;
        letter-spacing: 0.14em;
        text-transform: uppercase;
        opacity: 0.75;
      }
      .muted {
        opacity: 0.65;
        font-size: 0.85rem;
      }
      .small {
        font-size: 0.78rem;
      }
      .err {
        color: #ff6b8a;
        font-size: 0.78rem;
        font-style: normal;
      }
      .ok {
        color: var(--hud-cyan, #26e0ff);
        font-size: 0.82rem;
      }

      /* ---- lista ---- */
      .conn-list {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 0.5rem;
      }
      .conn {
        display: grid;
        grid-template-columns: 4px 1fr auto auto;
        align-items: center;
        gap: 0.85rem;
        padding: 0.6rem 0.75rem;
        border: 1px solid color-mix(in srgb, var(--hud-cyan, #26e0ff) 16%, transparent);
        border-radius: 6px;
      }
      .conn-msg {
        grid-column: 2 / -1;
        margin: 0.35rem 0 0;
        font-size: 0.78rem;
        opacity: 0.85;
      }
      .tone {
        align-self: stretch;
        border-radius: 2px;
        background: #64748b;
      }
      .tone-ok {
        background: var(--hud-cyan, #26e0ff);
      }
      .tone-warn {
        background: #f5a524;
      }
      .tone-bad {
        background: #ff3d6a;
      }
      .conn-main {
        display: flex;
        flex-direction: column;
        min-width: 0;
      }
      .meta {
        font-size: 0.72rem;
        opacity: 0.6;
      }
      .conn-state {
        display: flex;
        align-items: center;
        gap: 0.5rem;
      }
      .badge {
        font-size: 0.68rem;
        padding: 0.15rem 0.45rem;
        border-radius: 3px;
        border: 1px solid currentColor;
        text-transform: uppercase;
        letter-spacing: 0.06em;
      }
      .badge.tone-ok {
        color: var(--hud-cyan, #26e0ff);
        background: transparent;
      }
      .badge.tone-warn,
      .badge.warn {
        color: #f5a524;
        background: transparent;
      }
      .badge.tone-bad {
        color: #ff3d6a;
        background: transparent;
      }
      .conn-actions {
        display: flex;
        gap: 0.4rem;
      }

      /* ---- formulário ---- */
      .grid {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(15rem, 1fr));
        gap: 0.85rem;
      }
      .field {
        display: flex;
        flex-direction: column;
        gap: 0.3rem;
        position: relative;
      }
      .field > span {
        font-size: 0.72rem;
        letter-spacing: 0.08em;
        text-transform: uppercase;
        opacity: 0.7;
      }
      input,
      select {
        background: rgba(4, 8, 18, 0.6);
        border: 1px solid color-mix(in srgb, var(--hud-cyan, #26e0ff) 26%, transparent);
        border-radius: 5px;
        padding: 0.45rem 0.6rem;
        color: inherit;
        font: inherit;
        font-size: 0.85rem;
      }
      input:focus,
      select:focus {
        outline: 1px solid var(--hud-cyan, #26e0ff);
      }
      .reveal {
        position: absolute;
        right: 0.4rem;
        bottom: 0.4rem;
        background: transparent;
        border: 0;
        color: var(--hud-cyan, #26e0ff);
        font-size: 0.68rem;
        cursor: pointer;
        opacity: 0.8;
      }
      .creds {
        margin: 1rem 0 0;
        border: 1px solid color-mix(in srgb, var(--hud-cyan, #26e0ff) 18%, transparent);
        border-radius: 6px;
        padding: 0.85rem;
      }
      legend {
        font-size: 0.72rem;
        letter-spacing: 0.1em;
        text-transform: uppercase;
        opacity: 0.75;
        padding: 0 0.4rem;
      }
      .form-foot {
        display: flex;
        align-items: center;
        gap: 0.85rem;
        margin-top: 1rem;
        flex-wrap: wrap;
      }
      button.primary {
        background: color-mix(in srgb, var(--hud-cyan, #26e0ff) 18%, transparent);
        border: 1px solid var(--hud-cyan, #26e0ff);
        color: inherit;
        border-radius: 5px;
        padding: 0.5rem 1.1rem;
        font: inherit;
        font-size: 0.85rem;
        cursor: pointer;
      }
      button.ghost {
        background: transparent;
        border: 1px solid color-mix(in srgb, var(--hud-cyan, #26e0ff) 30%, transparent);
        color: inherit;
        border-radius: 5px;
        padding: 0.4rem 0.8rem;
        font: inherit;
        font-size: 0.8rem;
        cursor: pointer;
      }
      button.sm {
        padding: 0.25rem 0.6rem;
        font-size: 0.72rem;
      }
      button:disabled {
        opacity: 0.55;
        cursor: not-allowed;
      }
      code {
        font-size: 0.78rem;
        opacity: 0.85;
      }
    `,
  ],
})
export class IntegrationsComponent {
  private readonly api = inject(ConnectorService);
  private readonly fb = inject(FormBuilder);

  protected readonly providers = PROVIDERS;
  protected readonly label = statusLabel;
  protected readonly tone = statusTone;

  // ---- Estado da lista ----
  protected readonly connectors = signal<ConnectorConfig[]>([]);
  protected readonly loading = signal(false);
  protected readonly loadError = signal<string | null>(null);

  // ---- Estado do formulário ----
  protected readonly saveState = signal<SaveState>('idle');
  protected readonly saveError = signal<string | null>(null);
  protected readonly revealed = signal<Record<string, boolean>>({});

  // ---- Estado das ações por conector ----
  protected readonly busyId = signal<string | null>(null);
  protected readonly actionMsg = signal<Record<string, string>>({});

  protected readonly form: FormGroup = this.fb.group({
    providerKey: ['', Validators.required],
    displayName: ['', [Validators.required, Validators.minLength(2), Validators.maxLength(200)]],
    // Piso de 5 espelha o MinimumSyncIntervalMinutes do servidor (§20.3) — validar aqui só evita a
    // ida ao servidor; a regra continua sendo imposta lá, onde não pode ser burlada.
    syncIntervalMinutes: [360, [Validators.required, Validators.min(5), Validators.max(10080)]],
    credentials: this.fb.group({}),
  });

  /** Provedor selecionado, derivado do controle (não duplicado em signal próprio). */
  protected readonly spec = signal<ProviderSpec | undefined>(undefined);

  constructor() {
    this.reload();

    // Troca de provedor ⇒ reconstrói o grupo de credenciais. Um `effect` sobre o valueChanges manteria
    // duas fontes de verdade; aqui o formulário é a única, e o signal apenas espelha para o template.
    this.form.get('providerKey')!.valueChanges.subscribe((key: string) => {
      const next = providerByKey(key);
      this.spec.set(next);
      this.rebuildCredentials(next);
      this.revealed.set({});
      this.saveState.set('idle');
    });
  }

  /** Reconstrói o subgrupo de credenciais para o provedor escolhido. */
  private rebuildCredentials(next: ProviderSpec | undefined): void {
    const group = this.fb.group({});
    for (const field of next?.fields ?? []) {
      group.addControl(field.key, new FormControl('', Validators.required));
    }
    this.form.setControl('credentials', group);
  }

  protected reload(): void {
    this.loading.set(true);
    this.loadError.set(null);
    this.api.list().subscribe({
      next: (list) => {
        this.connectors.set(list);
        this.loading.set(false);
      },
      error: (err: Error) => {
        this.loadError.set(err.message);
        this.loading.set(false);
      },
    });
  }

  protected toggleReveal(key: string): void {
    this.revealed.update((r) => ({ ...r, [key]: !r[key] }));
  }

  protected showError(control: string): boolean {
    const c = this.form.get(control);
    return !!c && c.invalid && (c.touched || c.dirty);
  }

  protected showCredError(key: string): boolean {
    const c = this.form.get(['credentials', key]);
    return !!c && c.invalid && (c.touched || c.dirty);
  }

  protected submit(): void {
    const spec = this.spec();
    if (!spec) return;

    if (this.form.invalid) {
      // Sem isto, um campo nunca tocado permanece "pristine" e a mensagem de erro não aparece —
      // o usuário só veria o botão não fazer nada.
      this.form.markAllAsTouched();
      return;
    }

    const raw = this.form.getRawValue();
    this.saveState.set('saving');
    this.saveError.set(null);

    this.api
      .save({
        provider: spec.value,
        capability: spec.capabilityValue,
        authType: spec.authTypeValue,
        displayName: (raw.displayName as string).trim(),
        syncIntervalMinutes: Number(raw.syncIntervalMinutes),
        // O backend NÃO interpreta este JSON — apenas cifra e guarda. Quem o lê é o conector.
        settings: JSON.stringify(raw.credentials ?? {}),
      })
      .subscribe({
        next: () => {
          this.saveState.set('done');
          // Limpa APENAS as credenciais: o segredo não pode ficar no DOM depois de salvo, e a tela
          // nunca o recebe de volta do servidor para repopular.
          this.rebuildCredentials(spec);
          this.revealed.set({});
          this.reload();
        },
        error: (err: Error) => {
          this.saveState.set('error');
          this.saveError.set(err.message);
        },
      });
  }

  protected test(c: ConnectorConfig): void {
    this.busyId.set(c.id);
    this.api.test(c.id).subscribe({
      next: (h) => {
        this.setMsg(c.id, `${statusLabel(h.status)}${h.message ? ' — ' + h.message : ''}`);
        this.busyId.set(null);
        this.reload();
      },
      error: (err: Error) => {
        this.setMsg(c.id, `⚠ ${err.message}`);
        this.busyId.set(null);
      },
    });
  }

  protected sync(c: ConnectorConfig): void {
    this.busyId.set(c.id);
    this.api.sync(c.id).subscribe({
      next: (r) => {
        this.setMsg(c.id, `Coleta concluída: ${r.signalsCollected} sinal(is).`);
        this.busyId.set(null);
        this.reload();
      },
      error: (err: Error) => {
        this.setMsg(c.id, `⚠ ${err.message}`);
        this.busyId.set(null);
      },
    });
  }

  private setMsg(id: string, msg: string): void {
    this.actionMsg.update((m) => ({ ...m, [id]: msg }));
  }
}

import { Component, computed, effect, inject, signal } from '@angular/core';
import { FormBuilder, FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { environment } from '../../environments/environment';
import { ConnectorService } from '../services/connector.service';
import {
  buildMicrosoftHubRequest,
  buildSiemSyncMessage,
  ConnectorConfig,
  GENERIC_PROVIDERS,
  isGenericPush,
  isGuid,
  isKnightConnector,
  isMicrosoftFamily,
  MICROSOFT_HUB_SERVICES,
  MicrosoftServiceKey,
  MicrosoftServiceSelection,
  ProviderSpec,
  providerByKey,
  statusLabel,
  statusTone,
} from '../models/connector.models';

type SaveState = 'idle' | 'saving' | 'done' | 'error';

/** Ordem canônica das chaves de serviço Microsoft no formulário do hub. */
const MICROSOFT_SERVICE_KEYS: MicrosoftServiceKey[] = [
  'SecureScore',
  'IdentityPosture',
  'VulnerabilityScanner',
  'Sentinel',
];

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
      <!-- Sem <h1> redundante: a aba "Integrações" do shell de Configurações já rotula esta seção. -->
      <header class="page-head">
        <p class="sub">
          Conecte o Aegis aos ambientes do cliente. As credenciais são cifradas no servidor e nunca
          retornam para esta tela.
        </p>
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
          @for (grp of connectorGroups(); track grp.title) {
            @if (grp.title) {
              <h3 class="group-title">{{ grp.title }}</h3>
            }
            <ul class="conn-list">
            @for (c of grp.items; track c.id) {
              <li class="conn">
                <span class="tone" [class]="'tone-' + tone(c.lastStatus)" aria-hidden="true"></span>
                <div class="conn-main">
                  <strong>{{ c.displayName }}</strong>
                  <span class="meta">{{ c.provider }} · {{ c.capability }} · {{ c.authType }}</span>
                  @if (push(c)) {
                    <span class="meta ok-note">Push genérico disponível</span>
                  }
                </div>
                <div class="conn-state">
                  <span class="badge" [class]="'tone-' + tone(c.lastStatus)">{{ label(c.lastStatus) }}</span>
                  @if (push(c)) {
                    @if (!c.hasIngestionKey) {
                      <span class="badge warn">Sem chave</span>
                    }
                  } @else if (!c.hasCredentials) {
                    <span class="badge warn">Sem credencial</span>
                  }
                  <span class="meta">último: {{ lastSync(c) }}</span>
                </div>
                <div class="conn-actions">
                  <!-- IdentityPosture não usa o pipeline genérico: Testar/Coletar retornariam 501. A ação real
                       do KNIGHT (coleta do Entra) vive em /identity. -->
                  @if (knight(c)) {
                    <button type="button" class="ghost sm" (click)="openKnight()">Abrir AEGIS KNIGHT</button>
                  } @else {
                    <button type="button" class="ghost sm" (click)="test(c)" [disabled]="busyId() === c.id">
                      {{ busyId() === c.id ? '…' : 'Testar conexão' }}
                    </button>
                    <!-- [AEGIS-AUD-020] Push não tem coleta pull: nada de "Sincronizar" para conector genérico. -->
                    @if (!push(c)) {
                      <button type="button" class="ghost sm" (click)="sync(c)" [disabled]="busyId() === c.id">
                        Sincronizar agora
                      </button>
                    }
                  }
                </div>
                @if (push(c)) {
                  <p class="conn-endpoint" title="Endpoint de ingestão (envie eventos com POST + header X-Ingestion-Key)">
                    <span class="ep-label">Ingestão</span>
                    <code>POST {{ ingestionEndpoint(c.id) }}</code>
                  </p>
                }
                @if (actionMsg()[c.id]; as msg) {
                  <p class="conn-msg" [class.err]="msg.startsWith('⚠')">{{ msg }}</p>
                }
              </li>
            }
            </ul>
          }
        }
      </div>

      <!-- ---------- Conexão Microsoft unificada ---------- -->
      <form class="panel" [formGroup]="hubForm" (ngSubmit)="submitMicrosoftHub()">
        <h2 class="panel-title">Conexão Microsoft</h2>
        <p class="muted small">
          Informe a credencial <strong>uma única vez</strong> — ela é aplicada aos serviços Microsoft que você
          marcar. Cada serviço permanece <strong>independente</strong> (estado, teste e sincronização próprios).
          Organizações com separação rígida de privilégios podem usar registros de aplicativo distintos e
          configurar cada serviço pelo formulário genérico abaixo.
        </p>

        <fieldset class="creds">
          <legend>Credencial comum · OAuth client credentials</legend>
          <div class="grid">
            <label class="field">
              <span>Directory (tenant) ID</span>
              <input type="text" formControlName="tenantId" placeholder="00000000-0000-0000-0000-000000000000" />
              @if (showHubError('tenantId')) {
                <em class="err">Campo obrigatório.</em>
              }
            </label>
            <label class="field">
              <span>Application (client) ID</span>
              <input type="text" formControlName="clientId" placeholder="00000000-0000-0000-0000-000000000000" />
              @if (showHubError('clientId')) {
                <em class="err">Campo obrigatório.</em>
              }
            </label>
            <label class="field">
              <span>Client secret</span>
              <input
                [type]="hubRevealSecret() ? 'text' : 'password'"
                formControlName="clientSecret"
                autocomplete="new-password"
                spellcheck="false"
              />
              <button type="button" class="reveal" (click)="toggleHubReveal()">
                {{ hubRevealSecret() ? 'Ocultar' : 'Mostrar' }}
              </button>
              @if (showHubError('clientSecret')) {
                <em class="err">Campo obrigatório.</em>
              }
            </label>
            <label class="field">
              <span>Intervalo de coleta (min)</span>
              <input type="number" formControlName="syncIntervalMinutes" min="5" max="10080" />
            </label>
          </div>
        </fieldset>

        <fieldset class="creds" formGroupName="services">
          <legend>Serviços Microsoft (marque os que deseja conectar)</legend>
          <div class="svc-grid">
            @for (s of microsoftServices; track s.key) {
              <label class="svc">
                <input type="checkbox" [formControlName]="s.key" />
                <span class="svc-main">
                  <strong>{{ s.label }}</strong>
                  <span class="muted small">{{ s.description }}</span>
                </span>
              </label>
            }
          </div>
        </fieldset>

        @if (sentinelSelected()) {
          <fieldset class="creds">
            <legend>Microsoft Sentinel</legend>
            <div class="grid">
              <label class="field">
                <span>Log Analytics Workspace ID</span>
                <input type="text" formControlName="workspaceId" placeholder="00000000-0000-0000-0000-000000000000" />
                @if (showWorkspaceError()) {
                  <em class="err">Informe um GUID válido (ex.: 00000000-0000-0000-0000-000000000000).</em>
                }
                <em class="hint">
                  Exclusivo do Sentinel — não afeta os demais serviços. O service principal precisa de Azure RBAC
                  de leitura no workspace (Log Analytics Reader ou permissão mínima equivalente).
                </em>
              </label>
            </div>
          </fieldset>
        }

        <footer class="form-foot">
          <button type="submit" class="primary" [disabled]="hubState() === 'saving'">
            {{ hubState() === 'saving' ? 'Salvando…' : 'Salvar conexão Microsoft' }}
          </button>
          @if (hubState() === 'done') {
            <span class="ok">✓ Conexão Microsoft salva. As credenciais foram cifradas no servidor.</span>
          }
          @if (hubState() === 'error') {
            <span class="err">⚠ {{ hubError() }}</span>
          }
        </footer>
      </form>

      <!-- ---------- Formulário genérico (demais provedores) ---------- -->
      <form class="panel" [formGroup]="form" (ngSubmit)="submit()">
        <h2 class="panel-title">Outra integração</h2>
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

            @if (s.push) {
              <p class="note ok-note">
                Este conector <strong>recebe eventos por push autenticado</strong>. Configure uma chave de
                ingestão (mín. 24 caracteres); após salvar, o endpoint aparece na lista. Salvar de novo
                <strong>rotaciona</strong> a chave.
              </p>
            }
            @if (s.adapterNote) {
              <p class="note warn-note">⚠ {{ s.adapterNote }}</p>
            }
            <!-- infoNote/appPermissions valem para QUALQUER coletor real (KNIGHT e Secure Score), não só o KNIGHT. -->
            @if (s.infoNote) {
              <p class="note ok-note">{{ s.infoNote }}</p>
            }
            @if (s.appPermissions?.length) {
              <div class="perms">
                <span class="perms-label">Permissões/escopos (somente leitura) necessários:</span>
                <ul>
                  @for (perm of s.appPermissions; track perm) {
                    <li><code>{{ perm }}</code></li>
                  }
                </ul>
              </div>
            }

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
                  } @else if (f.options?.length) {
                    <!-- Seleção CONTROLADA (ex.: localidade do Google SecOps): sem texto livre — nada de URL/host arbitrário. -->
                    <select [formControlName]="f.key">
                      <option value="">Selecione…</option>
                      @for (o of f.options; track o.value) {
                        <option [value]="o.value">{{ o.label }}</option>
                      }
                    </select>
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
      /* Sem padding externo: o shell de Configurações já provê o espaçamento da página (evita margem dupla). */
      .page {
        padding: 0 0 1rem;
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
      .group-title {
        margin: 0.85rem 0 0.4rem;
        font-size: 0.72rem;
        letter-spacing: 0.12em;
        text-transform: uppercase;
        opacity: 0.85;
        color: var(--hud-cyan, #26e0ff);
      }
      .group-title:first-child {
        margin-top: 0;
      }
      .conn-list {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 0.5rem;
      }
      .conn-list + .group-title {
        margin-top: 1rem;
      }

      /* ---- seleção de serviços do hub Microsoft ---- */
      .svc-grid {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(18rem, 1fr));
        gap: 0.6rem;
      }
      .svc {
        display: flex;
        gap: 0.55rem;
        align-items: flex-start;
        padding: 0.55rem 0.65rem;
        border: 1px solid color-mix(in srgb, var(--hud-cyan, #26e0ff) 16%, transparent);
        border-radius: 6px;
        cursor: pointer;
      }
      .svc input[type='checkbox'] {
        margin-top: 0.2rem;
        width: auto;
        accent-color: var(--hud-cyan, #26e0ff);
      }
      .svc-main {
        display: flex;
        flex-direction: column;
        gap: 0.15rem;
        min-width: 0;
      }
      .hint {
        font-size: 0.72rem;
        opacity: 0.65;
        font-style: normal;
        line-height: 1.4;
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
      .ok-note {
        color: var(--hud-cyan, #26e0ff);
      }
      .note {
        margin: 0.6rem 0 0;
        font-size: 0.8rem;
        line-height: 1.4;
      }
      .warn-note {
        color: #f5a524;
      }
      .perms {
        margin: 0.5rem 0 0;
        font-size: 0.78rem;
      }
      .perms-label {
        display: block;
        opacity: 0.7;
        margin-bottom: 0.25rem;
      }
      .perms ul {
        margin: 0;
        padding-left: 1.1rem;
        display: flex;
        flex-wrap: wrap;
        gap: 0.15rem 1rem;
      }
      .perms li {
        list-style: none;
      }
      .perms li::before {
        content: '· ';
        opacity: 0.5;
      }
      .conn-endpoint {
        grid-column: 2 / -1;
        margin: 0.4rem 0 0;
        display: flex;
        align-items: center;
        gap: 0.5rem;
        min-width: 0;
      }
      .conn-endpoint code {
        overflow-x: auto;
        white-space: nowrap;
        opacity: 0.9;
      }
      .ep-label {
        font-size: 0.62rem;
        text-transform: uppercase;
        letter-spacing: 0.08em;
        opacity: 0.6;
        flex: none;
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
  private readonly router = inject(Router);

  // Formulário genérico: a família Microsoft sai daqui (vai para a conexão unificada).
  protected readonly providers = GENERIC_PROVIDERS;
  protected readonly microsoftServices = MICROSOFT_HUB_SERVICES;
  protected readonly label = statusLabel;
  protected readonly tone = statusTone;
  protected readonly push = isGenericPush;
  protected readonly knight = isKnightConnector;

  /**
   * Conectores agrupados: a família Microsoft sob um cabeçalho “Microsoft” (serviços filhos), o resto abaixo.
   * Os estados e botões permanecem INDIVIDUAIS — o agrupamento é só de apresentação.
   */
  protected readonly connectorGroups = computed(() => {
    const all = this.connectors();
    const ms = all.filter(isMicrosoftFamily);
    const rest = all.filter((c) => !isMicrosoftFamily(c));
    const groups: { title: string | null; items: ConnectorConfig[] }[] = [];
    if (ms.length) groups.push({ title: 'Microsoft', items: ms });
    if (rest.length) groups.push({ title: ms.length ? 'Outras integrações' : null, items: rest });
    return groups;
  });

  /** Abre a tela do AEGIS KNIGHT (postura de identidade); a coleta real do Entra é disparada de lá. */
  protected openKnight(): void {
    this.router.navigate(['/identity']);
  }

  /** Endpoint de ingestão do conector (só o connectorId; a chave viaja no header X-Ingestion-Key, nunca na URL). */
  protected ingestionEndpoint(connectorId: string): string {
    return `${environment.apiBase}/api/v1/ingestion/connectors/${connectorId}/events`;
  }

  /** Último recebimento/coleta em formato curto, ou "—" quando nunca houve. */
  protected lastSync(c: ConnectorConfig): string {
    if (!c.lastSyncAt) return '—';
    const d = new Date(c.lastSyncAt);
    return isNaN(d.getTime()) ? '—' : d.toLocaleString('pt-BR');
  }

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

  // ---- [AEGIS-MVP-MICROSOFT-HUB] Conexão Microsoft unificada ----
  protected readonly hubState = signal<SaveState>('idle');
  protected readonly hubError = signal<string | null>(null);
  protected readonly hubRevealSecret = signal(false);

  /** Uma credencial comum + a seleção de serviços. O workspaceId só é exigido/usado pelo Sentinel. */
  protected readonly hubForm: FormGroup = this.fb.group({
    tenantId: ['', Validators.required],
    clientId: ['', Validators.required],
    clientSecret: ['', Validators.required],
    syncIntervalMinutes: [360, [Validators.required, Validators.min(5), Validators.max(10080)]],
    workspaceId: [''],
    services: this.fb.group({
      SecureScore: [false],
      IdentityPosture: [false],
      VulnerabilityScanner: [false],
      Sentinel: [false],
    }),
  });

  /** True quando o Sentinel está marcado — só então o campo workspaceId aparece/é exigido. */
  protected sentinelSelected(): boolean {
    return !!this.hubForm.get(['services', 'Sentinel'])!.value;
  }

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

  protected showHubError(control: string): boolean {
    const c = this.hubForm.get(control);
    return !!c && c.invalid && (c.touched || c.dirty);
  }

  /** Erro do workspaceId: só quando o Sentinel está marcado e o valor está vazio ou não é GUID (após tocar). */
  protected showWorkspaceError(): boolean {
    if (!this.sentinelSelected()) return false;
    const c = this.hubForm.get('workspaceId')!;
    if (!c.touched && !c.dirty) return false;
    const v = ((c.value as string | null) ?? '').trim();
    return v.length === 0 || !isGuid(v);
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

  protected toggleHubReveal(): void {
    this.hubRevealSecret.update((v) => !v);
  }

  /**
   * [AEGIS-MVP-MICROSOFT-HUB] Salva a conexão Microsoft unificada: a credencial comum é aplicada+cifrada no
   * servidor a cada serviço marcado. A `buildMicrosoftHubRequest` garante que o workspaceId só vá ao Sentinel.
   */
  protected submitMicrosoftHub(): void {
    const raw = this.hubForm.getRawValue();
    const services = raw.services as Record<MicrosoftServiceKey, boolean>;
    const selectedKeys = MICROSOFT_SERVICE_KEYS.filter((k) => services[k]);

    // Ao menos um serviço + a credencial comum válida.
    if (this.hubForm.get('tenantId')!.invalid || this.hubForm.get('clientId')!.invalid ||
        this.hubForm.get('clientSecret')!.invalid || this.hubForm.get('syncIntervalMinutes')!.invalid ||
        selectedKeys.length === 0) {
      this.hubForm.markAllAsTouched();
      this.hubState.set('error');
      this.hubError.set('Informe a credencial comum e selecione ao menos um serviço Microsoft.');
      return;
    }

    // workspaceId é obrigatório e deve ser GUID SÓ quando o Sentinel está marcado. O backend é a autoridade
    // final; esta checagem só dá feedback imediato (não envia nada aos demais serviços).
    const workspaceId = (raw.workspaceId as string | null) ?? '';
    if (selectedKeys.includes('Sentinel')) {
      this.hubForm.get('workspaceId')!.markAsTouched();
      if (workspaceId.trim().length === 0) {
        this.hubState.set('error');
        this.hubError.set('O Microsoft Sentinel exige o Log Analytics Workspace ID.');
        return;
      }
      if (!isGuid(workspaceId)) {
        this.hubState.set('error');
        this.hubError.set('O Log Analytics Workspace ID deve ser um GUID válido (ex.: 00000000-0000-0000-0000-000000000000).');
        return;
      }
    }

    const selections: MicrosoftServiceSelection[] = selectedKeys.map((key) => ({
      key,
      syncIntervalMinutes: Number(raw.syncIntervalMinutes),
      // workspaceId só acompanha o Sentinel — a função pura reforça isso.
      workspaceId: key === 'Sentinel' ? workspaceId : null,
    }));

    const body = buildMicrosoftHubRequest(
      { tenantId: raw.tenantId as string, clientId: raw.clientId as string, clientSecret: raw.clientSecret as string },
      selections,
    );

    this.hubState.set('saving');
    this.hubError.set(null);
    this.api.saveMicrosoftHub(body).subscribe({
      next: () => {
        this.hubState.set('done');
        // Limpa APENAS o segredo do DOM (nunca volta do servidor); mantém tenant/client e a seleção.
        this.hubForm.get('clientSecret')!.reset('');
        this.hubRevealSecret.set(false);
        this.reload();
      },
      error: (err: Error) => {
        this.hubState.set('error');
        this.hubError.set(err.message);
        // Fan-out de upserts sequenciais: um erro intermediário pode ter configurado alguns filhos ANTES da
        // falha. Recarregar a lista mesmo no erro reflete o estado real; reexecutar é seguro (idempotente).
        this.reload();
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
        // Conector de vulnerabilidade não gera sinais: reporta as contagens reais (ativos/CVEs/exposições).
        if (r.vulnerabilities) {
          const v = r.vulnerabilities;
          const parcial = v.wasComplete ? '' : ' (coleta parcial/degradada)';
          this.setMsg(
            c.id,
            `Coleta concluída${parcial}: ${v.machinesObserved} máquina(s), ${v.cvesUpserted} CVE(s), ` +
              `${v.exposuresCreated} nova(s) exposição(ões), ${v.observationsOpened} observação(ões) aberta(s).`,
          );
        } else if (r.siem) {
          // SIEM (Sentinel, Google SecOps, …) não gera sinais de score: reporta a postura operacional observada
          // (fato consultivo, provider-neutral). A mensagem (função pura) só mostra a contagem de uma dimensão
          // quando o estado é Available/Partial — nunca "0" para um estado indisponível.
          this.setMsg(c.id, buildSiemSyncMessage(r.siem));
        } else {
          this.setMsg(c.id, `Coleta concluída: ${r.signalsCollected} sinal(is).`);
        }
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

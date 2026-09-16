import { DatePipe } from '@angular/common';
import {
  Component,
  DestroyRef,
  WritableSignal,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ActionPlan,
  ActionPlanStatus,
  DEVICE_VERIFICATION_PENDING,
  DeviceCaseSourceReading,
  PinnedPlanState,
  actionResult,
  actionSituation,
  actionStatusLabel,
  canManageActionPlans,
  devicePlansForCase,
  deviceOriginLead,
  eventLabel,
  methodLabel,
  normalizeCve,
  outcomeLabel,
  pinnedDevicePlanRejection,
  planForPanel,
  seededDeviceProposal,
  seededDeviceTitle,
  sourceReadingTone,
  validationBasis,
  validationScope,
} from '../../models/remediation.models';
import { DevicePriorityCase, bandTone, epssText, factorEffectLabel, factorKindLabel } from '../../models/device-priority.models';
import { ActionPlanConflictError, RemediationService } from '../../services/remediation.service';
import { AuthService } from '../../services/auth.service';

/**
 * [AEGIS-JOURNEY-01] O plano de tratamento de UM caso de vulnerabilidade em dispositivo (ativo × CVE) — o mesmo plano de
 * ação da jornada existente (responsável, área, prazo, ação proposta, execução, ciclo, trilha e concorrência), com a
 * origem nova.
 *
 * Cinco coisas aparecem SEPARADAS, porque é juntando-as que a tela mentiria:
 *   • a situação do plano (etapa, prazo, responsável) — decisão de gestão sobre o trabalho;
 *   • a execução relatada — o que alguém diz ter feito, não comprovação;
 *   • a situação atual na fonte — lida agora pela autoridade da prioridade, com leitura, falha e ausência próprias;
 *   • a validação — só atestação humana identificada; a verificação técnica automática está pendente e é dita;
 *   • o registro de origem — o contexto que motivou o plano, congelado pelo servidor na criação.
 *
 * Nada aqui envia faixa, fatores ou justificativa ao servidor: o pedido de criação só identifica ativo + CVE. Etapas
 * alcançáveis e motivo de bloqueio chegam prontos do servidor e não são reimplementados.
 *
 * Identidade de CONTEXTO (dispositivo × CVE × plano em foco): o painel é reutilizado ao trocar de caso, de plano ou de
 * dispositivo. Ao trocar, os rascunhos de edição, execução e atestação, os avisos e o "salvando" são reiniciados — nada
 * digitado para um plano pode ser enviado para outro. Uma nova versão do MESMO plano (releitura da lista, escrita própria)
 * não é troca: o que a pessoa está digitando fica. Respostas de escrita, conflito e releitura carregam o contexto em que
 * nasceram e não pintam, selecionam nem limpam nada em outro.
 */
@Component({
  selector: 'app-device-case-plan',
  standalone: true,
  imports: [DatePipe, FormsModule],
  template: `
    <section class="cp" [attr.aria-label]="'Plano de tratamento de ' + cveId()">
      <div class="cp-head">
        <h5>Plano de tratamento · <span class="mono">{{ cveId() }}</span> em {{ displayName() }}</h5>
        <button type="button" class="btn ghost sm" (click)="closed.emit()">Fechar plano</button>
      </div>

      @if (busy()) {
        <p class="pulse" role="status">Salvando…</p>
      }
      @if (notice(); as n) {
        <div class="state note" role="status">
          <b>{{ n }}</b>
          <button type="button" class="btn ghost sm" (click)="notice.set(null)">Fechar aviso</button>
        </div>
      }
      @if (error(); as e) {
        <div class="state err" role="alert">
          <b>{{ e }}</b>
          <button type="button" class="btn ghost sm" (click)="error.set(null)">Fechar aviso</button>
        </div>
      }
      @if (plansError(); as pe) {
        <p class="hint warn">Não foi possível ler a lista de planos deste dispositivo agora: {{ pe }} O plano ativo e os ciclos anteriores deste caso podem não aparecer.</p>
      }

      @switch (pinned().kind) {
        @case ('carregando') {
          <p class="pulse" role="status">Abrindo o plano indicado no endereço…</p>
        }
        @case ('indisponivel') {
          <div class="state err" role="alert">
            <b>{{ pinnedReason() }}</b>
            <button type="button" class="btn ghost sm" (click)="releasePin()">Ver o plano ativo deste caso</button>
          </div>
        }
      }

      @if (plan(); as p) {
        <!-- ---------- 1) Situação do plano ---------- -->
        <div class="kv">
          <span class="k">Situação do plano</span>
          <span class="v">
            <span class="chip" [class.late]="p.isOverdue">{{ situation(p) }}</span>
            <span class="mono">{{ p.title }} · aberto em {{ p.createdAt | date: 'dd/MM/yyyy' }}@if (p.dueDate) { · prazo {{ p.dueDate | date: 'dd/MM/yyyy' }} }</span>
          </span>
        </div>
        <div class="kv">
          <span class="k">Responsável</span>
          <span class="v">{{ p.responsiblePerson || 'não designado' }}@if (p.responsibleArea) { · {{ p.responsibleArea }} }</span>
        </div>
        @if (p.proposedAction) {
          <div class="kv"><span class="k">Ação proposta</span><span class="v">{{ p.proposedAction }}</span></div>
        }
        <div class="kv"><span class="k">Próxima providência</span><span class="v">{{ p.nextStep }}</span></div>

        <!-- ---------- 2) Execução relatada ---------- -->
        <div class="kv">
          <span class="k">Execução relatada</span>
          <span class="v">
            @if (p.executionNotes) {
              {{ p.executionNotes }}
              @if (p.executionEvidenceRef) { <span class="mono">referência: {{ p.executionEvidenceRef }}</span> }
              <span class="mono">em {{ p.executedAt | date: 'dd/MM/yyyy HH:mm' }} — relato de quem executou, não comprovação.</span>
            } @else {
              <span class="dim">Nenhuma execução relatada.</span>
            }
          </span>
        </div>

        <!-- ---------- 3) Validação ---------- -->
        <div class="kv">
          <span class="k">Validação</span>
          <span class="v">
            {{ result(p) }}
            <span class="mono">{{ verificationPending }}</span>
          </span>
        </div>

        <!-- ---------- 4) Situação atual na fonte (leitura própria) ---------- -->
        <div class="blk src">
          <div class="blk-head">
            <h6>Situação atual na fonte</h6>
            <button type="button" class="btn ghost sm" (click)="loadReading()" [disabled]="readingState() === 'loading'">Ler de novo</button>
          </div>
          @switch (readingState()) {
            @case ('loading') {
              <p class="pulse" role="status">Lendo a situação atual na fonte…</p>
            }
            @case ('error') {
              <p class="hint warn" role="alert">
                Não foi possível ler a situação atual na fonte agora ({{ readingError() }}). O plano continua válido, e nada
                é concluído a partir da falha.
              </p>
            }
            @case ('loaded') {
              @if (reading(); as r) {
                <p class="src-state">
                  <span class="chip tone-{{ tone(r.state) }}">{{ r.stateLabel }}</span>
                  <span class="mono">leitura de {{ r.evaluatedAt | date: 'dd/MM/yyyy HH:mm' }}</span>
                </p>
                <p class="hint">{{ r.explanation }}</p>
                @if (r.case; as c) {
                  <p class="hint">
                    Faixa atual: <b>{{ c.bandLabel }}</b> · {{ c.severityLabel }} · {{ c.exploitLabel }} ·
                    aquisição de {{ c.acquiredAt | date: 'dd/MM/yyyy HH:mm' }} ({{ c.acquisitionLabel }})
                    @if (r.isDeterminingCase) { · é o caso que determina a posição do dispositivo }
                  </p>
                }
                @if (r.comparisonNote) {
                  <p class="hint warn">{{ r.comparisonNote }}</p>
                }
                @if (r.caveats.length) {
                  <ul class="notes">@for (n of r.caveats; track n.code + n.text) { <li>{{ n.text }}</li> }</ul>
                }
                <p class="mono">{{ r.verificationNote }}</p>
              }
            }
          }
        </div>

        <!-- ---------- 5) Registro de origem (congelado na criação) ---------- -->
        @if (p.deviceOrigin; as o) {
          <details class="blk">
            <summary>{{ originLead(o) }}</summary>
            <p class="hint">
              Contexto registrado pelo servidor quando o plano foi criado — <b>não é a leitura atual</b>. Nome do
              dispositivo e faixa aparecem como eram; a chave do caso é o dispositivo e a CVE.
            </p>
            <div class="kv"><span class="k">Caso</span><span class="v">{{ o.caseReason }}@if (o.wasDeterminingCase) { <span class="mono">era o caso determinante do dispositivo</span> }</span></div>
            <div class="kv"><span class="k">Dispositivo na criação</span><span class="v">{{ o.deviceBandLabel }}<span class="mono">{{ o.devicePositionReason }}</span></span></div>
            <div class="kv">
              <span class="k">Fatos da fonte</span>
              <span class="v">
                {{ o.severityLabel }} · {{ o.exploitLabel }} · {{ epss(o.epss) }}
                <span class="mono">{{ o.source }} · aquisição de {{ o.acquiredAt | date: 'dd/MM/yyyy HH:mm' }} ({{ o.acquisitionLabel }}) · observada desde {{ o.firstSeenAt | date: 'dd/MM/yyyy' }}</span>
              </span>
            </div>
            <div class="scroll">
              <table class="ft">
                <thead><tr><th>Fator</th><th>Valor ou estado</th><th>Natureza</th><th>Efeito</th></tr></thead>
                <tbody>
                  @for (f of o.factors; track f.code) {
                    <tr>
                      <td>{{ f.label }}</td>
                      <td>{{ f.value }}</td>
                      <td>{{ kind(f.kind) }}</td>
                      <td>{{ effect(f.effect) }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
            @if (o.caveats.length) {
              <span class="k">Ressalvas na criação</span>
              <ul class="notes">@for (n of o.caveats; track n.code + n.text) { <li>{{ n.text }}</li> }</ul>
            }
            <p class="mono">{{ o.informationLabel }}</p>
            @if (o.absenceLabel) { <p class="mono">{{ o.absenceLabel }}</p> }
          </details>
        }

        @if (canManage()) {
          <!-- ---------- Etapa ---------- -->
          <div class="row">
            <span class="k">Mudar etapa</span>
            @for (t of p.allowedTransitions; track t) {
              <button type="button" class="btn ghost sm" (click)="changeStatus(t)" [disabled]="busy()">{{ statusLabel(t) }}</button>
            } @empty {
              <span class="dim">nenhuma mudança de etapa disponível</span>
            }
          </div>
          @if (p.closureBlockedReason; as motivo) {
            <p class="hint warn"><b>Encerrar ainda não está disponível.</b> {{ motivo }}</p>
          }

          <!-- ---------- Edição ---------- -->
          <details class="blk" [open]="editOpen()">
            <summary (click)="toggleEdit($event)">Editar responsável, prazo e ação</summary>
            <label class="fl"><span>Título</span><input type="text" [(ngModel)]="editTitle" name="editTitle" maxlength="200" /></label>
            <label class="fl"><span>Ação proposta</span><textarea rows="3" [(ngModel)]="editProposal" name="editProposal"></textarea></label>
            <div class="grid2">
              <label class="fl"><span>Responsável</span><input type="text" [(ngModel)]="editPerson" name="editPerson" maxlength="200" /></label>
              <label class="fl"><span>Área</span><input type="text" [(ngModel)]="editArea" name="editArea" maxlength="200" /></label>
              <label class="fl"><span>Prazo</span><input type="date" [(ngModel)]="editDue" name="editDue" /></label>
            </div>
            <p class="hint">Repactuar altera <b>este mesmo plano</b> — histórico, execução e validações continuam ligados a ele. Campo em branco mantém o valor atual.</p>
            <button type="button" class="btn real" (click)="saveEdit()" [disabled]="busy() || !editTitle.trim()">Salvar alterações</button>
          </details>

          <!-- ---------- Execução ---------- -->
          @if (p.status !== 'Concluido') {
            <details class="blk" [open]="execOpen()">
              <summary (click)="toggle($event, execOpen)">Registrar execução</summary>
              <label class="fl"><span>O que foi feito</span>
                <textarea rows="3" [(ngModel)]="execNotes" name="execNotes" placeholder="Descreva o que foi executado no dispositivo."></textarea>
              </label>
              <label class="fl"><span>Referência (opcional)</span>
                <input type="text" [(ngModel)]="execEvidence" name="execEvidence" placeholder="chamado, janela de mudança, registro" />
              </label>
              <p class="hint">Registrar a execução leva o plano a <b>aguardando validação</b>. Relatar não comprova que a CVE foi corrigida no dispositivo.</p>
              <button type="button" class="btn real" (click)="recordExecution()" [disabled]="busy() || !execNotes.trim()">Registrar execução</button>
            </details>
          }

          <!-- ---------- Validação (só atestação humana) ---------- -->
          <details class="blk" [open]="valOpen()">
            <summary (click)="toggle($event, valOpen)">Registrar atestação com evidência</summary>
            <p class="hint warn">{{ verificationPending }} A comparação de avaliações do AEGIS KNIGHT não se aplica a CVEs de dispositivo.</p>
            <label class="fl"><span>Referência da evidência (obrigatória)</span>
              <input type="text" [(ngModel)]="humanEvidence" name="humanEvidence" placeholder="relatório de atualização, chamado, registro do fornecedor…" />
            </label>
            <label class="fl"><span>Observação (opcional)</span><input type="text" [(ngModel)]="humanNote" name="humanNote" /></label>
            <p class="hint">Fica registrada como <b>atestação humana</b>, com autor e data — nunca como correção técnica comprovada.</p>
            <button type="button" class="btn ghost" (click)="attest()" [disabled]="busy() || !humanEvidence.trim()">Registrar atestação</button>
          </details>
        } @else {
          <p class="hint warn">Seu papel permite <b>acompanhar</b> este plano. Mudar etapa, editar, registrar execução e atestar exigem <b>Manager</b> ou <b>TenantAdmin</b>.</p>
        }

        @if (p.validations.length) {
          <div class="blk">
            <h6>Validações registradas</h6>
            @for (v of p.validations; track v.decidedAt) {
              <div class="val" [class.past]="!v.appliesToCurrentCycle">
                <b>{{ outcome(v.outcome) }}</b>
                <span class="mono">{{ method(v.method) }} · {{ v.decidedAt | date: 'dd/MM/yyyy HH:mm' }}@if (v.decidedByName) { · {{ v.decidedByName }} }@if (v.evidenceReference) { · {{ v.evidenceReference }} }</span>
                <span class="hint">{{ v.rationale }}</span>
                <span class="mono">{{ basis(v) }} {{ scope(v) }}</span>
              </div>
            }
          </div>
        }

        <div class="blk">
          <h6>Histórico</h6>
          @for (e of p.events; track e.at + e.kind) {
            <div class="ev">
              <span class="mono">{{ e.at | date: 'dd/MM/yyyy HH:mm' }}</span>
              <span>{{ eventLabel(e.kind) }}@if (e.actorName) { · {{ e.actorName }} }</span>
              @if (e.note) { <span class="dim">{{ e.note }}</span> }
            </div>
          }
        </div>
      } @else if (pinned().kind === 'livre') {
        <!-- Sem plano em foco. Criar só é oferecido quando a AUSÊNCIA de plano ativo foi lida e o caso pode ser lido agora:
             lista de planos com falha, prioridade sem leitura ou dispositivo ausente não viram convite para criar. -->
        @if (plansError()) {
          <div class="state err" role="alert">
            <b>Criar um plano fica indisponível até a lista de planos ser lida: sem ela, não dá para saber se já existe um plano ativo para este caso.</b>
            <button type="button" class="btn ghost sm" (click)="retryPlans.emit()">Ler os planos de novo</button>
          </div>
        } @else if (!canManage()) {
          <p class="hint warn">Nenhum plano ativo para esta CVE neste dispositivo. Criar um exige <b>Manager</b> ou <b>TenantAdmin</b> — seu papel permite acompanhar, não iniciar.</p>
        } @else if (creationBlockedReason()) {
          <p class="hint warn">Nenhum plano ativo para esta CVE neste dispositivo. {{ creationBlockedReason() }}</p>
        } @else {
          <p class="hint">
            Crie o plano para este caso. O servidor lê o caso na prioridade de tratamento e registra a faixa, os fatores,
            as fontes e as ressalvas daquele momento como <b>origem do plano</b> — nada desta tela é enviado como evidência.
          </p>
          <label class="fl"><span>Título</span><input type="text" [(ngModel)]="newTitle" name="newTitle" maxlength="200" /></label>
          <label class="fl"><span>Ação proposta</span><textarea rows="3" [(ngModel)]="newProposal" name="newProposal"></textarea></label>
          <div class="grid2">
            <label class="fl"><span>Responsável</span><input type="text" [(ngModel)]="newPerson" name="newPerson" maxlength="200" /></label>
            <label class="fl"><span>Área</span><input type="text" [(ngModel)]="newArea" name="newArea" maxlength="200" /></label>
            <label class="fl"><span>Prazo</span><input type="date" [(ngModel)]="newDue" name="newDue" /></label>
          </div>
          <button type="button" class="btn real" (click)="create()" [disabled]="busy() || !newTitle.trim()">Criar plano de tratamento</button>
        }
      }

      @if (previous().length) {
        <div class="blk">
          <h6>Ciclos anteriores deste caso</h6>
          @for (x of previous(); track x.id) {
            <button type="button" class="link" (click)="openPrevious(x)">
              {{ x.title }} · {{ situation(x) }} · aberto em {{ x.createdAt | date: 'dd/MM/yyyy' }}
            </button>
          }
        </div>
      }
    </section>
  `,
  styles: [
    `
      .cp { display: flex; flex-direction: column; gap: 8px; border: 1px solid var(--line); border-radius: 10px; padding: 10px 14px; max-width: 980px; }
      .cp-head, .blk-head { display: flex; justify-content: space-between; align-items: center; gap: 10px; flex-wrap: wrap; }
      .cp-head h5 { margin: 0; font-size: 13px; font-weight: 600; }
      h6, .k, .fl > span, .ft th { font-family: var(--sans); font-size: var(--fs-caps); color: var(--muted); text-transform: uppercase; letter-spacing: 0; font-weight: 500; margin: 0; }
      .kv { display: grid; grid-template-columns: 170px 1fr; gap: 10px; padding: 6px 0; border-top: 1px solid var(--line-2); }
      .v { font-size: 12.5px; line-height: 1.5; display: flex; flex-direction: column; gap: 3px; min-width: 0; overflow-wrap: anywhere; }
      .mono, .dim, .pulse { font-family: var(--mono); font-size: var(--fs-caps); color: var(--muted); line-height: 1.45; margin: 0; }
      .chip { font-family: var(--sans); font-size: var(--fs-caps); padding: 2px 8px; border-radius: 999px; border: 1px solid var(--line); align-self: flex-start; }
      .chip.late { color: #ff5c8a; }
      .chip.tone-warn, .notes, .state.note b { color: var(--amber); }
      .chip.tone-info { color: var(--cyan); }
      .blk { border-top: 1px solid var(--line-2); padding-top: 8px; display: flex; flex-direction: column; gap: 6px; }
      .src-state { display: flex; gap: 10px; align-items: baseline; flex-wrap: wrap; margin: 0; }
      summary, .link { cursor: pointer; font-family: var(--sans); font-size: var(--fs-meta); color: var(--cyan); }
      .row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; padding-top: 6px; border-top: 1px solid var(--line-2); }
      .fl { display: flex; flex-direction: column; gap: 3px; }
      .fl input, .fl textarea { background: rgba(122, 145, 190, 0.06); border: 1px solid var(--line); border-radius: 8px; padding: 6px 10px; color: inherit; font: inherit; font-size: 12.5px; width: 100%; box-sizing: border-box; }
      .grid2 { display: grid; grid-template-columns: repeat(auto-fit, minmax(170px, 1fr)); gap: 8px; }
      .hint { margin: 0; font-size: 12px; line-height: 1.55; color: var(--muted); }
      .hint b { color: var(--text); }
      .hint.warn { border-left: 2px solid var(--amber); padding: 4px 8px; }
      .notes { margin: 2px 0 0; padding-left: 18px; font-size: 12px; line-height: 1.5; }
      .scroll { overflow-x: auto; }
      .ft { width: 100%; border-collapse: collapse; font-size: var(--fs-meta); }
      .ft th, .ft td { text-align: left; padding: 4px 6px; vertical-align: top; border-bottom: 1px solid var(--line-2); overflow-wrap: anywhere; }
      .val { display: flex; flex-direction: column; gap: 2px; padding: 6px 0; border-bottom: 1px solid var(--line-2); font-size: 12.5px; }
      .val.past { opacity: 0.72; padding-left: 8px; }
      .ev, .state { display: flex; gap: 8px; flex-wrap: wrap; font-size: 12px; align-items: center; }
      .btn { cursor: pointer; font-family: var(--sans); font-size: var(--fs-meta); font-weight: 600; border-radius: 9px; padding: 6px 12px; border: 1px solid var(--line); color: inherit; background: rgba(122, 145, 190, 0.08); align-self: flex-start; }
      .btn.sm { padding: 4px 9px; font-size: var(--fs-caps); }
      .btn.real { color: var(--cyan); border-color: var(--cyan); }
      .btn:disabled { opacity: 0.5; cursor: not-allowed; }
      .link { background: none; border: none; padding: 0; text-align: left; font-size: 12px; }
      .state.err b { color: #ff5c8a; }
      @media (max-width: 720px) { .kv { grid-template-columns: 1fr; gap: 2px; } }
    `,
  ],
})
export class DeviceCasePlanComponent {
  private readonly api = inject(RemediationService);
  private readonly auth = inject(AuthService);

  readonly assetId = input.required<string>();
  /** Nome do dispositivo na leitura atual da prioridade — nulo quando ela falhou ou o dispositivo não existe mais. */
  readonly assetName = input<string | null>(null);
  readonly cveId = input.required<string>();
  /** O caso na leitura atual, quando está na página exibida — só para semear o formulário. */
  readonly caseInfo = input<DevicePriorityCase | null>(null);
  /** Planos de casos DESTE dispositivo, lidos pelo componente que contém este painel. */
  readonly plans = input<ActionPlan[]>([]);
  readonly plansError = input<string | null>(null);
  /** Plano nomeado pelo endereço (`?plan=`): é ele que ocupa o painel, mesmo encerrado. */
  readonly pinnedPlanId = input<string | null>(null);
  /**
   * Por que criar um plano NÃO pode ser oferecido agora (prioridade sem leitura, com falha, dispositivo ausente) — nulo
   * quando pode. Planos existentes continuam acessíveis; só a criação depende da leitura atual do caso.
   */
  readonly creationBlockedReason = input<string | null>(null);

  /** Um plano foi criado ou alterado — quem contém o painel relê os planos. */
  readonly changed = output<ActionPlan>();
  readonly closed = output<void>();
  /** O plano em foco mudou por escolha da pessoa (ciclo anterior ou volta ao ativo) — para o endereço acompanhar. */
  readonly planOpened = output<string | null>();
  /** Pedido explícito para reler a lista de planos (depois de uma falha). */
  readonly retryPlans = output<void>();

  protected readonly statusLabel = actionStatusLabel;
  protected readonly situation = actionSituation;
  protected readonly result = actionResult;
  protected readonly outcome = outcomeLabel;
  protected readonly method = methodLabel;
  protected readonly basis = validationBasis;
  protected readonly scope = validationScope;
  protected readonly eventLabel = eventLabel;
  protected readonly originLead = deviceOriginLead;
  protected readonly tone = sourceReadingTone;
  protected readonly kind = factorKindLabel;
  protected readonly effect = factorEffectLabel;
  protected readonly epss = epssText;
  protected readonly bandTone = bandTone;
  protected readonly verificationPending = DEVICE_VERIFICATION_PENDING;

  readonly error = signal<string | null>(null);
  readonly notice = signal<string | null>(null);
  readonly editOpen = signal(false);
  readonly execOpen = signal(false);
  readonly valOpen = signal(false);

  /** Estado do plano nomeado pelo endereço — enquanto carrega ou falha, NADA o substitui. */
  readonly pinned = signal<PinnedPlanState>({ kind: 'livre' });
  readonly pinnedReason = computed(() => {
    const p = this.pinned();
    return p.kind === 'indisponivel' ? p.reason : '';
  });
  /** Resposta da última escrita deste painel. */
  private readonly local = signal<ActionPlan | null>(null);

  readonly casePlans = computed(() => devicePlansForCase(this.plans(), this.assetId(), this.cveId()));
  readonly activePlan = computed(() => this.casePlans().find((p) => p.isActive) ?? null);

  /**
   * O plano em foco: o nomeado pelo endereço, ou o ativo do caso; a resposta da última escrita vence quando é o MESMO
   * plano e é mais nova. Uma escrita de outro caso (o painel é reutilizado ao trocar de CVE) nunca aparece aqui.
   */
  readonly plan = computed<ActionPlan | null>(() => {
    const base = planForPanel(this.pinned(), this.activePlan());
    const l = this.local();
    if (!l || pinnedDevicePlanRejection(l, this.assetId(), this.cveId()) !== null) return base;
    if (this.pinned().kind !== 'livre' && this.pinned().kind !== 'carregada') return base;
    if (!base) return l;
    return base.id === l.id && l.version >= base.version ? l : base;
  });

  readonly previous = computed(() => this.casePlans().filter((p) => !p.isActive && p.id !== this.plan()?.id));

  readonly canManage = computed(() => canManageActionPlans(this.auth.activeRole()));

  /** Nome exibido: o da leitura atual; sem ela, o do registro de origem do plano em foco (como era na criação). */
  readonly displayName = computed(
    () => this.assetName() || this.plan()?.deviceOrigin?.assetName || 'dispositivo indicado no endereço',
  );

  /** Identidade do contexto: dispositivo × CVE × plano em foco ("novo" quando não há plano). */
  readonly contextKey = computed(() =>
    [(this.assetId() ?? '').toLowerCase(), normalizeCve(this.cveId()), this.plan()?.id ?? 'novo'].join('|'),
  );
  private currentContext: string | null = null;

  /** Contexto da escrita em curso: o "salvando" pertence a ele, não ao painel. */
  private readonly pendingKey = signal<string | null>(null);
  readonly busy = computed(() => this.pendingKey() !== null && this.pendingKey() === this.contextKey());

  readonly reading = signal<DeviceCaseSourceReading | null>(null);
  readonly readingState = signal<'idle' | 'loading' | 'error' | 'loaded'>('idle');
  readonly readingError = signal<string | null>(null);
  private readingSeq = 0;
  private pinSeq = 0;
  private readingFor: string | null = null;
  private destroyed = false;

  protected newTitle = '';
  protected newProposal = '';
  protected newPerson = '';
  protected newArea = '';
  protected newDue = '';
  protected execNotes = '';
  protected execEvidence = '';
  protected humanEvidence = '';
  protected humanNote = '';
  protected editTitle = '';
  protected editProposal = '';
  protected editPerson = '';
  protected editArea = '';
  protected editDue = '';
  private seededFor: string | null = null;

  constructor() {
    // Plano nomeado pelo endereço: lido e CONFERIDO contra o caso aberto (outro dispositivo ou outra CVE = recusado).
    effect(() => {
      const id = this.pinnedPlanId();
      const asset = this.assetId();
      const cve = this.cveId();
      untracked(() => this.pin(id, asset, cve));
    });

    // Troca de contexto (outro caso, outro plano, outro dispositivo): rascunhos e avisos do contexto anterior saem.
    effect(() => {
      const key = this.contextKey();
      untracked(() => {
        if (key === this.currentContext) return;
        this.currentContext = key;
        this.resetDrafts();
      });
    });

    // A situação na fonte é lida para o plano em foco — e só relida quando o plano em foco muda.
    effect(() => {
      const id = this.plan()?.id ?? null;
      untracked(() => {
        if (id === this.readingFor) return;
        this.readingFor = id;
        this.reading.set(null);
        this.readingState.set('idle');
        if (id) this.loadReading();
      });
    });

    // Semeia o formulário de criação a cada caso sem plano — sugestão editável, não decisão.
    effect(() => {
      const key = `${(this.assetId() ?? '').toLowerCase()}|${normalizeCve(this.cveId())}`;
      const hasPlan = !!this.plan();
      const name = this.displayName();
      const cve = this.cveId();
      const info = this.caseInfo();
      untracked(() => {
        if (hasPlan || this.seededFor === key) return;
        this.seededFor = key;
        this.newTitle = seededDeviceTitle(cve, name);
        this.newProposal = seededDeviceProposal(cve, info);
        this.newPerson = '';
        this.newArea = '';
        this.newDue = '';
      });
    });

    inject(DestroyRef).onDestroy(() => (this.destroyed = true));
  }

  /** Rascunhos de edição, execução e atestação e os avisos pertencem ao contexto em que foram escritos. */
  private resetDrafts(): void {
    this.editTitle = '';
    this.editProposal = '';
    this.editPerson = '';
    this.editArea = '';
    this.editDue = '';
    this.execNotes = '';
    this.execEvidence = '';
    this.humanEvidence = '';
    this.humanNote = '';
    this.editOpen.set(false);
    this.execOpen.set(false);
    this.valOpen.set(false);
    this.notice.set(null);
    this.error.set(null);
  }

  /** O painel passou a outro contexto por uma resposta DESTE contexto (criação, plano ativo apontado): não é troca. */
  private adoptContext(): void {
    this.currentContext = this.contextKey();
  }

  private pin(id: string | null, asset: string, cve: string): void {
    const seq = ++this.pinSeq;
    if (!id) {
      this.pinned.set({ kind: 'livre' });
      return;
    }
    // O plano que este painel acabou de gravar já está em memória: fixá-lo não precisa de nova leitura (nem de um
    // intervalo em que o painel pareça vazio).
    const l = this.local();
    if (l && l.id === id && pinnedDevicePlanRejection(l, asset, cve) === null) {
      this.pinned.set({ kind: 'carregada', id, plan: l });
      return;
    }
    this.pinned.set({ kind: 'carregando', id });
    this.api.get(id).subscribe({
      next: (p) => {
        if (this.destroyed || seq !== this.pinSeq) return;
        const why = pinnedDevicePlanRejection(p, asset, cve);
        this.pinned.set(why ? { kind: 'indisponivel', id, reason: why } : { kind: 'carregada', id, plan: p });
      },
      error: (e: Error) => {
        if (this.destroyed || seq !== this.pinSeq) return;
        this.pinned.set({ kind: 'indisponivel', id, reason: `O plano indicado no endereço não pôde ser aberto: ${e.message}` });
      },
    });
  }

  /** Solta a fixação e volta ao plano ativo do caso (ou à criação) — ação explícita da pessoa. */
  releasePin(): void {
    this.pinSeq++;
    this.pinned.set({ kind: 'livre' });
    this.planOpened.emit(null);
  }

  openPrevious(p: ActionPlan): void {
    this.pinSeq++;
    this.pinned.set({ kind: 'carregada', id: p.id, plan: p });
    this.planOpened.emit(p.id);
  }

  loadReading(): void {
    const id = this.plan()?.id;
    if (!id) return;
    const seq = ++this.readingSeq;
    this.readingState.set('loading');
    this.readingError.set(null);
    this.api.sourceReading(id).subscribe({
      next: (r) => {
        if (this.destroyed || seq !== this.readingSeq) return;   // resposta tardia de outro plano
        this.reading.set(r);
        this.readingState.set('loaded');
      },
      error: (e: Error) => {
        if (this.destroyed || seq !== this.readingSeq) return;
        this.reading.set(null);
        this.readingError.set(e.message);
        this.readingState.set('error');
      },
    });
  }

  protected toggle(ev: Event, s: WritableSignal<boolean>): void {
    ev.preventDefault();
    s.set(!s());
  }

  protected toggleEdit(ev: Event): void {
    ev.preventDefault();
    const open = !this.editOpen();
    if (open) this.seedEdit();
    this.editOpen.set(open);
  }

  private seedEdit(): void {
    const p = this.plan();
    if (!p) return;
    this.editTitle = p.title;
    this.editProposal = p.proposedAction ?? '';
    this.editPerson = p.responsiblePerson ?? '';
    this.editArea = p.responsibleArea ?? '';
    this.editDue = p.dueDate ?? '';
  }

  create(): void {
    this.run(
      this.api.createForDeviceCase({
        assetId: this.assetId(),
        cveId: this.cveId(),
        title: this.newTitle.trim(),
        proposedAction: this.newProposal.trim() || null,
        responsiblePerson: this.newPerson.trim() || null,
        responsibleArea: this.newArea.trim() || null,
        dueDate: this.newDue || null,
      }),
      'Plano criado. O servidor registrou a leitura atual da prioridade como origem do plano.',
    );
  }

  changeStatus(status: ActionPlanStatus): void {
    const p = this.plan();
    if (!p) return;
    this.run(
      this.api.update(p.id, { expectedVersion: p.version, status }),
      status === 'Concluido'
        ? 'Plano concluído — decisão de gestão. A situação na fonte continua acompanhada à parte; concluído não é dispositivo corrigido.'
        : `Etapa alterada para "${actionStatusLabel(status)}".`,
    );
  }

  saveEdit(): void {
    const p = this.plan();
    if (!p) return;
    this.run(
      this.api.update(p.id, {
        expectedVersion: p.version,
        title: this.editTitle.trim() || null,
        proposedAction: this.editProposal.trim() || null,
        responsiblePerson: this.editPerson.trim() || null,
        responsibleArea: this.editArea.trim() || null,
        dueDate: this.editDue || null,
      }),
      'Alterações salvas neste plano.',
      () => this.editOpen.set(false),
    );
  }

  recordExecution(): void {
    const p = this.plan();
    if (!p) return;
    this.run(
      this.api.recordExecution(p.id, {
        expectedVersion: p.version,
        notes: this.execNotes.trim(),
        evidenceReference: this.execEvidence.trim() || null,
      }),
      'Execução registrada — é relato, não comprovação. O plano aguarda validação.',
      () => {
        this.execNotes = '';
        this.execEvidence = '';
        this.execOpen.set(false);
      },
    );
  }

  attest(): void {
    const p = this.plan();
    if (!p) return;
    this.run(
      this.api.validate(p.id, {
        expectedVersion: p.version,
        validationRunId: null,
        evidenceReference: this.humanEvidence.trim(),
        note: this.humanNote.trim() || null,
      }),
      'Atestação registrada — fica identificada como atestação humana, não como correção comprovada.',
      () => {
        this.humanEvidence = '';
        this.humanNote = '';
        this.valOpen.set(false);
      },
    );
  }

  /** A escrita do contexto `key` terminou: o "salvando" dele sai (o de outro contexto em curso, não). */
  private settle(key: string): void {
    if (this.pendingKey() === key) this.pendingKey.set(null);
  }

  private run(call: ReturnType<RemediationService['create']>, success: string, onDone?: () => void): void {
    const key = this.contextKey();
    this.pendingKey.set(key);
    this.error.set(null);
    this.notice.set(null);
    call.subscribe({
      next: (p) => {
        if (this.destroyed) return;
        this.settle(key);
        this.changed.emit(p);   // a escrita aconteceu: quem contém o painel relê de qualquer forma
        if (key !== this.contextKey()) return;   // resposta de outro caso/plano: nada é pintado aqui
        this.local.set(p);
        onDone?.();
        this.adoptContext();   // criar leva o painel do rascunho ao plano criado — sem apagar o aviso
        this.notice.set(success);
        // O endereço passa a nomear ESTE plano: recarregar ou compartilhar volta a ele — mesmo depois de concluído.
        if (this.pinnedPlanId() !== p.id) this.planOpened.emit(p.id);
      },
      error: (e: Error) => this.fail(e, key),
    });
  }

  /**
   * 409 de DUPLICIDADE (o servidor aponta o plano ativo do caso) abre esse plano — é o que o segundo clique, ou a
   * reabertura de um ciclo encerrado, deveria mostrar. 409 de VERSÃO relê o plano e pede conferência. Nenhuma escrita é
   * repetida, e uma resposta que chega depois de a pessoa trocar de caso ou de plano não mexe no painel.
   */
  private fail(e: Error, key: string): void {
    if (this.destroyed) return;
    if (key !== this.contextKey()) {
      this.settle(key);
      return;
    }
    if (e instanceof ActionPlanConflictError && e.existingActionPlanId) {
      this.openAnnounced(e.existingActionPlanId, key, this.plan());
      return;
    }
    if (e instanceof ActionPlanConflictError && this.plan()) {
      this.rereadAfterVersionConflict(this.plan()!.id, key, e.message);
      return;
    }
    this.settle(key);
    this.error.set(e.message);
  }

  /**
   * Abre o plano ATIVO que o servidor anunciou no 409, depois de conferir que é deste caso: ele passa a ser o plano em
   * foco (fixado) e o endereço acompanha — a mensagem descreve exatamente o que ficou na tela.
   */
  private openAnnounced(id: string, key: string, from: ActionPlan | null): void {
    this.api.get(id).subscribe({
      next: (p) => {
        if (this.destroyed) return;
        this.settle(key);
        this.changed.emit(p);   // a lista pode não conhecer esse plano (criado em outra sessão)
        if (key !== this.contextKey()) return;
        const why = pinnedDevicePlanRejection(p, this.assetId(), this.cveId());
        if (why) {
          this.error.set(`O servidor apontou outro plano (${id}), mas ele não pode ser exibido aqui: ${why} Nada foi criado nem reaberto.`);
          return;
        }
        this.pinSeq++;
        this.local.set(p);
        this.pinned.set({ kind: 'carregada', id: p.id, plan: p });
        this.resetDrafts();   // o que foi digitado para o plano anterior não segue para este
        this.adoptContext();
        this.notice.set(
          (from
            ? `O plano "${from.title}" não foi alterado: já existe um plano ativo para esta CVE neste dispositivo. `
            : 'Nenhum plano novo foi criado: já existe um plano ativo para esta CVE neste dispositivo. ') +
            `Agora está aberto o plano ativo "${p.title}" (${actionSituation(p)}) — o endereço aponta para ele.`,
        );
        if (this.pinnedPlanId() !== p.id) this.planOpened.emit(p.id);
      },
      error: (err: Error) => {
        if (this.destroyed) return;
        this.settle(key);
        if (key !== this.contextKey()) return;
        this.error.set(
          'Já existe um plano ativo para esta CVE neste dispositivo, mas ele não pôde ser aberto agora ' +
            `(${err.message}). Nada foi criado nem reaberto.`,
        );
      },
    });
  }

  /** Conflito de VERSÃO: o plano é relido; o que a pessoa digitou fica, para conferir antes de enviar de novo. */
  private rereadAfterVersionConflict(id: string, key: string, message: string): void {
    this.api.get(id).subscribe({
      next: (p) => {
        if (this.destroyed) return;
        this.settle(key);
        this.changed.emit(p);
        if (key !== this.contextKey()) return;
        if (pinnedDevicePlanRejection(p, this.assetId(), this.cveId()) !== null) return;
        this.local.set(p);
        this.adoptContext();
        this.notice.set(
          `${message} O plano foi relido com o estado atual — o que você digitou foi mantido; confira antes de enviar de novo.`,
        );
      },
      error: (err: Error) => {
        if (this.destroyed) return;
        this.settle(key);
        if (key !== this.contextKey()) return;
        this.error.set(`${message} Não foi possível reler o plano agora (${err.message}).`);
      },
    });
  }
}

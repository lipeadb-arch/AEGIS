import { DatePipe } from '@angular/common';
import { Component, DestroyRef, computed, effect, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ActionPlan,
  ActionPlanStatus,
  actionResult,
  actionSituation,
  actionStatusLabel,
  canManageActionPlans,
  eventLabel,
  isTechnicallyProven,
  methodLabel,
  originLabel,
  outcomeLabel,
  seededProposal,
  validationBasis,
  validationScope,
} from '../../models/remediation.models';
import { ActionPlanConflictError, RemediationService } from '../../services/remediation.service';
import { AuthService } from '../../services/auth.service';

/**
 * [AEGIS-MVP-PRODUCT-03] A jornada de UM achado, do plano à comprovação.
 *
 * Sem plano: um formulário MÍNIMO — título, ação proposta (semeada com o contexto do achado e editável),
 * responsável, área e prazo. Nada de cadastro de risco ou de processo de negócio para começar.
 *
 * Com plano: a etapa operacional, o resultado observado no achado e o método de validação aparecem
 * SEPARADOS. É o ponto em que a tela mais facilmente mentiria: "concluída" fala do trabalho, não do
 * ambiente, e só uma nova coleta compatível comprova que a exposição caiu.
 *
 * Três regras que este painel NÃO reimplementa, porque decidi-las aqui faria a tela divergir do servidor e
 * oferecer um controle que a gravação depois recusaria:
 *
 *   • as ETAPAS alcançáveis chegam prontas em `allowedTransitions`;
 *   • o motivo de encerrar ainda não estar disponível chega em `closureBlockedReason`, com as palavras do
 *     servidor — e não é reescrito aqui;
 *   • o resultado do CICLO ATUAL é `applicableValidation`. As validações anteriores continuam listadas,
 *     identificadas como de outro ciclo: o histórico é preservado, mas não autoriza mais nada.
 *
 * A validação por nova avaliação usa a avaliação ABERTA na tela — que é justamente a que o analista acabou
 * de executar. Quando a avaliação aberta é a própria origem do plano, o botão não aparece: uma coleta não
 * comprova a correção de um problema que ela mesma revelou.
 */
@Component({
  selector: 'app-knight-action-plan',
  standalone: true,
  imports: [DatePipe, FormsModule],
  template: `
    <div class="ap">
      @if (busy()) {
        <p class="pulse">Salvando…</p>
      }
      @if (notice(); as n) {
        <div class="state note inline">
          <b>{{ n }}</b>
          <button type="button" class="btn ghost sm" (click)="notice.set(null)">Fechar</button>
        </div>
      }
      @if (error(); as e) {
        <div class="state err inline">
          <b>{{ e }}</b>
          <button type="button" class="btn ghost sm" (click)="error.set(null)">Fechar</button>
        </div>
      }

      @if (plan(); as p) {
        <!-- ---------- Plano existente ---------- -->
        <div class="kv"><span class="k">Ação</span><span class="v">{{ p.title }}</span></div>
        <!-- PROCEDÊNCIA visível: "AK-ENTRA-001 na demonstração" e "AK-ENTRA-001 na coleta real" são dois
             problemas distintos, e quem olha precisa saber em qual deles está mexendo. -->
        <div class="kv">
          <span class="k">Origem da ação</span>
          <span class="v">
            <span class="chip" [class.demo]="p.originMode === 'Demo'">{{ origin(p) }}</span>
            <span class="mono">
              avaliação de origem {{ shortRun(p.originRunId) }} · aberta em {{ p.createdAt | date: 'dd/MM/yyyy' }}
              @if (reopened(p)) { · ciclo atual desde {{ p.cycleStartedAt | date: 'dd/MM/yyyy HH:mm' }} }
            </span>
          </span>
        </div>
        <div class="kv">
          <span class="k">Situação do plano</span>
          <span class="v">
            <span class="chip" [class.late]="p.isOverdue">{{ situation(p) }}</span>
            @if (p.dueDate) { <span class="mono">prazo {{ p.dueDate }}</span> }
          </span>
        </div>
        <div class="kv">
          <span class="k">Resultado no achado</span>
          <span class="v" [class.proven]="proven()">{{ result(p) }}</span>
        </div>
        <div class="kv"><span class="k">Próxima providência</span><span class="v">{{ p.nextStep }}</span></div>
        <div class="kv">
          <span class="k">Responsável</span>
          <span class="v">{{ p.responsiblePerson || 'não designado' }}@if (p.responsibleArea) { · {{ p.responsibleArea }} }</span>
        </div>
        @if (p.proposedAction) {
          <div class="kv"><span class="k">Ação proposta</span><span class="v">{{ p.proposedAction }}</span></div>
        }
        @if (p.executionNotes) {
          <div class="kv">
            <span class="k">Execução relatada</span>
            <span class="v">
              {{ p.executionNotes }}
              @if (p.executionEvidenceRef) { <span class="mono">evidência: {{ p.executionEvidenceRef }}</span> }
              <span class="mono">em {{ p.executedAt | date: 'dd/MM/yyyy HH:mm' }} — relato, não comprovação.</span>
            </span>
          </div>
        }

        @if (canManage()) {
          <!-- ---------- Etapa ---------- -->
          <div class="row">
            <span class="k">Avançar etapa</span>
            @for (t of transitions(); track t) {
              <button type="button" class="btn ghost sm" (click)="changeStatus(t)" [disabled]="busy()">
                {{ statusLabel(t) }}
              </button>
            } @empty {
              <span class="dim">nenhuma transição disponível</span>
            }
          </div>
          <!-- O motivo vem do SERVIDOR: é a mesma frase com que a gravação recusaria. -->
          @if (p.closureBlockedReason; as motivo) {
            <p class="hint warn"><b>Encerrar ainda não está disponível.</b> {{ motivo }}</p>
          }

          <!-- ---------- Edição simples ---------- -->
          <details class="blk" [open]="editOpen()">
            <summary (click)="toggleEdit()">Editar dados da ação</summary>
            <label class="fl">
              <span>Título</span>
              <input type="text" [(ngModel)]="editTitle" name="editTitle" maxlength="200" />
            </label>
            <label class="fl">
              <span>Ação proposta</span>
              <textarea rows="3" [(ngModel)]="editProposal" name="editProposal"></textarea>
            </label>
            <div class="grid2">
              <label class="fl">
                <span>Responsável</span>
                <input type="text" [(ngModel)]="editPerson" name="editPerson" maxlength="200" />
              </label>
              <label class="fl">
                <span>Área</span>
                <input type="text" [(ngModel)]="editArea" name="editArea" maxlength="200" />
              </label>
              <label class="fl">
                <span>Prazo</span>
                <input type="date" [(ngModel)]="editDue" name="editDue" />
              </label>
            </div>
            <p class="hint">
              Repactuar o prazo <b>altera esta mesma ação</b> — não cria outra: histórico, execução relatada e
              validações continuam ligados a ela. Campo deixado em branco mantém o valor atual.
            </p>
            <button type="button" class="btn real" (click)="saveEdit()" [disabled]="busy() || !editTitle.trim()">
              Salvar alterações
            </button>
          </details>

          <!-- ---------- Execução ---------- -->
          @if (p.status !== 'Concluido') {
            <details class="blk" [open]="execOpen()">
              <summary (click)="execOpen.set(!execOpen())">Registrar execução</summary>
              <label class="fl">
                <span>O que foi feito</span>
                <textarea rows="3" [(ngModel)]="execNotes" name="execNotes"
                          placeholder="Descreva o que foi executado — este texto é o que a validação vai verificar depois."></textarea>
              </label>
              <label class="fl">
                <span>Referência da evidência (opcional)</span>
                <input type="text" [(ngModel)]="execEvidence" name="execEvidence" placeholder="chamado, documento ou registro" />
              </label>
              <p class="hint">
                Registrar a execução leva a ação para <b>aguardando validação</b>. Relatar o que foi feito não
                comprova que a exposição foi corrigida — a comprovação é o passo seguinte.
              </p>
              <button type="button" class="btn real" (click)="recordExecution()" [disabled]="busy() || !execNotes.trim()">
                Registrar execução
              </button>
            </details>
          }

          <!-- ---------- Validação ---------- -->
          <details class="blk" [open]="valOpen()">
            <summary (click)="valOpen.set(!valOpen())">Validar com evidência</summary>

            @if (canValidateWithRun()) {
              <p class="hint">
                O AEGIS vai comparar esta avaliação com a que originou o achado e <b>decidir o desfecho</b> — a
                decisão não é escolhida aqui. Fontes diferentes, regras diferentes, ordem temporal inválida ou
                falta da capacidade que este achado consome produzem <b>evidência insuficiente</b>, nunca
                melhora. Uma coleta ANTERIOR ao relato de execução fica registrada com a ressalva de que o que
                ela observou não é atribuível a esta ação.
              </p>
              <button type="button" class="btn real" (click)="validateWithRun()" [disabled]="busy()">
                Validar com a avaliação aberta
              </button>
            } @else {
              <p class="hint warn">
                A avaliação aberta na tela é a <b>mesma</b> que originou este achado. Uma coleta não comprova a
                correção de um problema que ela própria revelou — execute uma nova avaliação e volte aqui.
              </p>
            }

            <label class="fl">
              <span>Ou registre uma validação humana (referência obrigatória)</span>
              <input type="text" [(ngModel)]="humanEvidence" name="humanEvidence"
                     placeholder="chamado, ata, relatório do diretório…" />
            </label>
            <label class="fl">
              <span>Observação (opcional)</span>
              <input type="text" [(ngModel)]="humanNote" name="humanNote" />
            </label>
            <p class="hint">
              Uma validação humana fica registrada como <b>atestação</b>, com autor e data. Ela nunca é
              apresentada como correção técnica comprovada — o AEGIS não verificou o ambiente.
            </p>
            <button type="button" class="btn ghost" (click)="validateByHuman()" [disabled]="busy() || !humanEvidence.trim()">
              Registrar atestação humana
            </button>
          </details>
        } @else {
          <p class="hint warn">
            Seu papel permite <b>acompanhar</b> esta ação, não alterá-la. Avançar etapa, editar, registrar
            execução e validar exigem <b>Manager</b> ou <b>TenantAdmin</b>.
          </p>
        }

        <!-- ---------- Validações registradas ---------- -->
        @if (p.validations.length) {
          <div class="blk">
            <h4>Validações registradas</h4>
            @for (v of p.validations; track v.decidedAt) {
              <div class="val" [class.past]="!v.appliesToCurrentCycle">
                <span class="val-top">
                  <b>{{ outcomeLabel(v.outcome) }}</b>
                  <span class="mono">{{ methodLabel(v.method) }} · {{ v.decidedAt | date: 'dd/MM/yyyy HH:mm' }}
                    @if (v.decidedByName) { · {{ v.decidedByName }} }
                    @if (v.evidenceCollectedAt) { · coleta de {{ v.evidenceCollectedAt | date: 'dd/MM/yyyy HH:mm' }} }</span>
                </span>
                <span class="val-body">{{ v.rationale }}</span>
                <span class="val-basis">{{ basis(v) }}</span>
                <!-- Cada linha diz por si mesma ATÉ ONDE vale. Sem isso, a lista se leria como um conjunto de
                     comprovações vigentes — inclusive as de ciclos já encerrados. -->
                <span class="val-basis scope">{{ scope(v) }}</span>
              </div>
            }
          </div>
        }

        <!-- ---------- Trilha ---------- -->
        <div class="blk">
          <h4>Histórico</h4>
          @for (e of p.events; track e.at) {
            <div class="ev">
              <span class="mono">{{ e.at | date: 'dd/MM/yyyy HH:mm' }}</span>
              <span>{{ eventLabel(e.kind) }}@if (e.actorName) { · {{ e.actorName }} }</span>
              @if (e.note) { <span class="dim">{{ e.note }}</span> }
            </div>
          }
        </div>
      } @else if (!canManage()) {
        <p class="hint warn">
          Nenhuma ação para este achado. Criar uma exige <b>Manager</b> ou <b>TenantAdmin</b> — seu papel
          permite acompanhar o trabalho, não iniciá-lo.
        </p>
      } @else {
        <!-- ---------- Sem plano: formulário mínimo ---------- -->
        <p class="hint">
          Crie a ação para este achado. Nada além do que está abaixo é exigido para começar — sem cadastro de
          risco e sem processo de negócio.
        </p>
        <label class="fl">
          <span>Título</span>
          <input type="text" [(ngModel)]="newTitle" name="newTitle" maxlength="200" />
        </label>
        <label class="fl">
          <span>Ação proposta</span>
          <textarea rows="4" [(ngModel)]="newProposal" name="newProposal"></textarea>
        </label>
        <div class="grid2">
          <label class="fl">
            <span>Responsável</span>
            <input type="text" [(ngModel)]="newPerson" name="newPerson" maxlength="200" />
          </label>
          <label class="fl">
            <span>Área</span>
            <input type="text" [(ngModel)]="newArea" name="newArea" maxlength="200" />
          </label>
          <label class="fl">
            <span>Prazo</span>
            <input type="date" [(ngModel)]="newDue" name="newDue" />
          </label>
        </div>
        <button type="button" class="btn real" (click)="create()" [disabled]="busy() || !newTitle.trim()">
          Criar plano de ação
        </button>
      }
    </div>
  `,
  styles: [
    `
      .ap { display: flex; flex-direction: column; gap: 10px; }
      .kv { display: grid; grid-template-columns: 190px 1fr; gap: 12px; padding: 8px 0; border-top: 1px solid var(--line); }
      .kv .k, .row .k { font-family: var(--mono); font-size: 11px; color: var(--muted); text-transform: uppercase; letter-spacing: 0.06em; }
      .kv .v { font-size: 13px; line-height: 1.55; color: var(--text); display: flex; flex-direction: column; gap: 3px; }
      .kv .v.proven { color: #4fd39a; }
      .mono, .dim { font-family: var(--mono); font-size: 10.5px; color: var(--muted); }
      .chip { font-family: var(--mono); font-size: 10.5px; padding: 2px 8px; border-radius: 999px; border: 1px solid var(--line); align-self: flex-start; }
      .chip.late { color: #ff5c8a; border-color: rgba(255, 92, 138, 0.45); }
      .chip.demo { color: var(--amber); border-color: rgba(255, 176, 32, 0.45); }
      .row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; padding-top: 8px; border-top: 1px solid var(--line); }
      .btn { cursor: pointer; font-family: var(--mono); font-size: 12px; font-weight: 600; border-radius: 11px; padding: 8px 14px; border: 1px solid var(--line); color: var(--text); background: rgba(122, 145, 190, 0.08); align-self: flex-start; }
      .btn.sm { padding: 5px 10px; font-size: 11px; }
      .btn.real { color: var(--cyan); background: rgba(38, 224, 255, 0.08); border-color: rgba(38, 224, 255, 0.45); }
      .btn:disabled { opacity: 0.5; cursor: not-allowed; }
      .blk { border-top: 1px solid var(--line); padding-top: 10px; display: flex; flex-direction: column; gap: 8px; }
      .blk h4 { margin: 0; font-family: var(--mono); font-size: 11px; text-transform: uppercase; letter-spacing: 0.08em; color: var(--muted); }
      details > summary { cursor: pointer; font-family: var(--mono); font-size: 12px; color: var(--cyan); list-style: none; }
      .fl { display: flex; flex-direction: column; gap: 4px; }
      .fl > span { font-family: var(--mono); font-size: 10.5px; color: var(--muted); text-transform: uppercase; letter-spacing: 0.06em; }
      .fl input, .fl textarea { background: rgba(122, 145, 190, 0.06); border: 1px solid var(--line); border-radius: 9px; padding: 8px 12px; color: var(--text); font-family: var(--sans); font-size: 13px; width: 100%; box-sizing: border-box; }
      .grid2 { display: grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 10px; }
      .hint { margin: 0; font-size: 12.5px; line-height: 1.6; color: var(--muted); }
      .hint.warn { border-left: 2px solid var(--amber); padding: 6px 10px; background: rgba(255, 176, 32, 0.05); border-radius: 0 8px 8px 0; }
      .hint b { color: var(--text); }
      .val { display: flex; flex-direction: column; gap: 3px; padding: 8px 0; border-bottom: 1px solid rgba(122, 145, 190, 0.12); }
      .val.past { opacity: 0.72; border-left: 2px solid rgba(122, 145, 190, 0.35); padding-left: 10px; }
      .val-top { display: flex; gap: 10px; align-items: baseline; flex-wrap: wrap; font-size: 13px; }
      .val-body { font-size: 12.5px; color: var(--muted); line-height: 1.5; }
      .val-basis { font-family: var(--mono); font-size: 10.5px; color: var(--muted); }
      .val-basis.scope { color: var(--amber); }
      .ev { display: flex; gap: 10px; flex-wrap: wrap; font-size: 12px; color: var(--text); padding: 3px 0; }
      .state.err, .state.note { display: flex; gap: 12px; align-items: center; flex-wrap: wrap; }
      .state.err b { color: #ff5c8a; font-size: 12.5px; }
      .state.note b { color: var(--amber); font-size: 12.5px; }
      .pulse { font-family: var(--mono); font-size: 12px; color: var(--muted); margin: 0; }
    `,
  ],
})
export class KnightActionPlanComponent {
  private readonly api = inject(RemediationService);
  private readonly auth = inject(AuthService);

  /** Achado ao qual a ação pertence. */
  readonly indicatorId = input.required<string>();
  /** Quantidade afetada no achado — semeia o texto da proposta. */
  readonly affectedCount = input.required<number>();
  /** Avaliação que ORIGINOU o achado (a exibida na tela quando o plano é criado). */
  readonly originRunId = input.required<string>();
  /** Avaliação ABERTA agora — candidata a evidência de validação quando não for a própria origem. */
  readonly currentRunId = input.required<string>();
  /**
   * A ação EM FOCO. Normalmente é a ação ativa do achado, mas pode ser uma ação ENCERRADA apontada por um
   * link — e nesse caso é ela que precisa aparecer, com a própria execução e o próprio histórico, mesmo
   * havendo outro ciclo ativo para o mesmo indicador.
   */
  readonly existing = input<ActionPlan | null>(null);

  /** Emite sempre que o plano muda, para a página recarregar o mapa de ações. */
  readonly changed = output<ActionPlan>();

  protected readonly statusLabel = actionStatusLabel;
  protected readonly outcomeLabel = outcomeLabel;
  protected readonly methodLabel = methodLabel;
  protected readonly eventLabel = eventLabel;
  protected readonly situation = actionSituation;
  protected readonly result = actionResult;
  protected readonly basis = validationBasis;
  protected readonly scope = validationScope;
  protected readonly origin = originLabel;

  private readonly local = signal<ActionPlan | null>(null);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  /** Aviso que NÃO é falha: conflito resolvido, estado relido, ação existente aberta. */
  readonly notice = signal<string | null>(null);
  readonly execOpen = signal(false);
  readonly valOpen = signal(false);
  readonly editOpen = signal(false);

  /** `true` depois que o painel é fechado — a partir daí nada é pintado, mas nada é desfeito. */
  private destroyed = false;

  /**
   * O plano exibido: a resposta da última escrita, quando ela pertence ao contexto ATUAL; senão, o que a
   * página passou. A checagem existe porque este componente é REUTILIZADO ao trocar de achado — sem ela, o
   * resultado de uma escrita anterior continuaria na tela ao lado do achado errado.
   */
  readonly plan = computed<ActionPlan | null>(() => {
    const alvo = this.existing();
    const l = this.local();
    if (!l) return alvo;
    if (l.knightIndicatorId !== this.indicatorId()) return alvo;
    if (alvo && alvo.id !== l.id) return alvo;
    return l;
  });

  /**
   * Gate de APRESENTAÇÃO dos controles de escrita. Esconder um botão não protege nada — o servidor continua
   * sendo a autoridade — mas oferecer um controle que responderia 403 é enganar quem clica.
   */
  readonly canManage = computed(() => canManageActionPlans(this.auth.activeRole()));

  /** O selo de comprovação olha para a validação do CICLO ATUAL, nunca para a mais recente. */
  readonly proven = computed(() => isTechnicallyProven(this.plan()?.applicableValidation ?? null));

  /** Etapas oferecidas: exatamente as que o servidor já decidiu serem alcançáveis. */
  readonly transitions = computed<ActionPlanStatus[]>(() => this.plan()?.allowedTransitions ?? []);

  /**
   * A avaliação aberta só serve de evidência quando NÃO é a que originou o plano. Bloquear aqui evita o
   * pedido inútil — e o servidor recusa de novo, porque a regra não pode viver só na tela.
   */
  readonly canValidateWithRun = computed(() => {
    const p = this.plan();
    return !!p && this.currentRunId() !== p.originRunId;
  });

  // Campos do formulário de criação (semeados na primeira leitura do achado).
  protected newTitle = '';
  protected newProposal = '';
  protected newPerson = '';
  protected newArea = '';
  protected newDue = '';

  // Campos de execução e validação.
  protected execNotes = '';
  protected execEvidence = '';
  protected humanEvidence = '';
  protected humanNote = '';

  // Campos de edição — semeados do plano ao abrir o bloco, para nunca enviar um valor obsoleto.
  protected editTitle = '';
  protected editProposal = '';
  protected editPerson = '';
  protected editArea = '';
  protected editDue = '';

  /** Achado para o qual o formulário de criação já foi semeado. */
  private seededFor: string | null = null;

  constructor() {
    // Semeia o formulário de criação a cada achado SEM plano. O texto é uma SUGESTÃO determinística,
    // editável — quem escreve a ação é a pessoa responsável.
    effect(() => {
      const id = this.indicatorId();
      const temPlano = !!this.plan();
      if (temPlano || this.seededFor === id) return;
      this.seededFor = id;
      this.newTitle = this.defaultTitle();
      this.newProposal = seededProposal(id, this.affectedCount());
      this.newPerson = '';
      this.newArea = '';
      this.newDue = '';
    });

    // Fechar o painel NÃO cancela uma escrita já enviada: o servidor já a recebeu, e tratá-la como desfeita
    // seria mentir sobre o que aconteceu. O que se perde é apenas a pintura — a página relê o estado ao
    // reabrir o achado.
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
    });
  }

  private defaultTitle(): string {
    switch (this.indicatorId()) {
      case 'AK-ENTRA-001':
        return 'Registrar segundo fator nas contas administrativas';
      case 'AK-ENTRA-002':
        return 'Revisar os papéis administrativos do diretório';
      case 'AK-ENTRA-004':
        return 'Confirmar a necessidade dos acessos de convidado';
      default:
        return `Tratar o achado ${this.indicatorId()}`;
    }
  }

  /** Os 8 primeiros dígitos identificam a avaliação sem transformar a linha num identificador ilegível. */
  protected shortRun(id: string | null): string {
    return id ? id.slice(0, 8) : 'não registrada';
  }

  /** A ação foi reaberta quando o ciclo vigente começou depois da criação. */
  protected reopened(p: ActionPlan): boolean {
    return Date.parse(p.cycleStartedAt) - Date.parse(p.createdAt) > 1000;
  }

  toggleEdit(): void {
    const abrir = !this.editOpen();
    if (abrir) this.seedEdit();
    this.editOpen.set(abrir);
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
      this.api.create({
        runId: this.originRunId(),
        indicatorId: this.indicatorId(),
        title: this.newTitle.trim(),
        proposedAction: this.newProposal.trim() || null,
        responsiblePerson: this.newPerson.trim() || null,
        responsibleArea: this.newArea.trim() || null,
        dueDate: this.newDue || null,
      }),
    );
  }

  changeStatus(status: ActionPlanStatus): void {
    const p = this.plan();
    if (!p) return;
    this.run(this.api.update(p.id, { expectedVersion: p.version, status }));
  }

  /**
   * Edição simples — a MESMA ação muda de título, proposta, responsável, área ou prazo. Campo em branco é
   * omitido do pedido: o contrato do servidor trata ausência como "não mexer", e enviar vazio apagaria um
   * dado que a pessoa não pediu para apagar.
   */
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
      () => {
        this.execNotes = '';
        this.execEvidence = '';
        this.execOpen.set(false);
      },
    );
  }

  validateWithRun(): void {
    const p = this.plan();
    if (!p) return;
    this.run(
      this.api.validate(p.id, {
        expectedVersion: p.version,
        validationRunId: this.currentRunId(),
        evidenceReference: null,
        note: null,
      }),
    );
  }

  validateByHuman(): void {
    const p = this.plan();
    if (!p) return;
    this.run(
      this.api.validate(p.id, {
        expectedVersion: p.version,
        validationRunId: null,
        evidenceReference: this.humanEvidence.trim(),
        note: this.humanNote.trim() || null,
      }),
      () => {
        this.humanEvidence = '';
        this.humanNote = '';
      },
    );
  }

  /**
   * O CONTEXTO ao qual uma resposta pertence: achado aberto × ação em foco. Uma resposta que chega depois de
   * o contexto ter mudado não pinta nada — mas continua avisando a página, porque a escrita ACONTECEU.
   */
  private contextKey(): string {
    return `${this.indicatorId()} ${this.plan()?.id ?? ''}`;
  }

  /** Executa uma escrita e adota a resposta como estado local, se ainda for deste contexto. */
  private run(call: ReturnType<RemediationService['create']>, onDone?: () => void): void {
    const chave = this.contextKey();
    this.busy.set(true);
    this.error.set(null);
    this.notice.set(null);
    call.subscribe({
      next: (p) => {
        if (this.destroyed) return;
        this.busy.set(false);
        // A página relê a lista de qualquer forma: a gravação já ocorreu, e ignorá-la aqui deixaria a fila
        // exibindo um estado que o servidor já não tem.
        this.changed.emit(p);
        if (chave !== this.contextKey()) return;
        this.local.set(p);
        onDone?.();
      },
      error: (e: Error) => this.fail(e, chave),
    });
  }

  /**
   * Um 409 tem duas leituras, e confundi-las seria grave:
   *
   *   • DUPLICIDADE — já existe ação ativa para a mesma origem. Não é falha: abre-se a ação existente, que é
   *     exatamente o que o segundo clique deveria ter aberto;
   *   • CONCORRÊNCIA — a ação mudou entre a leitura desta tela e a escrita. A escrita foi recusada, e o que
   *     resolve é RELER o estado atual, não repetir a mutação às cegas.
   */
  private fail(e: Error, chave: string): void {
    if (this.destroyed) return;
    if (e instanceof ActionPlanConflictError && e.existingActionPlanId) {
      this.reread(
        e.existingActionPlanId,
        chave,
        'Já existia uma ação ativa para este achado — ela foi aberta acima.',
      );
      return;
    }
    if (e instanceof ActionPlanConflictError) {
      const id = this.plan()?.id;
      if (id) {
        this.reread(
          id,
          chave,
          `${e.message} O painel foi relido com o estado atual — confira e envie de novo se ainda fizer sentido.`,
        );
        return;
      }
    }
    this.busy.set(false);
    if (chave === this.contextKey()) this.error.set(e.message);
  }

  /** Relê UMA ação e adota o estado devolvido. Nunca repete a mutação recusada. */
  private reread(id: string, chave: string, aviso: string): void {
    this.api.get(id).subscribe({
      next: (p) => {
        if (this.destroyed) return;
        this.busy.set(false);
        this.changed.emit(p);
        if (chave !== this.contextKey()) return;
        this.local.set(p);
        this.notice.set(aviso);
        this.seedEdit();
      },
      error: (err: Error) => {
        if (this.destroyed) return;
        this.busy.set(false);
        if (chave === this.contextKey()) this.error.set(err.message);
      },
    });
  }
}

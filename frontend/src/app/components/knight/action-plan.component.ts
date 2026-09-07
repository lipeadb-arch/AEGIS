import { DatePipe } from '@angular/common';
import { Component, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ActionPlan,
  ActionPlanStatus,
  actionResult,
  actionSituation,
  actionStatusLabel,
  allowedTransitions,
  eventLabel,
  isTechnicallyProven,
  methodLabel,
  outcomeLabel,
  seededProposal,
  validationBasis,
} from '../../models/remediation.models';
import { ActionPlanConflictError, RemediationService } from '../../services/remediation.service';

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
      @if (error(); as e) {
        <div class="state err inline">
          <b>{{ e }}</b>
          <button type="button" class="btn ghost" (click)="error.set(null)">Fechar</button>
        </div>
      }

      @if (plan(); as p) {
        <!-- ---------- Plano existente ---------- -->
        <div class="kv"><span class="k">Ação</span><span class="v">{{ p.title }}</span></div>
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
              falta da capacidade que este achado consome produzem <b>evidência insuficiente</b>, nunca melhora.
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

        <!-- ---------- Validações registradas ---------- -->
        @if (p.validations.length) {
          <div class="blk">
            <h4>Validações registradas</h4>
            @for (v of p.validations; track v.decidedAt) {
              <div class="val">
                <span class="val-top">
                  <b>{{ outcomeLabel(v.outcome) }}</b>
                  <span class="mono">{{ methodLabel(v.method) }} · {{ v.decidedAt | date: 'dd/MM/yyyy HH:mm' }}
                    @if (v.decidedByName) { · {{ v.decidedByName }} }</span>
                </span>
                <span class="val-body">{{ v.rationale }}</span>
                <span class="val-basis">{{ basis(v) }}</span>
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
      .val-top { display: flex; gap: 10px; align-items: baseline; flex-wrap: wrap; font-size: 13px; }
      .val-body { font-size: 12.5px; color: var(--muted); line-height: 1.5; }
      .val-basis { font-family: var(--mono); font-size: 10.5px; color: var(--muted); }
      .ev { display: flex; gap: 10px; flex-wrap: wrap; font-size: 12px; color: var(--text); padding: 3px 0; }
      .state.err { display: flex; gap: 12px; align-items: center; flex-wrap: wrap; }
      .state.err b { color: #ff5c8a; font-size: 12.5px; }
      .pulse { font-family: var(--mono); font-size: 12px; color: var(--muted); margin: 0; }
    `,
  ],
})
export class KnightActionPlanComponent {
  private readonly api = inject(RemediationService);

  /** Achado ao qual a ação pertence. */
  readonly indicatorId = input.required<string>();
  /** Quantidade afetada no achado — semeia o texto da proposta. */
  readonly affectedCount = input.required<number>();
  /** Avaliação que ORIGINOU o achado (a exibida na tela quando o plano é criado). */
  readonly originRunId = input.required<string>();
  /** Avaliação ABERTA agora — candidata a evidência de validação quando não for a própria origem. */
  readonly currentRunId = input.required<string>();
  /** Plano já existente para este achado, quando houver. */
  readonly existing = input<ActionPlan | null>(null);

  /** Emite sempre que o plano muda, para a página recarregar o mapa de ações ativas. */
  readonly changed = output<ActionPlan>();

  protected readonly statusLabel = actionStatusLabel;
  protected readonly outcomeLabel = outcomeLabel;
  protected readonly methodLabel = methodLabel;
  protected readonly eventLabel = eventLabel;
  protected readonly situation = actionSituation;
  protected readonly result = actionResult;
  protected readonly basis = validationBasis;

  private readonly local = signal<ActionPlan | null>(null);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly execOpen = signal(false);
  readonly valOpen = signal(false);

  /** O plano exibido: o carregado localmente após uma escrita, senão o que a página passou. */
  readonly plan = computed<ActionPlan | null>(() => this.local() ?? this.existing());

  readonly proven = computed(() => isTechnicallyProven(this.plan()?.latestValidation ?? null));
  readonly transitions = computed<ActionPlanStatus[]>(() => {
    const p = this.plan();
    return p ? allowedTransitions(p.status) : [];
  });

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

  constructor() {
    // Semeia o formulário na primeira renderização de um achado sem plano. O texto é uma SUGESTÃO
    // determinística, editável — quem escreve a ação é a pessoa responsável.
    queueMicrotask(() => {
      if (!this.plan()) {
        this.newTitle = this.defaultTitle();
        this.newProposal = seededProposal(this.indicatorId(), this.affectedCount());
      }
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
   * Executa uma escrita e adota a resposta como estado local. Um 409 de DUPLICIDADE não é falha: carrega a
   * ação existente, que é exatamente o que o segundo clique deveria ter aberto.
   */
  private run(call: ReturnType<RemediationService['create']>, onDone?: () => void): void {
    this.busy.set(true);
    this.error.set(null);
    call.subscribe({
      next: (p) => {
        this.local.set(p);
        this.busy.set(false);
        onDone?.();
        this.changed.emit(p);
      },
      error: (e: Error) => {
        if (e instanceof ActionPlanConflictError && e.existingActionPlanId) {
          this.api.get(e.existingActionPlanId).subscribe({
            next: (p) => {
              this.local.set(p);
              this.busy.set(false);
              this.error.set('Já existia uma ação ativa para este achado — ela foi aberta acima.');
              this.changed.emit(p);
            },
            error: () => {
              this.busy.set(false);
              this.error.set(e.message);
            },
          });
          return;
        }
        this.busy.set(false);
        this.error.set(e.message);
      },
    });
  }
}

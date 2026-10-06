import { DatePipe } from '@angular/common';
import { Component, DestroyRef, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Observable, Subscription } from 'rxjs';
import {
  NIST_PRIORITIES,
  NIST_SEVERITIES,
  NistAssignee,
  NistEvidence,
  NistFinding,
  NistFindingDraft,
  NistOwnerMode,
  NistPlan,
  NistPlanStatus,
  NistSubcategoryDetail,
  dateBr,
  findingDraftFrom,
  findingProblem,
  findingStatusLabel,
  levelLabel,
  planStatusLabel,
  planTransitionLabel,
  priorityLabel,
  responsibleRequest,
  severityBadgeClass,
  severityLabel,
  toFindingRequest,
} from '../../models/nist.models';
import { NistApiError, NistCtx, NistService } from '../../services/nist.service';

interface PlanForm {
  mode: 'execution' | 'validation' | 'edit' | 'create' | 'status';
  notes: string;
  evidence: string;
  title: string;
  action: string;
  ownerMode: NistOwnerMode;
  ownerUserId: string;
  ownerName: string;
  ownerContact: string;
  area: string;
  due: string;
  status: string;
}

const blankPlanForm = (mode: PlanForm['mode']): PlanForm => ({
  mode, notes: '', evidence: '', title: '', action: '', ownerMode: 'none', ownerUserId: '', ownerName: '', ownerContact: '', area: '', due: '', status: '',
});

/**
 * [AEGIS-NIST-JOURNEY-02] Achados de uma subcategoria e o tratamento de cada um. O achado é DECISÃO do analista sobre uma
 * lacuna documentada (lacuna observada ou procedimento insatisfatório), com risco, impacto, severidade e prioridade
 * justificados — nunca nasce sozinho de atual × alvo. O plano é o mesmo mecanismo de planos do produto, com origem NIST:
 * etapas, execução, validação humana com evidência e reabertura. Concluir o plano não muda a maturidade.
 */
@Component({
  selector: 'app-nist-findings-work',
  standalone: true,
  imports: [FormsModule, DatePipe],
  template: `
    <section class="panel" aria-labelledby="fw-h" id="achados">
      <div class="hd"><h3 id="fw-h">Achados e tratamento</h3><span class="hint">{{ findings().length }} registrado(s) nesta rodada</span></div>

      @if (blockedReason(); as br) {
        <p class="notice">{{ br }}</p>
      } @else if (canEdit()) {
        @if (!draft()) {
          <button type="button" class="ghost sm" (click)="startFinding()">Registrar achado</button>
        } @else {
          @let f = draft()!;
          <form class="grid" (ngSubmit)="createFinding(f)" aria-label="Novo achado">
            <label class="field wide"><span class="field-label">Problema (título do achado)</span><input name="ft" maxlength="200" [(ngModel)]="f.title" /></label>
            <label class="field wide"><span class="field-label">Condição observada</span><textarea name="fc" rows="3" maxlength="4000" [(ngModel)]="f.condition"></textarea></label>
            <label class="field"><span class="field-label">Risco fundamentado</span><textarea name="fr" rows="3" maxlength="2000" [(ngModel)]="f.risk"></textarea></label>
            <label class="field"><span class="field-label">Impacto</span><textarea name="fi" rows="3" maxlength="2000" [(ngModel)]="f.impact"></textarea></label>
            <label class="field"><span class="field-label">Severidade</span>
              <select name="fs" [(ngModel)]="f.severity"><option value="">Escolha…</option>@for (s of severities; track s) { <option [value]="s">{{ severityLabel(s) }}</option> }</select></label>
            <label class="field"><span class="field-label">Por que esta severidade</span><input name="fsr" maxlength="2000" [(ngModel)]="f.severityRationale" /></label>
            <label class="field"><span class="field-label">Prioridade</span>
              <select name="fp" [(ngModel)]="f.priority"><option value="">Escolha…</option>@for (p of priorities; track p) { <option [value]="p">{{ priorityLabel(p) }}</option> }</select></label>
            <label class="field"><span class="field-label">Por que esta prioridade</span><input name="fpr" maxlength="2000" [(ngModel)]="f.priorityRationale" /></label>
            <label class="field wide"><span class="field-label">Recomendação</span><textarea name="frec" rows="2" maxlength="4000" [(ngModel)]="f.recommendation"></textarea></label>
            @if (evidence().length) {
              <fieldset class="field wide ev"><legend class="field-label">Evidências citadas</legend>
                @for (e of evidence(); track e.id) { <label><input type="checkbox" [checked]="f.evidenceIds.includes(e.id)" (change)="toggle(f.evidenceIds, e.id)" /> {{ e.title }}</label> }
              </fieldset>
            }
            <label class="check wide"><input type="checkbox" name="fwp" [(ngModel)]="f.withPlan" /> Criar o plano de tratamento junto</label>
            @if (f.withPlan) {
              <label class="field"><span class="field-label">Título do plano</span><input name="pt" maxlength="200" [(ngModel)]="f.planTitle" /></label>
              <label class="field"><span class="field-label">Prazo</span><input type="date" name="pd" [(ngModel)]="f.planDue" /></label>
              <label class="field wide"><span class="field-label">Ação proposta</span><textarea name="pa" rows="2" maxlength="4000" [(ngModel)]="f.planAction"></textarea></label>
              <label class="field"><span class="field-label">Responsável pelo tratamento</span>
                <select name="pom" [(ngModel)]="f.planOwnerMode">
                  <option value="none">Sem responsável ainda</option><option value="user">Usuário do tenant</option>
                  <option value="external">Contato externo</option><option value="text">Texto livre</option></select></label>
              @if (f.planOwnerMode === 'user') {
                <label class="field"><span class="field-label">Usuário</span>
                  <select name="pou" [(ngModel)]="f.planOwnerUserId"><option value="">Escolha…</option>@for (a of assignees(); track a.userId) { <option [value]="a.userId">{{ a.displayName }}</option> }</select></label>
              } @else if (f.planOwnerMode !== 'none') {
                <label class="field"><span class="field-label">Nome</span><input name="pon" maxlength="200" [(ngModel)]="f.planOwnerName" /></label>
                @if (f.planOwnerMode === 'external') { <label class="field"><span class="field-label">Contato</span><input name="poc" maxlength="200" [(ngModel)]="f.planOwnerContact" /></label> }
              }
              <label class="field"><span class="field-label">Área</span><input name="par" maxlength="200" [(ngModel)]="f.planArea" /></label>
            }
            <div class="actions wide">
              <button type="submit" class="primary sm" [disabled]="busy() || !!findingProblem(f)">Registrar achado</button>
              <button type="button" class="ghost sm" (click)="draft.set(null)">Cancelar</button>
              @if (findingProblem(f); as pb) { <span class="muted">{{ pb }}</span> }
            </div>
          </form>
        }
      }
      @if (error(); as e) {
        <div class="notice error" role="alert">{{ e }} @if (conflict()) { <button type="button" class="ghost xs" (click)="changed.emit()">Recarregar</button> }</div>
      }

      @for (fd of findings(); track fd.id) {
        <details class="finding" [id]="'achado-' + fd.id" [open]="fd.id === openId()">
          <summary>
            <span [class]="'badge ' + severityBadgeClass(fd.severity)">{{ severityLabel(fd.severity) }}</span>
            <strong>{{ fd.title }}</strong>
            <span class="muted">prioridade {{ priorityLabel(fd.priority).toLowerCase() }} · achado {{ findingStatusLabel(fd.status).toLowerCase() }} · tratamento: {{ fd.treatmentLabel }}</span>
          </summary>
          <dl class="kv">
            <dt>Condição</dt><dd>{{ fd.condition }}</dd>
            <dt>Risco</dt><dd>{{ fd.risk }}</dd>
            <dt>Impacto</dt><dd>{{ fd.impact }}</dd>
            <dt>Severidade</dt><dd>{{ severityLabel(fd.severity) }} — {{ fd.severityRationale }}</dd>
            <dt>Prioridade</dt><dd>{{ priorityLabel(fd.priority) }} — {{ fd.priorityRationale }}</dd>
            <dt>Recomendação</dt><dd>{{ fd.recommendation }}</dd>
            @if (fd.evidenceIds.length) { <dt>Evidências</dt><dd>{{ titles(fd.evidenceIds) }}</dd> }
            @if (fd.origin; as o) {
              <dt>Origem registrada</dt>
              <dd>{{ o.cycleName }} · {{ o.scopeName }} · avaliação v{{ o.evaluationVersion }} · atual {{ levelLabel(o.currentLevel) }} · alvo {{ levelLabel(o.targetLevel) }}
                @if (o.gapsText) { · lacuna documentada: “{{ o.gapsText }}” }
                @for (pf of o.procedureFindings; track pf) { · {{ pf }} }</dd>
            }
            <dt>Registro</dt><dd>{{ fd.createdByName ?? '—' }} em {{ fd.createdAt | date: 'dd/MM/yyyy HH:mm' }}
              @if (fd.statusNote) { · {{ findingStatusLabel(fd.status) }}: {{ fd.statusNote }} ({{ fd.statusChangedByName ?? '—' }}) }</dd>
          </dl>
          @if (canWrite()) {
            <div class="actions">
              @if (fd.status === 'Open') {
                <button type="button" class="ghost xs" (click)="openForm(fd.id, 'status', 'RiskAccepted')">Aceitar o risco</button>
                <button type="button" class="ghost xs" (click)="openForm(fd.id, 'status', 'Closed')">Encerrar o achado</button>
              } @else {
                <button type="button" class="ghost xs" (click)="openForm(fd.id, 'status', 'Open')">Reabrir o achado</button>
              }
            </div>
          }

          <div class="plan">
            <h4>Plano de tratamento</h4>
            @if (fd.plan; as pl) {
              <p><strong>{{ pl.title }}</strong> · <span class="badge info">{{ planStatusLabel(pl.status) }}</span>
                @if (pl.isOverdue) { <span class="badge bad">prazo vencido</span> }
                @if (pl.wasReopened) { <span class="badge warn">reaberto</span> }</p>
              <p class="muted">Responsável: {{ pl.responsiblePerson ?? '—' }}@if (pl.responsibleIsExternal) { (externo{{ pl.responsibleContact ? ' · ' + pl.responsibleContact : '' }}) }
                @if (pl.responsibleArea) { · {{ pl.responsibleArea }} } · prazo {{ dateBr(pl.dueDate) }}</p>
              @if (pl.proposedAction) { <p class="pre">{{ pl.proposedAction }}</p> }
              <p><span class="lbl">Próximo passo:</span> {{ pl.nextStep }}</p>
              @if (pl.executionNotes) { <p><span class="lbl">Execução relatada:</span> {{ pl.executionNotes }} @if (pl.executionEvidenceRef) { ({{ pl.executionEvidenceRef }}) }</p> }
              @if (pl.applicableValidation; as v) {
                <p><span class="lbl">Validação:</span> {{ v.outcomeLabel }} — {{ v.decidedByName }} em {{ v.decidedAt | date: 'dd/MM/yyyy' }}@if (v.evidenceReference) { · {{ v.evidenceReference }} }</p>
              } @else if (pl.latestValidation) {
                <p class="muted">Validação anterior à reabertura: {{ pl.latestValidation.outcomeLabel }} ({{ pl.latestValidation.decidedAt | date: 'dd/MM/yyyy' }}) — não vale para o ciclo atual.</p>
              }
              @if (pl.closureBlockedReason) { <p class="muted">{{ pl.closureBlockedReason }}</p> }
              <p class="hint">Concluir o plano não altera a maturidade, o score nem a conformidade: a reavaliação da subcategoria é um ato separado.</p>
              @if (canWrite()) {
                <div class="actions">
                  @for (t of pl.allowedTransitions; track t) {
                    <button type="button" class="ghost xs" (click)="transition(fd, pl, t)" [disabled]="busy()">{{ planTransitionLabel(pl.status, t) }}</button>
                  }
                  @if (pl.status === 'EmAndamento' || pl.status === 'Aberto') { <button type="button" class="ghost xs" (click)="openForm(fd.id, 'execution')">Registrar execução</button> }
                  @if (pl.status === 'AguardandoValidacao') { <button type="button" class="ghost xs" (click)="openForm(fd.id, 'validation')">Validar com evidência</button> }
                  <button type="button" class="ghost xs" (click)="openForm(fd.id, 'edit', undefined, pl)">Editar responsável e prazo</button>
                </div>
              }
              <details class="events"><summary>Trilha do plano ({{ pl.events.length }})</summary>
                <ul>@for (ev of pl.events; track $index) {
                  <li>{{ ev.at | date: 'dd/MM/yyyy HH:mm' }} · {{ ev.actorName || '—' }} · {{ ev.kind }}@if (ev.fromStatus || ev.toStatus) { : {{ planStatusLabel(ev.fromStatus) }} → {{ planStatusLabel(ev.toStatus) }} }@if (ev.note) { — {{ ev.note }} }</li>
                }</ul></details>
            } @else {
              <p class="muted">Sem plano de tratamento.</p>
              @if (canWrite() && fd.status === 'Open') { <button type="button" class="ghost xs" (click)="openForm(fd.id, 'create')">Criar plano de tratamento</button> }
            }
          </div>

          @if (forms()[fd.id]; as pf) {
            <form class="grid sub" (ngSubmit)="submitForm(fd, pf)" [attr.aria-label]="formTitle(pf)">
              <p class="wide"><strong>{{ formTitle(pf) }}</strong></p>
              @switch (pf.mode) {
                @case ('status') {
                  <label class="field wide"><span class="field-label">Justificativa ({{ findingStatusLabel(pf.status) }})</span><textarea name="sn" rows="2" maxlength="2000" [(ngModel)]="pf.notes"></textarea></label>
                }
                @case ('execution') {
                  <label class="field wide"><span class="field-label">O que foi feito</span><textarea name="en" rows="2" maxlength="4000" [(ngModel)]="pf.notes"></textarea></label>
                  <label class="field"><span class="field-label">Referência de evidência (chamado, documento)</span><input name="ee" maxlength="500" [(ngModel)]="pf.evidence" /></label>
                }
                @case ('validation') {
                  <label class="field"><span class="field-label">Evidência que comprova a correção</span><input name="ve" maxlength="500" [(ngModel)]="pf.evidence" /></label>
                  <label class="field wide"><span class="field-label">Observação</span><textarea name="vn" rows="2" maxlength="2000" [(ngModel)]="pf.notes"></textarea></label>
                  <p class="hint wide">Validação humana: registra quem atestou e com qual evidência. Não é coleta automática.</p>
                }
                @default {
                  @if (pf.mode === 'create') {
                    <label class="field"><span class="field-label">Título do plano</span><input name="pt" maxlength="200" [(ngModel)]="pf.title" /></label>
                    <label class="field wide"><span class="field-label">Ação proposta</span><textarea name="pa" rows="2" maxlength="4000" [(ngModel)]="pf.action"></textarea></label>
                  }
                  <label class="field"><span class="field-label">Responsável</span>
                    <select name="om" [(ngModel)]="pf.ownerMode"><option value="none">Manter / sem responsável</option><option value="user">Usuário do tenant</option>
                      <option value="external">Contato externo</option><option value="text">Texto livre</option></select></label>
                  @if (pf.ownerMode === 'user') {
                    <label class="field"><span class="field-label">Usuário</span>
                      <select name="ou" [(ngModel)]="pf.ownerUserId"><option value="">Escolha…</option>@for (a of assignees(); track a.userId) { <option [value]="a.userId">{{ a.displayName }}</option> }</select></label>
                  } @else if (pf.ownerMode !== 'none') {
                    <label class="field"><span class="field-label">Nome</span><input name="on" maxlength="200" [(ngModel)]="pf.ownerName" /></label>
                    @if (pf.ownerMode === 'external') { <label class="field"><span class="field-label">Contato</span><input name="oc" maxlength="200" [(ngModel)]="pf.ownerContact" /></label> }
                  }
                  <label class="field"><span class="field-label">Área</span><input name="ar" maxlength="200" [(ngModel)]="pf.area" /></label>
                  <label class="field"><span class="field-label">Prazo</span><input type="date" name="du" [(ngModel)]="pf.due" /></label>
                }
              }
              <div class="actions wide">
                <button type="submit" class="primary sm" [disabled]="busy() || !!formProblem(pf)">Gravar</button>
                <button type="button" class="ghost sm" (click)="closeForm(fd.id)">Cancelar</button>
                @if (formProblem(pf); as pb) { <span class="muted">{{ pb }}</span> }
              </div>
            </form>
          }
        </details>
      }
    </section>
  `,
  styles: [
    `
      .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 220px), 1fr)); gap: var(--sp-3); align-items: end; margin-top: var(--sp-2); }
      .grid.sub { padding-top: var(--sp-3); border-top: 1px dashed var(--line-2); }
      .wide { grid-column: 1 / -1; }
      .ev { border: 0; padding: 0; margin: 0; display: flex; flex-direction: column; gap: 4px; font-size: var(--fs-sm); }
      .check { display: flex; gap: 6px; align-items: center; font-size: var(--fs-sm); }
      .actions { display: flex; flex-wrap: wrap; gap: var(--sp-2); align-items: center; margin: var(--sp-2) 0; }
      .finding { border-top: 1px solid var(--line-2); padding: var(--sp-3) 0; }
      .finding summary { cursor: pointer; display: flex; flex-wrap: wrap; gap: 6px var(--sp-2); align-items: baseline; }
      .kv { display: grid; grid-template-columns: minmax(110px, 160px) 1fr; gap: 4px var(--sp-3); margin: var(--sp-3) 0; }
      .kv dt { color: var(--muted); font-weight: 600; }
      .kv dd { margin: 0; white-space: pre-line; overflow-wrap: anywhere; }
      .plan p { margin: 0 0 4px; overflow-wrap: anywhere; }
      .pre { white-space: pre-line; }
      .lbl { color: var(--muted); font-weight: 600; }
      h4 { margin: var(--sp-3) 0 var(--sp-2); }
      .events ul { margin: 4px 0; padding-left: 18px; font-size: var(--fs-meta); }
      @media (max-width: 520px) { .kv { grid-template-columns: 1fr; } }
    `,
  ],
})
export class NistFindingsWorkComponent {
  private readonly nist = inject(NistService);

  readonly ctx = input.required<NistCtx>();
  readonly detail = input.required<NistSubcategoryDetail>();
  readonly assignees = input<NistAssignee[]>([]);
  /** Pode registrar achado (papel de escrita E rodada aberta). */
  readonly canEdit = input(false);
  /** Pode agir sobre achados e planos já registrados (papel de escrita; planos continuam após o encerramento da rodada). */
  readonly canWrite = input(false);
  readonly openId = input<string | null>(null);
  readonly changed = output<void>();

  protected readonly severities = NIST_SEVERITIES;
  protected readonly priorities = NIST_PRIORITIES;
  protected readonly severityLabel = severityLabel;
  protected readonly severityBadgeClass = severityBadgeClass;
  protected readonly priorityLabel = priorityLabel;
  protected readonly findingStatusLabel = findingStatusLabel;
  protected readonly planStatusLabel = planStatusLabel;
  protected readonly planTransitionLabel = planTransitionLabel;
  protected readonly levelLabel = levelLabel;
  protected readonly dateBr = dateBr;
  protected readonly findingProblem = findingProblem;

  protected readonly findings = computed(() => this.detail().findings ?? []);
  protected readonly evidence = computed<NistEvidence[]>(() => this.detail().evidence);
  protected readonly blockedReason = computed(() => (this.canEdit() ? this.detail().findingBlockedReason ?? null : null));
  protected readonly draft = signal<NistFindingDraft | null>(null);
  protected readonly forms = signal<Record<string, PlanForm>>({});
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly conflict = signal(false);

  private gen = 0;
  private sub: Subscription | null = null;
  private readonly key = computed(() => `${this.ctx().assessmentId}|${this.ctx().cycleId}|${this.ctx().scopeId}|${this.detail().code}`);

  constructor() {
    inject(DestroyRef).onDestroy(() => this.reset());
    effect(() => {
      this.key();
      untracked(() => this.reset());
    });
  }

  protected toggle(list: string[], id: string): void {
    const i = list.indexOf(id);
    if (i >= 0) list.splice(i, 1);
    else list.push(id);
  }

  protected titles(ids: string[]): string {
    return ids.map((id) => this.evidence().find((e) => e.id === id)?.title ?? 'evidência retirada').join('; ');
  }

  protected startFinding(): void {
    this.draft.set(findingDraftFrom(this.detail()));
  }

  protected createFinding(f: NistFindingDraft): void {
    if (this.busy() || findingProblem(f)) return;
    this.run(this.nist.createFinding(this.ctx(), this.detail().code, toFindingRequest(f)), () => this.draft.set(null));
  }

  protected openForm(findingId: string, mode: PlanForm['mode'], status?: string, plan?: NistPlan): void {
    const f = blankPlanForm(mode);
    if (status) f.status = status;
    if (plan) {
      f.area = plan.responsibleArea ?? '';
      f.due = plan.dueDate ?? '';
    }
    this.forms.update((m) => ({ ...m, [findingId]: f }));
  }

  protected closeForm(findingId: string): void {
    this.forms.update((m) => {
      const n = { ...m };
      delete n[findingId];
      return n;
    });
  }

  protected formTitle(f: PlanForm): string {
    switch (f.mode) {
      case 'status':
        return f.status === 'Open' ? 'Reabrir o achado' : f.status === 'RiskAccepted' ? 'Aceitar o risco' : 'Encerrar o achado';
      case 'execution':
        return 'Registrar a execução do plano';
      case 'validation':
        return 'Validar o plano com evidência';
      case 'create':
        return 'Novo plano de tratamento';
      default:
        return 'Editar responsável, área e prazo';
    }
  }

  protected formProblem(f: PlanForm): string | null {
    switch (f.mode) {
      case 'status':
        return f.status !== 'Open' && f.notes.trim().length < 10 ? 'Justifique (pelo menos 10 caracteres).' : null;
      case 'execution':
        return f.notes.trim().length < 10 ? 'Descreva o que foi feito (pelo menos 10 caracteres).' : null;
      case 'validation':
        return f.evidence.trim().length === 0 ? 'Referencie a evidência que comprova a correção.' : null;
      case 'create':
        if (!f.title.trim()) return 'Dê um título ao plano.';
        break;
    }
    if (f.ownerMode === 'user' && !f.ownerUserId) return 'Escolha o usuário responsável.';
    if (f.ownerMode === 'external' && !f.ownerName.trim()) return 'Informe o nome do contato externo.';
    return null;
  }

  protected submitForm(fd: NistFinding, f: PlanForm): void {
    if (this.busy() || this.formProblem(f)) return;
    const c = this.ctx();
    const pl = fd.plan;
    const responsible = responsibleRequest(f.ownerMode, f.ownerUserId, f.ownerName, f.ownerContact);
    const done = () => this.closeForm(fd.id);
    switch (f.mode) {
      case 'status':
        this.run(this.nist.setFindingStatus(c, fd.id, f.status, f.notes.trim() || null, fd.version), done);
        return;
      case 'execution':
        if (pl) this.run(this.nist.recordExecution(c, fd.id, pl.id, pl.version, f.notes.trim(), f.evidence.trim() || null), done);
        return;
      case 'validation':
        if (pl) this.run(this.nist.validatePlan(c, fd.id, pl.id, pl.version, f.evidence.trim(), f.notes.trim() || null), done);
        return;
      case 'create':
        this.run(
          this.nist.createPlan(c, fd.id, {
            title: f.title.trim(), proposedAction: f.action.trim() || null, responsible, responsibleArea: f.area.trim() || null, dueDate: f.due || null,
          }),
          done,
        );
        return;
      default:
        if (pl)
          this.run(
            this.nist.updatePlan(c, fd.id, pl.id, {
              expectedVersion: pl.version, responsible, responsibleArea: f.area.trim() || null, dueDate: f.due || null,
            }),
            done,
          );
    }
  }

  protected transition(fd: NistFinding, pl: NistPlan, to: NistPlanStatus): void {
    if (this.busy()) return;
    if (pl.status === 'Concluido' && !confirm('Reabrir o plano concluído? Um novo ciclo de execução e validação começa.')) return;
    this.run(this.nist.updatePlan(this.ctx(), fd.id, pl.id, { expectedVersion: pl.version, status: to }));
  }

  private run(request: Observable<unknown>, done?: () => void): void {
    const gen = this.gen;
    this.busy.set(true);
    this.error.set(null);
    this.conflict.set(false);
    this.sub = request.subscribe({
      next: () => {
        if (gen !== this.gen) return;
        this.busy.set(false);
        done?.();
        this.changed.emit();
      },
      error: (e: NistApiError) => {
        if (gen !== this.gen) return;
        this.busy.set(false);
        this.conflict.set(e.status === 409);
        this.error.set(e.message);
      },
    });
  }

  private reset(): void {
    this.gen++;
    this.sub?.unsubscribe();
    this.sub = null;
    this.draft.set(null);
    this.forms.set({});
    this.busy.set(false);
    this.error.set(null);
    this.conflict.set(false);
  }
}

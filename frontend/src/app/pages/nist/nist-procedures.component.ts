import { DatePipe } from '@angular/common';
import { Component, DestroyRef, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import {
  METHOD_HINTS,
  NIST_METHODS,
  NIST_OUTCOMES,
  NistEvidence,
  NistProcedure,
  NistProcedureResultDraft,
  NistProcedureStatus,
  NistTestMethod,
  contentOriginLabel,
  dateBr,
  methodLabel,
  outcomeBadgeClass,
  outcomeLabel,
  procedureDraftFrom,
  procedureResultProblem,
  procedureStatusLabel,
  toProcedureRequest,
} from '../../models/nist.models';
import { NistApiError, NistCtx, NistService } from '../../services/nist.service';

const STATUSES: NistProcedureStatus[] = ['Planned', 'InProgress', 'Performed', 'NotPerformed'];

/**
 * [AEGIS-NIST-JOURNEY-02] Procedimentos de avaliação de uma subcategoria (referência funcional: métodos de teste do CSF
 * Profile — examinar, entrevistar, testar). O PLANEJADO (método e procedimento) é registro separado do RESULTADO
 * (andamento, data, observação, conclusão, evidências citadas): escolher o método não comprova o teste. Cada gravação vai
 * com a versão lida; conflito (409) preserva o que foi digitado.
 */
@Component({
  selector: 'app-nist-procedures',
  standalone: true,
  imports: [FormsModule, DatePipe],
  template: `
    <section class="panel" aria-labelledby="proc-h" id="procedimentos">
      <div class="hd"><h3 id="proc-h">Procedimentos de avaliação</h3><span class="hint">{{ performed() }} de {{ procedures().length }} realizado(s) · método planejado ≠ teste realizado</span></div>
      @if (procedures().length === 0) { <p class="muted">Nenhum procedimento planejado nesta rodada.</p> }
      <ul class="procs">
        @for (p of procedures(); track p.id) {
          <li [id]="'proc-' + p.id" tabindex="-1">
            <p class="top"><span class="badge info">{{ methodLabel(p.method) }}</span>
              <span class="badge neutral">{{ procedureStatusLabel(p.status) }}</span>
              @if (p.outcome) { <span [class]="'badge ' + outcomeBadgeClass(p.outcome)">{{ outcomeLabel(p.outcome) }}</span> }
              @if (p.contentOrigin !== 'Analyst') { <span class="badge violet">{{ contentOriginLabel(p.contentOrigin) }}</span> }</p>
            <p class="txt">{{ p.procedure }}</p>
            @if (p.observation) { <p class="txt"><span class="lbl">Observado:</span> {{ p.observation }}</p> }
            <p class="muted">Planejado por {{ p.createdByName ?? '—' }} em {{ p.createdAt | date: 'dd/MM/yyyy' }}
              @if (p.performedOn) { · realizado em {{ dateBr(p.performedOn) }} }
              @if (p.resultRecordedByName) { · resultado registrado por {{ p.resultRecordedByName }} }
              @if (p.evidenceIds.length) { · evidências: {{ evidenceTitles(p.evidenceIds) }} }</p>
            @if (p.originNote) { <p class="muted">{{ p.originNote }}</p> }
            @if (p.assistedFrom; as af) {
              <p class="muted">Texto planejado a partir de sugestão {{ af.mode === 'Real' ? 'da IA' : 'SIMULADA (demonstração)' }}{{ af.edited ? ', editado pela pessoa' : '' }} —
                incorporado por {{ af.incorporatedByName ?? '—' }}. Planejar não é realizar.</p>
            }
            @if (canEdit()) {
              @if (editing()[p.id]; as d) {
                <form class="grid" (ngSubmit)="saveResult(p, d)" [attr.aria-label]="'Resultado do procedimento ' + methodLabel(p.method)">
                  <label class="field"><span class="field-label">Andamento</span>
                    <select name="st-{{ p.id }}" [(ngModel)]="d.status">@for (s of statuses; track s) { <option [value]="s">{{ procedureStatusLabel(s) }}</option> }</select></label>
                  @if (d.status === 'Performed') {
                    <label class="field"><span class="field-label">Conclusão observada</span>
                      <select name="oc-{{ p.id }}" [(ngModel)]="d.outcome"><option [ngValue]="null">Escolha…</option>@for (o of outcomes; track o) { <option [ngValue]="o">{{ outcomeLabel(o) }}</option> }</select></label>
                    <label class="field"><span class="field-label">Data de realização</span><input type="date" name="on-{{ p.id }}" [max]="today" [(ngModel)]="d.performedOn" /></label>
                  }
                  <label class="field wide"><span class="field-label">{{ d.status === 'NotPerformed' ? 'Por que não foi realizado' : 'O que foi observado' }}</span>
                    <textarea name="ob-{{ p.id }}" rows="3" maxlength="4000" [(ngModel)]="d.observation"></textarea></label>
                  @if (evidence().length) {
                    <fieldset class="field wide ev"><legend class="field-label">Evidências citadas</legend>
                      @for (e of evidence(); track e.id) {
                        <label><input type="checkbox" [checked]="d.evidenceIds.includes(e.id)" (change)="toggleEvidence(d, e.id)" /> {{ e.title }}</label>
                      }
                    </fieldset>
                  }
                  <div class="actions wide">
                    <button type="submit" class="primary sm" [disabled]="busy() || !!problem(d)">Gravar resultado</button>
                    <button type="button" class="ghost sm" (click)="cancel(p.id)">Cancelar</button>
                    @if (problem(d); as pb) { <span class="muted">{{ pb }}</span> }
                  </div>
                </form>
              } @else {
                <div class="actions">
                  <button type="button" class="ghost xs" (click)="edit(p)" [disabled]="busy()">Registrar resultado</button>
                  <button type="button" class="ghost xs" (click)="remove(p)" [disabled]="busy()">Retirar</button>
                </div>
              }
            }
          </li>
        }
      </ul>
      @if (error(); as e) {
        <div class="notice error" role="alert">{{ e }} @if (conflict()) { <button type="button" class="ghost xs" (click)="changed.emit()">Recarregar procedimentos</button> }</div>
      }
      @if (canEdit()) {
        <form class="add" (ngSubmit)="add()" aria-label="Planejar procedimento">
          <fieldset class="methods"><legend class="field-label">Método</legend>
            @for (m of methods; track m) {
              <label [title]="hints[m]"><input type="radio" name="nm" [value]="m" [(ngModel)]="newMethod" /> {{ methodLabel(m) }} <span class="muted">— {{ hints[m] }}</span></label>
            }
          </fieldset>
          <label class="field"><span class="field-label">Procedimento planejado</span>
            <textarea name="np" rows="2" maxlength="4000" [(ngModel)]="newText" placeholder="Ex.: Examinar a planilha de inventário e conferir o dono de cada servidor."></textarea></label>
          <button type="submit" class="ghost sm" [disabled]="busy() || newText.trim().length < 10">Planejar procedimento</button>
        </form>
      }
    </section>
  `,
  styles: [
    `
      .procs { list-style: none; padding: 0; margin: 0; }
      .procs li { padding: var(--sp-3) 0; border-top: 1px solid var(--line-2); display: flex; flex-direction: column; gap: 4px; }
      .procs p { margin: 0; overflow-wrap: anywhere; }
      .top { display: flex; flex-wrap: wrap; gap: 6px; }
      .txt { white-space: pre-line; }
      .lbl { color: var(--muted); font-weight: 600; }
      .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 200px), 1fr)); gap: var(--sp-3); margin-top: var(--sp-2); align-items: end; }
      .wide { grid-column: 1 / -1; }
      .ev, .methods { border: 0; padding: 0; margin: 0; display: flex; flex-direction: column; gap: 4px; font-size: var(--fs-sm); }
      .actions { display: flex; flex-wrap: wrap; gap: var(--sp-2); align-items: center; }
      .add { display: flex; flex-direction: column; gap: var(--sp-2); margin-top: var(--sp-3); padding-top: var(--sp-3); border-top: 1px solid var(--line-2); align-items: flex-start; }
      .add .field { width: 100%; }
    `,
  ],
})
export class NistProceduresComponent {
  private readonly nist = inject(NistService);

  readonly ctx = input.required<NistCtx>();
  readonly code = input.required<string>();
  readonly procedures = input.required<NistProcedure[]>();
  readonly evidence = input.required<NistEvidence[]>();
  readonly canEdit = input(false);
  readonly changed = output<void>();

  protected readonly methods = NIST_METHODS;
  protected readonly statuses = STATUSES;
  protected readonly outcomes = NIST_OUTCOMES;
  protected readonly hints = METHOD_HINTS;
  protected readonly methodLabel = methodLabel;
  protected readonly procedureStatusLabel = procedureStatusLabel;
  protected readonly outcomeLabel = outcomeLabel;
  protected readonly outcomeBadgeClass = outcomeBadgeClass;
  protected readonly contentOriginLabel = contentOriginLabel;
  protected readonly dateBr = dateBr;
  protected readonly today = new Date().toISOString().slice(0, 10);

  protected readonly editing = signal<Record<string, NistProcedureResultDraft>>({});
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly conflict = signal(false);
  protected readonly performed = computed(() => this.procedures().filter((p) => p.status === 'Performed').length);
  protected newMethod: NistTestMethod = 'Examine';
  protected newText = '';

  private gen = 0;
  private sub: Subscription | null = null;
  private readonly key = computed(() => `${this.ctx().assessmentId}|${this.ctx().cycleId}|${this.ctx().scopeId}|${this.code()}`);

  constructor() {
    inject(DestroyRef).onDestroy(() => this.reset());
    effect(() => {
      this.key();
      untracked(() => this.reset());
    });
  }

  protected evidenceTitles(ids: string[]): string {
    const ev = this.evidence();
    return ids.map((id) => ev.find((e) => e.id === id)?.title ?? 'evidência retirada').join('; ');
  }

  protected problem(d: NistProcedureResultDraft): string | null {
    return procedureResultProblem(d, this.today);
  }

  protected edit(p: NistProcedure): void {
    this.editing.update((m) => ({ ...m, [p.id]: procedureDraftFrom(p) }));
  }

  protected cancel(id: string): void {
    this.editing.update((m) => {
      const n = { ...m };
      delete n[id];
      return n;
    });
  }

  protected toggleEvidence(d: NistProcedureResultDraft, id: string): void {
    d.evidenceIds = d.evidenceIds.includes(id) ? d.evidenceIds.filter((x) => x !== id) : [...d.evidenceIds, id];
  }

  protected add(): void {
    if (this.busy() || this.newText.trim().length < 10) return;
    this.run(this.nist.addProcedure(this.ctx(), this.code(), this.newMethod, this.newText.trim()), () => (this.newText = ''));
  }

  protected saveResult(p: NistProcedure, d: NistProcedureResultDraft): void {
    if (this.busy() || this.problem(d)) return;
    this.run(this.nist.updateProcedure(this.ctx(), this.code(), p.id, toProcedureRequest(d, p.version)), () => this.cancel(p.id));
  }

  protected remove(p: NistProcedure): void {
    if (this.busy() || !confirm('Retirar este procedimento? A retirada fica registrada na trilha.')) return;
    this.run(this.nist.removeProcedure(this.ctx(), this.code(), p.id, p.version));
  }

  private run(request: ReturnType<NistService['removeProcedure']> | ReturnType<NistService['addProcedure']>, done?: () => void): void {
    const gen = this.gen;
    this.busy.set(true);
    this.error.set(null);
    this.conflict.set(false);
    this.sub = (request as ReturnType<NistService['addProcedure']>).subscribe({
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
    this.editing.set({});
    this.busy.set(false);
    this.error.set(null);
    this.conflict.set(false);
    this.newText = '';
    this.newMethod = 'Examine';
  }
}

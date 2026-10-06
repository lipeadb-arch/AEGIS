import { DatePipe } from '@angular/common';
import { Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import {
  NistFinding,
  NistFindingStatus,
  dateBr,
  findingStatusLabel,
  functionSlugOf,
  priorityLabel,
  severityBadgeClass,
  severityLabel,
} from '../../models/nist.models';
import { NistCtx, NistService } from '../../services/nist.service';

/**
 * [AEGIS-NIST-JOURNEY-02] Achados e planos da rodada e escopo: o acompanhamento do tratamento. Cada achado leva à sua
 * subcategoria, onde estão o registro completo, as evidências e as ações do plano (execução, validação, reabertura).
 */
@Component({
  selector: 'app-nist-findings-panel',
  standalone: true,
  imports: [RouterLink, DatePipe],
  template: `
    <section class="panel" aria-labelledby="fp-h">
      <div class="hd"><h3 id="fp-h">Achados e planos de tratamento</h3><span class="hint">{{ shown().length }} de {{ findings().length }}</span></div>
      <nav class="filters" aria-label="Filtrar achados">
        @for (f of filters; track f.value) {
          <button type="button" class="ghost xs" [class.on]="status() === f.value" [attr.aria-pressed]="status() === f.value" (click)="status.set(f.value)">{{ f.label }}</button>
        }
        <label class="chk"><input type="checkbox" [checked]="onlyOverdue()" (change)="onlyOverdue.set(!onlyOverdue())" /> só com prazo vencido</label>
      </nav>
      @if (loading()) {
        <p class="muted" role="status">Carregando os achados…</p>
      } @else if (error()) {
        <p class="notice error" role="alert">{{ error() }}</p>
      } @else if (findings().length === 0) {
        <p class="muted">Nenhum achado registrado nesta rodada e escopo. Um achado é registrado na subcategoria, a partir de uma lacuna
          documentada (lacuna observada ou procedimento insatisfatório) — atual × alvo, sozinho, não gera achado.</p>
      } @else if (shown().length === 0) {
        <p class="muted">Nenhum achado com este filtro.</p>
      } @else {
        <div class="table-wrap"><table class="data-table">
          <caption class="sr-only">Achados da rodada</caption>
          <thead><tr><th scope="col">Achado</th><th scope="col">Severidade · prioridade</th><th scope="col">Situação</th><th scope="col">Tratamento</th><th scope="col">Responsável · prazo</th></tr></thead>
          <tbody>
            @for (f of shown(); track f.id) {
              <tr>
                <td><a [routerLink]="['/nist', slug(f.subcategoryCode), f.subcategoryCode]" [queryParams]="params()" [fragment]="'achado-' + f.id">{{ f.title }}</a>
                  <span class="muted block"><span class="mono">{{ f.subcategoryCode }}</span> · registrado por {{ f.createdByName ?? '—' }} em {{ f.createdAt | date: 'dd/MM/yyyy' }}</span></td>
                <td><span [class]="'badge ' + severityBadgeClass(f.severity)">{{ severityLabel(f.severity) }}</span> <span class="muted">prioridade {{ priorityLabel(f.priority).toLowerCase() }}</span></td>
                <td>{{ findingStatusLabel(f.status) }}</td>
                <td>{{ f.treatmentLabel }}@if (f.plan?.wasReopened) { <span class="badge warn">reaberto</span> }</td>
                <td>{{ f.plan?.responsiblePerson ?? '—' }}
                  @if (f.plan?.dueDate) { <span class="muted block">até {{ dateBr(f.plan!.dueDate) }}@if (f.plan!.isOverdue) { <span class="badge bad">vencido</span> }</span> }</td>
              </tr>
            }
          </tbody>
        </table></div>
      }
    </section>
  `,
  styles: [
    `
      .filters { display: flex; flex-wrap: wrap; align-items: center; gap: var(--sp-2); margin-bottom: var(--sp-3); }
      .filters .on { font-weight: 600; border-color: var(--text-2); }
      .chk { display: inline-flex; align-items: center; gap: 6px; font-size: var(--fs-sm); }
      .block { display: block; }
    `,
  ],
})
export class NistFindingsPanelComponent {
  private readonly nist = inject(NistService);

  readonly ctx = input.required<NistCtx>();
  readonly params = input.required<Record<string, string>>();
  readonly initialStatus = input<string | null>(null);

  protected readonly filters: { value: NistFindingStatus | 'all'; label: string }[] = [
    { value: 'all', label: 'Todos' },
    { value: 'Open', label: 'Abertos' },
    { value: 'RiskAccepted', label: 'Risco aceito' },
    { value: 'Closed', label: 'Encerrados' },
  ];
  protected readonly severityLabel = severityLabel;
  protected readonly severityBadgeClass = severityBadgeClass;
  protected readonly priorityLabel = priorityLabel;
  protected readonly findingStatusLabel = findingStatusLabel;
  protected readonly dateBr = dateBr;
  protected readonly slug = functionSlugOf;

  protected readonly findings = signal<NistFinding[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly status = signal<NistFindingStatus | 'all'>('all');
  protected readonly onlyOverdue = signal(false);
  protected readonly shown = computed(() =>
    this.findings().filter((f) => (this.status() === 'all' || f.status === this.status()) && (!this.onlyOverdue() || !!f.plan?.isOverdue)),
  );

  private ticket = 0;
  private sub: Subscription | null = null;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.ticket++;
      this.sub?.unsubscribe();
    });
    effect(() => {
      const c = this.ctx();
      const s = this.initialStatus();
      untracked(() => {
        this.status.set(s === 'Open' || s === 'RiskAccepted' || s === 'Closed' ? s : 'all');
        this.load(c);
      });
    });
  }

  private load(c: NistCtx): void {
    const ticket = ++this.ticket;
    this.sub?.unsubscribe();
    this.loading.set(true);
    this.error.set(null);
    this.findings.set([]);
    this.sub = this.nist.findings(c.assessmentId, { cycleId: c.cycleId, scopeId: c.scopeId }).subscribe({
      next: (list) => {
        if (ticket !== this.ticket) return;
        // Defesa extra: só achados desta rodada e escopo preenchem a lista.
        this.findings.set(list.filter((f) => f.cycleId === c.cycleId && f.scopeId === c.scopeId));
        this.loading.set(false);
      },
      error: (e: Error) => {
        if (ticket !== this.ticket) return;
        this.error.set(e.message);
        this.loading.set(false);
      },
    });
  }
}

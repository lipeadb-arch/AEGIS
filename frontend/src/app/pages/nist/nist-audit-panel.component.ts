import { DatePipe } from '@angular/common';
import { Component, DestroyRef, effect, inject, input, signal, untracked } from '@angular/core';
import { Subscription } from 'rxjs';
import { NistAuditEntry, auditSubjectLabel } from '../../models/nist.models';
import { NistService } from '../../services/nist.service';

/**
 * [AEGIS-NIST-JOURNEY-02] Trilha de alterações da avaliação (ou de uma subcategoria): quem, quando, o quê, valor anterior e
 * novo — inclusive as etapas dos planos de tratamento vinculados. Só leitura; a trilha é append-only no banco.
 * Uma resposta pedida para outro contexto (avaliação, rodada, escopo, subcategoria ou atualização mais nova) é descartada.
 */
@Component({
  selector: 'app-nist-audit-panel',
  standalone: true,
  imports: [DatePipe],
  template: `
    <section class="panel" [attr.aria-labelledby]="'audit-' + uid">
      <div class="hd"><h3 id="audit-{{ uid }}">Trilha de alterações</h3>
        <button type="button" class="ghost xs" (click)="load()" [disabled]="loading()">Atualizar</button></div>
      @if (loading()) {
        <p class="muted" role="status">Carregando a trilha…</p>
      } @else if (error()) {
        <p class="notice error" role="alert">{{ error() }}</p>
      } @else if (entries().length === 0) {
        <p class="muted">Nenhuma alteração registrada{{ code() ? ' nesta subcategoria' : '' }} nesta rodada.</p>
      } @else {
        <ol class="trail">
          @for (e of shown(); track e.id) {
            <li>
              <p class="head"><strong>{{ subject(e.subject) }}</strong>
                @if (e.subcategoryCode && !code()) { <span class="mono">{{ e.subcategoryCode }}</span> }
                <span class="muted">{{ e.at | date: 'dd/MM/yyyy HH:mm' }} · {{ e.actorName || 'autor não identificado' }}</span></p>
              <p>{{ e.summary }}</p>
              @if (e.changes.length) {
                <details><summary>{{ e.changes.length }} campo(s) alterado(s)</summary>
                  <div class="table-wrap"><table class="data-table">
                    <caption class="sr-only">Valores anteriores e novos</caption>
                    <thead><tr><th scope="col">Campo</th><th scope="col">Antes</th><th scope="col">Depois</th></tr></thead>
                    <tbody>
                      @for (c of e.changes; track c.field) {
                        <tr><td>{{ c.label }}</td><td class="v">{{ c.from ?? '—' }}</td><td class="v">{{ c.to ?? '—' }}</td></tr>
                      }
                    </tbody>
                  </table></div>
                </details>
              }
            </li>
          }
        </ol>
        @if (entries().length > limit()) {
          <button type="button" class="ghost xs" (click)="limit.set(limit() + 50)">Mostrar mais ({{ entries().length - limit() }} restantes)</button>
        }
      }
    </section>
  `,
  styles: [
    `
      .trail { list-style: none; margin: 0; padding: 0; }
      .trail li { padding: var(--sp-3) 0; border-top: 1px solid var(--line-2); }
      .trail p { margin: 0 0 4px; overflow-wrap: anywhere; }
      .head { display: flex; flex-wrap: wrap; gap: 4px var(--sp-2); align-items: baseline; }
      .v { white-space: pre-line; overflow-wrap: anywhere; }
      details summary { cursor: pointer; font-size: var(--fs-sm); }
    `,
  ],
})
export class NistAuditPanelComponent {
  private static seq = 0;
  protected readonly uid = ++NistAuditPanelComponent.seq;
  private readonly nist = inject(NistService);

  readonly assessmentId = input.required<string>();
  readonly cycleId = input<string | null>(null);
  readonly scopeId = input<string | null>(null);
  readonly code = input<string | null>(null);
  /** Muda quando algo foi gravado na tela-mãe: a trilha é relida. */
  readonly refresh = input(0);

  protected readonly entries = signal<NistAuditEntry[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly limit = signal(50);

  private ticket = 0;
  private sub: Subscription | null = null;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.ticket++;
      this.sub?.unsubscribe();
    });
    effect(() => {
      this.assessmentId();
      this.cycleId();
      this.scopeId();
      this.code();
      this.refresh();
      untracked(() => this.load());
    });
  }

  protected shown(): NistAuditEntry[] {
    return this.entries().slice(0, this.limit());
  }

  protected subject(s: string): string {
    return auditSubjectLabel(s);
  }

  load(): void {
    const ticket = ++this.ticket;
    this.sub?.unsubscribe();
    this.loading.set(true);
    this.error.set(null);
    this.sub = this.nist.audit(this.assessmentId(), { cycleId: this.cycleId(), scopeId: this.scopeId(), code: this.code() }).subscribe({
      next: (list) => {
        if (ticket !== this.ticket) return;
        this.entries.set(list);
        this.limit.set(50);
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

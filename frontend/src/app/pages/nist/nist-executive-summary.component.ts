import { Component, DestroyRef, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import {
  NistAssistApplyEvent,
  NistAssistApplyTarget,
  NistExecutiveSummary,
  executiveDraftFrom,
  executiveProvenance,
} from '../../models/nist-assist.models';
import { AuthService } from '../../services/auth.service';
import { NistApiError, NistCtx, NistService } from '../../services/nist.service';
import { NistAssistPanelComponent } from './nist-assist-panel.component';

interface DraftSection {
  key: string;
  title: string;
  text: string;
}

/**
 * [AEGIS-NIST-AI-ASSIST-01] Resumo executivo da rodada e escopo, para a publicação. A IA (ou a própria pessoa) PREPARA uma
 * interpretação dos indicadores determinísticos do AEGIS; a pessoa revisa e edita aqui e ACEITA explicitamente — só então o texto
 * entra na próxima fotografia publicada, com a procedência (origem, modo, quem aceitou, edição, revisão posterior). Se a rodada
 * muda, o resumo aceito deixa de valer e sai da publicação até ser aceito de novo. Notas, contagens e cobertura nunca mudam.
 */
@Component({
  selector: 'app-nist-executive-summary',
  standalone: true,
  imports: [FormsModule, NistAssistPanelComponent],
  template: `
    <section class="panel" aria-labelledby="ex-h">
      <div class="hd"><h3 id="ex-h">Resumo executivo da rodada</h3><span class="hint">interpretação dos indicadores · entra na publicação só depois de aceito</span></div>
      <p class="muted">Notas, contagens, cobertura e situação dos planos vêm do AEGIS e não mudam com o resumo. O relatório continua completo sem ele.</p>
      @if (loadError(); as le) { <p class="notice error" role="alert">{{ le }}</p> }

      @if (summary(); as s) {
        <div class="accepted">
          <p class="meta">
            <span [class]="'badge ' + (s.current ? 'ok' : 'bad')">{{ s.current ? 'Aceito · vale para o estado atual' : 'Preparado sobre um estado anterior' }}</span>
            @if (s.mode === 'Simulated') { <span class="badge warn">Demonstração</span> }
            <span [class]="'badge ' + (s.reviewCurrent ? 'ok' : 'neutral')">{{ s.reviewCurrent ? 'Revisado por outra pessoa' : 'Sem revisão humana posterior' }}</span>
          </p>
          @if (!s.current) {
            <p class="notice warn" role="status">A rodada mudou depois do aceite: este resumo NÃO entra na próxima publicação até ser revisto e aceito de novo.</p>
          }
          @for (sec of s.sections; track sec.key) {
            <h4>{{ sec.title }}</h4>
            <p class="pre">{{ sec.text }}</p>
          }
          <p class="hint">{{ provenance(s) }}@if (s.reviewNote) { Nota da revisão: “{{ s.reviewNote }}”. }</p>
          @if (canWrite()) {
            <div class="actions">
              <button type="button" class="ghost xs" (click)="edit(s)" [disabled]="busy() || !!draft()">Editar e aceitar de novo</button>
              <button type="button" class="ghost xs" (click)="withdraw(s)" [disabled]="busy()">Retirar o resumo</button>
            </div>
            @if (s.current && !s.reviewCurrent) {
              <div class="grid">
                <label class="field wide"><span class="field-label">Nota da revisão (opcional)</span>
                  <textarea name="exrn" rows="2" maxlength="2000" [(ngModel)]="reviewNote"></textarea></label>
                <div class="actions wide">
                  <button type="button" class="ghost sm" (click)="review(s)" [disabled]="busy()">Registrar revisão deste resumo</button>
                  <span class="hint">A revisão é de outra pessoa que não quem aceitou o resumo.</span>
                </div>
              </div>
            }
          }
        </div>
      } @else if (!loading()) {
        <p class="muted">Nenhum resumo aceito para esta rodada e escopo.</p>
      }

      <app-nist-assist-panel kind="ExecutiveSummary" [ctx]="ctx()" [canGenerate]="canWrite()" [canIncorporate]="canWrite()" [refresh]="refresh()"
        [applyTargets]="useTargets" [linkParams]="params()" (apply)="useAssist($event)" />

      @if (canWrite() && !draft()) {
        <button type="button" class="ghost xs" (click)="startManual()">Redigir o resumo sem IA</button>
      }

      @if (draft(); as dr) {
        <form class="editor" (ngSubmit)="accept(false)" aria-label="Rascunho do resumo executivo">
          <p class="hint">{{ draftAssistanceId ? 'Rascunho vindo de sugestão da IA: revise e edite antes de aceitar.' : 'Rascunho redigido pela pessoa.' }}
            Aceitar não altera notas, contagens nem classificações.</p>
          @if (baseChanged()) {
            <div class="notice warn" role="status">
              {{ summary() ? 'O resumo vigente mudou (versão ' + summary()!.version + ')' : 'O resumo vigente foi retirado' }} depois que este rascunho começou
              {{ draftBase() === 0 ? '(sem resumo aceito)' : '(versão ' + draftBase() + ')' }}. Aceitar agora seria recusado para não sobrescrever a outra edição.
              Compare com o texto vigente acima; se ainda quiser substituí-lo pelo seu rascunho, confirme.
              <button type="button" class="ghost xs" (click)="adoptCurrentBase()" [disabled]="busy()">Substituir o vigente pelo meu rascunho</button>
            </div>
          }
          @for (sec of dr; track sec.key) {
            <label class="field"><span class="field-label">{{ sec.title }}</span>
              <textarea [name]="'ex-' + sec.key" rows="3" maxlength="2000" [(ngModel)]="sec.text"></textarea></label>
          }
          <div class="actions">
            <button type="submit" class="primary sm" [disabled]="busy() || !hasText(dr) || baseChanged()">{{ busy() ? 'Gravando…' : 'Aceitar este resumo' }}</button>
            <button type="button" class="ghost sm" (click)="discard()" [disabled]="busy()">Descartar o rascunho</button>
          </div>
        </form>
      }
      @if (saveError(); as se) {
        <div class="notice error" role="alert">{{ se }}
          @if (assistStale()) { <button type="button" class="ghost xs" (click)="accept(true)" [disabled]="busy()">Revisei diante do estado atual — aceitar assim mesmo</button> }
          @if (conflict()) { <button type="button" class="ghost xs" (click)="load()">Recarregar o resumo vigente para comparar (o seu texto continua aqui)</button> }
        </div>
      }
      @if (note(); as n) { <p class="notice" role="status">{{ n }}</p> }
    </section>
  `,
  styles: [
    `
      .meta { display: flex; flex-wrap: wrap; gap: 6px; margin: var(--sp-2) 0; }
      h4 { margin: var(--sp-3) 0 var(--sp-1); font-size: var(--fs-body); }
      .pre { white-space: pre-line; overflow-wrap: anywhere; }
      .actions { display: flex; flex-wrap: wrap; gap: var(--sp-2); align-items: center; margin: var(--sp-2) 0; }
      .editor { display: flex; flex-direction: column; gap: var(--sp-2); margin-top: var(--sp-3); }
      .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 240px), 1fr)); gap: var(--sp-2); }
      .wide { grid-column: 1 / -1; }
      p { overflow-wrap: anywhere; }
    `,
  ],
})
export class NistExecutiveSummaryComponent {
  private readonly nist = inject(NistService);
  private readonly auth = inject(AuthService);

  readonly ctx = input.required<NistCtx>();
  readonly canWrite = input(false);
  readonly params = input<Record<string, string>>({});
  /** Muda quando a rodada muda (para o painel conferir a atualidade da sugestão). */
  readonly refresh = input<unknown>(0);
  readonly changed = output<void>();

  protected readonly useTargets: NistAssistApplyTarget[] = [{ section: '*', field: '*', label: 'Usar como rascunho do resumo' }];
  protected readonly provenance = executiveProvenance;

  protected readonly summary = signal<NistExecutiveSummary | null>(null);
  protected readonly loading = signal(false);
  protected readonly loadError = signal<string | null>(null);
  protected readonly draft = signal<DraftSection[] | null>(null);
  protected readonly busy = signal(false);
  protected readonly saveError = signal<string | null>(null);
  protected readonly assistStale = signal(false);
  protected readonly conflict = signal(false);
  protected readonly note = signal<string | null>(null);
  protected draftAssistanceId: string | null = null;
  /**
   * Versão do resumo vigente sobre a qual o rascunho COMEÇOU (0 = não havia resumo). Só muda quando um rascunho é criado ou quando
   * a pessoa decide explicitamente substituir a versão vigente — recarregar o resumo nunca a atualiza em silêncio.
   */
  protected readonly draftBase = signal(0);
  /** O vigente mudou (ou foi retirado) depois que o rascunho começou: aceitar seria recusado. */
  protected readonly baseChanged = computed(() => !!this.draft() && (this.summary()?.version ?? 0) !== this.draftBase());
  protected reviewNote = '';

  private gen = 0;
  private subs: Subscription[] = [];
  private readonly key = computed(() => `${this.ctx().assessmentId}|${this.ctx().cycleId}|${this.ctx().scopeId}`);

  constructor() {
    inject(DestroyRef).onDestroy(() => this.reset());
    effect(() => {
      this.key();
      untracked(() => {
        this.reset();
        this.load();
      });
    });
  }

  protected hasText(d: DraftSection[]): boolean {
    return d.some((s) => s.text.trim() !== '');
  }

  load(): void {
    const gen = this.gen;
    const tenant = this.auth.activeTenantId();
    this.loading.set(true);
    this.loadError.set(null);
    this.conflict.set(false);
    this.subs.push(
      this.nist.executiveSummary(this.ctx()).subscribe({
        next: (s) => {
          if (!this.current(gen, tenant)) return;
          this.loading.set(false);
          this.summary.set(s);
        },
        error: (e: Error) => {
          if (!this.current(gen, tenant)) return;
          this.loading.set(false);
          this.loadError.set(e.message);
        },
      }),
    );
  }

  /** A sugestão vira RASCUNHO editável — nada é aceito sem o clique em "Aceitar". */
  protected useAssist(e: NistAssistApplyEvent): void {
    const c = this.ctx();
    const v = e.view;
    if (!this.canWrite() || v.kind !== 'ExecutiveSummary' || v.assessmentId !== c.assessmentId || v.cycleId !== c.cycleId || v.scopeId !== c.scopeId) return;
    this.draft.set(executiveDraftFrom(v.applicable));
    this.draftAssistanceId = v.id;
    this.draftBase.set(this.summary()?.version ?? 0);
    this.clearMessages();
    this.note.set('Sugestão copiada para o rascunho do resumo. Revise, edite e aceite.');
  }

  protected startManual(): void {
    this.draft.set(executiveDraftFrom(null));
    this.draftAssistanceId = null;
    this.draftBase.set(this.summary()?.version ?? 0);
    this.clearMessages();
  }

  protected edit(s: NistExecutiveSummary): void {
    this.draft.set(executiveDraftFrom(s.sections));
    this.draftAssistanceId = s.assistanceId;
    this.draftBase.set(s.version);
    this.clearMessages();
  }

  protected discard(): void {
    this.draft.set(null);
    this.draftAssistanceId = null;
    this.draftBase.set(0);
    this.clearMessages();
  }

  /** Decisão EXPLÍCITA da pessoa, depois de ver o vigente: o rascunho passa a substituir a versão exibida agora. */
  protected adoptCurrentBase(): void {
    if (!this.draft()) return;
    this.draftBase.set(this.summary()?.version ?? 0);
    this.clearMessages();
    this.note.set('O seu rascunho vai substituir a versão vigente exibida acima quando você aceitar.');
  }

  protected accept(acknowledgeStale: boolean): void {
    const d = this.draft();
    // Bloqueio no método, não só no botão: o vigente mudou desde o início do rascunho → a pessoa decide antes (adoptCurrentBase).
    if (!d || this.busy() || !this.hasText(d) || this.baseChanged()) return;
    const gen = this.gen;
    const tenant = this.auth.activeTenantId();
    this.busy.set(true);
    this.clearMessages();
    const sections = d.filter((s) => s.text.trim() !== '').map((s) => ({ key: s.key, text: s.text.trim() }));
    this.subs.push(
      this.nist
        // A versão enviada é a da BASE do rascunho, nunca a recém-carregada: se o vigente mudou, o servidor recusa (409).
        .saveExecutiveSummary(this.ctx(), { sections, assistanceId: this.draftAssistanceId, expectedVersion: this.draftBase(), acknowledgeStale })
        .subscribe({
          next: (s) => {
            if (!this.current(gen, tenant)) return;
            this.busy.set(false);
            this.summary.set(s);
            this.draft.set(null);
            this.draftAssistanceId = null;
            this.draftBase.set(0);
            this.note.set('Resumo aceito. Ele entra na próxima publicação enquanto a rodada não mudar; revise a prévia antes de publicar.');
            this.changed.emit();
          },
          error: (e: NistApiError) => {
            if (!this.current(gen, tenant)) return;
            this.busy.set(false);
            const stale = e.status === 409 && e.reason === 'AssistanceStale';
            this.assistStale.set(stale);
            this.conflict.set(e.status === 409 && !stale);
            this.saveError.set(e.message);
          },
        }),
    );
  }

  protected review(s: NistExecutiveSummary): void {
    if (this.busy()) return;
    const gen = this.gen;
    const tenant = this.auth.activeTenantId();
    this.busy.set(true);
    this.clearMessages();
    this.subs.push(
      this.nist.reviewExecutiveSummary(this.ctx(), s.version, this.reviewNote.trim() || null).subscribe({
        next: (r) => {
          if (!this.current(gen, tenant)) return;
          this.busy.set(false);
          this.summary.set(r);
          this.reviewNote = '';
          this.note.set('Revisão registrada.');
          this.changed.emit();
        },
        error: (e: NistApiError) => {
          if (!this.current(gen, tenant)) return;
          this.busy.set(false);
          this.conflict.set(e.status === 409);
          this.saveError.set(e.message);
        },
      }),
    );
  }

  protected withdraw(s: NistExecutiveSummary): void {
    if (this.busy() || !confirm('Retirar o resumo executivo? As próximas publicações sairão sem interpretação; fotografias já publicadas não mudam.')) return;
    const gen = this.gen;
    const tenant = this.auth.activeTenantId();
    this.busy.set(true);
    this.clearMessages();
    this.subs.push(
      this.nist.withdrawExecutiveSummary(this.ctx(), s.version).subscribe({
        next: () => {
          if (!this.current(gen, tenant)) return;
          this.busy.set(false);
          this.summary.set(null);
          this.note.set('Resumo retirado.');
          this.changed.emit();
        },
        error: (e: NistApiError) => {
          if (!this.current(gen, tenant)) return;
          this.busy.set(false);
          this.conflict.set(e.status === 409);
          this.saveError.set(e.message);
        },
      }),
    );
  }

  private clearMessages(): void {
    this.saveError.set(null);
    this.assistStale.set(false);
    this.conflict.set(false);
    this.note.set(null);
  }

  private current(gen: number, tenant: string | null): boolean {
    return gen === this.gen && tenant === this.auth.activeTenantId();
  }

  private reset(): void {
    this.gen++;
    this.subs.forEach((s) => s.unsubscribe());
    this.subs = [];
    this.summary.set(null);
    this.loading.set(false);
    this.loadError.set(null);
    this.draft.set(null);
    this.draftAssistanceId = null;
    this.draftBase.set(0);
    this.busy.set(false);
    this.reviewNote = '';
    this.clearMessages();
  }
}

import { DatePipe } from '@angular/common';
import { Component, DestroyRef, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { Router } from '@angular/router';
import { Subscription } from 'rxjs';
import {
  NistAssistApplyEvent,
  NistAssistApplyTarget,
  NistAssistAvailability,
  NistAssistContext,
  NistAssistFocus,
  NistAssistKind,
  NistAssistSource,
  NistAssistView,
  assistErrorText,
  assistIsCurrent,
  availabilityBadgeClass,
  basisBadgeClass,
  sourceAnchorId,
} from '../../models/nist-assist.models';
import { MATURITY_LEVEL_LABELS, functionSlugOf } from '../../models/nist.models';
import { AuthService } from '../../services/auth.service';
import { NistApiError, NistCtx, NistService } from '../../services/nist.service';

interface AssistAction {
  focus: NistAssistFocus | null;
  label: string;
}

/** Estado da IA por tenant, reaproveitado por um minuto entre os painéis da mesma tela. */
let availabilityCache: { tenant: string | null; at: number; value: NistAssistAvailability } | null = null;

/**
 * [AEGIS-NIST-AI-ASSIST-01] Painel da assistência de IA, comum à subcategoria, ao achado e ao resumo executivo.
 *
 *   • Nada é gerado ao abrir: a pessoa escolhe a finalidade (analisar as evidências, explicar o achado, sugerir tratamento,
 *     preparar o resumo). Uma geração por vez; dá para cancelar; uma sugestão já disponível com o mesmo contexto é
 *     reaproveitada pelo servidor sem chamar a IA.
 *   • A sugestão mostra o motor (real ou DEMONSTRAÇÃO), quando e a pedido de quem foi gerada, a base usada, cada afirmação com
 *     a sua classificação e as fontes clicáveis (âncoras da tela ou a subcategoria), e o que a validação do servidor descartou.
 *   • Se o contexto muda (o pai avisa por `refresh`), o painel confere a impressão digital e marca a sugestão como
 *     DESATUALIZADA — aproveitar fica bloqueado até gerar de novo.
 *   • Aproveitar só EMITE o conteúdo para o rascunho do pai (`apply`) ou planeja procedimentos escolhidos; nunca confirma
 *     avaliação, aprova revisão ou conclui plano. Respostas pedidas para outro contexto ou tenant são descartadas.
 */
@Component({
  selector: 'app-nist-assist-panel',
  standalone: true,
  imports: [DatePipe],
  template: `
    <section class="panel assist" [attr.aria-labelledby]="headingId()">
      <div class="hd"><h3 [id]="headingId()">{{ heading() }}</h3><span class="hint">sugestão para revisão — não é avaliação, aprovação nem conclusão</span></div>

      @if (availability(); as av) {
        <p class="avail"><span [class]="'badge ' + availabilityBadgeClass(av.state)">{{ av.label }}</span> <span class="muted">{{ av.detail }}</span></p>
      }

      <div class="actions">
        @for (a of actions(); track a.label) {
          <button type="button" class="ghost sm" (click)="generate(a.focus)" [disabled]="!canRequest() || running() !== null">
            {{ running() === (a.focus ?? 'Main') ? 'Gerando…' : a.label }}</button>
        }
        <button type="button" class="ghost xs" (click)="checkSources()" [disabled]="checking() || running() !== null">Conferir fontes</button>
        @if (running() !== null) { <button type="button" class="ghost xs" (click)="cancel()">Cancelar</button> }
      </div>
      @if (!canGenerate()) { <p class="muted">Seu papel permite conferir as fontes, mas não pedir sugestões (requer Manager ou TenantAdmin).</p> }
      @if (running() !== null) { <p class="muted" role="status">A IA está preparando a sugestão. A jornada continua disponível; nada será gravado sem a sua ação.</p> }
      @if (error(); as err) { <p class="notice warn" role="alert">{{ err }}</p> }
      @if (note(); as n) { <p class="notice" role="status">{{ n }}</p> }

      @if (!view() && context(); as cx) {
        <details class="sources" open>
          <summary>Fontes que a assistência usaria agora ({{ cx.sources.length }}) · {{ cx.summary }}</summary>
          <ul>@for (src of cx.sources; track src.key) {
            <li>
              <span class="mono">{{ src.key }}</span> · {{ src.kindLabel }} · <strong>{{ src.title }}</strong>
              <span [class]="'badge ' + basisBadgeClass(src.basis)">{{ src.basisLabel }}</span>
              @if (src.isDemo) { <span class="badge warn">Demonstração</span> }
              @if (!src.contentExamined) { <span class="badge warn">Conteúdo não examinado</span> }
              @if (src.limitation) { <span class="muted"> — {{ src.limitation }}</span> }
            </li>
          }</ul>
        </details>
      }

      @if (view(); as v) {
        <div class="result" [class.is-stale]="stale()">
          <p class="meta">
            <span [class]="'badge ' + (v.mode === 'Real' ? 'info' : 'warn')">{{ v.mode === 'Real' ? 'Sugestão da IA' : 'Demonstração' }}</span>
            @if (stale()) { <span class="badge bad">Desatualizada</span> }
            @if (v.reused) { <span class="badge neutral">Reaproveitada</span> }
            <span class="muted">{{ v.modeLabel }} · gerada em {{ v.generatedAt | date: 'dd/MM/yyyy HH:mm' }}@if (v.requestedByName) { a pedido de {{ v.requestedByName }} }</span>
          </p>
          @if (stale()) {
            <p class="notice warn" role="status">Esta sugestão foi gerada sobre um contexto que mudou depois (avaliação, evidências, procedimentos,
              achados ou planos). Gere de novo antes de aproveitar.</p>
          }
          <p class="hint">Base usada: {{ v.contextSummary }}</p>
          <p class="muted">{{ v.disclaimer }}</p>

          @if (applyAllTarget(); as t) {
            <div class="actions"><button type="button" class="primary sm" (click)="emitApply(v, t.field)" [disabled]="!canApply()">{{ t.label }}</button></div>
          }

          @for (s of v.sections; track s.key) {
            @if (s.items.length) {
              <h4>{{ s.title }} <span class="hint">{{ s.hint }}</span></h4>
              <ul class="items">
                @for (it of s.items; track $index) {
                  <li>
                    <span class="txt">{{ it.text }}</span>
                    <span [class]="'badge ' + basisBadgeClass(it.basis)">{{ it.basisLabel }}</span>
                    @for (k of it.sources; track k) {
                      @let src = sourceOf(v, k);
                      <button type="button" class="chip" (click)="openSource(src)" [disabled]="!src"
                        [attr.aria-label]="'Abrir a fonte ' + k + (src ? ': ' + src.title : '')" [title]="src?.title ?? k">{{ k }}@if (src) { · {{ short(src.title) }} }</button>
                    }
                  </li>
                }
              </ul>
              @for (t of targetsFor(s.key); track t.field) {
                @if (v.applicable[t.field] !== undefined) {
                  <button type="button" class="ghost xs" (click)="emitApply(v, t.field)" [disabled]="!canApply()">{{ t.label }}</button>
                }
              }
            }
          }

          @if (allowLevel()) {
            <h4>Sugestão de nível <span class="hint">escala 1–5 autoral do AEGIS{{ v.level ? ' · ' + v.level.methodologyVersion : '' }}</span></h4>
            @if (v.level; as lv) {
              <p><strong>{{ lv.level }} · {{ levelLabels[lv.level] ?? lv.levelName }}</strong> — {{ lv.rationale }}
                @for (k of lv.sources; track k) {
                  @let src = sourceOf(v, k);
                  <button type="button" class="chip" (click)="openSource(src)" [disabled]="!src" [attr.aria-label]="'Abrir a fonte ' + k">{{ k }}</button>
                }</p>
              <p class="hint">A confiança do modelo não prova exatidão; o nível só vale depois que você o revisa e grava.</p>
              @if (v.applicable['currentLevel'] !== undefined) {
                <button type="button" class="ghost xs" (click)="emitApply(v, 'currentLevel')" [disabled]="!canApply()">Usar o nível sugerido no rascunho</button>
              }
            } @else {
              <p class="muted">{{ v.levelNote ?? 'Sem sugestão de nível.' }}</p>
            }
          }

          @if (allowProcedures() && v.procedures.length) {
            <h4>Procedimentos de verificação sugeridos <span class="hint">entram como PLANEJADOS — planejar não é realizar</span></h4>
            <ul class="items">
              @for (p of v.procedures; track $index) {
                <li><label class="check"><input type="checkbox" [checked]="selected().has($index)" (change)="toggle($index)" [disabled]="!canApply()" />
                  <strong>{{ p.methodLabel }}</strong> — {{ p.procedure }}</label></li>
              }
            </ul>
            <button type="button" class="ghost xs" (click)="planSelected(v, false)" [disabled]="!canApply() || selected().size === 0 || planning()">
              {{ planning() ? 'Planejando…' : 'Planejar os selecionados' }}</button>
            @if (planError(); as pe) {
              <p class="notice warn" role="alert">{{ pe.message }}
                @if (pe.stale) { <button type="button" class="ghost xs" (click)="planSelected(v, true)">Revisei diante do estado atual — planejar assim mesmo</button> }</p>
            }
          }

          @if (v.validationNotes.length) {
            <div class="notes"><strong>O que a validação do servidor fez:</strong>
              <ul>@for (n of v.validationNotes; track n) { <li>{{ n }}</li> }</ul></div>
          }

          <details class="sources">
            <summary>Fontes consideradas ({{ v.sources.length }})</summary>
            <ul>
              @for (s of v.sources; track s.key) {
                <li>
                  <span class="mono">{{ s.key }}</span> · {{ s.kindLabel }} · <strong>{{ s.title }}</strong>
                  <span [class]="'badge ' + basisBadgeClass(s.basis)">{{ s.basisLabel }}</span>
                  @if (s.isDemo) { <span class="badge warn">Demonstração</span> }
                  @if (!s.contentExamined) { <span class="badge warn">Conteúdo não examinado</span> }
                  @if (s.status) { <span class="muted"> · {{ s.status }}</span> }
                  @if (s.date) { <span class="muted"> · {{ s.date }}</span> }
                  @if (s.detail) { <span class="detail">{{ s.detail }}</span> }
                  @if (s.limitation) { <span class="muted lim">{{ s.limitation }}</span> }
                  @if (canOpen(s)) { <button type="button" class="ghost xs" (click)="openSource(s)">Abrir</button> }
                </li>
              }
            </ul>
          </details>

          @if (canRequest()) {
            <div class="actions"><button type="button" class="ghost xs" (click)="generate(v.focus, false)" [disabled]="running() !== null">Gerar novamente</button></div>
          }
        </div>
      }
    </section>
  `,
  styles: [
    `
      .assist .avail { margin: 0 0 var(--sp-2); display: flex; flex-wrap: wrap; gap: 6px; align-items: baseline; }
      .actions { display: flex; flex-wrap: wrap; gap: var(--sp-2); align-items: center; margin: var(--sp-2) 0; }
      .meta { display: flex; flex-wrap: wrap; gap: 6px; align-items: baseline; margin: var(--sp-2) 0; }
      .result { border-top: 1px solid var(--line-2); margin-top: var(--sp-3); padding-top: var(--sp-2); }
      .result.is-stale { opacity: 0.85; }
      h4 { margin: var(--sp-3) 0 var(--sp-1); font-size: var(--fs-body); display: flex; flex-wrap: wrap; gap: 6px; align-items: baseline; }
      .items { list-style: none; padding: 0; margin: 0 0 var(--sp-2); display: flex; flex-direction: column; gap: 6px; }
      .items li { display: flex; flex-wrap: wrap; gap: 4px 6px; align-items: baseline; overflow-wrap: anywhere; }
      .items .txt { flex: 1 1 260px; min-width: 0; white-space: pre-line; }
      .chip { font-size: var(--fs-meta); padding: 1px 8px; border-radius: 999px; border: 1px solid var(--line-2); background: transparent; color: var(--text-2); cursor: pointer; max-width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
      .chip:focus-visible { outline: 2px solid var(--accent, currentColor); outline-offset: 2px; }
      .check { display: flex; gap: 6px; align-items: baseline; font-size: var(--fs-sm); }
      .notes { font-size: var(--fs-sm); margin: var(--sp-2) 0; }
      .notes ul { margin: 4px 0; padding-left: 18px; }
      .sources { margin-top: var(--sp-2); }
      .sources summary { cursor: pointer; font-weight: 500; overflow-wrap: anywhere; }
      .sources ul { list-style: none; padding: 0; margin: var(--sp-2) 0; display: flex; flex-direction: column; gap: var(--sp-2); }
      .sources li { overflow-wrap: anywhere; font-size: var(--fs-sm); }
      .sources .detail { display: block; color: var(--text-2); margin-top: 2px; }
      .sources .lim { display: block; }
      p { overflow-wrap: anywhere; }
    `,
  ],
})
export class NistAssistPanelComponent {
  private readonly nist = inject(NistService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly kind = input.required<NistAssistKind>();
  readonly ctx = input.required<NistCtx>();
  readonly code = input<string | null>(null);
  readonly findingId = input<string | null>(null);
  /** Papel de escrita: pode pedir sugestões. */
  readonly canGenerate = input(false);
  /** Pode aproveitar (aplicar ao rascunho, planejar procedimentos): papel de escrita e registro editável. */
  readonly canIncorporate = input(false);
  /** Muda quando os dados da tela mudam (contador ou versões): o painel confere se a sugestão exibida ainda vale. */
  readonly refresh = input<unknown>(0);
  readonly applyTargets = input<NistAssistApplyTarget[]>([]);
  readonly allowLevel = input(false);
  readonly allowProcedures = input(false);
  /** Seleção (avaliação · rodada · escopo) na URL, para os links que levam a outra subcategoria. */
  readonly linkParams = input<Record<string, string>>({});
  readonly apply = output<NistAssistApplyEvent>();
  readonly changed = output<void>();

  protected readonly availabilityBadgeClass = availabilityBadgeClass;
  protected readonly basisBadgeClass = basisBadgeClass;
  protected readonly levelLabels: Record<number, string> = MATURITY_LEVEL_LABELS;

  protected readonly availability = signal<NistAssistAvailability | null>(null);
  protected readonly view = signal<NistAssistView | null>(null);
  protected readonly stale = signal(false);
  protected readonly running = signal<NistAssistFocus | 'Main' | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly note = signal<string | null>(null);
  protected readonly context = signal<NistAssistContext | null>(null);
  protected readonly checking = signal(false);
  protected readonly selected = signal<Set<number>>(new Set());
  protected readonly planning = signal(false);
  protected readonly planError = signal<{ message: string; stale: boolean } | null>(null);

  protected readonly heading = computed(() =>
    this.kind() === 'Finding' ? 'Assistência da IA para este achado' : this.kind() === 'ExecutiveSummary' ? 'Assistência da IA — resumo executivo' : 'Assistência da IA');
  protected readonly headingId = computed(() => `assist-${this.kind()}-${this.findingId() ?? this.code() ?? 'rodada'}`);
  protected readonly actions = computed<AssistAction[]>(() =>
    this.kind() === 'Finding'
      ? [{ focus: 'Explain', label: 'Explicar o achado' }, { focus: 'Treatment', label: 'Sugerir tratamento' }]
      : this.kind() === 'ExecutiveSummary'
        ? [{ focus: null, label: 'Preparar resumo executivo' }]
        : [{ focus: null, label: 'Analisar as evidências' }]);
  protected readonly canRequest = computed(() => this.canGenerate() && (this.availability()?.canGenerate ?? false));
  protected readonly applyAllTarget = computed(() => this.applyTargets().find((t) => t.section === '*') ?? null);

  /** Geração do contexto: troca de seleção ou de tenant invalida tudo o que estava em curso. */
  private gen = 0;
  private sub: Subscription | null = null;
  private checkSub: Subscription | null = null;
  private readonly key = computed(() =>
    [this.kind(), this.ctx().assessmentId, this.ctx().cycleId, this.ctx().scopeId, this.code() ?? '', this.findingId() ?? ''].join('|'));

  constructor() {
    inject(DestroyRef).onDestroy(() => this.reset());
    effect(() => {
      this.key();
      untracked(() => {
        this.reset();
        this.loadAvailability();
      });
    });
    effect(() => {
      this.refresh();
      untracked(() => this.checkCurrent());
    });
  }

  protected canApply(): boolean {
    return this.canIncorporate() && !this.stale() && this.view() !== null;
  }

  protected targetsFor(section: string): NistAssistApplyTarget[] {
    return this.applyTargets().filter((t) => t.section === section);
  }

  protected sourceOf(v: NistAssistView, key: string): NistAssistSource | null {
    return v.sources.find((s) => s.key === key) ?? null;
  }

  protected short(title: string): string {
    return title.length <= 28 ? title : `${title.slice(0, 27)}…`;
  }

  protected canOpen(s: NistAssistSource): boolean {
    return sourceAnchorId(s) !== null || (!!s.link?.code && (s.link.target === 'Subcategory' || s.link.target === 'Finding'));
  }

  protected generate(focus: NistAssistFocus | null, reuse = true): void {
    if (this.running() !== null || !this.canRequest()) return;
    const c = this.ctx();
    const code = this.code();
    const findingId = this.findingId();
    const gen = this.gen;
    const tenant = this.auth.activeTenantId();
    this.running.set(focus ?? 'Main');
    this.error.set(null);
    this.note.set(null);
    this.planError.set(null);
    const request =
      this.kind() === 'Finding' && findingId
        ? this.nist.assistFinding(c, findingId, focus ?? 'Explain', reuse)
        : this.kind() === 'ExecutiveSummary'
          ? this.nist.assistExecutive(c, reuse)
          : this.nist.assistSubcategory(c, code ?? '', reuse);
    this.sub = request.subscribe({
      next: (v) => {
        if (!this.current(gen, tenant) || !this.sameTarget(v)) return;
        this.running.set(null);
        this.view.set(v);
        this.stale.set(!v.current);
        this.context.set(null);
        this.selected.set(new Set());
        if (v.reused) this.note.set('Sugestão reaproveitada: o contexto não mudou desde a última geração (nenhuma nova chamada à IA).');
      },
      error: (e: NistApiError) => {
        if (!this.current(gen, tenant)) return;
        this.running.set(null);
        this.error.set(assistErrorText(e.reason, e.message));
      },
    });
  }

  protected cancel(): void {
    if (this.running() === null) return;
    this.sub?.unsubscribe();
    this.sub = null;
    this.running.set(null);
    this.note.set('Geração cancelada. Nada foi gravado.');
  }

  /** Conferir as fontes que a assistência usaria agora — sem chamar a IA. */
  protected checkSources(): void {
    if (this.checking()) return;
    const gen = this.gen;
    const tenant = this.auth.activeTenantId();
    this.checking.set(true);
    this.checkSub?.unsubscribe();
    this.checkSub = this.contextRequest().subscribe({
      next: (cx) => {
        if (!this.current(gen, tenant)) return;
        this.checking.set(false);
        this.context.set(cx);
        const v = this.view();
        if (v) this.stale.set(!assistIsCurrent(v, cx.fingerprint));
        else this.context.set(cx);
      },
      error: (e: NistApiError) => {
        if (!this.current(gen, tenant)) return;
        this.checking.set(false);
        this.error.set(e.message);
      },
    });
  }

  protected emitApply(v: NistAssistView, field: string): void {
    if (!this.canApply() || v !== this.view()) return;
    this.apply.emit({ view: v, field });
  }

  protected toggle(i: number): void {
    const next = new Set(this.selected());
    if (next.has(i)) next.delete(i);
    else next.add(i);
    this.selected.set(next);
  }

  protected planSelected(v: NistAssistView, acknowledgeStale: boolean): void {
    if (this.planning() || v !== this.view() || this.selected().size === 0) return;
    const gen = this.gen;
    const tenant = this.auth.activeTenantId();
    const procedures = [...this.selected()].sort().map((i) => v.procedures[i]).filter((p) => !!p)
      .map((p) => ({ method: p.method, procedure: p.procedure }));
    this.planning.set(true);
    this.planError.set(null);
    this.nist.planProceduresFromAssistance(this.ctx(), this.code() ?? '', { assistanceId: v.id, procedures, acknowledgeStale }).subscribe({
      next: (created) => {
        if (!this.current(gen, tenant)) return;
        this.planning.set(false);
        this.selected.set(new Set());
        this.note.set(`${created.length} procedimento(s) planejado(s) a partir da sugestão. Planejar não comprova a realização: registre o resultado quando fizer.`);
        this.changed.emit();
      },
      error: (e: NistApiError) => {
        if (!this.current(gen, tenant)) return;
        this.planning.set(false);
        this.planError.set({ message: e.message, stale: e.status === 409 && e.reason === 'AssistanceStale' });
      },
    });
  }

  /** Abre a fonte na tela (âncora) ou leva à subcategoria — nunca a um endereço vindo da resposta. */
  protected openSource(s: NistAssistSource | null): void {
    if (!s) return;
    const id = sourceAnchorId(s);
    if (id && typeof document !== 'undefined') {
      const el = document.getElementById(id);
      if (el) {
        let d: HTMLElement | null = el;
        while (d) {
          if (d.tagName === 'DETAILS') (d as HTMLDetailsElement).open = true;
          d = d.parentElement;
        }
        if (!el.hasAttribute('tabindex')) el.setAttribute('tabindex', '-1');
        el.scrollIntoView({ block: 'center' });
        el.focus({ preventScroll: true });
        return;
      }
    }
    const code = s.link?.code;
    if (code && (s.link?.target === 'Subcategory' || s.link?.target === 'Finding')) {
      void this.router.navigate(['/nist', functionSlugOf(code), code], {
        queryParams: this.linkParams(),
        fragment: s.link.target === 'Finding' && s.link.id ? `achado-${s.link.id}` : undefined,
      });
    }
  }

  /** O pai avisou que os dados mudaram: confere (sem IA) se a sugestão exibida ainda é do contexto atual. */
  private checkCurrent(): void {
    const v = this.view();
    if (!v || this.stale()) return;
    const gen = this.gen;
    const tenant = this.auth.activeTenantId();
    this.checkSub?.unsubscribe();
    this.checkSub = this.contextRequest().subscribe({
      next: (cx) => {
        if (!this.current(gen, tenant) || this.view() !== v) return;
        this.stale.set(!assistIsCurrent(v, cx.fingerprint));
      },
      error: () => {
        /* sem a conferência, a sugestão continua marcada como estava; a gravação ainda confere no servidor */
      },
    });
  }

  private contextRequest() {
    const c = this.ctx();
    if (this.kind() === 'Finding') return this.nist.findingAssistContext(c, this.findingId() ?? '', this.view()?.focus ?? 'Explain');
    if (this.kind() === 'ExecutiveSummary') return this.nist.executiveAssistContext(c);
    return this.nist.subcategoryAssistContext(c, this.code() ?? '');
  }

  private loadAvailability(): void {
    const tenant = this.auth.activeTenantId();
    if (availabilityCache && availabilityCache.tenant === tenant && Date.now() - availabilityCache.at < 60_000) {
      this.availability.set(availabilityCache.value);
      return;
    }
    const gen = this.gen;
    this.nist.assistAvailability().subscribe({
      next: (av) => {
        availabilityCache = { tenant, at: Date.now(), value: av };
        if (this.current(gen, tenant)) this.availability.set(av);
      },
      error: () => {
        if (this.current(gen, tenant))
          this.availability.set({ state: 'Unknown', label: 'Estado da IA indisponível', detail: 'A jornada segue manual.', canGenerate: false });
      },
    });
  }

  /** A resposta pertence a esta tela (mesma seleção e tenant, painel vivo)? */
  private current(gen: number, tenant: string | null): boolean {
    return gen === this.gen && tenant === this.auth.activeTenantId();
  }

  private sameTarget(v: NistAssistView): boolean {
    const c = this.ctx();
    return v.kind === this.kind() && v.assessmentId === c.assessmentId && v.cycleId === c.cycleId && v.scopeId === c.scopeId
      && (this.kind() !== 'Subcategory' || v.subcategoryCode === this.code())
      && (this.kind() !== 'Finding' || v.findingId === this.findingId());
  }

  private reset(): void {
    this.gen++;
    this.sub?.unsubscribe();
    this.sub = null;
    this.checkSub?.unsubscribe();
    this.checkSub = null;
    this.view.set(null);
    this.stale.set(false);
    this.running.set(null);
    this.error.set(null);
    this.note.set(null);
    this.context.set(null);
    this.checking.set(false);
    this.selected.set(new Set());
    this.planning.set(false);
    this.planError.set(null);
  }
}

/** Só para testes: esquece o estado da IA guardado entre painéis. */
export function resetAssistAvailabilityCache(): void {
  availabilityCache = null;
}

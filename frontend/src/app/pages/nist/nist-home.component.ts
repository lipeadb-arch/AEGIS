import { DatePipe } from '@angular/common';
import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import {
  NIST_FUNCTIONS,
  NistAssessment,
  NistCycle,
  NistProfile,
  NistSeedMode,
  NistSelection,
  averageText,
  cycleIsClosed,
  cyclePeriodProblem,
  cyclePeriodText,
  cycleStatusLabel,
  gapText,
  nistFunctionTitle,
  periodKindLabel,
  resolveSelection,
  scopeProgressText,
  seedModeLabel,
  selectionKey,
  selectionParams,
  suggestNextCycle,
} from '../../models/nist.models';
import { AuthService } from '../../services/auth.service';
import { NistApiError, NistSelectionService, NistService, ctxOf } from '../../services/nist.service';
import { NistAuditPanelComponent } from './nist-audit-panel.component';
import { NistDashboardPanelComponent } from './nist-dashboard-panel.component';
import { NistFindingsPanelComponent } from './nist-findings-panel.component';
import { NistImportPanelComponent } from './nist-import-panel.component';
import { NistPeriodFieldsComponent, NistPeriodValue } from './nist-period-fields.component';
import { NistPublishPanelComponent } from './nist-publish-panel.component';

type HomeTab = 'painel' | 'achados' | 'publicacao' | 'importar' | 'trilha';
const TABS: { id: HomeTab; label: string }[] = [
  { id: 'painel', label: 'Painel' },
  { id: 'achados', label: 'Achados e planos' },
  { id: 'publicacao', label: 'Publicação e relatórios' },
  { id: 'importar', label: 'Importar CSV' },
  { id: 'trilha', label: 'Trilha de alterações' },
];

const today = () => new Date().toISOString().slice(0, 10);

/**
 * [AEGIS-NIST-JOURNEY-01/02] AEGIS NIST — entrada da avaliação organizacional pelo NIST CSF 2.0 (referência funcional:
 * CSF Profile — avaliação, rodadas, escopo, perfil atual × alvo, procedimentos, achados, planos, publicação, histórico).
 * Tudo persistido no backend; o navegador só lembra a última escolha. A seleção é SEMPRE avaliação → rodada → escopo:
 * uma resposta pedida para outra rodada nunca preenche a atual.
 *
 * Maturidade atual × alvo (escala 1–5 do AEGIS) NÃO é o score de postura do ambiente (AEGIS Score) nem o score do KNIGHT.
 */
@Component({
  selector: 'app-nist-home',
  standalone: true,
  imports: [
    RouterLink,
    FormsModule,
    DatePipe,
    NistPeriodFieldsComponent,
    NistDashboardPanelComponent,
    NistFindingsPanelComponent,
    NistPublishPanelComponent,
    NistImportPanelComponent,
    NistAuditPanelComponent,
  ],
  template: `
    <section class="page">
      <header class="page-head">
        <div>
          <p class="page-eyebrow">AEGIS NIST · NIST CSF 2.0</p>
          <h1>AEGIS NIST</h1>
          <p class="page-desc">
            Avaliação organizacional pelo NIST CSF 2.0 em rodadas: procedimentos (examinar, entrevistar, testar), evidências,
            <strong>maturidade atual e alvo</strong>, achados, planos de tratamento, revisão humana e publicação. A IA ajuda a
            interpretar; a decisão é do analista.
          </p>
        </div>
        @if (canWrite() && assessments().length > 0) {
          <div class="page-actions">
            <button type="button" class="ghost" (click)="toggleCreate()" [attr.aria-expanded]="creating()">Nova avaliação</button>
          </div>
        }
      </header>

      @if (loading()) {
        <div class="panel"><div class="state" role="status"><span class="spinner" aria-hidden="true"></span><p>Carregando as avaliações…</p></div></div>
      } @else if (error()) {
        <div class="panel"><div class="state error" role="alert"><p class="err">{{ error() }}</p>
          <button type="button" class="ghost" (click)="load()">Tentar novamente</button></div></div>
      } @else {
        @if (creating() || assessments().length === 0) {
          <section class="panel" aria-labelledby="nist-new">
            <div class="hd"><h3 id="nist-new">{{ assessments().length === 0 ? 'Comece uma avaliação' : 'Nova avaliação' }}</h3></div>
            @if (!canWrite()) {
              <p class="muted">Ainda não há avaliação NIST neste ambiente. Seu papel permite consultar; peça a um Manager ou TenantAdmin para criá-la.</p>
            } @else {
              <form class="form-grid" (ngSubmit)="create()">
                <label class="field"><span class="field-label">Nome da avaliação</span>
                  <input name="name" [(ngModel)]="form.name" required maxlength="200" placeholder="Ex.: Avaliação NIST CSF 2.0 — 2026" /></label>
                <label class="field"><span class="field-label">Início</span><input type="date" name="start" [(ngModel)]="form.startDate" /></label>
                <label class="field"><span class="field-label">Término</span><input type="date" name="end" [(ngModel)]="form.endDate" /></label>
                <label class="field wide"><span class="field-label">Objetivo e contexto (opcional)</span>
                  <textarea name="desc" rows="2" maxlength="2000" [(ngModel)]="form.description"></textarea></label>
                <label class="field"><span class="field-label">Escopo inicial</span>
                  <input name="scope" [(ngModel)]="form.scopeName" required maxlength="200" placeholder="Ex.: Matriz e operação em nuvem" /></label>
                <label class="field wide"><span class="field-label">O que entra e o que fica fora do escopo (opcional)</span>
                  <textarea name="scopeDesc" rows="2" maxlength="2000" [(ngModel)]="form.scopeDescription"></textarea></label>
                <label class="field"><span class="field-label">Primeira rodada</span>
                  <input name="cyc" [(ngModel)]="form.cycleName" maxlength="120" placeholder="Ex.: T4 2026" /></label>
                <div class="wide"><app-nist-period-fields [(value)]="firstPeriod" /></div>
                <div class="actions wide">
                  <button type="submit" class="primary" [disabled]="busy() || !form.name.trim() || !form.scopeName.trim() || !!firstPeriodProblem()">{{ busy() ? 'Criando…' : 'Criar avaliação' }}</button>
                  @if (assessments().length > 0) { <button type="button" class="ghost" (click)="toggleCreate()">Cancelar</button> }
                </div>
              </form>
            }
            @if (formError()) { <p class="notice error" role="alert">{{ formError() }}</p> }
          </section>
        }

        @if (selection(); as sel) {
          <section class="panel" aria-labelledby="nist-sel">
            <div class="hd"><h3 id="nist-sel">Avaliação, rodada e escopo</h3><span class="hint">metodologia {{ sel.assessment.methodologyVersion }}</span></div>
            <div class="sel-grid">
              <label class="field"><span class="field-label">Avaliação</span>
                <select [ngModel]="sel.assessment.id" (ngModelChange)="choose($event, null, null)" name="assessment">
                  @for (a of assessments(); track a.id) { <option [value]="a.id">{{ a.name }}</option> }
                </select></label>
              <label class="field"><span class="field-label">Rodada</span>
                <select [ngModel]="sel.cycle?.id ?? ''" (ngModelChange)="choose(sel.assessment.id, $event, sel.scope?.id ?? null)" name="cycle" [disabled]="!sel.assessment.cycles?.length">
                  @for (c of sel.assessment.cycles ?? []; track c.id) { <option [value]="c.id">{{ c.name }} · {{ cyclePeriodText(c) }}{{ c.status === 'Closed' ? ' · encerrada' : '' }}</option> }
                </select></label>
              <label class="field"><span class="field-label">Escopo</span>
                <select [ngModel]="sel.scope?.id ?? ''" (ngModelChange)="choose(sel.assessment.id, sel.cycle?.id ?? null, $event)" name="scope" [disabled]="sel.assessment.scopes.length === 0">
                  @for (s of sel.assessment.scopes; track s.id) { <option [value]="s.id">{{ s.name }}</option> }
                </select></label>
            </div>
            @if (sel.cycle; as cy) {
              <p class="meta">
                Rodada <strong>{{ cy.name }}</strong> · {{ periodKindLabel(cy.periodKind) }} · {{ cyclePeriodText(cy) }} ·
                <span [class]="'badge ' + (cy.status === 'Closed' ? 'neutral' : 'info')">{{ cycleStatusLabel(cy.status) }}</span>
                @if (cy.seedFromCycleName) { · origem: {{ cy.seedFromCycleName }} ({{ seedModeLabel(cy.seedMode).toLowerCase() }}) }
                · {{ cy.publications }} publicação(ões)
                @if (cy.closedAt) { · encerrada por {{ cy.closedByName ?? '—' }} em {{ cy.closedAt | date: 'dd/MM/yyyy' }} }
              </p>
            } @else {
              <p class="notice warn">Esta avaliação ainda não tem rodada. Crie a primeira para começar.</p>
            }
            @if (sel.assessment.description) { <p class="muted">{{ sel.assessment.description }}</p> }
            @if (sel.scope; as sc) {
              @if (sc.cycleId && sc.cycleId === sel.cycle?.id) {
                <p><strong>{{ scopeProgressText(sc) }}</strong></p>
              }
              @if (sc.description) { <p class="muted">Escopo: {{ sc.description }}</p> }
            }
            @if (canWrite()) {
              <div class="actions">
                <button type="button" class="ghost sm" (click)="openCycleForm(sel.assessment)" [attr.aria-expanded]="cycleFormOpen()">Nova rodada</button>
                @if (sel.cycle; as cy) {
                  <button type="button" class="ghost sm" (click)="toggleCycle(sel.assessment, cy)" [disabled]="busy()">
                    {{ cy.status === 'Closed' ? 'Reabrir a rodada' : 'Encerrar a rodada' }}</button>
                }
                <button type="button" class="ghost sm" (click)="addingScope.set(!addingScope())" [attr.aria-expanded]="addingScope()">Novo escopo</button>
              </div>
              @if (cycleFormOpen()) {
                <form class="form-grid sub" (ngSubmit)="createCycle(sel.assessment)" aria-label="Nova rodada">
                  <label class="field"><span class="field-label">Nome da rodada</span><input name="cn" [(ngModel)]="cycleForm.name" maxlength="120" required /></label>
                  <div class="wide"><app-nist-period-fields [(value)]="cyclePeriod" /></div>
                  @if (sel.assessment.cycles?.length) {
                    <label class="field"><span class="field-label">Rodada anterior de referência</span>
                      <select name="cs" [(ngModel)]="cycleForm.seedFrom">@for (c of sel.assessment.cycles ?? []; track c.id) { <option [value]="c.id">{{ c.name }}</option> }</select></label>
                    <fieldset class="field wide seed"><legend class="field-label">O que trazer da rodada anterior</legend>
                      @for (m of seedModes; track m) {
                        <label><input type="radio" name="sm" [value]="m" [(ngModel)]="cycleForm.seedMode" /> {{ seedModeLabel(m) }}</label>
                      }
                      <p class="hint">Rascunho trazido de outra rodada fica identificado e aguarda confirmação: não conta como nova revisão humana nem entra nas médias.</p>
                    </fieldset>
                  }
                  <div class="actions wide">
                    <button type="submit" class="primary sm" [disabled]="busy() || !cycleForm.name.trim() || !!cyclePeriodProblem()">Criar rodada</button>
                    <button type="button" class="ghost sm" (click)="cycleFormOpen.set(false)">Cancelar</button>
                  </div>
                </form>
              }
              @if (addingScope()) {
                <form class="form-grid sub" (ngSubmit)="addScope(sel.assessment)" aria-label="Novo escopo">
                  <label class="field"><span class="field-label">Nome do novo escopo</span><input name="ns" [(ngModel)]="scopeForm.name" maxlength="200" required /></label>
                  <label class="field wide"><span class="field-label">Descrição (opcional)</span><input name="nsd" [(ngModel)]="scopeForm.description" maxlength="2000" /></label>
                  <div class="actions wide">
                    <button type="submit" class="primary sm" [disabled]="busy() || !scopeForm.name.trim()">Criar escopo</button>
                    <button type="button" class="ghost sm" (click)="addingScope.set(false)">Cancelar</button>
                  </div>
                </form>
              }
            }
            @if (formError() && !creating()) { <p class="notice error" role="alert">{{ formError() }}</p> }
          </section>

          @if (ctx(); as c) {
            <section class="section" aria-labelledby="nist-fns">
              <div class="section-head"><h2 id="nist-fns">Funções</h2></div>
              <div class="fn-grid">
                @for (f of functions; track f.code) {
                  @let p = functionProfile(f.code);
                  <a class="card fn" [routerLink]="['/nist', f.slug]" [queryParams]="params()">
                    <span class="k"><span class="code">{{ f.code }}</span> {{ nistFunctionTitle(f) }}</span>
                    @if (p) {
                      <span class="v sm">{{ p.evaluated }}<small> de {{ p.subcategories }} avaliadas</small></span>
                      <span class="muted">Atual {{ averageText(p.current) }} · Alvo {{ averageText(p.target) }} · Lacuna {{ gapText(p.gap) }}</span>
                    } @else {
                      <span class="muted">{{ profileError() ? 'Indisponível' : 'Carregando…' }}</span>
                    }
                  </a>
                }
              </div>
            </section>

            <nav class="tabbar" aria-label="Seções da avaliação">
              @for (t of tabs; track t.id) {
                <a [routerLink]="[]" [queryParams]="tabParams(t.id)" [class.on]="tab() === t.id" [attr.aria-current]="tab() === t.id ? 'page' : null">{{ t.label }}</a>
              }
            </nav>

            @switch (tab()) {
              @case ('achados') { <app-nist-findings-panel [ctx]="c" [params]="params()" [initialStatus]="statusFilter()" /> }
              @case ('publicacao') {
                <app-nist-publish-panel [ctx]="c" [cycles]="sel.assessment.cycles ?? []" [params]="params()" [canWrite]="canWrite()" (publishedChange)="afterPublish()" />
              }
              @case ('importar') { <app-nist-import-panel [ctx]="c" [canWrite]="canWrite()" [closed]="closed()" (applied)="reloadProfile()" /> }
              @case ('trilha') { <app-nist-audit-panel [assessmentId]="c.assessmentId" [cycleId]="c.cycleId" [scopeId]="c.scopeId" /> }
              @default {
                @if (profileError()) {
                  <p class="notice error" role="alert">{{ profileError() }}</p>
                } @else if (profile()) {
                  <app-nist-dashboard-panel [profile]="profile()!" [params]="params()" />
                } @else {
                  <p class="muted" role="status">Carregando o painel…</p>
                }
              }
            }
          }

          <section class="notice" aria-label="Maturidade, postura e KNIGHT">
            <strong>Três leituras, nunca somadas.</strong> A maturidade desta avaliação (1 a 5, metodologia autoral do AEGIS — o NIST
            CSF descreve resultados, não define nota) mede a prática organizacional. O <strong>score de postura do ambiente</strong>
            (0 a 100, AEGIS Score) vem dos controles avaliados por telemetria e documentos; o <strong>score do KNIGHT</strong> vem do
            assessment técnico de configurações.
          </section>
        }
      }
    </section>
  `,
  styles: [
    `
      .form-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 220px), 1fr)); gap: var(--sp-3); }
      .form-grid .wide { grid-column: 1 / -1; }
      .form-grid.sub { margin-top: var(--sp-3); padding-top: var(--sp-3); border-top: 1px solid var(--line-2); }
      .actions { display: flex; flex-wrap: wrap; gap: var(--sp-2); }
      .seed { border: 0; padding: 0; margin: 0; display: flex; flex-direction: column; gap: 6px; font-size: var(--fs-sm); }
      .seed label { display: flex; gap: 6px; align-items: flex-start; }
      .sel-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 220px), 1fr)); gap: var(--sp-3); margin-bottom: var(--sp-3); }
      .meta { font-size: var(--fs-meta); color: var(--muted); margin: 0 0 var(--sp-2); }
      .fn-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 280px), 1fr)); gap: var(--sp-4); }
      .fn { display: flex; flex-direction: column; gap: 6px; text-decoration: none; color: inherit; }
      .fn .v.sm { font-size: 24px; }
      .code { padding: 1px 6px; border-radius: 6px; border: 1px solid var(--line-strong); font-family: var(--mono); font-size: var(--fs-meta); }
      section > p { margin: 0 0 var(--sp-2); }
    `,
  ],
})
export class NistHomeComponent implements OnInit {
  private readonly nist = inject(NistService);
  private readonly memory = inject(NistSelectionService);
  private readonly auth = inject(AuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  protected readonly functions = NIST_FUNCTIONS;
  protected readonly tabs = TABS;
  protected readonly seedModes: NistSeedMode[] = ['Reference', 'Draft', 'None'];
  protected readonly nistFunctionTitle = nistFunctionTitle;
  protected readonly averageText = averageText;
  protected readonly gapText = gapText;
  protected readonly scopeProgressText = scopeProgressText;
  protected readonly cyclePeriodText = cyclePeriodText;
  protected readonly cycleStatusLabel = cycleStatusLabel;
  protected readonly periodKindLabel = periodKindLabel;
  protected readonly seedModeLabel = seedModeLabel;

  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly assessments = signal<NistAssessment[]>([]);
  protected readonly selection = signal<NistSelection | null>(null);
  protected readonly profile = signal<NistProfile | null>(null);
  protected readonly profileError = signal<string | null>(null);
  protected readonly creating = signal(false);
  protected readonly addingScope = signal(false);
  protected readonly cycleFormOpen = signal(false);
  protected readonly busy = signal(false);
  protected readonly formError = signal<string | null>(null);
  protected readonly tab = signal<HomeTab>('painel');
  protected readonly statusFilter = signal<string | null>(null);

  protected form = { name: '', description: '', startDate: '', endDate: '', scopeName: '', scopeDescription: '', cycleName: '' };
  protected firstPeriod: NistPeriodValue = { kind: 'Quarterly', ...suggestNextCycle([], 'Quarterly', today()) };
  protected scopeForm = { name: '', description: '' };
  protected cycleForm: { name: string; seedFrom: string; seedMode: NistSeedMode } = { name: '', seedFrom: '', seedMode: 'Reference' };
  protected cyclePeriod: NistPeriodValue = { kind: 'Monthly', start: '', end: '' };

  protected readonly canWrite = computed(() => ['Manager', 'TenantAdmin'].includes(this.auth.activeRole() ?? ''));
  protected readonly params = computed(() => selectionParams(this.selection()));
  /** Contexto estável (mesmo objeto enquanto a seleção não muda): os painéis recarregam só quando ele muda. */
  protected readonly ctx = computed(() => ctxOf(this.selection()));
  protected readonly closed = computed(() => cycleIsClosed(this.selection()?.cycle));

  private selectionTicket = 0;
  private listTicket = 0;
  private profileSub: Subscription | null = null;
  private listSub: Subscription | null = null;
  private destroyed = false;
  private profileKey = '';

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.profileSub?.unsubscribe();
      this.listSub?.unsubscribe();
    });
    this.route.queryParamMap.pipe(takeUntilDestroyed()).subscribe((q) => {
      const t = q.get('aba') as HomeTab | null;
      this.tab.set(TABS.some((x) => x.id === t) ? (t as HomeTab) : 'painel');
      this.statusFilter.set(q.get('status'));
    });
  }

  ngOnInit(): void {
    this.load();
  }

  load(reselect?: { assessmentId: string; cycleId: string | null; scopeId: string | null }): void {
    const tenant = this.auth.activeTenantId();
    const ticket = ++this.listTicket;
    this.listSub?.unsubscribe();
    if (!reselect) this.loading.set(true);
    this.error.set(null);
    const current = () => this.alive(tenant) && ticket === this.listTicket;
    this.listSub = this.nist.list().subscribe({
      next: (list) => {
        if (!current()) return;
        this.assessments.set(list);
        const q = this.route.snapshot.queryParamMap;
        const requested = reselect ?? { assessmentId: q.get('avaliacao'), cycleId: q.get('rodada'), scopeId: q.get('escopo') };
        this.apply(resolveSelection(list, requested, this.memory.read(this.auth.activeTenantId())));
        this.loading.set(false);
      },
      error: (e: Error) => {
        if (!current()) return;
        this.error.set(e.message);
        this.loading.set(false);
      },
    });
  }

  protected choose(assessmentId: string, cycleId: string | null, scopeId: string | null): void {
    this.apply(resolveSelection(this.assessments(), { assessmentId, cycleId, scopeId }, null));
  }

  protected functionProfile(code: string) {
    return this.profile()?.functions.find((f) => f.code === code) ?? null;
  }

  protected tabParams(t: HomeTab): Record<string, string | null> {
    return { ...this.params(), aba: t === 'painel' ? null : t, status: null };
  }

  protected firstPeriodProblem(): string | null {
    return cyclePeriodProblem(this.firstPeriod.kind, this.firstPeriod.start, this.firstPeriod.end);
  }

  protected cyclePeriodProblem(): string | null {
    return cyclePeriodProblem(this.cyclePeriod.kind, this.cyclePeriod.start, this.cyclePeriod.end);
  }

  protected toggleCreate(): void {
    this.creating.update((v) => !v);
    this.formError.set(null);
  }

  protected openCycleForm(a: NistAssessment): void {
    if (this.cycleFormOpen()) {
      this.cycleFormOpen.set(false);
      return;
    }
    const cycles = a.cycles ?? [];
    const kind = cycles[0]?.periodKind ?? 'Monthly';
    const next = suggestNextCycle(cycles, kind, today());
    this.cycleForm = { name: next.name, seedFrom: cycles[0]?.id ?? '', seedMode: 'Reference' };
    this.cyclePeriod = { kind, start: next.start, end: next.end };
    this.formError.set(null);
    this.cycleFormOpen.set(true);
  }

  protected create(): void {
    if (this.busy()) return;
    this.busy.set(true);
    this.formError.set(null);
    const tenant = this.auth.activeTenantId();
    const ticket = this.selectionTicket;
    const f = this.form;
    this.nist
      .create({
        name: f.name.trim(),
        description: f.description.trim() || null,
        startDate: f.startDate || null,
        endDate: f.endDate || null,
        initialScopeName: f.scopeName.trim(),
        initialScopeDescription: f.scopeDescription.trim() || null,
        initialCycleName: f.cycleName.trim() || null,
        initialCyclePeriodKind: this.firstPeriod.kind,
        initialCyclePeriodStart: this.firstPeriod.start,
        initialCyclePeriodEnd: this.firstPeriod.end,
      })
      .subscribe({
        next: (a) => {
          // A avaliação existe no servidor; só é aberta se o usuário não escolheu outra enquanto aguardava.
          if (!this.alive(tenant)) return;
          this.busy.set(false);
          this.creating.set(false);
          this.form = { name: '', description: '', startDate: '', endDate: '', scopeName: '', scopeDescription: '', cycleName: '' };
          this.assessments.update((list) => [a, ...list]);
          if (ticket === this.selectionTicket) this.apply(resolveSelection(this.assessments(), { assessmentId: a.id, cycleId: null, scopeId: null }, null));
        },
        error: (e: NistApiError) => {
          if (!this.alive(tenant)) return;
          this.busy.set(false);
          this.formError.set(e.message);
        },
      });
  }

  protected createCycle(a: NistAssessment): void {
    if (this.busy() || this.cyclePeriodProblem()) return;
    this.busy.set(true);
    this.formError.set(null);
    const tenant = this.auth.activeTenantId();
    const hasPrevious = (a.cycles ?? []).length > 0;
    const seedMode: NistSeedMode = hasPrevious ? this.cycleForm.seedMode : 'None';
    this.nist
      .createCycle(a.id, {
        name: this.cycleForm.name.trim(),
        periodKind: this.cyclePeriod.kind,
        periodStart: this.cyclePeriod.start,
        periodEnd: this.cyclePeriod.end,
        seedFromCycleId: seedMode === 'None' ? null : this.cycleForm.seedFrom || null,
        seedMode,
      })
      .subscribe({
        next: (c) => {
          if (!this.alive(tenant)) return;
          this.busy.set(false);
          this.cycleFormOpen.set(false);
          // Relê a lista (andamento por rodada vem do servidor) e abre a nova rodada no mesmo escopo.
          this.load({ assessmentId: a.id, cycleId: c.id, scopeId: this.selection()?.scope?.id ?? null });
        },
        error: (e: NistApiError) => {
          if (!this.alive(tenant)) return;
          this.busy.set(false);
          this.formError.set(e.message);
        },
      });
  }

  protected toggleCycle(a: NistAssessment, c: NistCycle): void {
    if (this.busy()) return;
    const closing = c.status !== 'Closed';
    if (closing && !confirm(`Encerrar a rodada "${c.name}"? Avaliações, procedimentos e achados ficam bloqueados; planos continuam.`)) return;
    this.busy.set(true);
    this.formError.set(null);
    const tenant = this.auth.activeTenantId();
    this.nist.setCycleStatus(a.id, c.id, closing ? 'Closed' : 'Open', c.version).subscribe({
      next: () => {
        if (!this.alive(tenant)) return;
        this.busy.set(false);
        this.load({ assessmentId: a.id, cycleId: c.id, scopeId: this.selection()?.scope?.id ?? null });
      },
      error: (e: NistApiError) => {
        if (!this.alive(tenant)) return;
        this.busy.set(false);
        this.formError.set(e.message);
      },
    });
  }

  protected addScope(a: NistAssessment): void {
    if (this.busy()) return;
    this.busy.set(true);
    const tenant = this.auth.activeTenantId();
    this.nist.addScope(a.id, this.scopeForm.name.trim(), this.scopeForm.description.trim() || null).subscribe({
      next: (scope) => {
        if (!this.alive(tenant)) return;
        this.busy.set(false);
        this.addingScope.set(false);
        this.scopeForm = { name: '', description: '' };
        this.load({ assessmentId: a.id, cycleId: this.selection()?.cycle?.id ?? null, scopeId: scope.id });
      },
      error: (e: NistApiError) => {
        if (!this.alive(tenant)) return;
        this.busy.set(false);
        this.formError.set(e.message);
      },
    });
  }

  protected afterPublish(): void {
    const sel = this.selection();
    if (sel) this.load({ assessmentId: sel.assessment.id, cycleId: sel.cycle?.id ?? null, scopeId: sel.scope?.id ?? null });
  }

  protected reloadProfile(): void {
    this.profileKey = '';
    this.loadProfile(this.selection());
  }

  /**
   * Nova seleção: cancela o perfil da anterior e descarta qualquer resposta (ou erro) dela. Respostas que chegam depois de
   * sair da tela, de trocar o tenant ou de trocar a rodada não navegam nem repovoam.
   */
  private apply(sel: NistSelection | null): void {
    ++this.selectionTicket;
    this.selection.set(sel);
    if (!sel) {
      this.profileSub?.unsubscribe();
      this.profile.set(null);
      return;
    }
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { ...selectionParams(sel), rodada: sel.cycle?.id ?? null, escopo: sel.scope?.id ?? null },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
    if (sel.scope) this.memory.write(this.auth.activeTenantId(), sel.assessment.id, sel.scope.id, sel.cycle?.id ?? null);
    this.loadProfile(sel);
  }

  private loadProfile(sel: NistSelection | null): void {
    const key = selectionKey(sel);
    if (key === this.profileKey) return;
    this.profileKey = key;
    const ticket = this.selectionTicket;
    this.profileSub?.unsubscribe();
    this.profileSub = null;
    this.profile.set(null);
    this.profileError.set(null);
    const c = ctxOf(sel);
    if (!c) return;
    const tenant = this.auth.activeTenantId();
    const current = () => this.alive(tenant) && ticket === this.selectionTicket && key === this.profileKey;
    this.profileSub = this.nist.profile(c).subscribe({
      next: (p) => {
        if (current() && p.scopeId === c.scopeId && (!p.cycleId || p.cycleId === c.cycleId)) this.profile.set(p);
      },
      error: (e: Error) => {
        if (current()) this.profileError.set(e.message);
      },
    });
  }

  /** A tela ainda existe e continua no tenant em que a requisição foi feita. */
  private alive(tenant: string | null): boolean {
    return !this.destroyed && tenant === this.auth.activeTenantId();
  }
}

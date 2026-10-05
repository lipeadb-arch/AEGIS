import { DatePipe } from '@angular/common';
import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import {
  NIST_FUNCTIONS,
  NistAssessment,
  NistProfile,
  NistSelection,
  averageText,
  gapText,
  levelLabel,
  nistFunctionTitle,
  profileBasisText,
  resolveSelection,
  scopeCoverage,
  scopeProgressText,
  selectionParams,
} from '../../models/nist.models';
import { AuthService } from '../../services/auth.service';
import { NistApiError, NistSelectionService, NistService } from '../../services/nist.service';

/**
 * [AEGIS-NIST-JOURNEY-01] AEGIS NIST — entrada da avaliação organizacional pelo NIST CSF 2.0 (referência de organização:
 * CSF Profile — avaliação e escopo, perfil atual × alvo, função → categoria → subcategoria, lacunas). Tudo persistido no
 * backend; nada fica só no navegador além da lembrança da última escolha.
 *
 * Maturidade atual × alvo (escala 1–5 do AEGIS) NÃO é o score de postura do ambiente (AEGIS Score): a tela diz isso e
 * leva cada leitura ao seu lugar.
 */
@Component({
  selector: 'app-nist-home',
  standalone: true,
  imports: [RouterLink, FormsModule, DatePipe],
  template: `
    <section class="page">
      <header class="page-head">
        <div>
          <p class="page-eyebrow">AEGIS NIST · NIST CSF 2.0</p>
          <h1>AEGIS NIST</h1>
          <p class="page-desc">
            Avaliação organizacional pelo NIST CSF 2.0: políticas, processos, entrevistas e sinais técnicos sustentam a
            <strong>maturidade atual e o alvo</strong> de cada resultado. A IA ajuda a interpretar; a decisão é do analista.
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
                <div class="actions wide">
                  <button type="submit" class="primary" [disabled]="busy() || !form.name.trim() || !form.scopeName.trim()">{{ busy() ? 'Criando…' : 'Criar avaliação' }}</button>
                  @if (assessments().length > 0) { <button type="button" class="ghost" (click)="toggleCreate()">Cancelar</button> }
                </div>
              </form>
            }
            @if (formError()) { <p class="notice error" role="alert">{{ formError() }}</p> }
          </section>
        }

        @if (selection(); as sel) {
          <section class="panel" aria-labelledby="nist-sel">
            <div class="hd"><h3 id="nist-sel">Avaliação e escopo</h3><span class="hint">metodologia {{ sel.assessment.methodologyVersion }}</span></div>
            <div class="sel-grid">
              <label class="field"><span class="field-label">Avaliação</span>
                <select [ngModel]="sel.assessment.id" (ngModelChange)="choose($event, null)" name="assessment">
                  @for (a of assessments(); track a.id) { <option [value]="a.id">{{ a.name }}</option> }
                </select></label>
              <label class="field"><span class="field-label">Escopo</span>
                <select [ngModel]="sel.scope?.id ?? ''" (ngModelChange)="choose(sel.assessment.id, $event)" name="scope" [disabled]="sel.assessment.scopes.length === 0">
                  @for (s of sel.assessment.scopes; track s.id) { <option [value]="s.id">{{ s.name }}</option> }
                </select></label>
            </div>
            <p class="meta">
              {{ statusLabel(sel.assessment.status) }}
              @if (sel.assessment.startDate) { · início {{ sel.assessment.startDate | date: 'dd/MM/yyyy' }} }
              @if (sel.assessment.endDate) { · término {{ sel.assessment.endDate | date: 'dd/MM/yyyy' }} }
              @if (sel.assessment.lastReviewedAt) { · última revisão {{ sel.assessment.lastReviewedAt | date: 'dd/MM/yyyy HH:mm' }} }
            </p>
            @if (sel.assessment.description) { <p class="muted">{{ sel.assessment.description }}</p> }
            @if (sel.scope; as sc) {
              <p><strong>{{ scopeProgressText(sc) }}</strong> · cobertura da avaliação {{ coverageText(sc) }}</p>
              @if (sc.description) { <p class="muted">Escopo: {{ sc.description }}</p> }
            }
            @if (canWrite()) {
              @if (addingScope()) {
                <form class="form-grid" (ngSubmit)="addScope(sel.assessment)">
                  <label class="field"><span class="field-label">Nome do novo escopo</span><input name="ns" [(ngModel)]="scopeForm.name" maxlength="200" required /></label>
                  <label class="field wide"><span class="field-label">Descrição (opcional)</span><input name="nsd" [(ngModel)]="scopeForm.description" maxlength="2000" /></label>
                  <div class="actions wide">
                    <button type="submit" class="primary sm" [disabled]="busy() || !scopeForm.name.trim()">Criar escopo</button>
                    <button type="button" class="ghost sm" (click)="addingScope.set(false)">Cancelar</button>
                  </div>
                </form>
              } @else {
                <button type="button" class="ghost sm" (click)="addingScope.set(true)">Novo escopo</button>
              }
            }
          </section>

          @if (sel.scope) {
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
                      <span class="muted">Carregando…</span>
                    }
                  </a>
                }
              </div>
            </section>

            <section class="panel" aria-labelledby="nist-gaps">
              <div class="hd"><h3 id="nist-gaps">Perfil atual × alvo e lacunas</h3>
                @if (profile(); as pr) { <span class="hint">{{ profileBasisText(pr.overall) }}</span> }</div>
              @if (profileError()) {
                <p class="notice error" role="alert">{{ profileError() }}</p>
              } @else if (profile()) {
              @let pr = profile()!;
                <div class="cards">
                  <div class="card"><div class="k">Atual (média)</div><div class="v">{{ averageText(pr.overall.current) }}</div></div>
                  <div class="card"><div class="k">Alvo (média)</div><div class="v">{{ averageText(pr.overall.target) }}</div></div>
                  <div class="card"><div class="k">Lacuna média</div><div class="v">{{ gapText(pr.overall.gap) }}</div></div>
                </div>
                <p class="muted">
                  Médias dos níveis registrados, sem tratar ausência como zero; a lacuna usa só as subcategorias com atual
                  e alvo. Lacuna indeterminada em {{ pr.indeterminateGaps }} subcategoria(s).
                </p>
                @if (pr.gaps.length === 0) {
                  <p class="muted">Nenhuma lacuna determinável ainda: registre atual e alvo nas subcategorias.</p>
                } @else {
                  <div class="table-wrap"><table class="data-table">
                    <caption class="sr-only">Maiores lacunas</caption>
                    <thead><tr><th scope="col">Resultado</th><th scope="col">Atual</th><th scope="col">Alvo</th><th scope="col">Lacuna</th><th scope="col">Responsável</th></tr></thead>
                    <tbody>
                      @for (g of pr.gaps.slice(0, 10); track g.code) {
                        <tr>
                          <td><a class="title" [routerLink]="['/nist', g.code.slice(0, 2).toLowerCase(), g.code]" [queryParams]="params()">{{ g.title }}</a>
                            <span class="meta mono">{{ g.code }}</span></td>
                          <td>{{ levelLabel(g.currentLevel) }}</td><td>{{ levelLabel(g.targetLevel) }}</td>
                          <td><strong>{{ gapText(g.gap) }}</strong></td><td>{{ g.ownerName ?? '—' }}</td>
                        </tr>
                      }
                    </tbody>
                  </table></div>
                }
              } @else {
                <p class="muted">Carregando o perfil…</p>
              }
            </section>
          }

          <section class="notice" aria-label="Maturidade e postura">
            <strong>Dois conceitos, duas leituras.</strong> A maturidade desta avaliação (1 a 5, metodologia do AEGIS) mede a
            prática organizacional. O <strong>score de postura do ambiente</strong> (AEGIS Score, aegis-score-v1) vem dos
            controles avaliados por telemetria e documentos e aparece na aba "Postura do ambiente" de cada função. Os dois
            nunca são somados.
          </section>
        }
      }
    </section>
  `,
  styles: [
    `
      .form-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 220px), 1fr)); gap: var(--sp-3); }
      .form-grid .wide { grid-column: 1 / -1; }
      .actions { display: flex; flex-wrap: wrap; gap: var(--sp-2); }
      .sel-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 260px), 1fr)); gap: var(--sp-3); margin-bottom: var(--sp-3); }
      .meta { font-size: var(--fs-meta); color: var(--muted); margin: 0 0 var(--sp-2); }
      .fn-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 280px), 1fr)); gap: var(--sp-4); }
      .fn { display: flex; flex-direction: column; gap: 6px; text-decoration: none; color: inherit; }
      .fn .v.sm { font-size: 24px; }
      .code { padding: 1px 6px; border-radius: 6px; border: 1px solid var(--line-strong); font-family: var(--mono); font-size: var(--fs-meta); }
      .data-table .meta { display: block; }
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
  protected readonly nistFunctionTitle = nistFunctionTitle;
  protected readonly averageText = averageText;
  protected readonly gapText = gapText;
  protected readonly levelLabel = levelLabel;
  protected readonly profileBasisText = profileBasisText;
  protected readonly scopeProgressText = scopeProgressText;

  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly assessments = signal<NistAssessment[]>([]);
  protected readonly selection = signal<NistSelection | null>(null);
  protected readonly profile = signal<NistProfile | null>(null);
  protected readonly profileError = signal<string | null>(null);
  protected readonly creating = signal(false);
  protected readonly addingScope = signal(false);
  protected readonly busy = signal(false);
  protected readonly formError = signal<string | null>(null);

  protected form = { name: '', description: '', startDate: '', endDate: '', scopeName: '', scopeDescription: '' };
  protected scopeForm = { name: '', description: '' };

  protected readonly canWrite = computed(() => ['Manager', 'TenantAdmin'].includes(this.auth.activeRole() ?? ''));
  protected readonly params = computed(() => selectionParams(this.selection()));

  private selectionTicket = 0;
  private listTicket = 0;
  private profileSub: Subscription | null = null;
  private listSub: Subscription | null = null;
  private destroyed = false;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.profileSub?.unsubscribe();
      this.listSub?.unsubscribe();
    });
  }

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    const tenant = this.auth.activeTenantId();
    const ticket = ++this.listTicket;
    this.listSub?.unsubscribe();
    this.loading.set(true);
    this.error.set(null);
    const current = () => this.alive(tenant) && ticket === this.listTicket;
    this.listSub = this.nist.list().subscribe({
      next: (list) => {
        if (!current()) return;
        this.assessments.set(list);
        const q = this.route.snapshot.queryParamMap;
        this.apply(resolveSelection(list, { assessmentId: q.get('avaliacao'), scopeId: q.get('escopo') },
          this.memory.read(this.auth.activeTenantId())));
        this.loading.set(false);
      },
      error: (e: Error) => {
        if (!current()) return;
        this.error.set(e.message);
        this.loading.set(false);
      },
    });
  }

  protected choose(assessmentId: string, scopeId: string | null): void {
    this.apply(resolveSelection(this.assessments(), { assessmentId, scopeId }, null));
  }

  protected functionProfile(code: string) {
    return this.profile()?.functions.find((f) => f.code === code) ?? null;
  }

  protected coverageText(s: Parameters<typeof scopeCoverage>[0]): string {
    const c = scopeCoverage(s);
    return c === null ? '—' : `${c.toLocaleString('pt-BR', { maximumFractionDigits: 1 })}%`;
  }

  protected statusLabel(status: string): string {
    return ({ Draft: 'Rascunho', InProgress: 'Em andamento', InReview: 'Em revisão', Published: 'Publicada' } as Record<string, string>)[status] ?? status;
  }

  protected toggleCreate(): void {
    this.creating.update((v) => !v);
    this.formError.set(null);
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
      })
      .subscribe({
        next: (a) => {
          // A avaliação existe no servidor; só é aberta se o usuário não escolheu outra enquanto aguardava.
          if (!this.alive(tenant)) return;
          this.busy.set(false);
          this.creating.set(false);
          this.form = { name: '', description: '', startDate: '', endDate: '', scopeName: '', scopeDescription: '' };
          this.assessments.update((list) => [a, ...list]);
          if (ticket === this.selectionTicket) this.apply({ assessment: a, scope: a.scopes[0] ?? null });
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
    const ticket = this.selectionTicket;
    this.nist.addScope(a.id, this.scopeForm.name.trim(), this.scopeForm.description.trim() || null).subscribe({
      next: (scope) => {
        if (!this.alive(tenant)) return;
        this.busy.set(false);
        this.addingScope.set(false);
        this.scopeForm = { name: '', description: '' };
        this.assessments.update((list) => list.map((x) => (x.id === a.id ? { ...x, scopes: [...x.scopes, scope] } : x)));
        const updated = this.assessments().find((x) => x.id === a.id);
        if (updated && ticket === this.selectionTicket) this.apply({ assessment: updated, scope });
      },
      error: (e: NistApiError) => {
        if (!this.alive(tenant)) return;
        this.busy.set(false);
        this.formError.set(e.message);
      },
    });
  }

  /**
   * Nova seleção: cancela o perfil da anterior e descarta qualquer resposta (ou erro) dela. Respostas que chegam depois de
   * sair da tela ou de trocar o tenant não navegam nem repovoam.
   */
  private apply(sel: NistSelection | null): void {
    const ticket = ++this.selectionTicket;
    this.profileSub?.unsubscribe();
    this.profileSub = null;
    this.selection.set(sel);
    this.profile.set(null);
    this.profileError.set(null);
    if (!sel) return;
    void this.router.navigate([], { relativeTo: this.route, queryParams: selectionParams(sel), replaceUrl: true });
    if (!sel.scope) return;
    const tenant = this.auth.activeTenantId();
    this.memory.write(tenant, sel.assessment.id, sel.scope.id);
    const current = () => this.alive(tenant) && ticket === this.selectionTicket;
    this.profileSub = this.nist.profile(sel.assessment.id, sel.scope.id).subscribe({
      next: (p) => {
        if (current()) this.profile.set(p);
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

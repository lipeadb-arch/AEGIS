import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Observable, forkJoin, of } from 'rxjs';
import { catchError, map } from 'rxjs/operators';
import { MonthlyEvolutionComponent } from '../components/monthly-evolution.component';
import { knightReading, nistPostureReading, prioritizedFindings, scoreText } from '../models/dashboards.models';
import { KnightSourceLatest, severityLabel } from '../models/knight.models';
import { NIST_FUNCTIONS, NistAssessment, NistProfile, averageText, gapText, nistFunctionTitle, scopeCoverage } from '../models/nist.models';
import { PostureMonthlyHistory } from '../models/posture-history.models';
import { WorkspacePosture } from '../models/workspace.models';
import { AegisScoreService } from '../services/aegis-score.service';
import { KnightService } from '../services/knight.service';
import { NistService } from '../services/nist.service';
import { PostureHistoryService } from '../services/posture-history.service';

/** Bloco que pode falhar sozinho sem derrubar a tela. */
interface Block<T> {
  value: T | null;
  error: string | null;
}

/**
 * [AEGIS-NIST-JOURNEY-01] Dashboards — visão executiva dos DOIS assessments, cada um pela própria fonte:
 *   • AEGIS KNIGHT: nota, cobertura, findings prioritários, data e fonte de cada avaliação (por fonte; nunca somadas);
 *   • AEGIS NIST: score de postura do ambiente (aegis-score-v1) com cobertura e as seis funções, e — à parte — a
 *     maturidade atual × alvo da avaliação NIST mais recente;
 *   • evolução mensal quando há fotografias comparáveis.
 * Nada é somado, combinado ou recalculado aqui; "sem avaliação", "zero", "parcial" e "desatualizado" são estados ditos.
 */
@Component({
  selector: 'app-dashboards',
  standalone: true,
  imports: [RouterLink, DatePipe, MonthlyEvolutionComponent],
  template: `
    <section class="page">
      <header class="page-head">
        <div>
          <p class="page-eyebrow">Dashboards</p>
          <h1>Visão dos assessments</h1>
          <p class="page-desc">
            O <strong>AEGIS KNIGHT</strong> avalia configurações, políticas e exposição; o <strong>AEGIS NIST</strong> avalia a
            organização pelo NIST CSF 2.0. Cada número vem da sua avaliação — eles não são somados nem combinados.
          </p>
          @if (generatedAt(); as g) { <p class="page-meta">Leitura de {{ g | date: 'dd/MM/yyyy HH:mm' }}</p> }
        </div>
        <div class="page-actions"><button type="button" class="ghost" (click)="load()" [disabled]="loading()">Atualizar</button></div>
      </header>

      @if (loading()) {
        <div class="panel"><div class="state" role="status"><span class="spinner" aria-hidden="true"></span><p>Carregando os assessments…</p></div></div>
      } @else {
        <!-- ===== AEGIS KNIGHT ===== -->
        <section class="section" aria-labelledby="db-knight">
          <div class="section-head"><h2 id="db-knight">AEGIS KNIGHT</h2><a class="linknav" routerLink="/knight">Abrir o KNIGHT →</a></div>
          @if (knight().error) {
            <p class="notice error" role="alert">{{ knight().error }}</p>
          } @else if (knightSources().length === 0) {
            <div class="panel"><div class="state"><p><strong>Nenhuma avaliação técnica concluída.</strong> Conecte uma fonte e execute a primeira avaliação no KNIGHT.</p>
              <a class="primary" routerLink="/knight">Executar avaliação</a></div></div>
          } @else {
            <div class="grid-k">
              @for (s of knightSources(); track s.slug) {
                @let a = s.assessment;
                @let r = reading(s);
                <article class="panel kcard" [class.stale]="r.stale">
                  <div class="hd"><h3>{{ s.label }}</h3>
                    @if (a) { <a class="linkbtn" routerLink="/knight" [queryParams]="{ run: a.id }">Detalhes</a> }</div>
                  @if (a) {
                    <div class="nums">
                      <div><span class="k">Nota KNIGHT</span><span class="v">{{ scoreText(a.score) }}</span></div>
                      <div><span class="k">Cobertura</span><span class="v">{{ a.coverage }}<small>%</small></span></div>
                      <div><span class="k">Reprovados</span><span class="v">{{ a.counts.exposed }}</span></div>
                    </div>
                    <p class="muted">Concluída em {{ (a.completedAt ?? a.startedAt) | date: 'dd/MM/yyyy HH:mm' }} · fonte {{ a.source }} · catálogo {{ a.catalogVersion }}</p>
                  }
                  @if (r.labels.length) {
                    <p class="flags">@for (l of r.labels; track l) { <span class="badge" [class.warn]="r.stale || r.partial || r.demo" [class.neutral]="!(r.stale || r.partial || r.demo)">{{ l }}</span> }</p>
                  }
                  @if (s.unfinishedAttempt) { <p class="muted">Há uma tentativa posterior não concluída ({{ s.unfinishedAttempt.status }}).</p> }
                  @if (findings(s).length > 0) {
                    <h4>Findings prioritários</h4>
                    <ul class="fl">
                      @for (i of findings(s); track i.indicatorId) {
                        <li><a routerLink="/knight" [queryParams]="{ run: a!.id, finding: i.indicatorId }">{{ i.title }}</a>
                          <span class="muted">{{ severityLabel(i.severity) }} · {{ i.affectedObjectCount }} afetado(s) · <span class="mono">{{ i.indicatorId }}</span></span></li>
                      }
                    </ul>
                  } @else if (a && a.score !== null) {
                    <p class="muted">Nenhum controle reprovado nesta avaliação.</p>
                  }
                </article>
              }
            </div>
          }
        </section>

        <!-- ===== AEGIS NIST ===== -->
        <section class="section" aria-labelledby="db-nist">
          <div class="section-head"><h2 id="db-nist">AEGIS NIST</h2><a class="linknav" routerLink="/nist">Abrir o NIST →</a></div>
          <div class="grid-n">
            <article class="panel">
              <div class="hd"><h3>Postura do ambiente</h3><span class="hint">AEGIS Score · aegis-score-v1</span></div>
              @if (posture().error) {
                <p class="notice error" role="alert">{{ posture().error }}</p>
              } @else if (posture().value) {
              @let w = posture().value!;
                @let pr = postureReading();
                <div class="nums">
                  <div><span class="k">Score</span><span class="v">{{ w.overall.percentage === null ? '—' : scoreText(w.overall.percentage) }}@if (w.overall.percentage !== null) {<small>%</small>}</span></div>
                  <div><span class="k">Cobertura</span><span class="v">{{ scoreText(w.overall.coveragePercentage) }}<small>%</small></span></div>
                  <div><span class="k">Controles avaliados</span><span class="v">{{ w.overall.evaluatedControls }}<small> de {{ w.overall.eligibleControls }}</small></span></div>
                </div>
                <p class="flags">@for (l of pr.labels; track l) { <span class="badge" [class.warn]="pr.stale || pr.partial" [class.neutral]="!(pr.stale || pr.partial)">{{ l }}</span> }</p>
                <ul class="fns">
                  @for (f of functionsPosture(); track f.code) {
                    <li><a [routerLink]="['/nist', f.slug, 'postura']">{{ f.title }}</a>
                      <span>{{ f.state }}</span></li>
                  }
                </ul>
                @if (!pr.hasResult) { <p class="muted">Sem controle avaliado ainda. Conecte as fontes em <a routerLink="/settings/integrations">Integrações</a> ou envie documentos de governança.</p> }
              }
            </article>

            <article class="panel">
              <div class="hd"><h3>Maturidade da avaliação</h3><span class="hint">atual × alvo · escala 1–5</span></div>
              @if (maturity().error) {
                <p class="notice error" role="alert">{{ maturity().error }}</p>
              } @else if (latestAssessment()) {
              @let la = latestAssessment()!;
                <p><strong>{{ la.name }}</strong> · {{ la.scopes.length > 0 ? la.scopes[0].name : 'sem escopo' }}</p>
                @if (la.scopes[0]; as sc) {
                  <p class="muted">{{ sc.evaluated }} de {{ sc.subcategories }} subcategorias avaliadas · cobertura {{ coverage(sc) }} · metodologia {{ la.methodologyVersion }}</p>
                }
                @if (profile(); as p) {
                  <div class="nums">
                    <div><span class="k">Atual</span><span class="v">{{ averageText(p.overall.current) }}</span></div>
                    <div><span class="k">Alvo</span><span class="v">{{ averageText(p.overall.target) }}</span></div>
                    <div><span class="k">Lacuna</span><span class="v">{{ gapText(p.overall.gap) }}</span></div>
                  </div>
                  <ul class="fns">
                    @for (f of functionsMaturity(); track f.code) {
                      <li><a [routerLink]="['/nist', f.slug]" [queryParams]="{ avaliacao: la.id, escopo: la.scopes.length > 0 ? la.scopes[0].id : null }">{{ f.title }}</a><span>{{ f.state }}</span></li>
                    }
                  </ul>
                }
              } @else {
                <div class="state"><p><strong>Nenhuma avaliação NIST criada.</strong> Defina a avaliação e o escopo para começar.</p>
                  <a class="primary" routerLink="/nist">Criar avaliação</a></div>
              }
            </article>
          </div>
        </section>

        <!-- ===== Evolução ===== -->
        <section class="panel" aria-labelledby="db-evo">
          <div class="hd"><h3 id="db-evo">Evolução mensal</h3><a class="linkbtn" routerLink="/history">Histórico de postura</a></div>
          @if (monthly().error) {
            <p class="notice error" role="alert">{{ monthly().error }}</p>
          } @else if (comparableSeries() === 0) {
            <p class="muted">Ainda sem histórico comparável: a evolução aparece quando houver fotografias publicadas em meses diferentes. Publique registros em <a routerLink="/history" [queryParams]="{ vista: 'fotografias' }">Histórico de postura</a>.</p>
          } @else {
            <app-monthly-evolution [history]="monthly().value" />
          }
        </section>
      }
    </section>
  `,
  styles: [
    `
      .grid-k { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 320px), 1fr)); gap: var(--sp-4); }
      .grid-n { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 360px), 1fr)); gap: var(--sp-4); }
      .kcard.stale { border-color: rgba(255, 176, 32, 0.35); }
      .nums { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: var(--sp-3); margin-bottom: var(--sp-2); }
      .nums > div { display: flex; flex-direction: column; min-width: 0; }
      .nums .k { font-size: var(--fs-meta); color: var(--muted); }
      .nums .v { font-size: 26px; font-weight: 700; }
      .nums small { font-size: var(--fs-sm); color: var(--muted); font-weight: 500; }
      .flags { display: flex; flex-wrap: wrap; gap: 6px; margin: var(--sp-2) 0; }
      h4 { margin: var(--sp-3) 0 var(--sp-2); font-size: var(--fs-sm); }
      .fl, .fns { list-style: none; padding: 0; margin: 0; display: flex; flex-direction: column; gap: 6px; }
      .fl li { display: flex; flex-direction: column; }
      .fl .muted { font-size: var(--fs-meta); }
      .fns li { display: flex; flex-wrap: wrap; justify-content: space-between; gap: 4px var(--sp-3); font-size: var(--fs-sm); padding: 4px 0; border-top: 1px solid var(--line-2); }
      .fns li span { color: var(--text-2); }
      p { margin: 0 0 var(--sp-2); }
    `,
  ],
})
export class DashboardsComponent implements OnInit {
  private readonly knightApi = inject(KnightService);
  private readonly score = inject(AegisScoreService);
  private readonly nist = inject(NistService);
  private readonly historyApi = inject(PostureHistoryService);

  protected readonly scoreText = scoreText;
  protected readonly averageText = averageText;
  protected readonly gapText = gapText;
  protected readonly severityLabel = severityLabel;

  protected readonly loading = signal(true);
  protected readonly generatedAt = signal<Date | null>(null);
  protected readonly knight = signal<Block<KnightSourceLatest[]>>({ value: null, error: null });
  protected readonly posture = signal<Block<WorkspacePosture>>({ value: null, error: null });
  protected readonly maturity = signal<Block<NistAssessment[]>>({ value: null, error: null });
  protected readonly profile = signal<NistProfile | null>(null);
  protected readonly monthly = signal<Block<PostureMonthlyHistory>>({ value: null, error: null });

  protected readonly knightSources = computed(() => (this.knight().value ?? []).filter((s) => s.assessment || s.unfinishedAttempt));
  protected readonly latestAssessment = computed(() => this.maturity().value?.[0] ?? null);
  protected readonly postureReading = computed(() => nistPostureReading(this.posture().value?.overall ?? null, this.generatedAt() ?? new Date()));
  /** Séries com pelo menos dois meses publicados: um ponto isolado não é evolução. */
  protected readonly comparableSeries = computed(() => (this.monthly().value?.series ?? []).filter((s) => s.points.length >= 2).length);

  protected readonly functionsPosture = computed(() => {
    const w = this.posture().value;
    return NIST_FUNCTIONS.map((f) => {
      const p = w?.functions.find((x) => x.code === f.code);
      const state = !p || p.evaluationState === 'NotEvaluated' || p.percentage === null
        ? 'Sem controle avaliado'
        : `${scoreText(p.percentage)}% · cobertura ${scoreText(p.coveragePercentage)}%`;
      return { code: f.code, slug: f.slug, title: nistFunctionTitle(f), state };
    });
  });

  protected readonly functionsMaturity = computed(() => {
    const p = this.profile();
    return NIST_FUNCTIONS.map((f) => {
      const x = p?.functions.find((y) => y.code === f.code);
      const state = !x || x.evaluated === 0 ? 'Não avaliada' : `${x.evaluated} de ${x.subcategories} · atual ${averageText(x.current)} · alvo ${averageText(x.target)}`;
      return { code: f.code, slug: f.slug, title: nistFunctionTitle(f), state };
    });
  });

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.profile.set(null);
    const block = <T>(message: string) => (src: Observable<T>) =>
      src.pipe(
        map((value) => ({ value, error: null }) as Block<T>),
        catchError(() => of({ value: null, error: message } as Block<T>)),
      );
    forkJoin({
      knight: this.knightApi.getLatestBySource().pipe(map((r) => r.sources), block<KnightSourceLatest[]>('Não foi possível ler as avaliações do KNIGHT.')),
      posture: this.score.fetchWorkspace().pipe(block<WorkspacePosture>('Não foi possível ler a postura do ambiente.')),
      maturity: this.nist.list().pipe(block<NistAssessment[]>('Não foi possível ler as avaliações NIST.')),
      monthly: this.historyApi.monthly(12).pipe(block<PostureMonthlyHistory>('Não foi possível ler a evolução mensal.')),
    }).subscribe((r) => {
      this.generatedAt.set(new Date());
      this.knight.set(r.knight);
      this.posture.set(r.posture);
      this.maturity.set(r.maturity);
      this.monthly.set(r.monthly);
      this.loading.set(false);
      const la = r.maturity.value?.[0];
      if (la?.scopes[0])
        this.nist.profile(la.id, la.scopes[0].id).subscribe({ next: (p) => this.profile.set(p), error: () => this.profile.set(null) });
    });
  }

  protected reading(s: KnightSourceLatest) {
    return knightReading(s.assessment, this.generatedAt() ?? new Date());
  }

  protected findings(s: KnightSourceLatest) {
    return prioritizedFindings(s.assessment, 3);
  }

  protected coverage(sc: Parameters<typeof scopeCoverage>[0]): string {
    const c = scopeCoverage(sc);
    return c === null ? '—' : `${c.toLocaleString('pt-BR', { maximumFractionDigits: 1 })}%`;
  }
}
